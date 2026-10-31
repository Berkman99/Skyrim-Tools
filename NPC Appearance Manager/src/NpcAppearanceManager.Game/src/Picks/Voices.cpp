#include "PCH.h"

#include "Picks/Voices.h"

#include <detours/detours.h>
#include <mutex>

namespace NAM::Voices
{
    namespace
    {
        // Read once when the game has loaded its plugins; the hooks only read it afterwards.
        std::unordered_map<RE::TESNPC*, RE::BGSVoiceType*> g_speaksAs;

        // Whether the new voice has a recording of a line (by its path there), looked up once per game session.
        std::unordered_map<std::string, bool> g_hasRecording;
        std::mutex                            g_hasRecordingLock;

        // The game fills in each part of a line with its speaker; the part's sound path is made inside that, by a call
        // that gets the speaker's voice. Dynamic Dialogue Replacer hooks the same two places; both hooks run, in either
        // order, and NAM only changes a path the game made itself, so a dialogue mod's own sound always plays.
        using Populate = std::int64_t (*)(std::int64_t, RE::TESTopic*, RE::TESTopicInfo*, RE::Character*, RE::TESTopicInfo::TESResponse*);
        Populate                    g_populate = nullptr;
        thread_local RE::Character* t_speaker = nullptr;
        REL::Relocation<bool (*)(RE::TESTopicInfo::TESResponse*, char*, RE::BGSVoiceType*, RE::TESTopic*, RE::TESTopicInfo*)> g_construct;
        constexpr std::size_t       kPathSize = 0x104;

        // Lines a swapped NPC says that the new voice has no recording of: they play from the old voice's own file,
        // so the mouth moves as always, and the game makes the sound with no volume. Each waits for its sound once.
        struct Muted
        {
            std::uint32_t                         file = 0;
            std::uint32_t                         dir = 0;
            std::string                           path;
            std::chrono::steady_clock::time_point when;
        };
        std::vector<Muted> g_muted;
        std::mutex         g_mutedLock;
        int                g_unmatchedLogged = 0;

        using GetByFile = void (*)(RE::BSAudioManager*, RE::BSSoundHandle&, const RE::BSResource::ID&, std::uint32_t, std::uint32_t);
        GetByFile g_getByFile = nullptr;

        // Lines already in NAM.log: each is listed once per game session.
        std::unordered_set<std::string> g_logged;
        std::mutex                      g_loggedLock;

        bool FirstTime(std::string_view a_what)
        {
            std::scoped_lock lock(g_loggedLock);
            return g_logged.emplace(a_what).second;
        }

        bool IsSeparator(char a_char) { return a_char == '\\' || a_char == '/'; }

        // A line's sound sits in a folder named after the voice: sound\voice\<plugin>\<voice>\<line>. The same line in
        // another voice is the same path with that voice's folder.
        std::optional<std::string> InVoice(std::string_view a_path, std::string_view a_from, std::string_view a_to)
        {
            for (std::size_t at = 0; at + a_from.size() + 2 <= a_path.size(); ++at) {
                if (IsSeparator(a_path[at]) && IsSeparator(a_path[at + a_from.size() + 1]) &&
                    _strnicmp(a_path.data() + at + 1, a_from.data(), a_from.size()) == 0) {
                    return std::format("{}{}{}", a_path.substr(0, at + 1), a_to, a_path.substr(at + a_from.size() + 1));
                }
            }
            return std::nullopt;
        }

        // "Data\Sound\Voice\...\x.wav" -> "sound\voice\...\x" (Data-relative, no extension), as the game's file loader takes it.
        std::string Base(std::string_view a_path)
        {
            std::string lower{ a_path };
            for (auto& c : lower) {
                c = c == '/' ? '\\' : static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            }
            const auto from = lower.find("sound\\");
            if (from != std::string::npos) {
                lower.erase(0, from);
            }
            if (const auto dot = lower.find_last_of('.'); dot != std::string::npos && lower.find('\\', dot) == std::string::npos) {
                lower.erase(dot);
            }
            return lower;
        }

        // A recording through the game's own loader: loose files and every loaded archive.
        bool HasRecording(const std::string& a_base)
        {
            std::scoped_lock lock(g_hasRecordingLock);
            if (const auto it = g_hasRecording.find(a_base); it != g_hasRecording.end()) {
                return it->second;
            }
            const auto has = std::ranges::any_of(std::array{ ".fuz"sv, ".xwm"sv, ".wav"sv }, [&](std::string_view a_ext) {
                RE::BSResourceNiBinaryStream stream(a_base + std::string(a_ext));
                return stream.good();
            });
            return g_hasRecording[a_base] = has;
        }

        // The sound path the game itself gives a line: in the voice's folder, named after the line and its part
        // ("..._0001C5CA_1.wav"). Another mod's sound for it (a dialogue mod's) is named otherwise and stays as it is.
        bool IsGamesOwn(std::string_view a_path, const RE::TESTopicInfo* a_info, const RE::TESTopicInfo::TESResponse* a_response)
        {
            if (!a_info || !a_response) {
                return false;
            }
            const auto slash = a_path.find_last_of("\\/");
            auto       name = slash == std::string_view::npos ? a_path : a_path.substr(slash + 1);
            if (const auto dot = name.find_last_of('.'); dot != std::string_view::npos) {
                name = name.substr(0, dot);
            }
            const auto ending = std::format("_{:08X}_{}", a_info->GetLocalFormID(), a_response->responseNumber);
            return name.size() >= ending.size() && _strnicmp(name.data() + name.size() - ending.size(), ending.data(), ending.size()) == 0;
        }

        // The old voice's file of a line, to be made with no volume when the game makes its sound.
        void Mute(const std::string& a_base)
        {
            RE::BSResource::ID id;
            const auto         path = a_base + ".wav";
            id.GenerateFromPath(path.c_str());
            std::scoped_lock lock(g_mutedLock);
            const auto       now = std::chrono::steady_clock::now();
            std::erase_if(g_muted, [&](const Muted& a_muted) { return now - a_muted.when > std::chrono::seconds(30); });
            g_muted.push_back({ id.file, id.dir, a_base, now });
        }

        // The game makes a sound from a file (a line's voice among them): a muted line's gets no volume before it plays.
        void HookGetByFile(RE::BSAudioManager* a_manager, RE::BSSoundHandle& a_handle, const RE::BSResource::ID& a_file, std::uint32_t a_flags, std::uint32_t a_priority)
        {
            g_getByFile(a_manager, a_handle, a_file, a_flags, a_priority);
            std::scoped_lock lock(g_mutedLock);
            if (g_muted.empty()) {
                return;
            }
            // Same file and folder; the extension is whichever the game found (.fuz, .xwm or .wav).
            const auto it = std::ranges::find_if(g_muted, [&](const Muted& a_muted) { return a_muted.file == a_file.file && a_muted.dir == a_file.dir; });
            if (it == g_muted.end()) {
                if (g_unmatchedLogged < 20) {
                    ++g_unmatchedLogged;
                    SKSE::log::info("sound made from file {:08X}.{} in folder {:08X}, not a muted line (waiting: {} {:08X} {:08X})", a_file.file, std::string(a_file.ext, strnlen(a_file.ext, 4)),
                        a_file.dir, g_muted.back().path, g_muted.back().file, g_muted.back().dir);
                }
                return;
            }
            const bool muted = a_handle.SetVolume(0.0f);
            if (!muted || FirstTime("muted " + it->path)) SKSE::log::info("muted {}: {}", it->path, muted ? "no sound" : "COULDN'T SET THE VOLUME");
            g_muted.erase(it);
        }

        std::int64_t HookPopulate(std::int64_t a_1, RE::TESTopic* a_topic, RE::TESTopicInfo* a_info, RE::Character* a_speaker, RE::TESTopicInfo::TESResponse* a_response)
        {
            t_speaker = a_speaker;
            const auto result = g_populate(a_1, a_topic, a_info, a_speaker, a_response);
            t_speaker = nullptr;
            return result;
        }

        bool HookConstruct(RE::TESTopicInfo::TESResponse* a_response, char* a_path, RE::BGSVoiceType* a_voice, RE::TESTopic* a_topic, RE::TESTopicInfo* a_info)
        {
            if (!g_construct(a_response, a_path, a_voice, a_topic, a_info)) {
                return false;
            }
            auto*      npc = t_speaker ? t_speaker->GetActorBase() : nullptr;
            const auto it = npc ? g_speaksAs.find(npc) : g_speaksAs.end();
            if (it == g_speaksAs.end() || !a_voice || !a_path) {
                return true;
            }
            const auto info = a_info ? a_info->GetFormID() : 0;
            if (!IsGamesOwn(a_path, a_info, a_response)) {
                if (FirstTime(a_path)) SKSE::log::info("{:08X} says {:08X} with a sound another mod gave it, left as it is: {}", npc->GetFormID(), info, a_path);
                return true;
            }
            const std::string_view to = it->second->GetFormEditorID();
            const auto             moved = InVoice(a_path, a_voice->GetFormEditorID(), to);
            if (!moved || moved->size() >= kPathSize) {
                SKSE::log::warn("{:08X} says {:08X}: its sound \"{}\" isn't in a {} folder, so it stays as it is.", npc->GetFormID(), info, a_path, a_voice->GetFormEditorID());
                return true;
            }
            const auto newBase = Base(*moved);
            if (HasRecording(newBase)) {
                strcpy_s(a_path, kPathSize, moved->c_str());
                if (FirstTime(newBase)) SKSE::log::info("{:08X} says {:08X} in voice {}: {}", npc->GetFormID(), info, to, newBase);
            } else {
                Mute(Base(a_path));
                if (FirstTime(newBase)) SKSE::log::info("{:08X} says {:08X} silently with its own mouth movements: the new voice has no recording ({}).", npc->GetFormID(), info, Base(a_path));
            }
            return true;
        }
    }

    void Install()
    {
        REL::Relocation<std::uintptr_t> populate{ RELOCATION_ID(34429, 35249) };
        auto&                           trampoline = SKSE::GetTrampoline();
        g_construct = trampoline.write_call<5>(populate.address() + 0xDE, HookConstruct);

        g_populate = reinterpret_cast<Populate>(populate.address());
        g_getByFile = reinterpret_cast<GetByFile>(REL::Relocation<std::uintptr_t>{ RELOCATION_ID(66402, 67664) }.address());
        DetourTransactionBegin();
        DetourUpdateThread(GetCurrentThread());
        DetourAttach(reinterpret_cast<PVOID*>(&g_populate), reinterpret_cast<PVOID>(&HookPopulate));
        DetourAttach(reinterpret_cast<PVOID*>(&g_getByFile), reinterpret_cast<PVOID>(&HookGetByFile));
        const auto result = DetourTransactionCommit();
        SKSE::log::info("Line sounds hooked (result {}).", result);
    }

    void Set(std::unordered_map<RE::TESNPC*, RE::BGSVoiceType*> a_speaksAs)
    {
        g_speaksAs = std::move(a_speaksAs);
        SKSE::log::info("Lines of {} NPCs who changed sex play in their new voice.", g_speaksAs.size());
    }
}
