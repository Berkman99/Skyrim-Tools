using Mutagen.Bethesda;
using Mutagen.Bethesda.Installs;
using Mutagen.Bethesda.Plugins;
using Noggog;
using NpcFaceManager.Core.Build;

namespace NpcFaceManager.Core.Environment;

/// <summary>An installed mod folder (or the game's Data folder when not using MO2).</summary>
public sealed class ModFolder
{
    public required string Name { get; init; }
    public required string Path { get; init; }

    /// <summary>MO2 priority: higher number wins file conflicts (matches MO2's left pane numbering).</summary>
    public required int Priority { get; init; }

    public bool IsGameDataFolder { get; init; }

    /// <summary>Ticked in the MO2 profile right now.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Switched off by this tool's last build (it's treated as enabled when planning the next one).</summary>
    public bool DisabledByMerge { get; init; }

    /// <summary>Enabled, or only off because the last build merged it.</summary>
    public bool CountsAsEnabled => IsEnabled || DisabledByMerge;

    /// <summary>File names directly inside the folder (plugins, archives, ...).</summary>
    public required IReadOnlyList<string> TopLevelFiles { get; init; }

    /// <summary>The Nexus mod id MO2 saved in the folder's meta.ini ("modid="), or null when there is none.</summary>
    public int? NexusId => (_nexusId ??= new Lazy<int?>(() => ReadNexusId(Path))).Value;
    private Lazy<int?>? _nexusId;

    internal static int? ReadNexusId(string folder)
    {
        try
        {
            var meta = System.IO.Path.Combine(folder, "meta.ini");
            if (!File.Exists(meta)) return null;
            foreach (var line in File.ReadLines(meta))
            {
                if (!line.StartsWith("modid=", StringComparison.OrdinalIgnoreCase)) continue;
                return int.TryParse(line[6..].Trim(), out var id) && id > 0 ? id : null;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }

    public IEnumerable<string> Plugins => TopLevelFiles.Where(PluginNames.IsPlugin);
    public IEnumerable<string> Archives => TopLevelFiles.Where(f => f.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase));

    public override string ToString() => Name;
}

public static class PluginNames
{
    public static bool IsPlugin(string fileName) =>
        fileName.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".esl", StringComparison.OrdinalIgnoreCase);

    public static readonly string[] BaseMasters = ["Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm"];

    public static bool IsBaseMaster(string fileName) =>
        BaseMasters.Contains(fileName, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Everything the tool needs to know about the user's setup: where the game is, which mods are installed
/// (in priority order) and which plugins are active (in load order).
///
/// If an earlier build of this tool switched mods/plugins off, they're put back here as if the merge hadn't
/// happened, and the earlier output plugin is left out. That way rebuilding always starts from the original mods.
/// </summary>
public sealed class GameSetup
{
    public const Mutagen.Bethesda.Skyrim.SkyrimRelease Release = Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE;

    public required string GameFolder { get; init; }
    public required string DataFolder { get; init; }
    public Mo2Instance? Mo2 { get; init; }
    public string? ProfileName { get; init; }
    public string? ProfileDirectory { get; init; }
    public required string HowFound { get; init; }

    /// <summary>Every installed mod (enabled and disabled), highest priority first. Empty when not using MO2.</summary>
    public required IReadOnlyList<ModFolder> AllMods { get; init; }

    /// <summary>Mods that are enabled (or were only switched off by the last merge), highest priority first.</summary>
    public IEnumerable<ModFolder> Mods => AllMods.Where(m => m.CountsAsEnabled);

    /// <summary>Active plugins in load order, as they were before any merge by this tool.</summary>
    public required IReadOnlyList<ModKey> LoadOrder { get; init; }

    /// <summary>The output mod of an earlier build, if one is installed and enabled.</summary>
    public ModFolder? PreviousOutput { get; init; }
    public MergeMarker? PreviousMarker { get; init; }

    private readonly Dictionary<string, string> _pluginPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ModFolder> _pluginOwners = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string>? _active;

    public bool IsMo2 => Mo2 != null;

    public string? ResolvePluginPath(string fileName) => _pluginPaths.GetValueOrDefault(fileName);

    /// <summary>The mod folder whose copy of a plugin wins (null for plugins in the game Data folder).</summary>
    public ModFolder? PluginOwner(string fileName) => _pluginOwners.GetValueOrDefault(fileName);

    public bool IsActive(string fileName) =>
        (_active ??= LoadOrder.Select(k => k.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase)).Contains(fileName);

    public static GameSetup FromMo2(Mo2Instance instance, string? profile, string howFound)
    {
        profile ??= instance.SelectedProfile ?? instance.ListProfiles().FirstOrDefault()
            ?? throw new InvalidOperationException("This Mod Organizer 2 instance has no profiles.");
        var profileDir = instance.ProfileDirectory(profile);
        if (!Directory.Exists(profileDir))
            throw new DirectoryNotFoundException($"MO2 profile folder not found: {profileDir}");

        var gameFolder = instance.GamePath ?? FindGameFolder()
            ?? throw new InvalidOperationException("Could not find the Skyrim Special Edition folder from Mod Organizer 2.");

        var entries = ProfileFiles.ReadModList(profileDir)
            .Where(m => m.State != ModListState.Unmanaged).ToList();

        // Find an earlier output of this tool among the enabled mods.
        ModFolder? previousOutput = null;
        MergeMarker? marker = null;
        foreach (var e in entries.Where(e => e.State == ModListState.Enabled && !e.IsSeparator))
        {
            var path = System.IO.Path.Combine(instance.ModsDirectory, e.Name);
            if (!MergeMarker.IsOutputFolder(path)) continue;
            marker = MergeMarker.TryRead(path);
            previousOutput = new ModFolder { Name = e.Name, Path = path, Priority = 0, TopLevelFiles = ListTopLevelFiles(path) };
            break;
        }
        var disabledByMerge = new HashSet<string>(marker?.DisabledMods ?? [], StringComparer.OrdinalIgnoreCase);

        var all = new List<ModFolder>();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.IsSeparator) continue;
            var path = System.IO.Path.Combine(instance.ModsDirectory, e.Name);
            // Outputs of this tool (enabled or not) aren't mods to pick from.
            if (!Directory.Exists(path) || MergeMarker.IsOutputFolder(path)) continue;
            bool enabled = e.State == ModListState.Enabled;
            all.Add(new ModFolder
            {
                Name = e.Name,
                Path = path,
                Priority = entries.Count - i, // modlist.txt is highest priority first
                IsEnabled = enabled,
                DisabledByMerge = !enabled && disabledByMerge.Contains(e.Name),
                TopLevelFiles = ListTopLevelFiles(path),
            });
        }

        var setup = new GameSetup
        {
            GameFolder = gameFolder,
            DataFolder = System.IO.Path.Combine(gameFolder, "Data"),
            Mo2 = instance,
            ProfileName = profile,
            ProfileDirectory = profileDir,
            HowFound = howFound,
            AllMods = all,
            PreviousOutput = previousOutput,
            PreviousMarker = marker,
            LoadOrder = BuildLoadOrder(gameFolder, ProfileFiles.ReadPlugins(profileDir), marker),
        };

        // Overwrite beats every mod, enabled mods beat the game Data folder. Disabled mods come last: their
        // plugins are only ever read (for looks), never treated as the active copy.
        if (Directory.Exists(instance.OverwriteDirectory))
            foreach (var f in ListTopLevelFiles(instance.OverwriteDirectory).Where(PluginNames.IsPlugin))
                setup._pluginPaths.TryAdd(f, System.IO.Path.Combine(instance.OverwriteDirectory, f));
        foreach (var mod in all.Where(m => m.CountsAsEnabled))
            foreach (var plugin in mod.Plugins)
                if (setup._pluginPaths.TryAdd(plugin, System.IO.Path.Combine(mod.Path, plugin)))
                    setup._pluginOwners[plugin] = mod;
        setup.AddDataFolderPlugins();
        foreach (var mod in all.Where(m => !m.CountsAsEnabled))
            foreach (var plugin in mod.Plugins)
                if (setup._pluginPaths.TryAdd(plugin, System.IO.Path.Combine(mod.Path, plugin)))
                    setup._pluginOwners[plugin] = mod;
        return setup;
    }

    public static GameSetup FromGameFolder(string gameFolder, string howFound)
    {
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        var pluginsDir = System.IO.Path.Combine(appData, "Skyrim Special Edition");
        var setup = new GameSetup
        {
            GameFolder = gameFolder,
            DataFolder = System.IO.Path.Combine(gameFolder, "Data"),
            HowFound = howFound,
            AllMods = [],
            LoadOrder = BuildLoadOrder(gameFolder, ProfileFiles.ReadPlugins(pluginsDir), null),
        };
        setup.AddDataFolderPlugins();
        return setup;
    }

    private void AddDataFolderPlugins()
    {
        if (!Directory.Exists(DataFolder)) return;
        foreach (var f in ListTopLevelFiles(DataFolder).Where(PluginNames.IsPlugin))
            _pluginPaths.TryAdd(f, System.IO.Path.Combine(DataFolder, f));
    }

    private static List<ModKey> BuildLoadOrder(string gameFolder, List<(string Plugin, bool Active)> plugins, MergeMarker? marker)
    {
        var order = new List<string>(PluginNames.BaseMasters);
        var ccc = System.IO.Path.Combine(gameFolder, "Skyrim.ccc");
        if (File.Exists(ccc))
        {
            var dataFolder = System.IO.Path.Combine(gameFolder, "Data");
            order.AddRange(File.ReadAllLines(ccc).Select(l => l.Trim())
                .Where(l => l.Length > 0 && File.Exists(System.IO.Path.Combine(dataFolder, l))));
        }
        order.AddRange(plugins.Where(p => p.Active).Select(p => p.Plugin));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        order = order.Where(seen.Add).ToList();

        if (marker != null)
        {
            // Leave out the earlier output plugin and put back what it switched off, where it used to be.
            order.RemoveAll(p => p.Equals(marker.Plugin, StringComparison.OrdinalIgnoreCase));
            foreach (var d in marker.DeactivatedPlugins)
            {
                if (order.Contains(d.Plugin, StringComparer.OrdinalIgnoreCase)) continue;
                var at = d.After == null ? -1 : order.FindIndex(p => p.Equals(d.After, StringComparison.OrdinalIgnoreCase));
                order.Insert(at >= 0 ? at + 1 : order.Count, d.Plugin);
            }
        }
        return order.Select(p => ModKey.FromFileName(p)).ToList();
    }

    public static string? FindGameFolder()
    {
        try
        {
            if (GameLocations.TryGetGameFolder(GameRelease.SkyrimSE, out DirectoryPath path)) return path.Path;
        }
        catch
        {
            // Mutagen's lookup can throw on unusual Steam setups; fall through.
        }
        return null;
    }

    internal static IReadOnlyList<string> ListTopLevelFiles(string dir)
    {
        try { return Directory.GetFiles(dir).Select(System.IO.Path.GetFileName).OfType<string>().ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
