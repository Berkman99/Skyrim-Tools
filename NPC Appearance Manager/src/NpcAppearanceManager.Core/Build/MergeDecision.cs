using Mutagen.Bethesda.Plugins;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Core.Build;

/// <summary>
/// Decides for each enabled overhaul whether it is merged and switched off or left on: whichever leaves fewer NPCs
/// in the new mod. Merged, every face you keep from it has to go in; left on, only the NPCs you changed away from it.
/// An overhaul is decided together with the overhauls that need it.
/// </summary>
internal sealed class MergeDecision
{
    private readonly IReadOnlyList<NpcEntry> _npcs;
    private readonly NpcLook?[] _wanted;
    private readonly Dictionary<ModSummary, List<int>> _npcsByMod = [];
    private readonly LookRanking _ranking;

    public MergeDecision(BuildRequest request)
    {
        _ranking = new LookRanking(request.Setup);
        _npcs = request.Index.Npcs;
        _wanted = _npcs.Select(e => Wanted(e, request.Choices)).ToArray();
        for (int i = 0; i < _npcs.Count; i++)
            foreach (var mod in _npcs[i].Looks.Select(l => l.Source).Distinct())
            {
                if (!_npcsByMod.TryGetValue(mod, out var list)) _npcsByMod[mod] = list = [];
                list.Add(i);
            }
    }

    /// <summary>The look an NPC should end up with: the pick, or its original when nothing is picked.</summary>
    public static NpcLook? Wanted(NpcEntry entry, IReadOnlyDictionary<FormKey, NpcLook> choices) =>
        choices.TryGetValue(entry.FormKey, out var look) ? look : entry.OriginalIsLook ? entry.Original : null;

    /// <summary>The look the game shows once <paramref name="merged"/> are off (null: none of the looks, so the original).</summary>
    public static NpcLook? Shown(NpcEntry entry, IReadOnlySet<ModSummary> merged, LookRanking ranking) =>
        ranking.Winner(entry.Looks.Where(l => !merged.Contains(l.Source) && l.Source.Mod.CountsAsEnabled));

    private bool Written(int i, IReadOnlySet<ModSummary> merged) => _wanted[i] != Shown(_npcs[i], merged, _ranking);

    private IEnumerable<int> NpcsOf(IEnumerable<ModSummary> mods) =>
        mods.SelectMany(m => _npcsByMod.GetValueOrDefault(m) ?? []).Distinct();

    /// <summary>Which of the groups to merge. Each group is merged or left on as a whole.</summary>
    public HashSet<ModSummary> Decide(IReadOnlyList<IReadOnlyList<ModSummary>> groups)
    {
        var merged = new HashSet<ModSummary>();
        // One group's decision can change another's count (their faces are for the same NPCs), so go round until
        // nothing changes. Each round only switches a group when that strictly helps, so it settles quickly.
        for (int round = 0; round < 20; round++)
        {
            bool changed = false;
            foreach (var group in groups)
            {
                var npcs = NpcsOf(group).ToList();
                var without = merged.Except(group).ToHashSet();
                var with = without.Union(group).ToHashSet();
                int writtenWith = npcs.Count(i => Written(i, with));
                int writtenWithout = npcs.Count(i => Written(i, without));
                // A tie goes to switching it off only when none of its faces show anyway: then it does nothing.
                bool merge = writtenWith < writtenWithout ||
                             (writtenWith == writtenWithout && !npcs.Any(i => Shown(_npcs[i], without, _ranking) is { } s && group.Contains(s.Source)));
                if (merge == group.All(merged.Contains)) continue;
                merged = merge ? with : without;
                changed = true;
            }
            if (!changed) break;
        }
        return merged;
    }

    /// <summary>
    /// Why an overhaul is merged or stays on, in numbers: "2 of its 300 faces are used". Null for one that stays on
    /// with none of its NPCs in the new mod: the build doesn't touch it.
    /// </summary>
    public string? Reason(ModSummary mod, IReadOnlySet<ModSummary> merged)
    {
        var npcs = _npcsByMod.GetValueOrDefault(mod) ?? [];
        int used = npcs.Count(i => _wanted[i]?.Source == mod);
        var faces = $"{used:N0} of its {npcs.Count:N0} faces {(used == 1 ? "is" : "are")} used";
        if (merged.Contains(mod))
            return used == 0 ? "None of its faces are used." : $"{faces}. They go into the NAM merge.";

        int changed = npcs.Count(i => Shown(_npcs[i], merged, _ranking)?.Source == mod && Written(i, merged));
        if (changed == 0) return null;
        if (used < changed)
        {
            // Few faces kept, yet it stays on: another mod's faces for the same NPCs would show instead, so the new
            // mod would have to hold those NPCs anyway.
            var off = merged.Append(mod).ToHashSet();
            var other = npcs.Select(i => Shown(_npcs[i], off, _ranking)?.Source).OfType<ModSummary>()
                .GroupBy(m => m).MaxBy(g => g.Count())?.Key;
            if (other != null)
                return $"{faces}, but it stays on. With it off, {other.Mod.Name}'s faces would show for the same NPCs, " +
                       $"so the NAM merge wouldn't get smaller. It holds the {changed:N0} you changed.";
        }
        return $"{faces}, so it stays on. The NAM merge holds only the {changed:N0} you changed.";
    }
}
