using Mutagen.Bethesda.Plugins;
using NpcFaceManager.Core.Environment;

namespace NpcFaceManager.Core.Scanning;

/// <summary>
/// Finds each NPC's original look: the look from where the NPC comes from. That's the plugin the NPC's ID belongs
/// to, so it's exact: "Skyrim" for Skyrim, Update and the DLCs, "Creation Club" for cc plugins, or the mod that
/// adds the NPC. Overhauls never count, ticked or not. Within the source, loose face files beat archived ones.
/// </summary>
public sealed class OriginalSources
{
    public const string SkyrimName = "Skyrim";
    public const string CreationClubName = "Creation Club";

    private readonly GameSetup _setup;
    private readonly IReadOnlyList<ModSummary> _allMods;
    private readonly HashSet<string> _tickedFolders;
    private readonly HashSet<string> _activePlugins;
    private readonly ModSummary _gameData;
    private readonly Dictionary<ModSummary, Dictionary<string, FaceFiles>> _faces = [];

    private OriginalSources(GameSetup setup, IReadOnlyList<ModSummary> allMods, IReadOnlyCollection<ModSummary> ticked)
    {
        _setup = setup;
        _allMods = allMods;
        _tickedFolders = ticked.Where(t => !t.Mod.IsGameDataFolder).Select(t => t.Mod.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _activePlugins = setup.LoadOrder.Select(k => k.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gamePlugins = setup.LoadOrder.Select(k => k.FileName.String).Where(p => setup.PluginOwner(p) == null && IsGamePlugin(p)).ToList();
        _gameData = PseudoMod(SkyrimName, setup.DataFolder, gamePlugins);
    }

    public static OriginalSources Build(GameSetup setup, IReadOnlyList<ModSummary> allMods, IReadOnlyCollection<ModSummary> ticked) =>
        new(setup, allMods, ticked);

    /// <summary>Skyrim, Update, the DLCs and Creation Club plugins.</summary>
    public static bool IsGamePlugin(string plugin) =>
        PluginNames.IsBaseMaster(plugin) || plugin.StartsWith("cc", StringComparison.OrdinalIgnoreCase);

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
            name = plugin.StartsWith("cc", StringComparison.OrdinalIgnoreCase) ? CreationClubName : SkyrimName;
            source = _gameData;
        }
        else if (owner != null)
        {
            name = owner.Name;
            if (_tickedFolders.Contains(owner.Path))
                return looks.FirstOrDefault(l => l.Source.Mod.Path.Equals(owner.Path, StringComparison.OrdinalIgnoreCase));
            source = _allMods.FirstOrDefault(m => m.Mod == owner);
        }
        else
        {
            // Without MO2 every plugin in Data is its own "mod".
            name = plugin;
            source = _allMods.FirstOrDefault(m => m.Mod.IsGameDataFolder && m.Mod.Name.Equals(plugin, StringComparison.OrdinalIgnoreCase));
        }
        if (source == null) return null;

        if (!_faces.TryGetValue(source, out var faces))
            _faces[source] = faces = NpcScanner.IndexFaceFiles(source, _activePlugins);
        if (!faces.TryGetValue(FaceGenPaths.Key(npc), out var files) || !files.HasNif) return null;

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

    private static ModSummary PseudoMod(string name, string path, IReadOnlyList<string> plugins) => new()
    {
        Mod = new ModFolder
        {
            Name = name,
            Path = path,
            Priority = 0,
            IsGameDataFolder = true,
            TopLevelFiles = GameSetup.ListTopLevelFiles(path),
        },
        Kind = ModKind.FacesOnly,
        Plugins = plugins.Select(p => new PluginSummary(p, true, true, [], [])).ToList(),
        FaceFileCount = 0,
        FacesInArchives = false,
        FacePlugins = [],
    };
}
