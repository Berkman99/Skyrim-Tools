#include "PCH.h"

#include "Files/Redirect.h"
#include "Picks/Picks.h"
#include "Picks/Voices.h"

namespace
{
    // NAM.log next to the picks list, in NAM's saved-files folder, where NAM writes its own log (so it follows when that folder
    // moves). Without NAM's folder (NAM never ran), NpcAppearanceManager.log next to SKSE's own logs.
    std::optional<std::filesystem::path> LogFile()
    {
        std::error_code ec;
        if (std::filesystem::is_directory(NAM::SavedFolder(), ec)) {
            return NAM::SavedFolder() / "NAM.log";
        }
        const auto folder = SKSE::log::log_directory();
        return folder ? std::optional(*folder / std::format("{}.log", NAM::kName)) : std::nullopt;
    }

    void StartLog()
    {
        const auto file = LogFile();
        if (!file) {
            return;
        }
        std::shared_ptr<spdlog::sinks::basic_file_sink_mt> sink;
        try {
            sink = std::make_shared<spdlog::sinks::basic_file_sink_mt>(file->string(), false);
        } catch (...) {
            return;
        }
        auto log = std::make_shared<spdlog::logger>("NAM", std::move(sink));
        log->set_level(spdlog::level::info);
        log->flush_on(spdlog::level::info);
        spdlog::set_default_logger(std::move(log));
        spdlog::set_pattern("[%Y-%m-%d %H:%M:%S] [game] %v");
        SKSE::log::info("==== Skyrim started ====");
    }

    consteval SKSE::PluginVersionData VersionData()
    {
        SKSE::PluginVersionData data;
        data.PluginVersion(REL::Version{ NAM::kVersionMajor, NAM::kVersionMinor, NAM::kVersionPatch, 0 });
        data.PluginName(NAM::kName);
        data.AuthorName("Berkman99"sv);
        // Every game address comes from Address Library, so one file works on SE, AE and VR.
        data.UsesAddressLibrary();
        data.UsesNoStructs();
        return data;
    }
}

SKSE_PLUGIN_VERSION = VersionData();

// What SKSE for Skyrim SE 1.5.97 and VR reads instead of the version data above.
SKSE_EXPORT bool SKSEPlugin_Query(SKSE::QueryInterface*, SKSE::PluginInfo* a_info)
{
    a_info->infoVersion = SKSE::PluginInfo::kVersion;
    a_info->name = NAM::kName.data();
    a_info->version = REL::Version{ NAM::kVersionMajor, NAM::kVersionMinor, NAM::kVersionPatch, 0 }.pack();
    return true;
}

SKSEPluginLoad(const SKSE::LoadInterface* a_skse)
{
    SKSE::Init(a_skse);
    StartLog();
    SKSE::log::info("{} {}.{}.{} loading.", NAM::kName, NAM::kVersionMajor, NAM::kVersionMinor, NAM::kVersionPatch);

    SKSE::AllocTrampoline(1 << 6);
    NAM::Files::Redirect::Install();
    NAM::Voices::Install();
    NAM::Picks::Start();
    return true;
}
