using Mutagen.Bethesda.Skyrim;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>
/// The only races whose NPCs NAM lists (creatures aren't NPCs here), in the order the race filter shows them. An NPC's
/// race is one of these when its race record says so: a race the game flags as a child's is "Child", the old people
/// race is "Elder", a Snow Elf race (by its editor ID) is "Snow Elf", and any other race by its in-game name.
/// </summary>
public static class NpcRaces
{
    public static readonly IReadOnlyList<string> Labels =
    [
        "Nord", "Imperial", "Breton", "Redguard", "Dark Elf", "High Elf", "Wood Elf", "Orc", "Argonian", "Khajiit",
        "Elder", "Child", "Dremora", "Snow Elf", "Golden Saint", "Dark Seducer",
    ];

    /// <summary>Where a race filter choice sits in <see cref="Labels"/> (the others after them).</summary>
    public static int OrderOf(string label)
    {
        for (var i = 0; i < Labels.Count; i++)
            if (string.Equals(Labels[i], label, StringComparison.CurrentCultureIgnoreCase)) return i;
        return Labels.Count;
    }

    /// <summary>The race filter's label for a race, or null when its NPCs aren't listed.</summary>
    public static string? LabelOf(IRaceGetter race, string? name)
    {
        if (race.Flags.HasFlag(Race.Flag.Child) && race.Flags.HasFlag(Race.Flag.FaceGenHead)) return "Child";
        if (string.Equals(name, "Old People Race", StringComparison.OrdinalIgnoreCase)) return "Elder";
        if (race.EditorID?.Contains("SnowElfRace", StringComparison.OrdinalIgnoreCase) == true) return "Snow Elf";
        return Labels.FirstOrDefault(l => string.Equals(l, name, StringComparison.OrdinalIgnoreCase) && l is not ("Elder" or "Child" or "Snow Elf"));
    }
}
