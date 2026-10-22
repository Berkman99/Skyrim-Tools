using System.Text;
using NpcFaceManager.Core.Build;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Tests;

public sealed class MergeRoundTripTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nfm-merge-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ApplyMergeResult_RestoresWhatIsNoLongerMerged()
    {
        File.WriteAllLines(Path.Combine(_dir, "modlist.txt"), ["+Merged Faces", "-Overhaul A", "-Overhaul B", "+Patches"]);
        File.WriteAllLines(Path.Combine(_dir, "plugins.txt"), ["*Patches.esp", "Patch A.esp", "*Merged.esp"]);

        // Second build: only Overhaul B is merged now; A and its patch plugin should come back.
        ProfileFiles.ApplyMergeResult(_dir, new MergeChanges(
            ModsToDisable: ["Overhaul B"],
            PluginsToDeactivate: ["OverhaulB.esp"],
            ModsToEnable: ["Overhaul A"],
            PluginsToActivate: [new DeactivatedPlugin("Patch A.esp", "Patches.esp"), new DeactivatedPlugin("OverhaulA.esp", null)],
            OutputModName: "Merged Faces",
            OutputPlugin: "Merged.esp"));

        Assert.Equal(["+Merged Faces", "+Overhaul A", "-Overhaul B", "+Patches"], File.ReadAllLines(Path.Combine(_dir, "modlist.txt")));
        Assert.Equal(["*Patches.esp", "*Patch A.esp", "*OverhaulA.esp", "*Merged.esp"], File.ReadAllLines(Path.Combine(_dir, "plugins.txt")));
    }

    [Fact]
    public void GameSetup_PutsBackPluginsSwitchedOffByTheLastBuild()
    {
        var game = Path.Combine(_dir, "game");
        Directory.CreateDirectory(Path.Combine(game, "Data"));
        var mods = Path.Combine(_dir, "mods");
        var profile = Path.Combine(_dir, "profiles", "Default");
        Directory.CreateDirectory(profile);
        foreach (var (mod, plugin) in new[] { ("Overhaul", "Overhaul.esp"), ("Patches", "Patch.esp"), ("Out", "Merged.esp") })
        {
            Directory.CreateDirectory(Path.Combine(mods, mod));
            File.WriteAllText(Path.Combine(mods, mod, plugin), "");
        }
        new MergeMarker
        {
            Plugin = "Merged.esp",
            DisabledMods = ["Overhaul"],
            DeactivatedPlugins = [new DeactivatedPlugin("Overhaul.esp", "Update.esm"), new DeactivatedPlugin("Patch.esp", "Overhaul.esp")],
        }.Write(Path.Combine(mods, "Out"));
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), ["+Out", "+Patches", "-Overhaul"]);
        File.WriteAllLines(Path.Combine(profile, "plugins.txt"), ["Patch.esp", "*Merged.esp"]);
        File.WriteAllText(Path.Combine(_dir, "ModOrganizer.ini"), $"[General]\ngamePath={game.Replace(@"\", @"\\")}\nselected_profile=Default\n");

        var setup = GameSetup.FromMo2(Mo2Instance.Load(Path.Combine(_dir, "ModOrganizer.ini")), null, "test");

        var order = setup.LoadOrder.Select(k => k.FileName.String).ToList();
        Assert.DoesNotContain("Merged.esp", order);
        Assert.Equal(["Update.esm", "Overhaul.esp", "Patch.esp"], order.SkipWhile(p => p != "Update.esm").Take(3));
        Assert.True(setup.AllMods.Single(m => m.Name == "Overhaul").CountsAsEnabled);
        Assert.DoesNotContain(setup.AllMods, m => m.Name == "Out");
    }

    [Fact]
    public void BsaFolderReader_ReadsFolderNamesAndCounts()
    {
        var path = Path.Combine(_dir, "test.bsa");
        File.WriteAllBytes(path, BuildBsa([(@"meshes\actors\character\facegendata\facegeom\skyrim.esm", 3), (@"textures\misc", 1)]));

        var folders = BsaFolderReader.Read(path);

        Assert.NotNull(folders);
        Assert.Equal([(@"meshes\actors\character\facegendata\facegeom\skyrim.esm", 3), (@"textures\misc", 1)],
            folders!.Select(f => (f.Name, f.FileCount)));
        Assert.Equal([("skyrim.esm", 3)], ModPreScanner.ArchiveFaceFolders(path));
    }

    /// <summary>A minimal v105 BSA: header, folder records and the name-prefixed file-record blocks (files empty).</summary>
    private static byte[] BuildBsa((string Name, int Files)[] folders)
    {
        const int headerSize = 36, folderRecordSize = 24, fileRecordSize = 16;
        int totalFolderNames = folders.Sum(f => f.Name.Length + 2);
        int fileNameTable = 0; // no file names needed for this test
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x00415342u); w.Write(105u); w.Write(36u); w.Write(0x3u);
        w.Write((uint)folders.Length); w.Write((uint)folders.Sum(f => f.Files));
        w.Write((uint)totalFolderNames); w.Write((uint)fileNameTable); w.Write(0u);

        long blockStart = headerSize + folderRecordSize * folders.Length;
        long offset = blockStart;
        foreach (var f in folders)
        {
            w.Write(0UL); w.Write((uint)f.Files); w.Write(0u); w.Write((ulong)(offset + fileNameTable));
            offset += f.Name.Length + 2 + fileRecordSize * f.Files;
        }
        foreach (var f in folders)
        {
            w.Write((byte)(f.Name.Length + 1));
            w.Write(Encoding.ASCII.GetBytes(f.Name));
            w.Write((byte)0);
            w.Write(new byte[fileRecordSize * f.Files]);
        }
        return ms.ToArray();
    }
}
