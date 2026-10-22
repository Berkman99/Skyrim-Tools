using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace NpcFaceManager.Core.Scanning;

/// <summary>
/// BSA helpers. The game loads "Plugin.bsa" and "Plugin - Anything.bsa" alongside "Plugin.esp",
/// so an archive belongs to the plugin whose name it starts with.
/// </summary>
public static class ArchiveHelper
{
    private static readonly ConcurrentDictionary<string, IArchiveReader?> Readers = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsOwnedBy(string archiveFileName, string pluginFileName)
    {
        var archiveBase = Path.GetFileNameWithoutExtension(archiveFileName);
        var pluginBase = Path.GetFileNameWithoutExtension(pluginFileName);
        return archiveBase.Equals(pluginBase, StringComparison.OrdinalIgnoreCase) ||
               archiveBase.StartsWith(pluginBase + " ", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Archives in a folder that the game would load because one of the given plugins is active.</summary>
    public static IEnumerable<string> LoadedArchives(IEnumerable<string> archiveFileNames, IReadOnlyCollection<string> activePluginNames) =>
        archiveFileNames.Where(a => activePluginNames.Any(p => IsOwnedBy(a, p)));

    public static IArchiveReader? Open(string archivePath) =>
        Readers.GetOrAdd(archivePath, p =>
        {
            try { return Archive.CreateReader(GameRelease.SkyrimSE, p); }
            catch { return null; }
        });

    /// <summary>Lists face files inside an archive (lower-case relative paths).</summary>
    public static IEnumerable<string> FaceGenEntries(string archivePath)
    {
        var reader = Open(archivePath);
        if (reader == null) yield break;
        foreach (var file in reader.Files)
        {
            var path = file.Path.Replace('/', '\\');
            if (path.Contains(@"\facegendata\", StringComparison.OrdinalIgnoreCase)) yield return path;
        }
    }

    public static IArchiveFile? Find(string archivePath, string relativePath)
    {
        var reader = Open(archivePath);
        if (reader == null) return null;
        var wanted = relativePath.Replace('/', '\\');
        var dir = Path.GetDirectoryName(wanted) ?? "";
        if (reader.TryGetFolder(dir, out var folder))
        {
            foreach (var f in folder.Files)
                if (f.Path.Replace('/', '\\').Equals(wanted, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }

    public static void ClearCache() => Readers.Clear();
}
