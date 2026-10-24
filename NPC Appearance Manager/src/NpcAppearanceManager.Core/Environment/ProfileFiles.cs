using System.Text;
using NpcFaceManager.Core.Build;

namespace NpcFaceManager.Core.Environment;

public sealed record MergeChanges(
    IReadOnlyCollection<string> ModsToDisable,
    IReadOnlyCollection<string> PluginsToDeactivate,
    IReadOnlyCollection<string> ModsToEnable,
    IReadOnlyCollection<DeactivatedPlugin> PluginsToActivate,
    string OutputModName,
    string OutputPlugin)
{
    /// <summary>What a build changes, including undoing parts of the previous build that aren't merged any more.</summary>
    public static MergeChanges From(BuildReport report, MergeMarker? previous, string outputModName, string outputPlugin)
    {
        var disable = report.ModsSafeToDisable.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deactivate = report.PluginsToDeactivate.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MergeChanges(
            report.ModsSafeToDisable,
            report.PluginsToDeactivate,
            previous?.DisabledMods.Where(m => !disable.Contains(m)).ToList() ?? [],
            previous?.DeactivatedPlugins.Where(p => !deactivate.Contains(p.Plugin)).ToList() ?? [],
            outputModName,
            outputPlugin);
    }
}

public enum ModListState { Enabled, Disabled, Unmanaged }

public sealed record ModListEntry(string Name, ModListState State)
{
    public bool IsSeparator => Name.EndsWith("_separator", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Reads and edits the text files inside an MO2 profile folder.</summary>
public static class ProfileFiles
{
    /// <summary>
    /// Reads modlist.txt. MO2 writes it highest-priority first; the returned list keeps that order.
    /// </summary>
    public static List<ModListEntry> ReadModList(string profileDir)
    {
        var path = Path.Combine(profileDir, "modlist.txt");
        var result = new List<ModListEntry>();
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.TrimEnd();
            if (line.Length < 2 || line.StartsWith('#')) continue;
            var state = line[0] switch
            {
                '+' => ModListState.Enabled,
                '-' => ModListState.Disabled,
                '*' => ModListState.Unmanaged,
                _ => (ModListState?)null,
            };
            if (state != null) result.Add(new ModListEntry(line[1..], state.Value));
        }
        return result;
    }

    /// <summary>Reads plugins.txt: every listed plugin in order, with its active flag.</summary>
    public static List<(string Plugin, bool Active)> ReadPlugins(string profileDir)
    {
        var path = Path.Combine(profileDir, "plugins.txt");
        var result = new List<(string, bool)>();
        if (!File.Exists(path)) return result;
        foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            bool active = line.StartsWith('*');
            result.Add((active ? line[1..] : line, active));
        }
        return result;
    }

    /// <summary>Backup suffix shown in MO2's "Restore backup" lists, e.g. "NAM 2026-10-22 14.30.00".</summary>
    public static string BackupSuffix(DateTime time) => "NAM " + time.ToString("yyyy-MM-dd HH.mm.ss");

    /// <summary>
    /// Backs up the profile's lists so MO2's own "Restore backup" can undo the change. MO2 restores the plugin
    /// list together with loadorder.txt and lockedorder.txt, so all of those get a backup (empty if missing).
    /// </summary>
    internal static void WriteBackups(string profileDir, DateTime time)
    {
        var suffix = "." + BackupSuffix(time);
        foreach (var name in new[] { "modlist.txt", "plugins.txt", "loadorder.txt", "lockedorder.txt" })
        {
            var path = Path.Combine(profileDir, name);
            if (File.Exists(path)) File.Copy(path, path + suffix, overwrite: true);
            else if (name != "modlist.txt") File.WriteAllText(path + suffix, "", new UTF8Encoding(false));
        }
    }

    /// <summary>
    /// Applies a build to an MO2 profile: switches the merged mods and plugins off, switches back on anything an
    /// earlier build had switched off that isn't merged any more, and enables the output mod (top priority) and
    /// plugin (last). Backups of the original files are written next to them first.
    /// </summary>
    public static void ApplyMergeResult(string profileDir, MergeChanges changes)
    {
        var modlistPath = Path.Combine(profileDir, "modlist.txt");
        var pluginsPath = Path.Combine(profileDir, "plugins.txt");
        var loadorderPath = Path.Combine(profileDir, "loadorder.txt");
        WriteBackups(profileDir, DateTime.Now);

        var disable = new HashSet<string>(changes.ModsToDisable, StringComparer.OrdinalIgnoreCase);
        var enable = new HashSet<string>(changes.ModsToEnable, StringComparer.OrdinalIgnoreCase);
        var lines = File.Exists(modlistPath) ? File.ReadAllLines(modlistPath, Encoding.UTF8).ToList() : [];
        var output = new List<string>();
        int insertAt = 0;
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.StartsWith('#')) { output.Add(line); insertAt = output.Count; continue; }
            if (line.Length > 1 && line[1..].Equals(changes.OutputModName, StringComparison.OrdinalIgnoreCase)) continue;
            if (line.StartsWith('+') && disable.Contains(line[1..])) line = "-" + line[1..];
            else if (line.StartsWith('-') && enable.Contains(line[1..])) line = "+" + line[1..];
            output.Add(line);
        }
        output.Insert(insertAt, "+" + changes.OutputModName);
        File.WriteAllLines(modlistPath, output, new UTF8Encoding(false));

        var deactivate = new HashSet<string>(changes.PluginsToDeactivate, StringComparer.OrdinalIgnoreCase);
        var plugins = (File.Exists(pluginsPath) ? File.ReadAllLines(pluginsPath, Encoding.UTF8).ToList() : [])
            .Where(l => !l.TrimStart('*').Equals(changes.OutputPlugin, StringComparison.OrdinalIgnoreCase))
            .Select(l => l.StartsWith('*') && deactivate.Contains(l[1..]) ? l[1..] : l)
            .ToList();
        foreach (var p in changes.PluginsToActivate.Where(p => !deactivate.Contains(p.Plugin)))
        {
            int existing = plugins.FindIndex(l => l.TrimStart('*').Equals(p.Plugin, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0) { plugins[existing] = "*" + p.Plugin; continue; }
            int after = p.After == null ? -1 : plugins.FindIndex(l => l.TrimStart('*').Equals(p.After, StringComparison.OrdinalIgnoreCase));
            plugins.Insert(after >= 0 ? after + 1 : plugins.Count, "*" + p.Plugin);
        }
        plugins.Add("*" + changes.OutputPlugin);
        File.WriteAllLines(pluginsPath, plugins, new UTF8Encoding(false));

        if (File.Exists(loadorderPath))
        {
            var lo = File.ReadAllLines(loadorderPath, Encoding.UTF8)
                .Where(l => !l.Equals(changes.OutputPlugin, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var p in changes.PluginsToActivate.Where(p => !lo.Contains(p.Plugin, StringComparer.OrdinalIgnoreCase)))
            {
                int after = p.After == null ? -1 : lo.FindIndex(l => l.Equals(p.After, StringComparison.OrdinalIgnoreCase));
                lo.Insert(after >= 0 ? after + 1 : lo.Count, p.Plugin);
            }
            lo.Add(changes.OutputPlugin);
            File.WriteAllLines(loadorderPath, lo, new UTF8Encoding(false));
        }
    }
}
