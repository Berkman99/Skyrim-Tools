using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Scanning;

namespace NpcAppearanceManager.Core.Build;

/// <summary>Add-ons NAM couldn't link to a face mod, for the question Save asks: one group per face mod you picked from.</summary>
public sealed record AddOnGroup(string FaceMod, IReadOnlyList<string> AddOns);

/// <summary>
/// Enabled mods that change the face files of an NPC you picked a face for, without being a face of their own (makeup
/// removers, colour textures, .tri files). A mod with both the face mesh and the face tint of an NPC is a face mod for it,
/// not an add-on. An add-on counts for a picked face when it sits above the face's mod and belongs to it: its plugin
/// requires the face mod's plugin, or its name starts with the face mod's name. Any other is asked about in
/// <see cref="Ask"/> and counts when ticked.
/// </summary>
public sealed class AddOnScan
{
    private sealed record Candidate(ModSummary AddOn, FaceFiles Files, bool Linked);

    public static readonly AddOnScan None = new([], []);

    private readonly Dictionary<(ModSummary Face, FormKey Npc), List<Candidate>> _candidates;

    private AddOnScan(Dictionary<(ModSummary, FormKey), List<Candidate>> candidates, IReadOnlyList<AddOnGroup> ask)
    {
        _candidates = candidates;
        Ask = ask;
    }

    /// <summary>Add-ons that couldn't be linked, by the face mod they sit above.</summary>
    public IReadOnlyList<AddOnGroup> Ask { get; }

    /// <summary>How a ticked add-on is remembered.</summary>
    public static string Key(string faceMod, string addOn) => faceMod + "|" + addOn;

    public static AddOnScan Find(GameSetup setup, IReadOnlyList<ModSummary> allMods, NpcIndex index,
        IReadOnlyDictionary<FormKey, NpcLook> picks, CancellationToken ct = default)
    {
        // The faces picked from a mod (a copied face is the other NPC's). The game's own faces have no add-ons.
        var faces = picks.Values.Where(l => !l.Source.Mod.IsGameDataFolder).Select(l => (l.Source, l.Npc)).Distinct().ToList();
        if (faces.Count == 0) return None;
        var needed = faces.Select(f => FaceGenPaths.Key(f.Npc)).ToHashSet();
        var active = setup.LoadOrder.Select(k => k.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // What each enabled mod has of those NPCs' face files, leaving out mods that have both files of an NPC.
        var found = new ConcurrentDictionary<ModSummary, Dictionary<string, FaceFiles>>();
        Parallel.ForEach(allMods.Where(m => m.Mod.IsEnabled && !m.Mod.IsGameDataFolder),
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, mod =>
            {
                var files = NpcScanner.IndexFaceFiles(mod, active, archives: mod.IsAppearanceMod)
                    .Where(f => needed.Contains(f.Key) && !(f.Value.HasNif && f.Value.HasDds))
                    .ToDictionary(f => f.Key, f => f.Value);
                if (files.Count > 0) found[mod] = files;
            });

        var entries = index.Npcs.ToDictionary(n => n.FormKey);
        var candidates = new Dictionary<(ModSummary, FormKey), List<Candidate>>();
        foreach (var (face, npc) in faces)
        {
            var key = FaceGenPaths.Key(npc);
            var faceMods = (entries.TryGetValue(npc, out var entry) ? entry.Looks : [])
                .Where(l => l.Faces.HasNif && l.Faces.HasDds).Select(l => l.Source).Append(face).Distinct().ToList();
            foreach (var (addOn, files) in found)
            {
                if (addOn == face || addOn.Mod.Priority <= face.Mod.Priority || !files.TryGetValue(key, out var addOnFiles)) continue;
                var owner = Owner(addOn, faceMods.Where(m => m != addOn).ToList(), face);
                if (owner != null && owner != face) continue; // belongs to another face mod
                if (!candidates.TryGetValue((face, npc), out var list)) candidates[(face, npc)] = list = [];
                list.Add(new Candidate(addOn, addOnFiles, owner != null));
            }
        }

        var ask = candidates.GroupBy(c => c.Key.Item1)
            .Select(g => (Face: g.Key, Names: g.SelectMany(c => c.Value).Where(c => !c.Linked).Select(c => c.AddOn.Mod.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase).ToList()))
            .Where(g => g.Names.Count > 0)
            .OrderBy(g => g.Face.Mod.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new AddOnGroup(g.Face.Mod.Name, g.Names))
            .ToList();
        return new AddOnScan(candidates, ask);
    }

    /// <summary>
    /// The face mod an add-on belongs to: the one whose plugin its enabled plugin requires, else the one whose name its name
    /// starts with (the longest). Null when neither tells.
    /// </summary>
    private static ModSummary? Owner(ModSummary addOn, List<ModSummary> faceMods, ModSummary preferred)
    {
        var masters = addOn.Plugins.Where(p => p.Active).SelectMany(p => p.Masters).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (masters.Count > 0)
        {
            var byPlugin = faceMods.Where(m => m.Plugins.Any(p => masters.Contains(p.FileName))).ToList();
            if (byPlugin.Count > 0) return byPlugin.Contains(preferred) ? preferred : byPlugin[0];
        }
        return faceMods
            .Where(m => addOn.Mod.Name.Length > m.Mod.Name.Length && addOn.Mod.Name.StartsWith(m.Mod.Name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.Mod.Name.Length).FirstOrDefault();
    }

    /// <summary>
    /// A picked face's files with its add-ons in: for the mesh and for the tint, the file of the highest add-on that belongs
    /// to the face's mod (or is ticked), else the face mod's own.
    /// </summary>
    public FaceFiles Apply(NpcLook look, IReadOnlySet<string> ticked)
    {
        if (!_candidates.TryGetValue((look.Source, look.Npc), out var list)) return look.Faces;
        var use = list.Where(c => c.Linked || ticked.Contains(Key(look.ModName, c.AddOn.Mod.Name)))
            .OrderByDescending(c => c.AddOn.Mod.Priority).ToList();
        var nif = use.FirstOrDefault(c => c.Files.HasNif)?.Files;
        var dds = use.FirstOrDefault(c => c.Files.HasDds)?.Files;
        if (nif == null && dds == null) return look.Faces;
        return new FaceFiles
        {
            NifLoosePath = nif != null ? nif.NifLoosePath : look.Faces.NifLoosePath,
            NifArchive = nif != null ? nif.NifArchive : look.Faces.NifArchive,
            DdsLoosePath = dds != null ? dds.DdsLoosePath : look.Faces.DdsLoosePath,
            DdsArchive = dds != null ? dds.DdsArchive : look.Faces.DdsArchive,
        };
    }
}
