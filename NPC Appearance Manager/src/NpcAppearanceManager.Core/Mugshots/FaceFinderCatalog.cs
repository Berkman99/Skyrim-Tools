using System.Globalization;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.Core.Mugshots;

/// <summary>A face from a mod you don't have installed: it can be picked, but built only once the mod is installed.</summary>
public sealed record OnlineLook(FaceFinderMod Mod, FaceFinderFace Face)
{
    /// <summary>How the pick is saved: "ff:" and FaceFinder's mod id.</summary>
    public string ChoiceKey => ChoicePrefix + Mod.Id;
    public const string ChoicePrefix = "ff:";

    public static int? ModIdOf(string choice) =>
        choice.StartsWith(ChoicePrefix, StringComparison.Ordinal) && int.TryParse(choice[ChoicePrefix.Length..], out var id) ? id : null;
}

/// <summary>
/// FaceFinder's whole list: every mod and every face it has. Downloaded once and kept in the cache folder; it's only
/// downloaded again when you press "Update FaceFinder" in Settings.
/// </summary>
public sealed class FaceFinderCatalog
{
    public DateTime DownloadedAt { get; init; }
    public required List<FaceFinderMod> Mods { get; init; }
    public required List<FaceFinderFace> Faces { get; init; }

    private Dictionary<int, FaceFinderMod>? _modsById;
    private Dictionary<string, List<FaceFinderFace>>? _facesByNpc;

    public static string FilePath => Path.Combine(AppSettings.CacheFolder, "FaceFinder", "catalog.json");

    public FaceFinderMod? Mod(int id) => (_modsById ??= Mods.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First())).GetValueOrDefault(id);

    public IReadOnlyList<FaceFinderFace> FacesFor(FormKey npc) => FacesByNpc.GetValueOrDefault(NpcKey(npc)) ?? [];

    /// <summary>Every NPC with at least one face (keys as <see cref="NpcKey(string?, string?)"/> makes them).</summary>
    public IReadOnlyDictionary<string, List<FaceFinderFace>> FacesByNpc =>
        _facesByNpc ??= Faces.Where(f => f.NpcKey != null).GroupBy(f => f.NpcKey!).ToDictionary(g => g.Key, g => g.ToList());

    /// <summary>"0A2C8E:skyrim.esm": the NPC's ID inside its plugin, and the plugin in lower case.</summary>
    public static string NpcKey(FormKey npc) => $"{npc.ID:X6}:{npc.ModKey.FileName.String.ToLowerInvariant()}";

    /// <summary>
    /// The same key from FaceFinder's "ref_id" (8 hex digits, the first two being the load order slot) and "source".
    /// FE-prefixed IDs belong to light plugins and only their last three digits count.
    /// </summary>
    public static string? NpcKey(string? refId, string? plugin)
    {
        if (string.IsNullOrWhiteSpace(refId) || string.IsNullOrWhiteSpace(plugin)) return null;
        if (!uint.TryParse(refId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)) return null;
        var id = raw >> 24 == 0xFE ? raw & 0xFFF : raw & 0xFFFFFF;
        return $"{id:X6}:{plugin.Trim().ToLowerInvariant()}";
    }

    /// <summary>The installed mod a FaceFinder mod is: same Nexus id, or else the same name.</summary>
    public static ModFolder? InstalledMod(FaceFinderMod mod, IEnumerable<ModFolder> installed)
    {
        var list = installed as IReadOnlyCollection<ModFolder> ?? installed.ToList();
        if (mod.NexusId is { } nexus && list.FirstOrDefault(m => m.NexusId == nexus) is { } byId) return byId;
        var name = Normalize(mod.Name);
        return list.FirstOrDefault(m => Normalize(m.Name) == name);
    }

    /// <summary>
    /// Faces in <paramref name="older"/> that FaceFinder has changed or removed since: their saved pictures are out
    /// of date ("Update FaceFinder" deletes them, so they're downloaded again).
    /// </summary>
    public static List<FaceFinderFace> ChangedSince(FaceFinderCatalog older, FaceFinderCatalog newer)
    {
        var now = newer.Faces.Where(f => f.NpcKey != null)
            .GroupBy(f => (f.ModId, f.NpcKey))
            .ToDictionary(g => g.Key, g => g.First());
        return older.Faces.Where(f => f.NpcKey != null &&
            (!now.TryGetValue((f.ModId, f.NpcKey), out var current) || current.UpdatedAt != f.UpdatedAt || current.ImageUrl != f.ImageUrl))
            .ToList();
    }

    /// <summary>
    /// FaceFinder's faces of the game itself ("Skyrim Special Edition", and its "(Children)" and "(Creation Club)"
    /// lists): they're the original look of Skyrim's and Creation Club's NPCs, not a mod to install.
    /// </summary>
    public static bool IsBaseGame(FaceFinderMod mod) =>
        Normalize(mod.Name).StartsWith(Normalize(Scanning.OriginalSources.BaseGameName), StringComparison.Ordinal);

    public static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static FaceFinderCatalog? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            using var stream = File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<FaceFinderCatalog>(stream, Json);
        }
        catch { return null; }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        using (var stream = File.Create(tmp)) JsonSerializer.Serialize(stream, this, Json);
        File.Move(tmp, FilePath, overwrite: true);
    }

    /// <summary>
    /// Downloads the whole list: every mod, then each mod's faces (a few mods at a time; FaceFinder's limits are
    /// honoured by the client). Reports "mods done / mods". Saved when it's complete.
    /// </summary>
    public static async Task<FaceFinderCatalog> DownloadAsync(IProgress<(int Done, int Total)>? progress, CancellationToken ct)
    {
        var client = new FaceFinderClient();
        var mods = await client.GetModsAsync(ct);
        if (mods.Count == 0) throw new InvalidOperationException("FaceFinder didn't answer. Check your internet connection and try again.");
        var faces = new List<FaceFinderFace>();
        int done = 0;
        progress?.Report((0, mods.Count));
        await Parallel.ForEachAsync(mods, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, async (mod, token) =>
        {
            var modFaces = await client.GetModFacesAsync(mod.Id, token);
            lock (faces) faces.AddRange(modFaces);
            progress?.Report((Interlocked.Increment(ref done), mods.Count));
        });
        var catalog = new FaceFinderCatalog { DownloadedAt = DateTime.Now, Mods = mods, Faces = faces };
        catalog.Save();
        return catalog;
    }
}
