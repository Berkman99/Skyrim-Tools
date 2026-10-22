using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.Core.Mugshots;

public interface IMugshotGenerator
{
    string Name { get; }

    /// <summary>Renders the look to a PNG. Returns false if it couldn't.</summary>
    Task<bool> GenerateAsync(NpcLook look, string outputPng, CancellationToken ct);
}

/// <summary>
/// Finds a picture for a look, in this order: your own mugshot folder, FaceFinder, then generated.
/// Folder layout (same as NPC Plugin Chooser 2 / EasyNPC): &lt;Root&gt;\&lt;Mod name&gt;\&lt;Plugin.esp&gt;\&lt;FormID&gt;.png
/// </summary>
public sealed class MugshotService
{
    /// <summary>Shown for an NPC whose original look has no known source (nothing has a face for it).</summary>
    public const string OriginalLookName = "Original look";
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    private (string Root, string[] Folders)? _folders;

    private readonly AppSettings _settings;
    private readonly FaceFinderClient _faceFinder = new();
    private readonly ConcurrentDictionary<FormKey, Task<IReadOnlyList<FaceFinderFace>>> _faceFinderLookups = new();

    public IMugshotGenerator? Generator { get; set; }

    public MugshotService(AppSettings settings)
    {
        _settings = settings;
        // Pictures from before each renderer had its own folder (and the old framing) are dropped once.
        try
        {
            var old = Path.Combine(AppSettings.CacheFolder, "Generated");
            if (Directory.Exists(old)) Directory.Delete(old, true);
        }
        catch { }
    }

    /// <summary>Each renderer keeps its own pictures, so switching renderer never shows the other one's.</summary>
    private string GeneratedFolder => Path.Combine(AppSettings.CacheFolder, "Generated pictures", _settings.Generator.ToString());

    public static bool FaceFinderAvailable => FaceFinderClient.IsAvailable;

    private string FolderNameFor(string modName) => _settings.MugshotFolderLinks.GetValueOrDefault(modName) ?? modName;

    private static string RelativeImageBase(string folder, FormKey npc) =>
        Path.Combine(folder, npc.ModKey.FileName.String, npc.ID.ToString("X8"));

    /// <summary>The folder a mod's mugshots are in (its own name unless linked to another folder).</summary>
    public string MugshotFolderOf(string modName) => FolderNameFor(modName);

    /// <summary>Every folder in your mugshot folder that has a picture of this NPC, with that picture.</summary>
    public List<(string Folder, string Picture)> FindAllMugshots(FormKey npc)
    {
        var result = new List<(string, string)>();
        var root = _settings.MugshotsFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return result;
        if (_folders?.Root != root)
        {
            try { _folders = (root, Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().Order().ToArray()); }
            catch { return result; }
        }
        foreach (var folder in _folders.Value.Folders)
            if (FindImage(Path.Combine(root, RelativeImageBase(folder, npc))) is { } picture)
                result.Add((folder, picture));
        return result;
    }

    /// <summary>Only looks at files already on disk (fast; safe to call for every tile).</summary>
    public string? FindExisting(string modName, FormKey npc)
    {
        if (modName == OriginalLookName) return null;
        if (!string.IsNullOrWhiteSpace(_settings.MugshotsFolder) && Directory.Exists(_settings.MugshotsFolder) &&
            FindImage(Path.Combine(_settings.MugshotsFolder, RelativeImageBase(FolderNameFor(modName), npc))) is { } curated)
            return curated;
        if (FaceFinderAvailable && _settings.UseFaceFinder &&
            FindImage(Path.Combine(AppSettings.CacheFolder, "FaceFinder", RelativeImageBase(Safe(modName), npc))) is { } ff)
            return ff;
        if (_settings.Generator == MugshotGeneratorKind.Off) return null;
        return FindImage(Path.Combine(GeneratedFolder, RelativeImageBase(Safe(modName), npc)));
    }

    /// <summary>Full lookup, including downloading from FaceFinder or generating, as allowed by settings.</summary>
    public async Task<string?> GetAsync(string modName, NpcLook? look, FormKey npc, CancellationToken ct)
    {
        if (modName == OriginalLookName) return null;
        var existing = FindExisting(modName, npc);
        if (existing != null) return existing;

        if (FaceFinderAvailable && _settings.UseFaceFinder)
        {
            var downloaded = await TryFaceFinderAsync(modName, npc, ct);
            if (downloaded != null) return downloaded;
        }

        if (look != null && Generator != null && _settings.Generator != MugshotGeneratorKind.Off)
        {
            var output = Path.Combine(GeneratedFolder, RelativeImageBase(Safe(modName), npc)) + ".png";
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            try
            {
                if (await Generator.GenerateAsync(look, output, ct) && File.Exists(output)) return output;
                Log($"{Generator.Name} produced no picture for {npc} ({modName}).");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log($"{Generator.Name} failed for {npc} ({modName}): {ex}"); }
        }
        return null;
    }

    private async Task<string?> TryFaceFinderAsync(string modName, FormKey npc, CancellationToken ct)
    {
        var faces = await _faceFinderLookups.GetOrAdd(npc, k => _faceFinder.GetFacesForNpcAsync(k, CancellationToken.None));
        var wanted = new[] { modName, FolderNameFor(modName) }.Select(Normalize).ToHashSet();
        var match = faces.FirstOrDefault(f => wanted.Contains(Normalize(f.ModName)))
                    ?? faces.FirstOrDefault(f => wanted.Any(w => w.Length > 5 && (Normalize(f.ModName).Contains(w) || w.Contains(Normalize(f.ModName)))));
        if (match == null) return null;

        var ext = Path.GetExtension(new Uri(match.ImageUrl).AbsolutePath);
        if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) ext = ".png";
        var dest = Path.Combine(AppSettings.CacheFolder, "FaceFinder", RelativeImageBase(Safe(modName), npc)) + ext;
        return await _faceFinder.DownloadAsync(match, dest, ct) ? dest : null;
    }

    private static string? FindImage(string pathWithoutExtension)
    {
        foreach (var ext in ImageExtensions)
        {
            var p = pathWithoutExtension + ext;
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Appends to "Mugshot Cache\mugshots.log" so failed pictures can be diagnosed.</summary>
    public static void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.CacheFolder);
            File.AppendAllText(Path.Combine(AppSettings.CacheFolder, "mugshots.log"), $"[{DateTime.Now:u}] {line}{System.Environment.NewLine}");
        }
        catch { }
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
