using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4.Streams;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>
/// A forgiving Skyrim BSA reader, used when the regular one gives up on an archive. It reads the tables in order
/// like the game does, so a wrong length in the header (e.g. a packer that counted characters instead of bytes
/// for non-English file names) doesn't stop it.
/// </summary>
public static class BsaReader
{
    private const uint IncludeDirectoryNames = 0x1;
    private const uint IncludeFileNames = 0x2;
    private const uint CompressedByDefault = 0x4;
    private const uint EmbedFileNames = 0x100;
    private const uint CompressionToggle = 0x40000000;

    public static IReadOnlyList<ArchiveEntry> Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        using var br = new BinaryReader(fs);
        if (br.ReadUInt32() != 0x00415342) throw new InvalidDataException("Not a BSA"); // "BSA\0"
        uint version = br.ReadUInt32();
        if (version != 104 && version != 105) throw new InvalidDataException($"Unsupported BSA version {version}");
        br.ReadUInt32(); // header size (36)
        uint flags = br.ReadUInt32();
        int folderCount = br.ReadInt32();
        int fileCount = br.ReadInt32();
        br.ReadUInt32(); // total folder name length
        br.ReadUInt32(); // total file name length (not trusted)
        br.ReadUInt32(); // file flags
        if ((flags & IncludeDirectoryNames) == 0 || (flags & IncludeFileNames) == 0)
            throw new InvalidDataException("BSA has no file names");

        var counts = new int[folderCount];
        for (int i = 0; i < folderCount; i++)
        {
            br.ReadUInt64(); // name hash
            counts[i] = br.ReadInt32();
            if (version == 105) { br.ReadUInt32(); br.ReadUInt64(); }
            else br.ReadUInt32();
        }

        var records = new List<(string Folder, uint Size, uint Offset)>(fileCount);
        foreach (var count in counts)
        {
            int len = br.ReadByte();
            var folder = Latin1(br.ReadBytes(len)).TrimEnd('\0');
            for (int i = 0; i < count; i++)
            {
                br.ReadUInt64(); // name hash
                records.Add((folder, br.ReadUInt32(), br.ReadUInt32()));
            }
        }

        var entries = new List<ArchiveEntry>(records.Count);
        var name = new List<byte>(64);
        foreach (var (folder, size, offset) in records)
        {
            name.Clear();
            for (int b = br.ReadByte(); b != 0; b = br.ReadByte()) name.Add((byte)b);
            var fileName = Latin1(name.ToArray());
            bool compressed = ((flags & CompressedByDefault) != 0) != ((size & CompressionToggle) != 0);
            var dataSize = size & 0x3FFFFFFF;
            entries.Add(new ArchiveEntry(folder.Length == 0 ? fileName : folder + "\\" + fileName,
                () => Open(path, version, (flags & EmbedFileNames) != 0, compressed, offset, dataSize)));
        }
        return entries;
    }

    private static Stream Open(string path, uint version, bool embedNames, bool compressed, uint offset, uint size)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var br = new BinaryReader(fs);
        fs.Position = offset;
        long remaining = size;
        if (embedNames)
        {
            int len = br.ReadByte();
            fs.Position += len;
            remaining -= len + 1;
        }
        if (!compressed) return new MemoryStream(br.ReadBytes((int)remaining));

        int original = br.ReadInt32();
        var packed = new MemoryStream(br.ReadBytes((int)(remaining - 4)));
        using Stream decoder = version == 105 ? LZ4Stream.Decode(packed) : new ZLibStream(packed, CompressionMode.Decompress);
        var result = new MemoryStream(original);
        decoder.CopyTo(result);
        result.Position = 0;
        return result;
    }

    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);
}
