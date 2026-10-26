using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Scanning;

namespace NpcAppearanceManager.Core.Build;

/// <summary>What an overhaul's files are for: its faces, or something else as well.</summary>
public static class ModContents
{
    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".rtf", ".doc", ".docx", ".html", ".htm", ".url",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".psd",
    };

    /// <summary>
    /// The files a mod puts into the game, relative to Data: loose ones and those in archives its active plugins
    /// load. Plugins, archives, MO2's meta.ini, installer folders and read-me files are left out.
    /// </summary>
    public static Dictionary<string, (string? Loose, string? Archive)> Files(ModSummary mod)
    {
        var result = new Dictionary<string, (string?, string?)>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(mod.Mod.Path))
            foreach (var file in Directory.EnumerateFiles(mod.Mod.Path, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(mod.Mod.Path, file);
                if (!rel.Contains('\\') && (Environment.PluginNames.IsPlugin(rel) || rel.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) ||
                                            rel.Equals("meta.ini", StringComparison.OrdinalIgnoreCase) || MergeMarker.IsMarkerFile(rel)))
                    continue;
                if (rel.Split('\\')[0].Equals("fomod", StringComparison.OrdinalIgnoreCase) || IsDoc(rel)) continue;
                result[rel] = (file, null);
            }
        var active = mod.Plugins.Where(p => p.Active || !mod.Mod.CountsAsEnabled).Select(p => p.FileName).ToList();
        foreach (var archive in ArchiveHelper.LoadedArchives(mod.Mod.Archives, active))
        {
            var path = Path.Combine(mod.Mod.Path, archive);
            var reader = ArchiveHelper.Open(path);
            if (reader == null) continue;
            foreach (var f in reader.Files)
            {
                var rel = f.Path.Replace('/', '\\').TrimStart('\\');
                if (!IsDoc(rel)) result.TryAdd(rel, (null, path)); // loose files win over archived ones
            }
        }
        return result;
    }

    /// <summary>
    /// A file of the mod that none of its faces and none of its records use, but that replaces a file the game or
    /// another mod has (a body texture, another mod's hair physics) and is the copy the game uses: switching the mod
    /// off would change that. Null when there is none. A leftover only this mod has, or a copy another mod wins
    /// over, changes nothing, so it doesn't count.
    /// </summary>
    public static string? ReplacedFile(ModSummary mod, IEnumerable<ISkyrimModGetter> plugins, GameSetup setup) =>
        ReplacedFile(mod, plugins, ReplacesInGame(setup, mod));

    /// <param name="replaces">Whether the mod's copy of a path (loose or not) is used by the game and replaces another.</param>
    public static string? ReplacedFile(ModSummary mod, IEnumerable<ISkyrimModGetter> plugins, Func<string, bool, bool> replaces)
    {
        var files = Files(mod);
        return UnusedFiles(files, plugins).FirstOrDefault(f => replaces(f, files[f].Loose != null));
    }

    /// <summary>Every file of the mod that none of its faces and none of its records use.</summary>
    public static List<string> UnusedFiles(ModSummary mod, IEnumerable<ISkyrimModGetter> plugins) => UnusedFiles(Files(mod), plugins);

    private static List<string> UnusedFiles(Dictionary<string, (string? Loose, string? Archive)> files, IEnumerable<ISkyrimModGetter> plugins)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in plugins)
            foreach (var record in plugin.EnumerateMajorRecords())
                foreach (var asset in record.EnumerateAssetLinks(AssetLinkQuery.Listed))
                    used.Add(NifTextures.NormalizeDataPath(asset.DataRelativePath.Path));

        // Follow every mesh: face meshes, and meshes the records use, and so on.
        var queue = new Queue<string>(files.Keys.Where(f => IsFaceGen(f) || used.Contains(f)).Where(IsNif));
        var read = new HashSet<string>(queue, StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var nif = queue.Dequeue();
            var (loose, archive) = files[nif];
            var inside = loose != null ? NifTextures.Read(loose) : NifTextures.ReadFromArchive(archive!, nif);
            foreach (var f in inside)
            {
                used.Add(f);
                if (IsNif(f) && files.ContainsKey(f) && read.Add(f)) queue.Enqueue(f);
            }
        }
        return files.Keys.Where(f => !IsFaceGen(f) && !used.Contains(f))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Whether the mod's copy of a path is the one the game uses, and the game's own archives or another enabled mod
    /// have the path too (so the copy replaces something). Loose files win over archives; between loose files the
    /// higher MO2 priority wins.
    /// </summary>
    public static Func<string, bool, bool> ReplacesInGame(GameSetup setup, ModSummary mod)
    {
        // A mod this tool built holds copies of merged files; it doesn't count.
        var others = setup.AllMods.Where(m => m.CountsAsEnabled && m != mod.Mod && !m.IsGameDataFolder &&
                                              !m.TopLevelFiles.Any(MergeMarker.IsMarkerFile)).ToList();
        return (path, loose) =>
        {
            var looseElsewhere = others.Where(m => File.Exists(Path.Combine(m.Path, path))).ToList();
            bool used = loose ? looseElsewhere.All(m => m.Priority < mod.Mod.Priority) : looseElsewhere.Count == 0;
            return used && (looseElsewhere.Count > 0 || GameArchiveFiles(setup).Contains(path));
        };
    }

    /// <summary>
    /// For every enabled overhaul-only mod: a file it replaces that none of its faces use (null when there's none).
    /// Reading every face mesh takes a while, so the tool does this once in the background after loading.
    /// </summary>
    public static Dictionary<ModSummary, string?> CheckOverhauls(GameSetup setup, NpcIndex index, PluginCache cache)
    {
        var mods = index.Mods.Values
            .Where(d => d.Role == ModRole.Overhaul && d.Summary.Mod.CountsAsEnabled && !d.Summary.Mod.IsGameDataFolder &&
                        MergeBuilder.OnlyLooks(d.Summary))
            .Select(d => d.Summary).ToList();
        var result = new System.Collections.Concurrent.ConcurrentDictionary<ModSummary, string?>();
        Parallel.ForEach(mods, new ParallelOptions { MaxDegreeOfParallelism = 4 }, mod =>
        {
            var plugins = mod.Plugins.Where(p => p.Active).Select(p => cache.Get(p.FileName)).OfType<ISkyrimModGetter>().ToList();
            result[mod] = ReplacedFile(mod, plugins, setup);
        });
        return new Dictionary<ModSummary, string?>(result);
    }

    private static readonly Dictionary<string, HashSet<string>> GameArchiveCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file in the archives of the game's Data folder (Skyrim's own, the DLCs, Creation Club).</summary>
    private static HashSet<string> GameArchiveFiles(GameSetup setup)
    {
        lock (GameArchiveCache)
        {
            if (GameArchiveCache.TryGetValue(setup.DataFolder, out var cached)) return cached;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(setup.DataFolder))
                foreach (var archive in Directory.EnumerateFiles(setup.DataFolder, "*.bsa"))
                    if (ArchiveHelper.Open(archive) is { } reader)
                        foreach (var f in reader.Files) files.Add(f.Path.Replace('/', '\\').TrimStart('\\'));
            return GameArchiveCache[setup.DataFolder] = files;
        }
    }

    public static bool IsFaceGen(string relativePath) =>
        relativePath.StartsWith(FaceGenPaths.GeomFolder + @"\", StringComparison.OrdinalIgnoreCase) ||
        relativePath.StartsWith(FaceGenPaths.TintFolder + @"\", StringComparison.OrdinalIgnoreCase);

    private static bool IsNif(string path) => path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase);

    private static bool IsDoc(string path) => DocExtensions.Contains(Path.GetExtension(path));
}
