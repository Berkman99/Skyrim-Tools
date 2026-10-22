using System.Collections.Concurrent;
using NpcFaceManager.Core.Build;
using NpcFaceManager.Core.Environment;

namespace NpcFaceManager.Core.Scanning;

public enum ModKind
{
    /// <summary>Has a plugin that edits NPCs and ships face files.</summary>
    FacesAndPlugin,
    /// <summary>Ships face files but no plugin that edits NPCs (e.g. Nordic Faces).</summary>
    FacesOnly,
    /// <summary>No face files: not an appearance overhaul.</summary>
    Other,
}

public sealed record PluginSummary(string FileName, bool Active, bool HasNpcRecords, IReadOnlyList<string> Masters,
    IReadOnlyList<string> RecordTypes);

public sealed class ModSummary
{
    public required ModFolder Mod { get; init; }
    public required ModKind Kind { get; init; }
    public required IReadOnlyList<PluginSummary> Plugins { get; init; }
    public required int FaceFileCount { get; init; }
    public required bool FacesInArchives { get; init; }

    /// <summary>Plugin names the face files belong to (e.g. Skyrim.esm).</summary>
    public required IReadOnlyList<string> FacePlugins { get; init; }

    public bool IsAppearanceMod => Kind is ModKind.FacesAndPlugin or ModKind.FacesOnly;

    public override string ToString() => Mod.Name;
}

/// <summary>
/// Quick look at every installed mod: lists its top folder, peeks at plugin headers and checks for face
/// files (loose, or by reading only the folder list of its archives). Never reads records or textures.
/// </summary>
public static class ModPreScanner
{
    public static IReadOnlyList<ModSummary> Scan(GameSetup setup, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var folders = setup.IsMo2 ? setup.AllMods : DataFolderPseudoMods(setup);
        var results = new ConcurrentBag<ModSummary>();
        int done = 0;

        Parallel.ForEach(folders, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, mod =>
        {
            if (mod.TopLevelFiles.Any(MergeMarker.IsMarkerFile)) return;
            results.Add(ScanOne(mod, setup));
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0) progress?.Report($"Checked {n} of {folders.Count} mods...");
        });

        return results.OrderByDescending(r => r.Mod.Priority).ToList();
    }

    private static ModSummary ScanOne(ModFolder mod, GameSetup setup)
    {
        var plugins = mod.Plugins
            .Select(p => (Name: p, Header: PluginHeader.TryRead(Path.Combine(mod.Path, p))))
            .Select(p => new PluginSummary(p.Name, setup.IsActive(p.Name), p.Header?.HasNpcRecords ?? false,
                p.Header?.Masters ?? [], p.Header?.RecordTypes ?? []))
            .ToList();

        var facePlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int faceCount = 0;

        // Loose face files: only the face folders are listed, never the whole mod.
        var geomRoot = Path.Combine(mod.Path, FaceGenPaths.GeomFolder);
        if (Directory.Exists(geomRoot))
        {
            foreach (var pluginDir in Directory.EnumerateDirectories(geomRoot))
            {
                var pluginName = Path.GetFileName(pluginDir);
                if (mod.IsGameDataFolder && !mod.Plugins.Contains(pluginName, StringComparer.OrdinalIgnoreCase)) continue;
                int n = Directory.EnumerateFiles(pluginDir, "*.nif").Count();
                if (n == 0) continue;
                faceCount += n;
                facePlugins.Add(pluginName);
            }
        }

        // Face files in archives the game loads (archives named after one of the mod's plugins). Only the
        // archive's folder list is read, so this is cheap even for big archives.
        bool inArchives = false;
        var ownPlugins = plugins.Select(p => p.FileName).ToList();
        foreach (var archive in ArchiveHelper.LoadedArchives(mod.Archives, ownPlugins))
        {
            foreach (var (plugin, count) in ArchiveFaceFolders(Path.Combine(mod.Path, archive)))
            {
                if (mod.IsGameDataFolder && !ownPlugins.Contains(plugin, StringComparer.OrdinalIgnoreCase)) continue;
                faceCount += count;
                facePlugins.Add(plugin);
                inArchives = true;
            }
        }

        var kind = faceCount == 0 ? ModKind.Other
            : plugins.Any(p => p.HasNpcRecords) ? ModKind.FacesAndPlugin
            : ModKind.FacesOnly;

        return new ModSummary
        {
            Mod = mod,
            Kind = kind,
            Plugins = plugins,
            FaceFileCount = faceCount,
            FacesInArchives = inArchives,
            FacePlugins = facePlugins.OrderBy(x => x).ToList(),
        };
    }

    /// <summary>(plugin, number of face meshes) for each facegeom folder inside an archive.</summary>
    internal static IEnumerable<(string Plugin, int Count)> ArchiveFaceFolders(string archivePath)
    {
        var folders = BsaFolderReader.TryRead(archivePath);
        if (folders == null)
        {
            // Unusual archive: fall back to listing its files.
            foreach (var g in ArchiveHelper.FaceGenEntries(archivePath)
                         .Select(e => FaceGenPaths.TryParse(e, out var p, out _, out var isNif) && isNif ? p : null)
                         .OfType<string>().GroupBy(p => p, StringComparer.OrdinalIgnoreCase))
                yield return (g.Key, g.Count());
            yield break;
        }
        var prefix = FaceGenPaths.GeomFolder + @"\";
        foreach (var f in folders)
        {
            var name = f.Name.Replace('/', '\\');
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var plugin = name[prefix.Length..];
            if (plugin.Length > 0 && !plugin.Contains('\\') && f.FileCount > 0) yield return (plugin, f.FileCount);
        }
    }

    /// <summary>Without MO2 everything sits in Data, so each plugin becomes its own row.</summary>
    private static IReadOnlyList<ModFolder> DataFolderPseudoMods(GameSetup setup)
    {
        var dataFiles = GameSetup.ListTopLevelFiles(setup.DataFolder);
        var result = new List<ModFolder>();
        int priority = setup.LoadOrder.Count;
        foreach (var key in setup.LoadOrder)
        {
            var plugin = key.FileName.String;
            if (PluginNames.IsBaseMaster(plugin)) { priority--; continue; }
            result.Add(new ModFolder
            {
                Name = plugin,
                Path = setup.DataFolder,
                Priority = priority--,
                IsGameDataFolder = true,
                TopLevelFiles = dataFiles.Where(f => f.Equals(plugin, StringComparison.OrdinalIgnoreCase) ||
                                                     (f.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) && ArchiveHelper.IsOwnedBy(f, plugin))).ToList(),
            });
        }
        return result;
    }
}
