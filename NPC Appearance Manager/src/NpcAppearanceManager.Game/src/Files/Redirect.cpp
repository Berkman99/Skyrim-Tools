#include "PCH.h"

#include "Files/Redirect.h"

#include "Files/Archive.h"

#include <Windows.h>
#include <detours/detours.h>
#include <regex>

namespace NAM::Files::Redirect
{
    namespace
    {
        // Where a served file is: a loose file in a mod's folder, or a file
        // inside one of its archives.
        struct Target
        {
            std::wstring file;   // the loose file, or the archive
            std::string  entry;  // the file's path inside the archive; empty for a loose file
        };

        // Data-relative path (lower case, backslashes) -> where it is.
        // Written once at kDataLoaded, read-only afterwards.
        std::unordered_map<std::string, Target> g_targets;
        std::unordered_set<std::string>         g_sources;  // the served loose files, lower case
        std::unordered_set<std::string>         g_missing;  // listed game paths whose file isn't there (Data-relative, lower case)

        // A mod folder's Nexus ID: Mod Organizer keeps it in the folder's meta.ini ("modid="), Vortex in the folder's
        // name ("Name-12376-4-1-1637004719", or "Name 266 4.3.9c 2026-09-05T22-22Z lNkz9LChN").
        std::optional<int> NexusId(const std::filesystem::path& a_folder)
        {
            std::ifstream meta(a_folder / "meta.ini");
            for (std::string line; std::getline(meta, line);) {
                if (_strnicmp(line.c_str(), "modid=", 6) == 0) {
                    const auto id = std::atoi(line.c_str() + 6);
                    return id > 0 ? std::optional(id) : std::nullopt;
                }
            }
            static const std::regex nexusFile(R"(^(.+?)-(\d+)-[0-9A-Za-z.-]*?-(\d{9,11})$)");
            static const std::regex newVortex(R"(^(.+) (\d+) \S+ \d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z \S+$)");
            const auto name = a_folder.filename().string();
            std::smatch match;
            if (std::regex_match(name, match, nexusFile) || std::regex_match(name, match, newVortex)) {
                return std::atoi(match[2].str().c_str());
            }
            return std::nullopt;
        }

        // A file of a mod renamed since Save: the folders next to its old one with the same Nexus ID, the first that has
        // the file at the same place. The folders are read once, only when a mod was renamed.
        std::optional<std::filesystem::path> InRenamedMod(const std::filesystem::path& a_old, int a_id, const std::filesystem::path& a_inMod)
        {
            static std::unordered_map<std::wstring, std::unordered_multimap<int, std::filesystem::path>> byFolder;
            auto oldMod = a_old;
            for (auto part = a_inMod; !part.empty() && part != part.parent_path(); part = part.parent_path()) {
                oldMod = oldMod.parent_path();
            }
            const auto mods = oldMod.parent_path();
            auto [it, isNew] = byFolder.try_emplace(mods.wstring());
            if (isNew) {
                std::error_code error;
                for (const auto& folder : std::filesystem::directory_iterator(mods, error)) {
                    if (folder.is_directory(error)) {
                        if (const auto id = NexusId(folder.path())) {
                            it->second.emplace(*id, folder.path());
                        }
                    }
                }
            }
            for (auto [first, last] = it->second.equal_range(a_id); first != last; ++first) {
                if (auto file = first->second / a_inMod; std::filesystem::exists(file)) {
                    return file;
                }
            }
            return std::nullopt;
        }
        std::atomic_bool                        g_ready = false;
        std::atomic_int                         g_logged = 0;
        std::atomic_uint                        g_opened = 0;

        decltype(&CreateFileA)          g_createFileA = CreateFileA;
        decltype(&CreateFileW)          g_createFileW = CreateFileW;
        decltype(&GetFileAttributesA)   g_getAttributesA = GetFileAttributesA;
        decltype(&GetFileAttributesW)   g_getAttributesW = GetFileAttributesW;
        decltype(&GetFileAttributesExA) g_getAttributesExA = GetFileAttributesExA;
        decltype(&GetFileAttributesExW) g_getAttributesExW = GetFileAttributesExW;
        decltype(&FindFirstFileA)       g_findFirstA = FindFirstFileA;
        decltype(&FindFirstFileW)       g_findFirstW = FindFirstFileW;

        std::string Lower(std::string_view a_path)
        {
            std::string s{ a_path };
            for (auto& c : s) {
                c = c == '/' ? '\\' : static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            }
            return s;
        }

        std::string Narrow(const wchar_t* a_path)
        {
            std::string s;
            for (; *a_path; ++a_path) {
                s += *a_path < 128 ? static_cast<char>(*a_path) : '?';
            }
            return s;
        }

        std::wstring Wide(const std::string& a_utf8)
        {
            const auto   length = MultiByteToWideChar(CP_UTF8, 0, a_utf8.data(), static_cast<int>(a_utf8.size()), nullptr, 0);
            std::wstring wide(static_cast<std::size_t>(length), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, a_utf8.data(), static_cast<int>(a_utf8.size()), wide.data(), length);
            return wide;
        }

        // The part of a path from its first "meshes\" or "textures\" folder:
        // "...\Data\meshes\x.nif" and "...\mods\Some Mod\meshes\x.nif" both
        // give "meshes\x.nif", with or without a mod manager in between.
        std::string_view Tail(std::string_view a_lower)
        {
            std::size_t best = std::string_view::npos;
            for (const auto root : { "meshes\\"sv, "textures\\"sv }) {
                for (std::size_t at = a_lower.find(root); at != std::string_view::npos; at = a_lower.find(root, at + 1)) {
                    if (at == 0 || a_lower[at - 1] == '\\') {
                        best = std::min(best, at);
                        break;
                    }
                }
            }
            return best == std::string_view::npos ? std::string_view{} : a_lower.substr(best);
        }

        const Target* Find(std::string_view a_path)
        {
            if (!g_ready) {
                return nullptr;
            }
            const auto lower = Lower(a_path);
            // A file being served is opened as it is (a copied face's file
            // must not be swapped again for that NPC's own pick).
            if (g_sources.contains(lower)) {
                return nullptr;
            }
            const auto tail = Tail(lower);
            if (tail.empty()) {
                return nullptr;
            }
            const auto it = g_targets.find(std::string{ tail });
            if (it == g_targets.end()) {
                return nullptr;
            }
            if (g_logged < 100) {
                ++g_logged;
                SKSE::log::info("served {}", tail);
            }
            return &it->second;
        }

        const Target* Find(const char* a_path) { return a_path ? Find(std::string_view{ a_path }) : nullptr; }
        const Target* Find(const wchar_t* a_path) { return a_path ? Find(Narrow(a_path)) : nullptr; }

        // A file inside an archive is read from the archive into memory and
        // handed to the game as a temporary file that Windows deletes as soon
        // as the game closes it (it normally never leaves memory).
        HANDLE OpenFromArchive(const Target& a_target, DWORD a_access, DWORD a_share, LPSECURITY_ATTRIBUTES a_security, DWORD a_flags)
        {
            const auto bytes = Archive::Read(a_target.file, a_target.entry);
            if (!bytes) {
                SetLastError(ERROR_FILE_NOT_FOUND);
                return INVALID_HANDLE_VALUE;
            }
            wchar_t folder[MAX_PATH]{};
            GetTempPathW(MAX_PATH, folder);
            const auto temp = std::format(L"{}NAM {}-{}{}", folder, GetCurrentProcessId(), ++g_opened,
                std::filesystem::path(a_target.entry).extension().wstring());

            const auto writer = g_createFileW(temp.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
                CREATE_ALWAYS, FILE_ATTRIBUTE_TEMPORARY | FILE_FLAG_DELETE_ON_CLOSE, nullptr);
            if (writer == INVALID_HANDLE_VALUE) {
                return INVALID_HANDLE_VALUE;
            }
            DWORD written = 0;
            WriteFile(writer, bytes->data(), static_cast<DWORD>(bytes->size()), &written, nullptr);
            const auto reader = g_createFileW(temp.c_str(), a_access & ~(GENERIC_WRITE | FILE_WRITE_DATA | FILE_APPEND_DATA),
                a_share | FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, a_security, OPEN_EXISTING,
                (a_flags & ~FILE_FLAG_DELETE_ON_CLOSE) | FILE_ATTRIBUTE_TEMPORARY, nullptr);
            CloseHandle(writer);  // the file goes once the game closes its handle too
            return reader;
        }

        HANDLE Open(const Target& a_target, DWORD a_access, DWORD a_share, LPSECURITY_ATTRIBUTES a_security, DWORD a_disposition, DWORD a_flags, HANDLE a_template)
        {
            return a_target.entry.empty()
                       ? g_createFileW(a_target.file.c_str(), a_access, a_share, a_security, a_disposition, a_flags, a_template)
                       : OpenFromArchive(a_target, a_access, a_share, a_security, a_flags);
        }

        // Size and dates of a served file: a loose file's own, or the archive's
        // dates with the size of the file inside it.
        bool Describe(const Target& a_target, WIN32_FILE_ATTRIBUTE_DATA& a_data)
        {
            if (!g_getAttributesExW(a_target.file.c_str(), GetFileExInfoStandard, &a_data)) {
                return false;
            }
            if (!a_target.entry.empty()) {
                const auto size = Archive::Size(a_target.file, a_target.entry);
                if (!size) {
                    return false;
                }
                a_data.dwFileAttributes = FILE_ATTRIBUTE_ARCHIVE;
                a_data.nFileSizeHigh = static_cast<DWORD>(*size >> 32);
                a_data.nFileSizeLow = static_cast<DWORD>(*size);
            }
            return true;
        }

        HANDLE WINAPI HookCreateFileA(LPCSTR a_name, DWORD a_access, DWORD a_share, LPSECURITY_ATTRIBUTES a_security, DWORD a_disposition, DWORD a_flags, HANDLE a_template)
        {
            if (const auto* target = Find(a_name)) {
                return Open(*target, a_access, a_share, a_security, a_disposition, a_flags, a_template);
            }
            return g_createFileA(a_name, a_access, a_share, a_security, a_disposition, a_flags, a_template);
        }

        HANDLE WINAPI HookCreateFileW(LPCWSTR a_name, DWORD a_access, DWORD a_share, LPSECURITY_ATTRIBUTES a_security, DWORD a_disposition, DWORD a_flags, HANDLE a_template)
        {
            if (const auto* target = Find(a_name)) {
                return Open(*target, a_access, a_share, a_security, a_disposition, a_flags, a_template);
            }
            return g_createFileW(a_name, a_access, a_share, a_security, a_disposition, a_flags, a_template);
        }

        DWORD Attributes(const Target& a_target)
        {
            WIN32_FILE_ATTRIBUTE_DATA data{};
            return Describe(a_target, data) ? data.dwFileAttributes : INVALID_FILE_ATTRIBUTES;
        }

        DWORD WINAPI HookGetAttributesA(LPCSTR a_name)
        {
            if (const auto* target = Find(a_name)) {
                return Attributes(*target);
            }
            return g_getAttributesA(a_name);
        }

        DWORD WINAPI HookGetAttributesW(LPCWSTR a_name)
        {
            if (const auto* target = Find(a_name)) {
                return Attributes(*target);
            }
            return g_getAttributesW(a_name);
        }

        BOOL AttributesEx(const Target& a_target, GET_FILEEX_INFO_LEVELS a_level, LPVOID a_info)
        {
            if (a_level != GetFileExInfoStandard || !a_info) {
                return g_getAttributesExW(a_target.file.c_str(), a_level, a_info);
            }
            return Describe(a_target, *static_cast<WIN32_FILE_ATTRIBUTE_DATA*>(a_info));
        }

        BOOL WINAPI HookGetAttributesExA(LPCSTR a_name, GET_FILEEX_INFO_LEVELS a_level, LPVOID a_info)
        {
            if (const auto* target = Find(a_name)) {
                return AttributesEx(*target, a_level, a_info);
            }
            return g_getAttributesExA(a_name, a_level, a_info);
        }

        BOOL WINAPI HookGetAttributesExW(LPCWSTR a_name, GET_FILEEX_INFO_LEVELS a_level, LPVOID a_info)
        {
            if (const auto* target = Find(a_name)) {
                return AttributesEx(*target, a_level, a_info);
            }
            return g_getAttributesExW(a_name, a_level, a_info);
        }

        // A find handle on the served file (a loose file, or the archive), with
        // the size and name of the file the game asked for.
        HANDLE FindServed(const Target& a_target, std::wstring_view a_asked, WIN32_FIND_DATAW& a_data)
        {
            WIN32_FILE_ATTRIBUTE_DATA info{};
            if (!Describe(a_target, info)) {
                SetLastError(ERROR_FILE_NOT_FOUND);
                return INVALID_HANDLE_VALUE;
            }
            const auto handle = g_findFirstW(a_target.file.c_str(), &a_data);
            if (handle == INVALID_HANDLE_VALUE) {
                return handle;
            }
            a_data.dwFileAttributes = info.dwFileAttributes;
            a_data.nFileSizeHigh = info.nFileSizeHigh;
            a_data.nFileSizeLow = info.nFileSizeLow;
            const auto slash = a_asked.find_last_of(L"\\/");
            const auto leaf = slash == std::wstring_view::npos ? a_asked : a_asked.substr(slash + 1);
            std::fill(std::begin(a_data.cFileName), std::end(a_data.cFileName), L'\0');
            leaf.copy(a_data.cFileName, std::min(leaf.size(), static_cast<std::size_t>(MAX_PATH - 1)));
            return handle;
        }

        HANDLE WINAPI HookFindFirstA(LPCSTR a_name, LPWIN32_FIND_DATAA a_data)
        {
            if (const auto* target = Find(a_name)) {
                WIN32_FIND_DATAW wide{};
                const std::string asked{ a_name };
                const auto        handle = FindServed(*target, std::wstring(asked.begin(), asked.end()), wide);
                if (handle != INVALID_HANDLE_VALUE) {
                    *a_data = {};
                    a_data->dwFileAttributes = wide.dwFileAttributes;
                    a_data->ftCreationTime = wide.ftCreationTime;
                    a_data->ftLastAccessTime = wide.ftLastAccessTime;
                    a_data->ftLastWriteTime = wide.ftLastWriteTime;
                    a_data->nFileSizeHigh = wide.nFileSizeHigh;
                    a_data->nFileSizeLow = wide.nFileSizeLow;
                    const auto slash = asked.find_last_of("\\/");
                    const auto leaf = slash == std::string::npos ? asked : asked.substr(slash + 1);
                    leaf.copy(a_data->cFileName, std::min(leaf.size(), static_cast<std::size_t>(MAX_PATH - 1)));
                }
                return handle;
            }
            return g_findFirstA(a_name, a_data);
        }

        HANDLE WINAPI HookFindFirstW(LPCWSTR a_name, LPWIN32_FIND_DATAW a_data)
        {
            if (const auto* target = Find(a_name)) {
                return FindServed(*target, a_name, *a_data);
            }
            return g_findFirstW(a_name, a_data);
        }

        std::vector<char> Head(const Target& a_target, std::size_t a_count)
        {
            if (!a_target.entry.empty()) {
                auto bytes = Archive::Read(a_target.file, a_target.entry).value_or(std::vector<char>{});
                bytes.resize(std::min(bytes.size(), a_count));
                return bytes;
            }
            std::vector<char> bytes(a_count);
            std::ifstream     in(a_target.file, std::ios::binary);
            in.read(bytes.data(), static_cast<std::streamsize>(a_count));
            bytes.resize(static_cast<std::size_t>(in.gcount()));
            return bytes;
        }

        template <class F>
        void Attach(F& a_original, F a_hook)
        {
            DetourAttach(reinterpret_cast<PVOID*>(&a_original), reinterpret_cast<PVOID>(a_hook));
        }
    }

    void Install()
    {
        // Hook where the game asks Windows for a file. These are Windows
        // functions, so nothing here depends on the game version.
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        Attach(g_createFileA, &HookCreateFileA);
        Attach(g_createFileW, &HookCreateFileW);
        Attach(g_getAttributesA, &HookGetAttributesA);
        Attach(g_getAttributesW, &HookGetAttributesW);
        Attach(g_getAttributesExA, &HookGetAttributesExA);
        Attach(g_getAttributesExW, &HookGetAttributesExW);
        Attach(g_findFirstA, &HookFindFirstA);
        Attach(g_findFirstW, &HookFindFirstW);
        const auto result = DetourTransactionCommit();
        SKSE::log::info("Face file loading hooked (result {}).", result);
    }

    void Load(const toml::table& a_list)
    {
        std::size_t missing = 0;
        if (const auto* files = a_list["files"].as_array()) {
            for (const auto& node : *files) {
                const auto* entry = node.as_table();
                if (!entry) {
                    continue;
                }
                const auto game = (*entry)["game"].value<std::string>();
                const auto from = (*entry)["from"].value<std::string>();
                if (!game || !from) {
                    continue;
                }
                const auto key = Lower(*game);
                const auto tail = Tail(key);
                if (tail.empty()) {
                    SKSE::log::warn("Not a meshes or textures path: {}", *game);
                    continue;
                }
                auto path = std::filesystem::path(Wide(*from));
                if (!std::filesystem::exists(path)) {
                    // The mod was renamed since Save: found again by its Nexus ID, the file at the same place in it.
                    const auto id = (*entry)["mod_id"].value<std::int64_t>();
                    const auto inMod = (*entry)["in_mod"].value<std::string>();
                    const auto moved = id && inMod ? InRenamedMod(path, static_cast<int>(*id), std::filesystem::path(Wide(*inMod))) : std::nullopt;
                    if (!moved) {
                        ++missing;
                        g_missing.insert(std::string{ tail });
                        SKSE::log::warn("Face file not found (its mod was removed or renamed): {}", *from);
                        continue;
                    }
                    SKSE::log::info("Face file found in its mod's new folder: {}", Narrow(moved->c_str()));
                    path = *moved;
                }
                g_targets[std::string{ tail }] = Target{ path.lexically_normal().wstring(), (*entry)["entry"].value_or(std::string{}) };
            }
        }
        SKSE::log::info("Face files listed: {} ({} missing).", g_targets.size(), missing);
    }

    void Drop(std::string_view a_gamePath)
    {
        const auto key = Lower(a_gamePath);
        const auto tail = Tail(key);
        if (const auto it = g_targets.find(std::string{ tail }); it != g_targets.end()) {
            g_targets.erase(it);
        }
    }

    void Start()
    {
        for (const auto& [_, target] : g_targets) {
            if (target.entry.empty()) {
                g_sources.insert(Lower(Narrow(target.file.c_str())));
            }
        }
        g_ready = !g_targets.empty();
    }

    bool IsMissing(std::string_view a_gamePath)
    {
        const auto key = Lower(a_gamePath);
        const auto tail = Tail(key);
        return !tail.empty() && g_missing.contains(std::string{ tail });
    }

    void SelfCheck()
    {
        // Five files, archive ones first, each opened through the game's own
        // loader and compared with the source.
        std::vector<std::pair<const std::string*, const Target*>> picked;
        for (const auto& [game, target] : g_targets) {
            if (!target.entry.empty()) {
                picked.emplace_back(&game, &target);
            }
        }
        for (const auto& [game, target] : g_targets) {
            if (target.entry.empty()) {
                picked.emplace_back(&game, &target);
            }
        }
        picked.resize(std::min<std::size_t>(picked.size(), 5));

        for (const auto& [game, target] : picked) {
            const auto expected = Head(*target, 256);
            RE::BSResourceNiBinaryStream stream(*game);
            if (!stream.good()) {
                SKSE::log::error("Check FAIL (not opened): {}", *game);
                continue;
            }
            std::vector<char> got(expected.size());
            stream.read(got.data(), static_cast<std::uint32_t>(got.size()));
            SKSE::log::info("Check {}{}: {}", !expected.empty() && got == expected ? "PASS" : "FAIL (other file)",
                target->entry.empty() ? "" : " (from archive)", *game);
        }
    }
}
