using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcFaceManager.Core.Environment;

namespace NpcFaceManager.Core.Scanning;

/// <summary>
/// Reads every mod with faces and builds the list of NPCs with all their available looks, which look the game shows
/// right now, and what each mod is (overhaul, NPC mod or both).
/// </summary>
public static class NpcScanner
{
    public static NpcIndex Scan(GameSetup setup, IReadOnlyList<ModSummary> mods, IReadOnlyList<ModSummary> allMods, PluginCache plugins,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var loadOrderIndex = setup.LoadOrder.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i);
        var activePlugins = setup.LoadOrder.Select(k => k.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var looksByNpc = new Dictionary<FormKey, List<NpcLook>>();
        var details = new Dictionary<string, ModDetails>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.OrderByDescending(m => m.Mod.Priority))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Reading {mod.Mod.Name}...");
            var info = new ModDetails { Summary = mod };
            details[mod.Mod.Name] = info;

            var faces = IndexFaceFiles(mod, activePlugins);
            var claimed = new HashSet<string>();

            // NPC records the mod's plugins edit. Later plugins in the load order win, so walk them last-first.
            var modPlugins = mod.Plugins.Where(p => p.HasNpcRecords)
                .OrderByDescending(p => loadOrderIndex.GetValueOrDefault(ModKey.FromFileName(p.FileName), int.MaxValue));
            foreach (var plugin in modPlugins)
            {
                var pluginKey = ModKey.FromFileName(plugin.FileName);
                var overlay = mod.Mod.IsGameDataFolder
                    ? plugins.Get(plugin.FileName)
                    : plugins.GetByPath(Path.Combine(mod.Mod.Path, plugin.FileName));
                if (overlay == null) continue;

                foreach (var npc in overlay.Npcs)
                {
                    if (npc.FormKey.ModKey == pluginKey && !info.PluginsAddingNewNpcs.Contains(plugin.FileName))
                        info.PluginsAddingNewNpcs.Add(plugin.FileName);

                    var key = FaceGenPaths.Key(npc.FormKey);
                    if (!faces.TryGetValue(key, out var faceFiles) || !faceFiles.HasNif) continue;
                    if (!claimed.Add(key)) continue; // an earlier (winning) plugin of this mod already supplied it

                    if (IsTemplatedForLooks(npc))
                    {
                        info.TemplatedNpcsSkipped++;
                        continue;
                    }
                    Add(looksByNpc, new NpcLook { Source = mod, Npc = npc.FormKey, SourcePlugin = pluginKey, Faces = faceFiles });
                    info.LookCount++;
                }
            }

            // Face files with no matching record in this mod: the mod reuses the existing NPC record as-is.
            foreach (var (key, faceFiles) in faces)
            {
                if (claimed.Contains(key) || !faceFiles.HasNif) continue;
                var parts = key.Split('|');
                var formKey = FaceGenPaths.ToFormKey(parts[0], parts[1]);
                if (formKey == null || !activePlugins.Contains(formKey.Value.ModKey.FileName)) continue;
                Add(looksByNpc, new NpcLook { Source = mod, Npc = formKey.Value, SourcePlugin = null, Faces = faceFiles });
                info.LookCount++;
            }
        }

        ModRoles.Assign(setup, details, looksByNpc.Values.SelectMany(l => l));

        // A disabled mod only matters for its overhaul looks: its own NPCs don't exist in the game.
        foreach (var looks in looksByNpc.Values)
            looks.RemoveAll(l => !l.Source.Mod.CountsAsEnabled && details[l.Source.Mod.Name].Role != ModRole.Overhaul);

        var originals = OriginalSources.Build(setup, allMods, mods);
        var ranking = new LookRanking(setup);

        progress?.Report("Looking up NPC names...");
        var entries = new List<NpcEntry>(looksByNpc.Count);
        foreach (var (formKey, looks) in looksByNpc)
        {
            ct.ThrowIfCancellationRequested();
            if (looks.Count == 0) continue;
            var record = FindRecord(plugins, formKey);
            if (record == null) continue; // face files for an NPC that doesn't exist in this load order
            // An NPC that takes its look from a template ignores face files: only looks that come with a plugin
            // giving it a look of its own count (those were checked above).
            var templated = IsTemplatedForLooks(record);
            if (templated) looks.RemoveAll(l => l.SourcePlugin == null);
            if (looks.Count == 0) continue;
            var sorted = looks.OrderByDescending(l => l.Source.Mod.Priority).ToList();
            NpcLook? original = null;
            string originalName;
            if (templated) originalName = originals.NameOf(formKey);
            else original = originals.Find(formKey, sorted, out originalName);
            var (borrowedFrom, random) = templated ? LookSource(plugins, record) : (null, false);
            entries.Add(new NpcEntry
            {
                FormKey = formKey,
                EditorId = record.EditorID,
                Name = SafeName(record),
                IsFemale = record.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female),
                Looks = sorted,
                DefaultLook = ranking.Winner(sorted.Where(l => l.Source.Mod.CountsAsEnabled)),
                Original = original,
                OriginalName = originalName,
                OriginalPicture = borrowedFrom is { } from ? originals.Find(from, [], out _) : null,
                OriginalIsRandom = random,
                OriginalBorrowedFrom = borrowedFrom,
            });
        }

        return new NpcIndex
        {
            Npcs = entries.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(),
            Mods = details,
        };
    }

    public static bool IsTemplatedForLooks(INpcGetter npc) =>
        !npc.Template.IsNull && npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits);

    /// <summary>
    /// Where a templated NPC's look comes from in game: the NPC its template leads to (followed through templates
    /// of templates), or <c>Random</c> when it leads to a leveled list, so the game picks one of several faces each
    /// time the NPC appears.
    /// </summary>
    public static (FormKey? From, bool Random) LookSource(PluginCache plugins, INpcGetter npc)
    {
        var current = npc;
        for (int hops = 0; hops < 8 && IsTemplatedForLooks(current); hops++)
        {
            var key = current.Template.FormKey;
            var mod = plugins.Get(key.ModKey.FileName);
            if (mod == null) return (null, false);
            if (mod.LeveledNpcs.RecordCache.TryGetValue(key) != null) return (null, true);
            if (mod.Npcs.RecordCache.TryGetValue(key) is not { } next) return (null, false);
            current = next;
        }
        return IsTemplatedForLooks(current) ? (null, false) : (current.FormKey, false);
    }

    public static INpcGetter? FindRecord(PluginCache plugins, FormKey formKey)
    {
        var origin = plugins.Get(formKey.ModKey.FileName);
        if (origin == null) return null;
        return origin.Npcs.RecordCache.TryGetValue(formKey);
    }

    private static string? SafeName(INpcGetter npc)
    {
        try { return npc.Name?.String; }
        catch { return null; } // localized plugin whose string files couldn't be read
    }

    private static void Add(Dictionary<FormKey, List<NpcLook>> map, NpcLook look)
    {
        if (!map.TryGetValue(look.Npc, out var list)) map[look.Npc] = list = [];
        list.Add(look);
    }

    /// <summary>Finds every face mesh/tint the mod provides, loose or inside its loaded archives.</summary>
    internal static Dictionary<string, FaceFiles> IndexFaceFiles(ModSummary mod, HashSet<string> activePlugins, string? alsoForPlugin = null)
    {
        var result = new Dictionary<string, FaceFiles>();
        FaceFiles Get(string plugin, string id8)
        {
            var key = FaceGenPaths.Key(plugin, id8);
            if (!result.TryGetValue(key, out var f)) result[key] = f = new FaceFiles();
            return f;
        }

        // Archives load for the mod's own plugins, and for a plugin of the same name in another mod (an update can replace it).
        var ownPlugins = mod.Plugins.Select(p => p.FileName).Append(alsoForPlugin).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var archive in ArchiveHelper.LoadedArchives(mod.Mod.Archives, ownPlugins))
        {
            var archivePath = Path.Combine(mod.Mod.Path, archive);
            foreach (var entry in ArchiveHelper.FaceGenEntries(archivePath))
            {
                if (!FaceGenPaths.TryParse(entry, out var plugin, out var id8, out var isNif)) continue;
                if (mod.Mod.IsGameDataFolder && !ownPlugins.Contains(plugin, StringComparer.OrdinalIgnoreCase)) continue;
                var f = Get(plugin, id8);
                if (isNif) f.NifArchive ??= archivePath; else f.DdsArchive ??= archivePath;
            }
        }

        // Loose files beat archives, same as in the game.
        foreach (var (folder, isNif, ext) in new[] { (FaceGenPaths.GeomFolder, true, "*.nif"), (FaceGenPaths.TintFolder, false, "*.dds") })
        {
            var root = Path.Combine(mod.Mod.Path, folder);
            if (!Directory.Exists(root)) continue;
            foreach (var pluginDir in Directory.EnumerateDirectories(root))
            {
                var plugin = Path.GetFileName(pluginDir);
                if (mod.Mod.IsGameDataFolder && !ownPlugins.Contains(plugin, StringComparer.OrdinalIgnoreCase)) continue;
                foreach (var file in Directory.EnumerateFiles(pluginDir, ext))
                {
                    var id8 = Path.GetFileNameWithoutExtension(file);
                    if (!FaceGenPaths.TryParseId(id8, out _)) continue;
                    var f = Get(plugin, id8);
                    if (isNif) f.NifLoosePath = file; else f.DdsLoosePath = file;
                }
            }
        }
        return result;
    }
}
