using System.Text;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.Core.Logging;

/// <summary>
/// NAM.log in NAM's saved-files folder: the one log for NAM and its game part, which adds to the same file each time
/// Skyrim starts. Until NAM has opened it, lines wait in memory (or go in the folder if NAM closes first).
/// </summary>
public static class NamLog
{
    public const string FileName = "NAM.log";

    // Past MaxSize the oldest part goes, down to KeepSize.
    private const long MaxSize = 4_000_000;
    private const int KeepSize = 2_000_000;

    private static readonly object Lock = new();
    private static readonly List<string> Waiting = [];
    private static string? _file;

    /// <summary>NAM.log in this folder; lines written so far go in.</summary>
    public static void UseFolder(string folder)
    {
        lock (Lock)
        {
            var file = Path.Combine(folder, FileName);
            if (file.Equals(_file, StringComparison.OrdinalIgnoreCase)) return;
            _file = file;
            try
            {
                Directory.CreateDirectory(folder);
                Trim(file);
            }
            catch { }
            Append(Waiting);
            Waiting.Clear();
        }
    }

    public static void Write(string line)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [NAM] {line}";
        lock (Lock)
        {
            if (_file == null) Waiting.Add(text);
            else Append([text]);
        }
    }

    /// <summary>Lines still waiting go in NAM.log in the saved-files folder (NAM is closing before it opened it).</summary>
    public static void Flush()
    {
        lock (Lock)
        {
            if (_file != null || Waiting.Count == 0) return;
            _file = Path.Combine(StorageLocation.Current, FileName);
            try { Directory.CreateDirectory(StorageLocation.Current); } catch { }
            Append(Waiting);
            Waiting.Clear();
        }
    }

    private static void Append(IEnumerable<string> lines)
    {
        try
        {
            // Shared, as the game part may have the file open at the same time.
            using var stream = new FileStream(_file!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var line in lines) writer.WriteLine(line);
        }
        catch { }
    }

    private static void Trim(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= MaxSize) return;
        var bytes = File.ReadAllBytes(file);
        var start = bytes.Length - KeepSize;
        while (start < bytes.Length && bytes[start - 1] != '\n') start++;
        File.WriteAllBytes(file, bytes[start..]);
    }
}
