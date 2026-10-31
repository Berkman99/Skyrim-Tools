#pragma once

namespace NAM::Files::Archive
{
    /// The bytes of one file inside a Skyrim archive (.bsa, SE or LE format),
    /// read straight from the archive; nullopt when it isn't there.
    std::optional<std::vector<char>> Read(const std::wstring& a_archive, std::string_view a_entry);

    /// The size of one file inside an archive, without reading it.
    std::optional<std::uint64_t> Size(const std::wstring& a_archive, std::string_view a_entry);
}
