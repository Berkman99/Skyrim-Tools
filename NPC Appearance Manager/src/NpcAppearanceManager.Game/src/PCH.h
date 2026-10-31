#pragma once

#include <RE/Skyrim.h>
#include <REL/Relocation.h>
#include <SKSE/SKSE.h>

#include <spdlog/sinks/basic_file_sink.h>
#include <toml++/toml.h>

#include <algorithm>
#include <atomic>
#include <filesystem>
#include <format>
#include <fstream>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

using namespace std::literals;

namespace NAM
{
    constexpr auto          kName = "NpcAppearanceManager"sv;
    constexpr std::uint32_t kVersionMajor = 1;
    constexpr std::uint32_t kVersionMinor = 0;
    constexpr std::uint32_t kVersionPatch = 0;

    /// Where NAM keeps its saved files (the picks list and NAM.log): AppData\Local\Npc Appearance Manager, or the folder
    /// the note file location.txt there points to (Settings, "Location of saved files").
    inline std::filesystem::path SavedFolder()
    {
        static const auto folder = []() -> std::filesystem::path {
            std::filesystem::path base;
            if (const auto* local = _wgetenv(L"LOCALAPPDATA")) {
                base = std::filesystem::path(local) / L"Npc Appearance Manager";
            }
            try {
                std::ifstream note(base / "location.txt", std::ios::binary);
                std::string   line;
                if (note && std::getline(note, line)) {
                    while (!line.empty() && (line.back() == '\r' || line.back() == '\n' || line.back() == ' ')) {
                        line.pop_back();
                    }
                    if (!line.empty()) {
                        return std::filesystem::path(std::u8string(line.begin(), line.end()));
                    }
                }
            } catch (...) {
            }
            return base;
        }();
        return folder;
    }
}
