using Mutagen.Bethesda.Plugins;
using NpcAppearanceManager.Core.Environment;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>
/// Finds each NPC's original look: the look from where the NPC comes from. That's the plugin the NPC's ID belongs
/// to, so it's exact: "Skyrim Special Edition" for Skyrim, Update, the DLCs and Creation Club plugins, or the mod
/// that adds the NPC. Overhauls never count. Within the source, loose face files beat archived ones.
/// </summary>
public sealed class OriginalSources
{
    /// <summary>Skyrim, Update, the DLCs and Creation Club: one source, named like FaceFinder names it.</summary>
    public const string BaseGameName = "Skyrim Special Edition";

    /// <summary>Mugshot folders NPC Plugin Chooser 2 uses for the same looks; still read when "Skyrim Special Edition" has none.</summary>
    public static readonly string[] OlderBaseGameFolders = ["Skyrim", "Creation Club"];

    public static bool IsBaseGame(string name) =>
        name.Equals(BaseGameName, StringComparison.OrdinalIgnoreCase) || OlderBaseGameFolders.Contains(name, StringComparer.OrdinalIgnoreCase);

    private readonly GameSetup _setup;
    private readonly IReadOnlyList<ModSummary> _allMods;
    private readonly HashSet<string> _tickedFolders;
    private readonly HashSet<string> _activePlugins;
    private readonly ModSummary _gameData;
    private readonly Dictionary<(ModSummary, string?), Dictionary<string, FaceFiles>> _faces = [];

    private OriginalSources(GameSetup setup, IReadOnlyList<ModSummary> allMods, IReadOnlyCollection<ModSummary> ticked)
    {
        _setup = setup;
        _allMods = allMods;
        _tickedFolders = ticked.Where(t => !t.Mod.IsGameDataFolder).Select(t => t.Mod.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _activePlugins = setup.LoadOrder.Select(k => k.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gamePlugins = setup.LoadOrder.Select(k => k.FileName.String).Where(p => setup.PluginOwner(p) == null && IsGamePlugin(p)).ToList();
        // With Vortex or no manager, loose face files in Data come from mods, never from the game itself.
        _gameData = PseudoMod(BaseGameName, setup.DataFolder, gamePlugins, ignoreLoose: !setup.IsMo2);
    }

    public static OriginalSources Build(GameSetup setup, IReadOnlyList<ModSummary> allMods, IReadOnlyCollection<ModSummary> ticked) =>
        new(setup, allMods, ticked);

    /// <summary>Skyrim, Update, the DLCs and Creation Club plugins.</summary>
    public static bool IsGamePlugin(string plugin) =>
        PluginNames.IsBaseMaster(plugin) || plugin.StartsWith("cc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the NPC comes from, without looking for a face: Skyrim Special Edition, or the mod with its plugin.</summary>
    public string NameOf(FormKey npc)
    {
        var plugin = npc.ModKey.FileName.String;
        var owner = _setup.PluginOwner(plugin);
        if (owner == null) return IsGamePlugin(plugin) ? BaseGameName : plugin;
        // The main mod (lowest priority copy of the plugin) rather than an update that only replaces it.
        return PackageOf(plugin).Where(m => m.Plugins.Any(p => p.FileName.Equals(plugin, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.Mod.Priority).FirstOrDefault()?.Mod.Name ?? owner.Name;
    }

    /// <summary>
    /// The NPC's original look and the name of its source. When the NPC is added by a ticked mod, the original is
    /// that mod's own look from <paramref name="looks"/>. Null when the source has no face for the NPC.
    /// </summary>
    public NpcLook? Find(FormKey npc, IReadOnlyList<NpcLook> looks, out string name)
    {
        var plugin = npc.ModKey.FileName.String;
        var owner = _setup.PluginOwner(plugin);
        ModSummary? source;
        if (owner == null && IsGamePlugin(plugin))
        {
            name = BaseGameName;
            source = _gameData;
        }
        else if (owner != null)
        {
            // The NPC's own face is the one the game loads for its plugin: a loose file in a mod that has the plugin,
            // or one in an archive loaded for the plugin (named after it). These can be in a different mod than
            // the copy of the plugin that wins, e.g. when an update replaces the plugin but the faces stay in the
            // main mod. Loose files win, then MO2 priority, as in the game.
            var found = PackageOf(plugin)
                .Select(m => (Mod: m, Files: FacesOf(m, plugin).GetValueOrDefault(FaceGenPaths.Key(npc))))
                .Where(x => x.Files is { HasNif: true })
                .OrderByDescending(x => x.Mod.Mod.CountsAsEnabled) // an enabled copy is what the game uses; a disabled one still shows the face
                .ThenByDescending(x => x.Files!.NifLoosePath != null)
                .ThenByDescending(x => x.Mod.Mod.Priority)
                .FirstOrDefault();
            if (found.Mod == null)
            {
                name = owner.Name;
                return null;
            }
            name = found.Mod.Mod.Name;
            if (_tickedFolders.Contains(found.Mod.Mod.Path) &&
                looks.FirstOrDefault(l => l.Source.Mod.Path.Equals(found.Mod.Mod.Path, StringComparison.OrdinalIgnoreCase)) is { } own)
                return own;
            return OriginalLook(found.Mod, npc, found.Files!);
        }
        else
        {
            // Without MO2 every plugin in Data is its own "mod".
            name = plugin;
            source = _allMods.FirstOrDefault(m => m.Mod.IsGameDataFolder && m.Mod.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        }
        if (source == null) return null;
        var files = FacesOf(source, null).GetValueOrDefault(FaceGenPaths.Key(npc));
        return files is { HasNif: true } ? OriginalLook(source, npc, files) : null;
    }

    /// <summary>Mods (enabled or not) that have this plugin file, or an archive the game loads for it.</summary>
    private List<ModSummary> PackageOf(string plugin)
    {
        if (_packages.TryGetValue(plugin, out var list)) return list;
        list = _allMods.Where(m => !m.Mod.IsGameDataFolder &&
                (m.Plugins.Any(p => p.FileName.Equals(plugin, StringComparison.OrdinalIgnoreCase)) ||
                 m.Mod.Archives.Any(a => ArchiveHelper.IsOwnedBy(a, plugin))))
            .ToList();
        return _packages[plugin] = list;
    }

    private readonly Dictionary<string, List<ModSummary>> _packages = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, FaceFiles> FacesOf(ModSummary mod, string? plugin)
    {
        // A mod's own plugins are always included, so those don't need an index of their own.
        if (plugin != null && mod.Plugins.Any(p => p.FileName.Equals(plugin, StringComparison.OrdinalIgnoreCase))) plugin = null;
        var key = (mod, plugin?.ToLowerInvariant());
        if (!_faces.TryGetValue(key, out var faces))
            _faces[key] = faces = NpcScanner.IndexFaceFiles(mod, _activePlugins, plugin);
        return faces;
    }

    private static NpcLook OriginalLook(ModSummary source, FormKey npc, FaceFiles files)
    {
        return new NpcLook
        {
            Source = source,
            Npc = npc,
            SourcePlugin = null,
            IsOriginal = true,
            RecordFrom = source.Plugins.Select(p => ModKey.FromFileName(p.FileName)).ToHashSet(),
            Faces = new FaceFiles
            {
                NifLoosePath = files.NifLoosePath,
                NifArchive = files.NifLoosePath == null ? files.NifArchive : null,
                DdsLoosePath = files.DdsLoosePath,
                DdsArchive = files.DdsLoosePath == null ? files.DdsArchive : null,
            },
        };
    }

    private static ModSummary PseudoMod(string name, string path, IReadOnlyList<string> plugins, bool ignoreLoose) => new()
    {
        Mod = new ModFolder
        {
            Name = name,
            Path = path,
            Priority = 0,
            IsGameDataFolder = true,
            IgnoreLooseFaces = ignoreLoose,
            TopLevelFiles = GameSetup.ListTopLevelFiles(path),
        },
        Kind = ModKind.FacesOnly,
        Plugins = plugins.Select(p => new PluginSummary(p, true, true, [], [])).ToList(),
        FaceFileCount = 0,
        FacesInArchives = false,
        FacePlugins = [],
    };
}
