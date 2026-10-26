using System.Collections.Concurrent;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>One file inside a BSA.</summary>
public sealed class ArchiveEntry(string path, Func<Stream> open)
{
    /// <summary>Relative path with backslashes, e.g. meshes\actors\...\00012345.nif.</summary>
    public string Path { get; } = path.Replace('/', '\\').TrimStart('\\');
    public Stream AsStream() => open();
}

/// <summary>The file list of a BSA.</summary>
public sealed class ArchiveContents
{
    private readonly Dictionary<string, ArchiveEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public ArchiveContents(IReadOnlyList<ArchiveEntry> files)
    {
        Files = files;
        foreach (var f in files) _byPath.TryAdd(f.Path, f);
    }

    public IReadOnlyList<ArchiveEntry> Files { get; }

    public ArchiveEntry? Find(string relativePath) =>
        _byPath.GetValueOrDefault(relativePath.Replace('/', '\\').TrimStart('\\'));
}

/// <summary>
/// BSA helpers. The game loads "Plugin.bsa" and "Plugin - Anything.bsa" alongside "Plugin.esp",
/// so an archive belongs to the plugin whose name it starts with.
/// </summary>
public static class ArchiveHelper
{
    private static readonly ConcurrentDictionary<string, ArchiveContents?> Readers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Broken = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// Reads an archive's file list. Damaged archives the regular reader gives up on are read by the forgiving one;
    /// if neither manages, the archive is skipped (null) and listed in <see cref="UnreadableArchives"/>.
    /// </summary>
    public static ArchiveContents? Open(string archivePath) =>
        Readers.GetOrAdd(archivePath, p =>
        {
            try
            {
                var reader = Archive.CreateReader(GameRelease.SkyrimSE, p);
                return new ArchiveContents(reader.Files.Select(f => new ArchiveEntry(f.Path, f.AsStream)).ToList());
            }
            catch { }
            try { return new ArchiveContents(BsaReader.Read(p)); }
            catch
            {
                Broken.TryAdd(p, 0);
                return null;
            }
        });

    /// <summary>Archives that couldn't be read at all since the last <see cref="ClearCache"/>.</summary>
    public static IReadOnlyList<string> UnreadableArchives => Broken.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Lists face files inside an archive (relative paths).</summary>
    public static IEnumerable<string> FaceGenEntries(string archivePath) =>
        Open(archivePath)?.Files.Select(f => f.Path).Where(p => p.Contains(@"\facegendata\", StringComparison.OrdinalIgnoreCase)) ?? [];

    public static ArchiveEntry? Find(string archivePath, string relativePath) => Open(archivePath)?.Find(relativePath);

    public static void ClearCache()
    {
        Readers.Clear();
        Broken.Clear();
    }
}
