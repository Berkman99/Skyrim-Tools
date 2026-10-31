namespace NpcAppearanceManager.Core.Settings;

/// <summary>Why NAM couldn't move its saved files. The message says what happened and that nothing was lost.</summary>
public sealed class StorageMoveException(string message) : Exception(message);

/// <summary>
/// Where NAM keeps what it saves (settings, mugshot cache, picks list, NAM.log): AppData\Local\Npc Appearance Manager,
/// or a folder you chose in Settings. A small file in the default folder says where the chosen one is, so NAM and its
/// game part both find it. Nothing is kept next to the program: mod managers replace that folder when NAM is updated.
/// </summary>
public static class StorageLocation
{
    public const string FolderName = "Npc Appearance Manager";

    /// <summary>The file in <see cref="DefaultFolder"/> that holds the chosen folder (not there while the default is used).</summary>
    public const string PointerFileName = "location.txt";

    /// <summary>Tests use a folder of their own, so the real one is never touched.</summary>
    internal static string? DefaultFolderOverride { get; set; }

    public static string DefaultFolder => DefaultFolderOverride ??
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), FolderName);

    private static string PointerFile => Path.Combine(DefaultFolder, PointerFileName);

    /// <summary>The free space on a drive (null when it can't be asked, like a network folder). Tests replace it.</summary>
    internal static Func<string, long?> FreeSpaceOf { get; set; } = root =>
        root.Length >= 2 && root[1] == ':' && new DriveInfo(root) is { IsReady: true } drive ? drive.AvailableFreeSpace : null;

    private static string? _current;

    /// <summary>The folder NAM keeps its saved files in.</summary>
    public static string Current
    {
        get => _current ??= ReadPointer();
        set => _current = value;
    }

    private static string ReadPointer()
    {
        try
        {
            if (File.Exists(PointerFile))
            {
                var text = File.ReadAllText(PointerFile).Trim();
                if (text.Length > 0 && Path.IsPathRooted(text)) return text;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return DefaultFolder;
    }

    /// <summary>
    /// What may already be in the folder you move to: the note file, and NAM.log (the game keeps adding to it where Skyrim last
    /// found it, so it can be in the old place again after a move).
    /// </summary>
    private static bool IsLeftover(string name) =>
        name.Equals(PointerFileName, StringComparison.OrdinalIgnoreCase) || name.Equals("NAM.log", StringComparison.OrdinalIgnoreCase);

    private static string Full(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);

    private static bool Inside(string folder, string parent) =>
        Full(folder).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything NAM keeps in a folder, as paths relative to it (the pointer file isn't part of it).</summary>
    private static List<string> FilesIn(string folder) => !Directory.Exists(folder) ? [] :
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f))
            .Where(f => !f.Equals(PointerFileName, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Moves everything NAM keeps in <paramref name="from"/> to <paramref name="to"/> and makes it the place NAM looks.
    /// All or nothing: with too little space or any failure, what was copied is deleted again, the old files stay where
    /// they are and <see cref="Current"/> doesn't change. The old files are only deleted once the copy is checked.
    /// </summary>
    public static void Move(string from, string to, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Run(from, to, progress, ct, storage: true);

    /// <summary>
    /// The same all-or-nothing move for any other folder NAM owns (your mugshots): nothing about where NAM keeps its
    /// saved files changes.
    /// </summary>
    public static void MoveFolder(string from, string to, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Run(from, to, progress, ct, storage: false);

    private static void Run(string from, string to, IProgress<string>? progress, CancellationToken ct, bool storage)
    {
        if (Same(from, to)) return;
        if (Inside(to, from) || Inside(from, to))
            throw new StorageMoveException("Pick a folder that isn't inside the current one (or around it). Nothing was moved.");
        if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any(e => !IsLeftover(Path.GetFileName(e))))
            throw new StorageMoveException("That folder isn't empty. Pick an empty folder, or make a new one. Nothing was moved.");

        var files = FilesIn(from);
        long needed = files.Sum(f => new FileInfo(Path.Combine(from, f)).Length);
        var root = Path.GetPathRoot(Full(to));
        if (!string.IsNullOrEmpty(root) && FreeSpaceOf(root) is { } free && free < needed + 32_000_000)
        {
            var name = root.TrimEnd('\\');
            throw new StorageMoveException($"Not enough free space on {name}. NAM needs {Size(needed)} and {name} has {Size(free)} free. Nothing was moved.");
        }

        var createdRoot = !Directory.Exists(to);
        var copied = new List<string>();
        try
        {
            Directory.CreateDirectory(to);
            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (i % 25 == 0) progress?.Report($"Moving your files... {i * 100 / Math.Max(1, files.Count)}%");
                var target = Path.Combine(to, files[i]);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(from, files[i]), target, overwrite: true);
                copied.Add(target);
            }
            foreach (var file in files)
                if (new FileInfo(Path.Combine(from, file)).Length != new FileInfo(Path.Combine(to, file)).Length)
                    throw new IOException($"{file} wasn't copied completely.");
            if (storage) SetPointer(to);
        }
        catch (Exception ex) when (ex is not StorageMoveException)
        {
            Rollback(to, createdRoot, copied);
            if (ex is OperationCanceledException) throw;
            throw new StorageMoveException($"NAM couldn't move your files: {ex.Message} Nothing was lost: NAM is still using the old folder, and what was copied was deleted again.");
        }

        if (storage) Current = to;
        // The copy is checked: the old files go. One a program still has open stays, which is no problem.
        foreach (var file in files)
            try { File.Delete(Path.Combine(from, file)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        RemoveEmptyFolders(from);
    }

    private static void SetPointer(string folder)
    {
        if (Same(folder, DefaultFolder))
        {
            if (File.Exists(PointerFile)) File.Delete(PointerFile);
            return;
        }
        Directory.CreateDirectory(DefaultFolder);
        File.WriteAllText(PointerFile, Full(folder));
    }

    private static void Rollback(string to, bool createdRoot, List<string> copied)
    {
        try
        {
            if (createdRoot && Directory.Exists(to)) { Directory.Delete(to, true); return; }
            foreach (var file in copied) File.Delete(file);
            RemoveEmptyFolders(to, keepRoot: true); // the folder you picked stays, as it was
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void RemoveEmptyFolders(string folder, bool keepRoot = false)
    {
        try
        {
            if (!Directory.Exists(folder)) return;
            foreach (var dir in Directory.GetDirectories(folder, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            if (!keepRoot && !Same(folder, DefaultFolder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>How much space the saved files take.</summary>
    public static long SizeOf(string folder) => FilesIn(folder).Sum(f => new FileInfo(Path.Combine(folder, f)).Length);

    public static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{Math.Max(1, bytes / (1L << 20))} MB";
}
