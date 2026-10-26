using NpcAppearanceManager.Core.Environment;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>What a mod with faces does, worked out from the NPCs its faces belong to.</summary>
public enum ModRole
{
    /// <summary>No usable looks.</summary>
    None,
    /// <summary>Only changes NPCs that come from Skyrim or other mods. Can be merged and switched off.</summary>
    Overhaul,
    /// <summary>Only has faces for NPCs it adds itself. Never merged or switched off.</summary>
    NpcMod,
    /// <summary>Adds NPCs and changes others' (e.g. Lawless). Stays on because it adds NPCs.</summary>
    Both,
}

/// <summary>
/// Every NPC's ID names the plugin that adds it, so for each look a mod offers we know whether the NPC is the mod's
/// own or someone else's. A mod's role follows from that.
/// </summary>
public static class ModRoles
{
    public static void Assign(GameSetup setup, IReadOnlyDictionary<string, ModDetails> mods, IEnumerable<NpcLook> looks)
    {
        foreach (var group in looks.GroupBy(l => l.Source))
        {
            if (!mods.TryGetValue(group.Key.Mod.Name, out var details)) continue;
            details.OwnLooks = group.Count(l => IsOwn(setup, l));
            details.OthersLooks = group.Count() - details.OwnLooks;
        }
        foreach (var details in mods.Values)
            details.Role = (details.OwnLooks, details.OthersLooks) switch
            {
                (0, 0) => ModRole.None,
                (0, _) => ModRole.Overhaul,
                (_, 0) => ModRole.NpcMod,
                _ => ModRole.Both,
            };
    }

    /// <summary>The NPC is added by the look's own mod (one of its plugins created the NPC).</summary>
    public static bool IsOwn(GameSetup setup, NpcLook look) =>
        look.Source.Plugins.Any(p => p.FileName.Equals(look.Npc.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase)) ||
        setup.PluginOwner(look.Npc.ModKey.FileName) == look.Source.Mod;
}

/// <summary>
/// Which look the game shows when several mods give an NPC a face, by the game's own rules: loose face files beat
/// archived ones; among loose files the highest MO2 priority wins; among archives the one whose plugin loads last.
/// </summary>
public sealed class LookRanking
{
    private readonly Dictionary<string, int> _loadOrderIndex = new(StringComparer.OrdinalIgnoreCase);

    public LookRanking(GameSetup setup)
    {
        for (int i = 0; i < setup.LoadOrder.Count; i++) _loadOrderIndex[setup.LoadOrder[i].FileName.String] = i;
    }

    /// <summary>The winning look among <paramref name="looks"/>, or null when there are none.</summary>
    public NpcLook? Winner(IEnumerable<NpcLook> looks) => looks.Where(l => l.Faces.HasNif).MaxBy(Rank);

    /// <summary>The look the game would show for an NPC without <paramref name="modName"/> (null: its original).</summary>
    public NpcLook? WinnerWithout(NpcEntry entry, string modName) =>
        Winner(entry.Looks.Where(l => l.ModName != modName && l.Source.Mod.CountsAsEnabled));

    private (int, int) Rank(NpcLook look)
    {
        if (look.Faces.NifLoosePath != null) return (1, look.Source.Mod.Priority);
        var archive = Path.GetFileName(look.Faces.NifArchive!);
        var owner = look.Source.Plugins.Select(p => p.FileName).FirstOrDefault(p => ArchiveHelper.IsOwnedBy(archive, p));
        return (0, owner != null ? _loadOrderIndex.GetValueOrDefault(owner, -1) : -1);
    }
}
