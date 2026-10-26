using System.Diagnostics;
using System.Globalization;
using System.Text;
using Mutagen.Bethesda.Plugins;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Scanning;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.Core.Mugshots;

/// <summary>
/// Renders pictures with the external NPC Portrait Creator program (the "legacy" renderer NPC2 also offers).
/// </summary>
public sealed class PortraitCreatorGenerator : IMugshotGenerator
{
    private readonly string _exePath;
    private readonly GameSetup _setup;
    private readonly SemaphoreSlim _slots = new(Math.Max(1, System.Environment.ProcessorCount / 4));

    // Defaults match NPC Plugin Chooser 2's Portrait Creator settings.
    private const string LightingJson =
        "{\"lights\":[{\"color\":[1.0,0.88,0.7],\"intensity\":0.65,\"type\":\"ambient\"}," +
        "{\"color\":[1.0,0.85,0.65],\"direction\":[-0.0798,-0.9964,-0.0292],\"intensity\":1.6,\"type\":\"directional\"}," +
        "{\"color\":[1.0,0.87,0.68],\"direction\":[0.1225,-0.6894,0.714],\"intensity\":0.8,\"type\":\"directional\"}]}";

    public PortraitCreatorGenerator(string exePath, GameSetup setup)
    {
        _exePath = exePath;
        _setup = setup;
    }

    public string Name => "Portrait Creator";

    public async Task<bool> GenerateAsync(NpcLook look, string outputPng, CancellationToken ct)
    {
        if (!File.Exists(_exePath)) return false;
        var nif = GetNifOnDisk(look);
        if (nif == null) return false;

        await _slots.WaitAsync(ct);
        try
        {
            var args = new StringBuilder();
            args.Append($"--headless -f \"{nif}\" -o \"{outputPng}\" ");
            args.Append($"--gamedata \"{_setup.DataFolder}\" ");
            if (!look.Source.Mod.IsGameDataFolder) args.Append($"--data \"{look.Source.Mod.Path}\" ");
            args.Append(string.Create(CultureInfo.InvariantCulture,
                $"--fov 25 --imgX 750 --imgY 750 --bgcolor \"{58 / 255f},{61 / 255f},{64 / 255f}\" "));
            args.Append("--normal-hack true --mod-fallback-textures true ");
            args.Append($"--lighting-json \"{LightingJson.Replace("\"", "\\\"")}\" ");
            args.Append(string.Create(CultureInfo.InvariantCulture, $"--pitch 2 --yaw 90 --roll 0 --head-top-offset 0 --head-bottom-offset -0.05"));

            using var process = Process.Start(new ProcessStartInfo(_exePath, args.ToString())
            {
                WorkingDirectory = Path.GetDirectoryName(_exePath)!,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process == null) return false;
            _ = process.StandardOutput.ReadToEndAsync(ct);
            _ = process.StandardError.ReadToEndAsync(ct);
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { process.Kill(true); } catch { }
                throw;
            }
            return process.ExitCode == 0 && File.Exists(outputPng);
        }
        finally { _slots.Release(); }
    }

    /// <summary>The face mesh as a real file (extracted from its archive if needed).</summary>
    private static string? GetNifOnDisk(NpcLook look)
    {
        if (look.Faces.NifLoosePath != null) return look.Faces.NifLoosePath;
        if (look.Faces.NifArchive == null) return null;
        var rel = FaceGenPaths.NifPath(look.Npc);
        var entry = ArchiveHelper.Find(look.Faces.NifArchive, rel);
        if (entry == null) return null;
        var dest = Path.Combine(AppSettings.CacheFolder, "Extracted", Safe(look.ModName), rel);
        if (!File.Exists(dest))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using var src = entry.AsStream();
            using var dst = File.Create(dest);
            src.CopyTo(dst);
        }
        return dest;
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
