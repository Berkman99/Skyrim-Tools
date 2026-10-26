using System.Runtime.InteropServices;
using NpcAppearanceManager.Core.Scanning;

namespace NpcAppearanceManager.Core.Build;

/// <summary>
/// Copies files into the output mod, loose or out of archives. The first copy of a path wins, so callers
/// copy the most important files (the chosen faces) first. Loose files are hard-linked when possible,
/// which is instant and takes no extra disk space; it falls back to a normal copy.
/// </summary>
public sealed class AssetCopier
{
    private readonly string _outputFolder;
    private readonly IReadOnlyList<ModSummary> _overhauls;
    private readonly Dictionary<string, string> _copied = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings;
    private bool _hardLinksWork = true;

    public AssetCopier(string outputFolder, IReadOnlyList<ModSummary> overhauls, List<string> warnings)
    {
        _outputFolder = outputFolder;
        _overhauls = overhauls;
        _warnings = warnings;
    }

    public int FilesCopied => _copied.Count;

    /// <summary>Paths (relative to the output folder) of copied .nif files, for texture following.</summary>
    public IEnumerable<string> CopiedNifs => _copied.Keys.Where(k => k.EndsWith(".nif", StringComparison.OrdinalIgnoreCase));

    public bool Has(string relativePath) => _copied.ContainsKey(relativePath);

    public string OutputPath(string relativePath) => Path.Combine(_outputFolder, relativePath);

    /// <summary>
    /// Copies one file from an exact source (loose path or archive) to <paramref name="relativePath"/>.
    /// <paramref name="archiveEntry"/> is the file's path inside the archive when it differs from the destination.
    /// </summary>
    public bool CopyExact(string relativePath, string? loosePath, string? archivePath, string sourceLabel,
        bool warnOnConflict = true, string? archiveEntry = null)
    {
        relativePath = relativePath.Replace('/', '\\').TrimStart('\\');
        if (_copied.TryGetValue(relativePath, out var existing))
        {
            if (warnOnConflict && existing != sourceLabel)
                _warnings.Add($"{relativePath} is in both {existing} and {sourceLabel}. The one from {existing} was used.");
            return true;
        }
        if (loosePath != null && File.Exists(loosePath))
        {
            Prepare(relativePath);
            LinkOrCopy(loosePath, OutputPath(relativePath));
        }
        else if (archivePath != null && ArchiveHelper.Find(archivePath, archiveEntry ?? relativePath) is { } entry)
        {
            Prepare(relativePath);
            using var src = entry.AsStream();
            using var dst = File.Create(OutputPath(relativePath));
            src.CopyTo(dst);
        }
        else return false;

        _copied[relativePath] = sourceLabel;
        return true;
    }

    /// <summary>
    /// Copies a file referenced by a merged record or mesh, looking in the preferred mod first and then
    /// the other ticked overhauls by priority. Returns false if no overhaul ships it.
    /// </summary>
    public bool CopyFromOverhauls(string relativePath, ModSummary? preferred)
    {
        relativePath = relativePath.Replace('/', '\\').TrimStart('\\');
        if (_copied.ContainsKey(relativePath)) return true;

        var order = preferred == null ? _overhauls : _overhauls.Where(o => o != preferred).Prepend(preferred);
        foreach (var mod in order)
        {
            var loose = Path.Combine(mod.Mod.Path, relativePath);
            if (!mod.Mod.IsGameDataFolder && File.Exists(loose))
                return CopyExact(relativePath, loose, null, mod.Mod.Name);

            foreach (var archive in ArchiveHelper.LoadedArchives(mod.Mod.Archives, mod.Plugins.Select(p => p.FileName).ToList()))
            {
                var archivePath = Path.Combine(mod.Mod.Path, archive);
                if (ArchiveHelper.Find(archivePath, relativePath) != null)
                    return CopyExact(relativePath, null, archivePath, mod.Mod.Name);
            }
        }
        return false;
    }

    /// <summary>Copies every loose file of a mod folder (except plugins and MO2/installer files).</summary>
    public void CopyWholeFolder(string folder, string label, Func<string, bool> skip)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(folder, file);
            var top = rel.Split('\\')[0];
            if (!rel.Contains('\\') && (Environment.PluginNames.IsPlugin(rel) || rel.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) ||
                                        rel.Equals("meta.ini", StringComparison.OrdinalIgnoreCase) || MergeMarker.IsMarkerFile(rel)))
                continue;
            if (top.Equals("fomod", StringComparison.OrdinalIgnoreCase)) continue;
            if (skip(rel)) continue;
            CopyExact(rel, file, null, label, warnOnConflict: false);
        }
    }

    /// <summary>Unpacks every file of an archive into the output (the archive stops loading once its plugin is off).</summary>
    public void ExtractArchive(string archivePath, string label, Func<string, bool> skip)
    {
        var reader = ArchiveHelper.Open(archivePath);
        if (reader == null)
        {
            _warnings.Add($"Couldn't read {Path.GetFileName(archivePath)}, so its files weren't copied.");
            return;
        }
        foreach (var f in reader.Files)
        {
            var rel = f.Path.Replace('/', '\\').TrimStart('\\');
            if (_copied.ContainsKey(rel) || skip(rel)) continue;
            Prepare(rel);
            using (var src = f.AsStream())
            using (var dst = File.Create(OutputPath(rel)))
                src.CopyTo(dst);
            _copied[rel] = label;
        }
    }

    private void Prepare(string relativePath) => Directory.CreateDirectory(Path.GetDirectoryName(OutputPath(relativePath))!);

    private void LinkOrCopy(string source, string dest)
    {
        if (File.Exists(dest)) File.Delete(dest);
        if (_hardLinksWork && CreateHardLink(dest, source, IntPtr.Zero)) return;
        _hardLinksWork = false; // different drive or unsupported file system: stop trying
        File.Copy(source, dest, true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newFile, string existingFile, IntPtr securityAttributes);
}
