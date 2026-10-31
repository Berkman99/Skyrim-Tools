#include "PCH.h"

#include "Files/Archive.h"

#include <lz4frame.h>
#include <zlib.h>

namespace NAM::Files::Archive
{
    namespace
    {
        constexpr std::uint32_t kDirNames = 0x1;
        constexpr std::uint32_t kFileNames = 0x2;
        constexpr std::uint32_t kCompressed = 0x4;
        constexpr std::uint32_t kEmbedNames = 0x100;
        constexpr std::uint32_t kToggleCompressed = 0x40000000;

        struct Entry
        {
            std::uint64_t offset = 0;
            std::uint32_t size = 0;  // stored size, without the toggle bit
            bool          compressed = false;
        };

        struct Index
        {
            std::uint32_t                          version = 0;
            bool                                   embed_names = false;
            std::unordered_map<std::string, Entry> entries;  // lower-case "folder\file"
        };

        std::mutex                                                 g_lock;
        std::unordered_map<std::wstring, std::shared_ptr<Index>> g_indexes;

        std::string Lower(std::string_view a_text)
        {
            std::string s{ a_text };
            for (auto& c : s) {
                c = c == '/' ? '\\' : static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
            }
            return s;
        }

        template <class T>
        bool Get(std::ifstream& a_in, T& a_value)
        {
            return static_cast<bool>(a_in.read(reinterpret_cast<char*>(&a_value), sizeof(T)));
        }

        std::shared_ptr<Index> Load(const std::wstring& a_path)
        {
            std::ifstream in(a_path, std::ios::binary);
            char          magic[4]{};
            in.read(magic, 4);
            std::uint32_t version = 0, offset = 0, flags = 0, folders = 0, files = 0, folder_names = 0, file_names = 0;
            if (!in || std::memcmp(magic, "BSA\0", 4) != 0 || !Get(in, version) || !Get(in, offset) || !Get(in, flags) ||
                !Get(in, folders) || !Get(in, files) || !Get(in, folder_names) || !Get(in, file_names)) {
                return nullptr;
            }
            if (version != 104 && version != 105) {
                return nullptr;
            }
            in.seekg(offset);

            std::vector<std::uint32_t> counts(folders);
            for (auto& count : counts) {
                std::uint64_t hash = 0;
                std::uint32_t pad = 0;
                Get(in, hash);
                Get(in, count);
                if (version == 105) {
                    std::uint64_t folder_offset = 0;
                    Get(in, pad);
                    Get(in, folder_offset);
                } else {
                    Get(in, pad);
                }
            }

            struct Pending
            {
                std::string folder;
                Entry       entry;
            };
            std::vector<Pending> pending;
            pending.reserve(files);
            for (std::uint32_t f = 0; f < folders; ++f) {
                std::string folder;
                if (flags & kDirNames) {
                    std::uint8_t length = 0;
                    Get(in, length);
                    folder.resize(length);
                    in.read(folder.data(), length);
                    while (!folder.empty() && folder.back() == '\0') {
                        folder.pop_back();
                    }
                }
                for (std::uint32_t i = 0; i < counts[f]; ++i) {
                    std::uint64_t hash = 0;
                    std::uint32_t size = 0, data = 0;
                    Get(in, hash);
                    Get(in, size);
                    Get(in, data);
                    Entry entry;
                    entry.offset = data;
                    entry.size = size & ~kToggleCompressed;
                    entry.compressed = ((flags & kCompressed) != 0) != ((size & kToggleCompressed) != 0);
                    pending.push_back({ folder, entry });
                }
            }
            if (!(flags & kFileNames) || !in) {
                return nullptr;
            }

            auto index = std::make_shared<Index>();
            index->version = version;
            index->embed_names = (flags & kEmbedNames) != 0;
            std::string name;
            for (auto& p : pending) {
                std::getline(in, name, '\0');
                index->entries.emplace(Lower(p.folder + "\\" + name), p.entry);
            }
            return index;
        }

        std::shared_ptr<Index> IndexOf(const std::wstring& a_archive)
        {
            std::scoped_lock lock(g_lock);
            auto [it, added] = g_indexes.try_emplace(a_archive);
            if (added) {
                it->second = Load(a_archive);
                if (!it->second) {
                    SKSE::log::error("Couldn't read the archive {}.", std::filesystem::path(a_archive).string());
                }
            }
            return it->second;
        }

        const Entry* Find(const Index& a_index, std::string_view a_entry)
        {
            const auto it = a_index.entries.find(Lower(a_entry));
            return it == a_index.entries.end() ? nullptr : &it->second;
        }

        bool Inflate(const Index& a_index, const std::vector<char>& a_packed, std::vector<char>& a_out)
        {
            if (a_index.version == 105) {
                LZ4F_dctx* context = nullptr;
                if (LZ4F_isError(LZ4F_createDecompressionContext(&context, LZ4F_VERSION))) {
                    return false;
                }
                std::size_t out_size = a_out.size();
                std::size_t in_size = a_packed.size();
                const auto  result = LZ4F_decompress(context, a_out.data(), &out_size, a_packed.data(), &in_size, nullptr);
                LZ4F_freeDecompressionContext(context);
                return !LZ4F_isError(result) && out_size == a_out.size();
            }
            auto out_size = static_cast<uLongf>(a_out.size());
            return uncompress(reinterpret_cast<Bytef*>(a_out.data()), &out_size,
                       reinterpret_cast<const Bytef*>(a_packed.data()), static_cast<uLong>(a_packed.size())) == Z_OK &&
                   out_size == a_out.size();
        }
    }

    std::optional<std::vector<char>> Read(const std::wstring& a_archive, std::string_view a_entry)
    {
        const auto index = IndexOf(a_archive);
        const auto* entry = index ? Find(*index, a_entry) : nullptr;
        if (!entry) {
            return std::nullopt;
        }

        std::ifstream in(a_archive, std::ios::binary);
        in.seekg(static_cast<std::streamoff>(entry->offset));
        std::uint32_t remaining = entry->size;
        if (index->embed_names) {
            std::uint8_t length = 0;
            Get(in, length);
            in.seekg(length, std::ios::cur);
            remaining -= 1u + length;
        }
        if (!entry->compressed) {
            std::vector<char> bytes(remaining);
            in.read(bytes.data(), remaining);
            return in ? std::optional{ std::move(bytes) } : std::nullopt;
        }

        std::uint32_t original = 0;
        Get(in, original);
        remaining -= 4;
        std::vector<char> packed(remaining);
        in.read(packed.data(), remaining);
        std::vector<char> bytes(original);
        if (!in || !Inflate(*index, packed, bytes)) {
            SKSE::log::error("Couldn't unpack {} from {}.", a_entry, std::filesystem::path(a_archive).string());
            return std::nullopt;
        }
        return bytes;
    }

    std::optional<std::uint64_t> Size(const std::wstring& a_archive, std::string_view a_entry)
    {
        const auto index = IndexOf(a_archive);
        const auto* entry = index ? Find(*index, a_entry) : nullptr;
        if (!entry) {
            return std::nullopt;
        }
        std::ifstream in(a_archive, std::ios::binary);
        in.seekg(static_cast<std::streamoff>(entry->offset));
        std::uint32_t name_bytes = 0;
        if (index->embed_names) {
            std::uint8_t length = 0;
            Get(in, length);
            in.seekg(length, std::ios::cur);
            name_bytes = 1u + length;
        }
        if (!entry->compressed) {
            return entry->size - name_bytes;
        }
        // The unpacked size is stored at the start of the file's data.
        std::uint32_t original = 0;
        Get(in, original);
        return original;
    }
}
