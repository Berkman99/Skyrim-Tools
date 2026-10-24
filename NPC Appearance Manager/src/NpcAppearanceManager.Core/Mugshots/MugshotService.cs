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
/// Finds a picture for a look, in this order: your own mugshot folder, FaceFinder (loaded from the web when shown,
/// never saved), then generated.
/// Folder layout (same as NPC Plugin Chooser 2 / EasyNPC): &lt;Root&gt;\&lt;Mod name&gt;\&lt;Plugin.esp&gt;\&lt;FormID&gt;.png
/// </summary>
public sealed class MugshotService
{
    /// <summary>Shown for an NPC whose original look has no known source (nothing has a face for it).</summary>
    public const string OriginalLookName = "Original look";
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp", ".bmp"];
    private (string Root, string[] Folders)? _folders;

    private readonly AppSettings _settings;

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

    /// <summary>FaceFinder's whole list, once it's loaded or downloaded (pictures then need no per-NPC lookups).</summary>
    public FaceFinderCatalog? Catalog { get; set; }

    /// <summary>How many pictures the current renderer has made so far.</summary>
    public int GeneratedCount =>
        Directory.Exists(GeneratedFolder) ? Directory.EnumerateFiles(GeneratedFolder, "*.png", SearchOption.AllDirectories).Count() : 0;

    /// <summary>
    /// Deletes the pictures the current renderer made, so they're made again. Your mugshots folder and FaceFinder
    /// pictures are never touched.
    /// </summary>
    public void ClearGenerated()
    {
        FailedRenders.Clear(); // they're tried again too (the list is a file in the folder deleted below)
        if (!Directory.Exists(GeneratedFolder)) return;
        // A picture still being made can add or hold a file while this runs, so files are deleted one by one and
        // anything in use or already gone is skipped instead of stopping everything.
        foreach (var file in Directory.EnumerateFiles(GeneratedFolder, "*", SearchOption.AllDirectories).ToList())
        {
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        try { Directory.Delete(GeneratedFolder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Renders that produced no picture, kept in "failed.txt" in the renderer's folder so they aren't tried on every
    // launch. "Generate pictures again" deletes that folder, so they're tried again then.
    private ConcurrentDictionary<string, bool>? _failedRenders;
    private MugshotGeneratorKind _failedRendersFor;
    private string FailedRendersFile => Path.Combine(GeneratedFolder, "failed.txt");

    private ConcurrentDictionary<string, bool> FailedRenders
    {
        get
        {
            if (_failedRenders != null && _failedRendersFor == _settings.Generator) return _failedRenders;
            var set = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FailedRendersFile))
                    foreach (var line in File.ReadAllLines(FailedRendersFile)) set.TryAdd(line, true);
            }
            catch (IOException) { }
            _failedRendersFor = _settings.Generator;
            return _failedRenders = set;
        }
    }

    private static string FailKey(string modName, FormKey npc) => $"{modName}|{npc}";

    private readonly object _failedFileLock = new();

    private void RememberFailedRender(string modName, FormKey npc)
    {
        var key = FailKey(modName, npc);
        if (!FailedRenders.TryAdd(key, true)) return;
        lock (_failedFileLock)
        {
            try
            {
                Directory.CreateDirectory(GeneratedFolder);
                File.AppendAllText(FailedRendersFile, key + System.Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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

    /// <summary>Your mugshots folder only.</summary>
    private string? FindMugshot(string modName, FormKey npc)
    {
        var root = _settings.MugshotsFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return null;
        if (FindImage(Path.Combine(root, RelativeImageBase(FolderNameFor(modName), npc))) is { } picture) return picture;
        // Skyrim Special Edition's looks: NPC Plugin Chooser 2 keeps them in "Skyrim" and "Creation Club".
        if (modName != OriginalSources.BaseGameName) return null;
        foreach (var folder in OriginalSources.OlderBaseGameFolders)
            if (FindImage(Path.Combine(root, RelativeImageBase(folder, npc))) is { } older) return older;
        return null;
    }

    private string? FindGenerated(string modName, FormKey npc) =>
        _settings.Generator == MugshotGeneratorKind.Off ? null : FindImage(Path.Combine(GeneratedFolder, RelativeImageBase(Safe(modName), npc)));

    /// <summary>Only looks at files already on disk: your mugshots folder, then pictures rendered before.</summary>
    public string? FindExisting(string modName, FormKey npc) =>
        modName == OriginalLookName ? null : FindMugshot(modName, npc) ?? FindGenerated(modName, npc);

    /// <summary>
    /// The look has a picture already, or never gets rendered: a mugshot, a FaceFinder face, one rendered before,
    /// or a render that failed before (tried again only after "Generate pictures again"). Quick.
    /// </summary>
    public bool HasPicture(string modName, NpcLook? look, FormKey npc) =>
        modName == OriginalLookName || FindMugshot(modName, npc) != null ||
        (_settings.UseFaceFinder && FindFaceFinderFace(modName, look, npc) != null) ||
        FindGenerated(modName, npc) != null || FailedRenders.ContainsKey(FailKey(modName, npc));

    /// <summary>
    /// A FaceFinder picture not saved yet is its web address: the tile that shows it downloads and saves it
    /// (<see cref="SaveWebPictureAsync"/>), like NPC Plugin Chooser 2 does.
    /// </summary>
    public static bool IsWebPicture(string picture) =>
        picture.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || picture.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where FaceFinder pictures are saved: Mugshot Cache\FaceFinder\&lt;mod&gt;\&lt;plugin&gt;\&lt;FormID&gt;.webp.</summary>
    public static string FaceFinderPictureFolder => Path.GetDirectoryName(FaceFinderCatalog.FilePath)!;

    /// <summary>The saved picture's path for a FaceFinder face (whether it's saved yet or not).</summary>
    public static string? FaceFinderPicturePath(FaceFinderFace face)
    {
        if (face.NpcKey is not { } key) return null;
        var colon = key.IndexOf(':');
        var ext = Path.GetExtension(Uri.TryCreate(face.ImageUrl, UriKind.Absolute, out var uri) ? uri.AbsolutePath : "");
        if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) ext = ".webp";
        return Path.Combine(FaceFinderPictureFolder, SafeFolder(face.ModName), SafeFolder(key[(colon + 1)..]), "00" + key[..colon]) + ext;
    }

    /// <summary>A file or folder name Windows accepts: no invalid characters, and no trailing spaces or dots.</summary>
    public static string SafeFolder(string name)
    {
        var safe = Safe(name).Trim().TrimEnd('.');
        return safe.Length == 0 ? "_" : safe;
    }

    // Web addresses handed out by GetAsync/GetOnline, so the tile's download knows where to save the picture.
    private readonly ConcurrentDictionary<string, FaceFinderFace> _webFaces = new();

    private string? FaceFinderPicture(FaceFinderFace face)
    {
        if (FaceFinderPicturePath(face) is { } saved && File.Exists(saved)) return saved;
        _webFaces[face.ImageUrl] = face;
        return face.ImageUrl;
    }

    /// <summary>
    /// A picture already on disk, found without waiting: your mugshot, a saved FaceFinder picture, or one rendered
    /// before (in that order). Null when it still has to be downloaded or rendered.
    /// </summary>
    public string? FindSaved(string modName, NpcLook? look, FormKey npc)
    {
        if (modName == OriginalLookName) return null;
        if (FindMugshot(modName, npc) is { } mugshot) return mugshot;
        if (_settings.UseFaceFinder && FindFaceFinderFace(modName, look, npc) is { } face)
            return FaceFinderPicturePath(face) is { } saved && File.Exists(saved) ? saved : null;
        return FindGenerated(modName, npc);
    }

    /// <summary>A face from a mod you don't have: its mugshot or saved FaceFinder picture, if either is on disk.</summary>
    public string? FindSavedOnline(FaceFinderFace face, FormKey npc) =>
        FindMugshot(face.ModName, npc) ??
        (_settings.UseFaceFinder && FaceFinderPicturePath(face) is { } saved && File.Exists(saved) ? saved : null);

    /// <summary>
    /// The picture for a look, in this order: your mugshots folder, FaceFinder (a saved picture, or the web address
    /// of one not saved yet), a picture rendered before, or a new render. A look FaceFinder has a face for is never
    /// rendered, and a render that failed isn't tried again until "Generate pictures again".
    /// </summary>
    public async Task<string?> GetAsync(string modName, NpcLook? look, FormKey npc, CancellationToken ct)
    {
        if (modName == OriginalLookName) return null;
        if (FindMugshot(modName, npc) is { } mugshot) return mugshot;
        if (_settings.UseFaceFinder && FindFaceFinderFace(modName, look, npc) is { } face) return FaceFinderPicture(face);
        if (FindGenerated(modName, npc) is { } generated) return generated;

        if (look != null && Generator != null && _settings.Generator != MugshotGeneratorKind.Off && !FailedRenders.ContainsKey(FailKey(modName, npc)))
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
            RememberFailedRender(modName, npc);
        }
        return null;
    }

    /// <summary>The picture of a face from a mod you don't have: your mugshots folder first, else FaceFinder's address.</summary>
    public string? GetOnline(FaceFinderFace face, FormKey npc) =>
        FindMugshot(face.ModName, npc) ?? (_settings.UseFaceFinder ? FaceFinderPicture(face) : null);

    /// <summary>The face FaceFinder's list has for this look: the mod found by its Nexus id first, then by name.</summary>
    private FaceFinderFace? FindFaceFinderFace(string modName, NpcLook? look, FormKey npc)
    {
        var catalog = Catalog;
        if (catalog == null) return null;
        var faces = catalog.FacesFor(npc);
        if (faces.Count == 0) return null;
        // The game's own look: FaceFinder's "Skyrim Special Edition" (or its Children / Creation Club lists).
        if (modName == OriginalSources.BaseGameName)
            return faces.Where(f => catalog.Mod(f.ModId) is { } m && FaceFinderCatalog.IsBaseGame(m))
                .OrderBy(f => f.ModName.Equals(OriginalSources.BaseGameName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
        if (look?.Source.Mod.NexusId is { } nexus && faces.FirstOrDefault(f => catalog.Mod(f.ModId)?.NexusId == nexus) is { } byId)
            return byId;
        // Otherwise only the exact same name, and only when the Nexus ids don't say they're different mods. A name
        // that merely contains another (Tragedian's "...Interesting NPCs - 3DNPC Followers Reimagined" contains
        // "Interesting NPCs (3DNPC)") is a different mod, so its picture would be the wrong face.
        var wanted = new[] { modName, FolderNameFor(modName) }.Select(Normalize).ToHashSet();
        var ownNexus = look?.Source.Mod.NexusId;
        return faces.FirstOrDefault(f => wanted.Contains(Normalize(f.ModName)) &&
            (ownNexus == null || catalog.Mod(f.ModId)?.NexusId is not { } theirs || theirs == ownNexus));
    }

    /// <summary>Downloads a FaceFinder picture and saves it, so it's there straight away next time. Returns the file.</summary>
    public async Task<string?> SaveWebPictureAsync(string url, CancellationToken ct)
    {
        if (!_webFaces.TryGetValue(url, out var face) || FaceFinderPicturePath(face) is not { } path) return null;
        return await SaveFaceFinderPictureAsync(face, path, ct) ? path : null;
    }

    /// <summary>Downloads one FaceFinder picture to its saved place (also used by "Download all FaceFinder pictures").</summary>
    public static async Task<bool> SaveFaceFinderPictureAsync(FaceFinderFace face, string path, CancellationToken ct)
    {
        if (File.Exists(path)) return true;
        var bytes = await FaceFinderClient.GetImageAsync(face.ImageUrl, ct);
        if (bytes == null) return false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".part";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (IOException) { return File.Exists(path); }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>How many FaceFinder pictures are saved, and their size.</summary>
    public static (int Files, long Bytes) SavedFaceFinderPictures()
    {
        int files = 0; long bytes = 0;
        foreach (var dir in SavedFaceFinderFolders())
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                files++;
                try { bytes += new FileInfo(file).Length; } catch (IOException) { }
            }
        return (files, bytes);
    }

    /// <summary>Deletes the saved pictures of these faces (FaceFinder changed them), so they're downloaded again.</summary>
    public static int DeleteSavedPictures(IEnumerable<FaceFinderFace> faces)
    {
        int deleted = 0;
        foreach (var face in faces)
        {
            if (FaceFinderPicturePath(face) is not { } path || !File.Exists(path)) continue;
            try { File.Delete(path); deleted++; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }

    /// <summary>Deletes every saved FaceFinder picture (FaceFinder's list of faces stays).</summary>
    public void DeleteSavedFaceFinderPictures()
    {
        foreach (var dir in SavedFaceFinderFolders())
        {
            try { Directory.Delete(dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The \\?\ form also reaches folders an older version named with a trailing space.
    private static IEnumerable<string> SavedFaceFinderFolders()
    {
        var root = FaceFinderPictureFolder;
        if (!Directory.Exists(root)) return [];
        try { return Directory.GetDirectories(@"\\?\" + Path.GetFullPath(root)); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
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
