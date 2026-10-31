namespace NpcAppearanceManager.Core.Scanning;

/// <summary>
/// How a pick names its mod in the settings. A mod with a Nexus ID is saved as "nexus:ID:name", so renaming its folder
/// keeps the pick; others by their folder name. "" (the original) and "ff:id" (a FaceFinder face) are saved as they are.
/// In memory a pick is the installed mod's folder name; a pick whose mod isn't installed keeps its saved form.
/// </summary>
public static class PickKeys
{
    private const string Nexus = "nexus:";

    /// <summary>The saved form of a pick key among an NPC's looks.</summary>
    public static string Save(string key, IEnumerable<NpcLook> looks)
    {
        if (key.Length == 0 || key.StartsWith("ff:", StringComparison.Ordinal) || key.StartsWith(Nexus, StringComparison.Ordinal)) return key;
        var look = looks.FirstOrDefault(l => l.ModName == key);
        return look?.Source.Mod.NexusId is { } id ? $"{Nexus}{id}:{key}" : key;
    }

    /// <summary>
    /// The pick key of a saved pick: the installed mod's name (the same folder name first, else any mod with that Nexus
    /// ID, renamed or not). When no installed mod has it, the saved form, which <see cref="IsMissing"/> tells apart.
    /// </summary>
    public static string Load(string saved, IReadOnlyCollection<NpcLook> looks)
    {
        if (!saved.StartsWith(Nexus, StringComparison.Ordinal))
            return saved;
        var (id, name) = Split(saved);
        return looks.FirstOrDefault(l => l.ModName == name && l.Source.Mod.NexusId == id)?.ModName
               ?? looks.FirstOrDefault(l => l.Source.Mod.NexusId == id)?.ModName
               ?? (looks.Any(l => l.ModName == name) ? name : saved);
    }

    /// <summary>A pick of a mod that isn't installed: not the original, not a FaceFinder face, and none of the looks.</summary>
    public static bool IsMissing(string key, IEnumerable<NpcLook> looks) =>
        key.Length > 0 && !key.StartsWith("ff:", StringComparison.Ordinal) && !looks.Any(l => l.ModName == key);

    /// <summary>The mod's name in a pick key (saved form or not).</summary>
    public static string NameOf(string key) => key.StartsWith(Nexus, StringComparison.Ordinal) ? Split(key).Name : key;

    /// <summary>The mod's Nexus ID in a saved pick key, if it has one.</summary>
    public static int? NexusIdOf(string key) => key.StartsWith(Nexus, StringComparison.Ordinal) ? Split(key).Id : null;

    private static (int? Id, string Name) Split(string saved)
    {
        var rest = saved[Nexus.Length..];
        var colon = rest.IndexOf(':');
        return colon > 0 && int.TryParse(rest[..colon], out var id) ? (id, rest[(colon + 1)..]) : (null, rest);
    }
}
