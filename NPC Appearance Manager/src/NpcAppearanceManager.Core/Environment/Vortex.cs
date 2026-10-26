using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NpcAppearanceManager.Core.Environment;

/// <summary>
/// What NAM can learn about Vortex without its (locked) database: Vortex writes vortex.deployment.json into the game's
/// Data folder every time it deploys. It names Vortex's mods folder and, for every deployed file, the mod it came from.
/// That's always the active Vortex profile, as Vortex redeploys when you switch.
/// </summary>
public static partial class Vortex
{
    public const string DeploymentFile = "vortex.deployment.json";

    /// <summary>Vortex's mods folder and each deployed file (Data-relative, lower case) with the mod folder it came from.</summary>
    public sealed record Deployment(string StagingPath, IReadOnlyDictionary<string, string> Files);

    public static bool HasDeployed(string gameFolder) => File.Exists(Path.Combine(gameFolder, "Data", DeploymentFile));

    public static Deployment? ReadDeployment(string gameFolder)
    {
        var path = Path.Combine(gameFolder, "Data", DeploymentFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var staging = root.GetProperty("stagingPath").GetString();
            if (string.IsNullOrWhiteSpace(staging) || !Directory.Exists(staging)) return null;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("files", out var list))
                foreach (var f in list.EnumerateArray())
                    if (f.TryGetProperty("relPath", out var rel) && f.TryGetProperty("source", out var src) &&
                        rel.GetString() is { } r && src.GetString() is { } s)
                        files[r.Replace('/', '\\')] = s;
            return new Deployment(staging, files);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string VortexAppData => Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Vortex");

    /// <summary>Whether Vortex manages Skyrim Special Edition on this PC.</summary>
    public static bool IsInstalledForSkyrim => Directory.Exists(Path.Combine(VortexAppData, "skyrimse"));

    /// <summary>
    /// Skyrim's game folder as Vortex knows it (also for installs Steam doesn't know about), from Vortex's own logs:
    /// they record the game path whenever Vortex checks its mods folder.
    /// </summary>
    public static string? FindGameFolder() => FromLogs(GamePathPattern());

    /// <summary>Vortex.exe, from Vortex's logs, or where it's normally installed.</summary>
    public static string? FindExecutable()
    {
        if (FromLogs(VortexPathPattern()) is { } dir && File.Exists(Path.Combine(dir, "Vortex.exe"))) return Path.Combine(dir, "Vortex.exe");
        var standard = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "Vortex", "Vortex.exe");
        return File.Exists(standard) ? standard : null;
    }

    private static string? FromLogs(Regex pattern)
    {
        try
        {
            if (!Directory.Exists(VortexAppData)) return null;
            foreach (var log in new DirectoryInfo(VortexAppData).GetFiles("vortex*.log").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                string? last = null;
                using var stream = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains("skyrimse", StringComparison.OrdinalIgnoreCase) && !line.Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase)) continue;
                    if (pattern.Match(line) is { Success: true } m) last = JsonSerializer.Deserialize<string>($"\"{m.Groups[1].Value}\"");
                }
                if (last != null && Directory.Exists(last)) return last;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    [GeneratedRegex("\"gamePath\":\"((?:[^\"\\\\]|\\\\.)*Skyrim Special Edition[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex GamePathPattern();

    [GeneratedRegex("\"vortexPath\":\"((?:[^\"\\\\]|\\\\.)*)\"")]
    private static partial Regex VortexPathPattern();

    /// <summary>
    /// NAM's add-on for Vortex: whenever Vortex finds the NAM merge sharing files with other mods, it sets the NAM merge
    /// to load after them, so the NAM merge wins (as it does in MO2, where it sits last) and no conflict is left open.
    /// Vortex loads add-ons when it starts. Returns true when it was added now (Vortex then needs a restart once).
    /// </summary>
    public static bool InstallHelper()
    {
        var folder = Path.Combine(VortexAppData, "plugins", "npc-appearance-manager-helper");
        var index = Path.Combine(folder, "index.js");
        bool isNew = !File.Exists(index);
        Directory.CreateDirectory(folder);
        if (isNew || File.ReadAllText(index) != HelperScript) File.WriteAllText(index, HelperScript);
        var info = Path.Combine(folder, "info.json");
        if (!File.Exists(info))
            File.WriteAllText(info, """{"name":"NPC Appearance Manager helper","author":"NPC Appearance Manager","version":"1.0.0","description":"Makes the NAM merge win its file conflicts, like MO2 placing it last."}""");
        return isNew;
    }

    private const string HelperScript = """
        'use strict';
        // NPC Appearance Manager helper: the NAM merge (a mod with NAM's marker file) loads after every mod it shares
        // files with, so it wins them, as in MO2 where it sits last.
        const { actions, selectors, util } = require('vortex-api');
        const fs = require('fs');
        const path = require('path');
        const MARKER = 'NpcAppearanceManager.output.json';

        function isNamMerge(state, gameId, mod) {
          try {
            const root = selectors.installPathForGame(state, gameId);
            return !!mod && !!mod.installationPath && fs.existsSync(path.join(root, mod.installationPath, MARKER));
          } catch (err) { return false; }
        }

        function hasRule(mod, other) {
          return (mod.rules || []).some(r => ['before', 'after', 'conflicts'].includes(r.type) && util.testModReference(other, r.reference));
        }

        function winConflicts(api) {
          const state = api.getState();
          const gameId = selectors.activeGameId(state);
          const mods = util.getSafe(state, ['persistent', 'mods', gameId], {});
          const conflicts = util.getSafe(state, ['session', 'dependencies', 'conflicts'], undefined);
          if (!gameId || !conflicts) return;
          const rules = [];
          Object.keys(conflicts).forEach(modId => {
            const nam = mods[modId];
            if (!isNamMerge(state, gameId, nam)) return;
            (conflicts[modId] || []).forEach(c => {
              const other = c.otherMod === undefined ? undefined : mods[c.otherMod.id];
              if (other === undefined || other.id === modId || hasRule(nam, other) || hasRule(other, nam)) return;
              rules.push(actions.addModRule(gameId, modId, { type: 'after', reference: { id: other.id } }));
            });
          });
          if (rules.length === 0) return;
          if (typeof util.batchDispatch === 'function') util.batchDispatch(api.store, rules);
          else rules.forEach(r => api.store.dispatch(r));
        }

        function main(context) {
          context.once(() => {
            context.api.onStateChange(['session', 'dependencies', 'conflicts'], () => winConflicts(context.api));
          });
          return true;
        }

        module.exports = { default: main };
        """;

    /// <summary>Hands an archive to Vortex to install (it enables and deploys it as usual). Starts Vortex when it's closed.</summary>
    public static bool InstallArchive(string archive)
    {
        var exe = FindExecutable();
        if (exe == null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--install-archive", archive }, UseShellExecute = false });
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// A mod's name from its Vortex folder name, plus the Nexus mod number in it. Vortex names the folder after the
    /// downloaded file: "The Ordinary Women-12376-4-1-1637004719" (Nexus file name) or
    /// "Unofficial Skyrim Special Edition Patch 266 4.3.9c 2026-09-05T22-22Z lNkz9LChN" (newer Vortex).
    /// </summary>
    public static (string Name, int? NexusId) CleanName(string folder)
    {
        if (NexusFilePattern().Match(folder) is { Success: true } a) return (a.Groups[1].Value.Trim(), int.Parse(a.Groups[2].Value));
        if (NewVortexPattern().Match(folder) is { Success: true } b) return (b.Groups[1].Value.Trim(), int.Parse(b.Groups[2].Value));
        return (folder, null);
    }

    [GeneratedRegex(@"^(.+?)-(\d+)-[0-9A-Za-z.-]*?-(\d{9,11})$")]
    private static partial Regex NexusFilePattern();

    [GeneratedRegex(@"^(.+) (\d+) \S+ \d{4}-\d{2}-\d{2}T\d{2}-\d{2}Z \S+$")]
    private static partial Regex NewVortexPattern();
}
