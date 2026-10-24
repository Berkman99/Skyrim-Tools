using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpcFaceManager.Core.Settings;

public enum MugshotGeneratorKind
{
    Off,
    InternalRenderer,
    PortraitCreator,
}

/// <summary>Choices made for one MO2 profile (or one game folder without MO2).</summary>
public sealed class ProfileSelections
{
    /// <summary>NPC FormKey text → chosen mod name. An empty string means "keep the original look".</summary>
    public Dictionary<string, string> Choices { get; set; } = [];

    /// <summary>The NPC that was selected last (FormKey text).</summary>
    public string? LastSelectedNpc { get; set; }
}

/// <summary>Settings saved next to the program as NpcAppearanceManager.settings.json.</summary>
public sealed class AppSettings
{
    public const string AppName = "NPC Appearance Manager";

    public string? Mo2IniPath { get; set; }
    public string? Mo2Profile { get; set; }
    public string? GameFolder { get; set; }

    /// <summary>"Disable merged mods after build".</summary>
    public bool AutoDisableInMo2 { get; set; } = true;

    // Remembered UI state.
    public string NpcSearch { get; set; } = "";
    public string? NpcModFilter { get; set; }
    public bool ShowSingleLookNpcs { get; set; }
    public bool ShowDisabledOverhauls { get; set; }
    public double PictureSize { get; set; } = 230;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public string OutputModName { get; set; } = "NAM Merge";

    /// <summary>Plugin name without ".esp" (it's always an .esp).</summary>
    public string OutputPluginName { get; set; } = "NAM Merge";

    public string? MugshotsFolder { get; set; }
    /// <summary>
    /// "Use FaceFinder". Saved under a new name: while FaceFinder waited for a key it was always saved as off, and
    /// that old value shouldn't keep it off now that it works.
    /// </summary>
    [JsonPropertyName("FaceFinderOn")]
    public bool UseFaceFinder { get; set; } = true;

    /// <summary>"Download all FaceFinder pictures": saves every picture in the background, not just the NPCs you open.</summary>
    public bool DownloadAllFaceFinder { get; set; }
    public MugshotGeneratorKind Generator { get; set; } = MugshotGeneratorKind.Off;

    /// <summary>Mod name → folder name inside the mugshots folder, when they differ.</summary>
    public Dictionary<string, string> MugshotFolderLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ProfileSelections> Profiles { get; set; } = [];

    [JsonIgnore]
    public static string AppFolder => AppContext.BaseDirectory;

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppFolder, "NpcAppearanceManager.settings.json");

    /// <summary>The settings file from before the rename to NPC Appearance Manager; read once if there's no new one.</summary>
    [JsonIgnore]
    public static string OldFilePath => Path.Combine(AppFolder, "NpcFaceManager.settings.json");

    /// <summary>"NAM Merge.esp" or "NAM Merge" → "NAM Merge".</summary>
    public static string PluginBaseName(string name)
    {
        name = name.Trim();
        return name.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name;
    }

    /// <summary>Where downloaded and generated pictures are cached.</summary>
    [JsonIgnore]
    public static string CacheFolder => Path.Combine(AppFolder, "Mugshot Cache");

    /// <summary>The tool's own mugshot folder, used until you pick another.</summary>
    [JsonIgnore]
    public static string DefaultMugshotsFolder => Path.Combine(CacheFolder, "Mugshots");

    /// <summary>NPC Portrait Creator, shipped in its own folder next to the exe.</summary>
    [JsonIgnore]
    public static string PortraitCreatorExe => Path.Combine(AppFolder, "NPC Portrait Creator", "NPCPortraitCreator.exe");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load() => Load(FilePath, OldFilePath);

    internal static AppSettings Load(string filePath, string oldFilePath)
    {
        try
        {
            var path = File.Exists(filePath) ? filePath : oldFilePath;
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
                if (loaded != null)
                {
                    loaded.MugshotFolderLinks = new Dictionary<string, string>(loaded.MugshotFolderLinks, StringComparer.OrdinalIgnoreCase);
                    loaded.OutputPluginName = PluginBaseName(loaded.OutputPluginName);
                    return loaded;
                }
            }
        }
        catch
        {
            // A damaged settings file shouldn't stop the program; start fresh and keep a copy of the old one.
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { }
        }
        return new AppSettings();
    }

    public void Save()
    {
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public ProfileSelections ForProfile(string key)
    {
        if (!Profiles.TryGetValue(key, out var p)) Profiles[key] = p = new ProfileSelections();
        return p;
    }
}
