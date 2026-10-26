using System.Text.Json;
using System.Text.Json.Serialization;

namespace NpcAppearanceManager.Core.Settings;

public enum MugshotGeneratorKind
{
    Off,
    InternalRenderer,
    PortraitCreator,
}

/// <summary>A face copied from another NPC: that NPC (FormKey text) and the face (a pick key like in Choices).</summary>
public sealed class FaceCopy
{
    public string From { get; set; } = "";
    public string Face { get; set; } = "";
}

/// <summary>Choices made for one MO2 profile (or one game folder without MO2).</summary>
public sealed class ProfileSelections
{
    /// <summary>NPC FormKey text → chosen mod name. An empty string means "keep the original look".</summary>
    public Dictionary<string, string> Choices { get; set; } = [];

    /// <summary>The NPC that was selected last (FormKey text).</summary>
    public string? LastSelectedNpc { get; set; }

    /// <summary>"Copy faces": NPC FormKey text → the NPC whose face it gets, and which of its faces.</summary>
    public Dictionary<string, FaceCopy> Copies { get; set; } = [];

    /// <summary>NPC FormKey text → the name you gave it (shown as "Original (New)", and its name in the game).</summary>
    public Dictionary<string, string> Names { get; set; } = [];

    /// <summary>Copy faces' last NPC 1 and NPC 2 (FormKey text).</summary>
    public string? LastCopyTarget { get; set; }
    public string? LastCopySource { get; set; }

    /// <summary>No manager: FaceFinder mods (FaceFinder ids) you said you copied into Data.</summary>
    public List<int> InstalledByHand { get; set; } = [];
}

/// <summary>Settings saved next to the program as NpcAppearanceManager.settings.json.</summary>
public sealed class AppSettings
{
    public const string AppName = "NPC Appearance Manager";

    /// <summary>The mod manager picked on first start ("Select Mod Manager"); null until then.</summary>
    public Environment.ModManager? Manager { get; set; }

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

    // "Copy faces": the NPC 2 list has its own search and filters.
    public string CopySearch { get; set; } = "";
    public string? CopyModFilter { get; set; }
    public bool CopyShowSingleLookNpcs { get; set; }
    public bool CopyShowDisabledOverhauls { get; set; }

    // Where the NPC lists' dividers and scroll bars were left.
    public double NpcListWidth { get; set; } = 340;
    public double CopyListsWidth { get; set; } = 560;
    public double NpcListScroll { get; set; }
    public double CopyTargetScroll { get; set; }
    public double CopySourceScroll { get; set; }
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

    /// <summary>"Hide mods not installed": FaceFinder faces of mods you don't have aren't shown (a picked one still is).</summary>
    public bool HideNotInstalled { get; set; }

    public MugshotGeneratorKind Generator { get; set; } = MugshotGeneratorKind.Off;

    /// <summary>"Render all NPCs": renders every missing mugshot in the background, not just the NPC you open.</summary>
    public bool RenderAllNpcs { get; set; }

    /// <summary>Mod name → folder name inside the mugshots folder, when they differ.</summary>
    public Dictionary<string, string> MugshotFolderLinks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, ProfileSelections> Profiles { get; set; } = [];

    [JsonIgnore]
    public static string AppFolder => AppContext.BaseDirectory;

    [JsonIgnore]
    public static string FilePath => Path.Combine(AppFolder, "NpcAppearanceManager.settings.json");

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

    public static AppSettings Load() => Load(FilePath);

    internal static AppSettings Load(string path)
    {
        try
        {
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
