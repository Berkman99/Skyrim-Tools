using System.Text.Json;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.Core.Build;

/// <summary>
/// No mod manager: the NAM merge copied into Skyrim's Data folder. Every file it replaces is backed up first, and this
/// record (next to the program) lists what went in, so "Remove NAM merge" can put Data back exactly as it was.
/// </summary>
public sealed class DataInstall
{
    public const string FileName = "NAM merge in Data.json";

    public static string RecordPath => Path.Combine(AppSettings.AppFolder, FileName);
    public static string BackupFolder => Path.Combine(AppSettings.AppFolder, "Data backup");

    public string DataFolder { get; set; } = "";
    public string Plugin { get; set; } = "";

    /// <summary>Data-relative files the NAM merge put in Data.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>Those of <see cref="Files"/> that replaced a file, which is kept in <see cref="BackupFolder"/>.</summary>
    public List<string> Backups { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The NAM merge in this Data folder, or null when there's none.</summary>
    public static DataInstall? Read(string dataFolder)
    {
        try
        {
            if (!File.Exists(RecordPath)) return null;
            var install = JsonSerializer.Deserialize<DataInstall>(File.ReadAllText(RecordPath));
            return install != null && Path.GetFullPath(install.DataFolder).Equals(Path.GetFullPath(dataFolder), StringComparison.OrdinalIgnoreCase)
                ? install : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>For reading Data as the mods left it: each NAM merge file → its backup, or null when it replaced nothing.</summary>
    public IReadOnlyDictionary<string, string?> Replacements()
    {
        var backups = Backups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Files) result[f] = backups.Contains(f) ? Path.Combine(BackupFolder, f) : null;
        return result;
    }

    /// <summary>
    /// Copies a built NAM merge into Data, backing up each file it replaces, and turns its plugin on at the end of the
    /// load order. An older NAM merge must be removed first.
    /// </summary>
    public static DataInstall Install(string outputFolder, string dataFolder, string plugin, IProgress<string>? progress = null)
    {
        var install = new DataInstall { DataFolder = dataFolder, Plugin = plugin };
        try
        {
            int n = 0;
            foreach (var file in Directory.EnumerateFiles(outputFolder, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(outputFolder, file);
                if (!rel.Contains('\\') && (MergeMarker.IsMarkerFile(rel) || rel.Equals("meta.ini", StringComparison.OrdinalIgnoreCase))) continue;
                var target = Path.Combine(dataFolder, rel);
                if (File.Exists(target))
                {
                    var backup = Path.Combine(BackupFolder, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                    File.Copy(target, backup, overwrite: true);
                    install.Backups.Add(rel);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                // A copy, not a move: Vortex-style hard links in Data would otherwise change the mod's own file too.
                if (File.Exists(target)) File.Delete(target);
                File.Copy(file, target);
                install.Files.Add(rel);
                if (++n % 200 == 0) progress?.Report($"Copying the NAM merge into Data... ({n:N0} files)");
            }
        }
        finally { install.Save(); } // even a half-done copy can be undone
        PluginsTxt.SetActive(plugin, true);
        return install;
    }

    /// <summary>Puts Data back as it was before the NAM merge: its files deleted, the replaced ones restored, its plugin off.</summary>
    public void Remove(IProgress<string>? progress = null)
    {
        var backups = Backups.ToHashSet(StringComparer.OrdinalIgnoreCase);
        progress?.Report("Removing the NAM merge from Data...");
        foreach (var rel in Files)
        {
            var target = Path.Combine(DataFolder, rel);
            if (File.Exists(target)) File.Delete(target);
            var backup = Path.Combine(BackupFolder, rel);
            if (backups.Contains(rel) && File.Exists(backup)) File.Move(backup, target);
        }
        PluginsTxt.SetActive(Plugin, false);
        File.Delete(RecordPath);
        if (Directory.Exists(BackupFolder)) Directory.Delete(BackupFolder, true);
    }

    private void Save() => File.WriteAllText(RecordPath, JsonSerializer.Serialize(this, Options));
}

/// <summary>Skyrim's own plugin list (what the game loads without a mod manager).</summary>
public static class PluginsTxt
{
    public static string FilePath => Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "plugins.txt");

    /// <summary>Turns a plugin on (at the end of the list) or takes it off the list.</summary>
    public static void SetActive(string plugin, bool active)
    {
        var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).ToList() : [];
        lines.RemoveAll(l => l.TrimStart('*').Trim().Equals(plugin, StringComparison.OrdinalIgnoreCase));
        if (active) lines.Add("*" + plugin);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllLines(FilePath, lines);
    }
}
