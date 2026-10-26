using System.Text;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>
/// Reads just the header and top-level group list of a plugin. This is a few small reads per file,
/// so it can be run over every enabled plugin without loading any records.
/// </summary>
public sealed class PluginHeader
{
    public required string FileName { get; init; }
    public bool IsMaster { get; init; }
    public bool IsLight { get; init; }
    public required IReadOnlyList<string> Masters { get; init; }

    /// <summary>True if the plugin has a non-empty NPC_ group (new NPCs or edits to existing ones).</summary>
    public bool HasNpcRecords { get; init; }

    /// <summary>Record types (top-level group labels, e.g. "NPC_", "QUST") that have content.</summary>
    public required IReadOnlyList<string> RecordTypes { get; init; }

    /// <summary>
    /// Record types an appearance overhaul can contain and still be switched off after merging:
    /// the merge copies whatever the chosen looks use from these.
    /// </summary>
    public static readonly IReadOnlySet<string> AppearanceRecordTypes = new HashSet<string>
    {
        "NPC_", "HDPT", "TXST", "ARMO", "ARMA", "CLFM", "RACE", "FLST", "KYWD", "OTFT", "EYES",
    };

    /// <summary>Record types outside <see cref="AppearanceRecordTypes"/>.</summary>
    public IEnumerable<string> NonAppearanceRecordTypes => RecordTypes.Where(t => !AppearanceRecordTypes.Contains(t));

    private const uint MasterFlag = 0x1;
    private const uint LightFlag = 0x200;

    public static PluginHeader? TryRead(string path)
    {
        try { return Read(path); }
        catch { return null; }
    }

    public static PluginHeader Read(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
        using var br = new BinaryReader(fs);

        if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "TES4")
            throw new InvalidDataException($"{path} is not a Skyrim plugin.");
        uint dataSize = br.ReadUInt32();
        uint flags = br.ReadUInt32();
        br.ReadBytes(12); // form id, version control, version + unknown

        var masters = new List<string>();
        long headerEnd = fs.Position + dataSize;
        uint? sizeOverride = null;
        while (fs.Position < headerEnd)
        {
            var type = Encoding.ASCII.GetString(br.ReadBytes(4));
            uint size = br.ReadUInt16();
            if (sizeOverride != null) { size = sizeOverride.Value; sizeOverride = null; }
            if (type == "XXXX") { sizeOverride = br.ReadUInt32(); continue; }
            var data = br.ReadBytes((int)size);
            if (type == "MAST") masters.Add(Encoding.UTF8.GetString(data).TrimEnd('\0'));
        }
        fs.Position = headerEnd;

        var types = new List<string>();
        var groupHeader = new byte[24];
        while (fs.Length - fs.Position >= 24)
        {
            fs.ReadExactly(groupHeader);
            if (groupHeader[0] != 'G' || groupHeader[1] != 'R' || groupHeader[2] != 'U' || groupHeader[3] != 'P') break;
            uint groupSize = BitConverter.ToUInt32(groupHeader, 4);
            var label = Encoding.ASCII.GetString(groupHeader, 8, 4);
            if (groupSize > 24) types.Add(label);
            if (groupSize < 24) break;
            fs.Position += groupSize - 24;
        }

        return new PluginHeader
        {
            FileName = Path.GetFileName(path),
            IsMaster = (flags & MasterFlag) != 0 || path.EndsWith(".esm", StringComparison.OrdinalIgnoreCase),
            IsLight = (flags & LightFlag) != 0 || path.EndsWith(".esl", StringComparison.OrdinalIgnoreCase),
            Masters = masters,
            HasNpcRecords = types.Contains("NPC_"),
            RecordTypes = types,
        };
    }
}
