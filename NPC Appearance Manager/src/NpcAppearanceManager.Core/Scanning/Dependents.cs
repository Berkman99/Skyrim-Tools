namespace NpcFaceManager.Core.Scanning;

/// <summary>
/// Dependencies between mods: mod B depends on mod A when one of B's plugins lists one of A's plugins as a
/// master. Merging B together with A is what lets A be switched off without breaking B.
/// An enabled mod's dependencies are the enabled mods that need it; a disabled mod's also include disabled ones
/// (its patches are usually disabled along with it).
/// </summary>
public sealed class Dependents
{
    /// <summary>Skyrim's limit for one plugin.</summary>
    public const int MaxMasters = 254;

    private readonly Dictionary<ModSummary, List<ModSummary>> _dependents = [];
    private readonly Dictionary<ModSummary, int> _masterCounts = [];

    public Dependents(IReadOnlyList<ModSummary> allMods)
    {
        // Which mods list each plugin as a master (an enabled mod's plugins only count while they're active).
        var byMaster = new Dictionary<string, HashSet<ModSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in allMods)
        {
            var own = mod.Plugins.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _masterCounts[mod] = mod.Plugins.SelectMany(p => p.Masters).Where(m => !own.Contains(m))
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            foreach (var p in mod.Plugins.Where(p => p.Active || !mod.Mod.CountsAsEnabled))
                foreach (var master in p.Masters)
                {
                    if (!byMaster.TryGetValue(master, out var set)) byMaster[master] = set = [];
                    set.Add(mod);
                }
        }
        foreach (var mod in allMods)
        {
            var deps = mod.Plugins.SelectMany(p => byMaster.GetValueOrDefault(p.FileName) ?? [])
                .Where(d => d != mod && (d.Mod.CountsAsEnabled || !mod.Mod.CountsAsEnabled))
                .Distinct().OrderByDescending(d => d.Mod.Priority).ToList();
            if (deps.Count > 0) _dependents[mod] = deps;
        }
    }

    /// <summary>Mods that directly depend on this mod.</summary>
    public IReadOnlyList<ModSummary> Of(ModSummary mod) => _dependents.GetValueOrDefault(mod) ?? [];

    /// <summary>How many other plugins this mod's plugins list as masters (combined).</summary>
    public int MasterCount(ModSummary mod) => _masterCounts.GetValueOrDefault(mod);

    public bool HasTooManyMasters(ModSummary mod) => MasterCount(mod) > MaxMasters;

    /// <summary>
    /// The given mods plus everything that needs them, and what needs those, and so on ("Merge dependencies").
    /// Mods with too many masters aren't added, and neither are mods <paramref name="exclude"/> rejects.
    /// </summary>
    public List<ModSummary> WithDependencies(IEnumerable<ModSummary> mods, Func<ModSummary, bool> exclude)
    {
        var result = mods.ToList();
        var seen = result.ToHashSet();
        for (int i = 0; i < result.Count; i++)
            foreach (var d in Of(result[i]))
                if (!HasTooManyMasters(d) && !exclude(d) && seen.Add(d))
                    result.Add(d);
        return result;
    }
}
