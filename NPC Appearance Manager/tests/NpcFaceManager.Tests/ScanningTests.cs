using System.Text;
using Mutagen.Bethesda.Plugins;
using NpcFaceManager.Core.Build;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Tests;

public sealed class ScanningTests
{
    [Fact]
    public void FaceGenPaths_MatchGameLayout()
    {
        var npc = FormKey.Factory("013BBD:Skyrim.esm");
        Assert.Equal(@"meshes\actors\character\facegendata\facegeom\Skyrim.esm\00013BBD.nif", FaceGenPaths.NifPath(npc));
        Assert.Equal(@"textures\actors\character\facegendata\facetint\Skyrim.esm\00013BBD.dds", FaceGenPaths.DdsPath(npc));
        Assert.Equal("skyrim.esm|00013BBD", FaceGenPaths.Key(npc));
    }

    [Theory]
    [InlineData(@"meshes\actors\character\FaceGenData\FaceGeom\Skyrim.esm\00013BBD.nif", "Skyrim.esm", "00013BBD", true)]
    [InlineData(@"textures/actors/character/facegendata/facetint/Dawnguard.esm/0000ABCD.dds", "Dawnguard.esm", "0000ABCD", false)]
    public void FaceGenPaths_ParseFaceFiles(string path, string plugin, string id, bool isNif)
    {
        Assert.True(FaceGenPaths.TryParse(path, out var p, out var i, out var n));
        Assert.Equal((plugin, id, isNif), (p, i, n));
    }

    [Theory]
    [InlineData(@"meshes\actors\character\facegendata\facegeom\Skyrim.esm\notanid.nif")]
    [InlineData(@"meshes\actors\character\facegendata\facegeom\Skyrim.esm\00013BBD.dds")]
    [InlineData(@"meshes\armor\iron\cuirass.nif")]
    public void FaceGenPaths_RejectOtherFiles(string path) => Assert.False(FaceGenPaths.TryParse(path, out _, out _, out _));

    [Fact]
    public void FaceGenPaths_ToFormKey_DropsLoadOrderByte()
    {
        var key = FaceGenPaths.ToFormKey("Skyrim.esm", "FF013BBD");
        Assert.Equal(FormKey.Factory("013BBD:Skyrim.esm"), key);
    }

    [Theory]
    [InlineData("Bijin AIO.bsa", "Bijin AIO.esp", true)]
    [InlineData("Bijin AIO - Textures.bsa", "Bijin AIO.esp", true)]
    [InlineData("Bijin AIOX.bsa", "Bijin AIO.esp", false)]
    [InlineData("Other.bsa", "Bijin AIO.esp", false)]
    public void Archives_BelongToPluginByName(string archive, string plugin, bool owned) =>
        Assert.Equal(owned, ArchiveHelper.IsOwnedBy(archive, plugin));

    [Theory]
    [InlineData(@"actors\character\hair\hair01.dds", @"textures\actors\character\hair\hair01.dds")]
    [InlineData(@"textures\actors\character\hair\hair01.dds", @"textures\actors\character\hair\hair01.dds")]
    [InlineData(@"C:\Skyrim\Data\textures\foo.dds", @"textures\foo.dds")]
    public void NifTexturePaths_AreNormalized(string raw, string expected) => Assert.Equal(expected, NifTextures.Normalize(raw));

    [Fact]
    public void PluginHeader_ReadsMastersFlagsAndRecordTypes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nfm-{Guid.NewGuid():N}.esp");
        try
        {
            File.WriteAllBytes(path, BuildPlugin(flags: 0x200, masters: ["Skyrim.esm", "Update.esm"], groups: [("NPC_", 40), ("HDPT", 24), ("QUST", 30)]));
            var header = PluginHeader.Read(path);

            Assert.True(header.IsLight);
            Assert.False(header.IsMaster);
            Assert.Equal(["Skyrim.esm", "Update.esm"], header.Masters);
            Assert.True(header.HasNpcRecords);
            Assert.Equal(["NPC_", "QUST"], header.RecordTypes); // the empty HDPT group doesn't count
            Assert.Equal(["QUST"], header.NonAppearanceRecordTypes);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Writes a minimal plugin: TES4 header with MAST subrecords, then (empty-bodied) top-level groups.</summary>
    private static byte[] BuildPlugin(uint flags, string[] masters, (string Label, int Size)[] groups)
    {
        var sub = new MemoryStream();
        var sw = new BinaryWriter(sub);
        void Sub(string type, byte[] data) { sw.Write(Encoding.ASCII.GetBytes(type)); sw.Write((ushort)data.Length); sw.Write(data); }
        Sub("HEDR", new byte[12]);
        foreach (var m in masters)
        {
            Sub("MAST", Encoding.ASCII.GetBytes(m + "\0"));
            Sub("DATA", new byte[8]);
        }

        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("TES4"));
        w.Write((uint)sub.Length);
        w.Write(flags);
        w.Write(new byte[12]);
        w.Write(sub.ToArray());
        foreach (var (label, size) in groups)
        {
            w.Write(Encoding.ASCII.GetBytes("GRUP"));
            w.Write((uint)size);
            w.Write(Encoding.ASCII.GetBytes(label));
            w.Write(new byte[12]);
            w.Write(new byte[size - 24]);
        }
        return ms.ToArray();
    }
}
