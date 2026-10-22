using System.Text;

namespace NpcFaceManager.Core.Scanning;

/// <summary>
/// Reads only the folder list (names + file counts) of a Skyrim BSA. That's a handful of small reads,
/// so every archive of every mod can be checked for face files without unpacking anything.
/// </summary>
public static class BsaFolderReader
{
    public sealed record Folder(string Name, int FileCount);

    private const uint IncludeDirectoryNames = 0x1;

    /// <summary>Returns null if the file isn't a readable BSA (or doesn't store folder names).</summary>
    public static IReadOnlyList<Folder>? TryRead(string path)
    {
        try { return Read(path); }
        catch { return null; }
    }

    public static IReadOnlyList<Folder>? Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt32() != 0x00415342) return null; // "BSA\0"
        uint version = br.ReadUInt32();
        if (version != 103 && version != 104 && version != 105) return null;
        br.ReadUInt32(); // header size (36)
        uint archiveFlags = br.ReadUInt32();
        uint folderCount = br.ReadUInt32();
        br.ReadUInt32(); // file count
        br.ReadUInt32(); // total folder name length
        uint totalFileNameLength = br.ReadUInt32();
        br.ReadUInt32(); // file flags
        if ((archiveFlags & IncludeDirectoryNames) == 0) return null;

        var records = new (int Count, long Offset)[folderCount];
        for (int i = 0; i < folderCount; i++)
        {
            br.ReadUInt64(); // name hash
            int count = (int)br.ReadUInt32();
            long offset;
            if (version == 105) { br.ReadUInt32(); offset = (long)br.ReadUInt64(); }
            else offset = br.ReadUInt32();
            records[i] = (count, offset);
        }

        // Each folder's name sits at the start of its file-record block; the stored offset includes the
        // length of the file-name table, which has to be subtracted.
        var result = new List<Folder>((int)folderCount);
        foreach (var (count, offset) in records)
        {
            fs.Position = offset - totalFileNameLength;
            int len = br.ReadByte();
            var bytes = br.ReadBytes(len);
            var name = Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            result.Add(new Folder(name, count));
        }
        return result;
    }
}
