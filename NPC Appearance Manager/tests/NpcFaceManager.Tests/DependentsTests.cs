using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Tests;

public sealed class DependentsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nfm-deps-").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static ModSummary Mod(string name, bool enabled, params (string Plugin, string[] Masters)[] plugins) => new()
    {
        Mod = new ModFolder { Name = name, Path = name, Priority = 0, IsEnabled = enabled, TopLevelFiles = plugins.Select(p => p.Plugin).ToList() },
        Kind = ModKind.Other,
        Plugins = plugins.Select(p => new PluginSummary(p.Plugin, enabled, false, p.Masters, [])).ToList(),
        FaceFileCount = 0,
        FacesInArchives = false,
        FacePlugins = [],
    };

    [Fact]
    public void FindsModsThatNeedAMod()
    {
        var overhaul = Mod("Overhaul", true, ("Overhaul.esp", ["Skyrim.esm"]));
        var patch = Mod("Patches", true, ("Patch.esp", ["Skyrim.esm", "Overhaul.esp"]), ("Other.esp", ["Skyrim.esm"]));
        var disabledPatch = Mod("Old Patch", false, ("Old.esp", ["Overhaul.esp"]));
        var unrelated = Mod("Unrelated", true, ("Unrelated.esp", ["Skyrim.esm"]));

        var deps = new Dependents([overhaul, patch, disabledPatch, unrelated]);

        Assert.Equal([patch], deps.Of(overhaul));          // whole mod, not just the plugin
        Assert.Empty(deps.Of(unrelated));
        Assert.Empty(deps.Of(patch));
    }

    [Fact]
    public void CountsMastersAcrossTheModsPlugins()
    {
        var masters = Enumerable.Range(0, 300).Select(i => $"M{i}.esp").ToArray();
        var big = Mod("Generated", true, ("Gen.esp", masters), ("Gen2.esp", ["Gen.esp", "M0.esp"]));
        var small = Mod("Small", true, ("Small.esp", ["Skyrim.esm", "Update.esm"]));

        var deps = new Dependents([big, small]);

        Assert.Equal(300, deps.MasterCount(big)); // its own plugins and duplicates don't count
        Assert.True(deps.HasTooManyMasters(big));
        Assert.Equal(2, deps.MasterCount(small));
        Assert.False(deps.HasTooManyMasters(small));
    }

    [Fact]
    public void Backups_IncludeLockedOrderAndUseReadableNames()
    {
        File.WriteAllLines(Path.Combine(_dir, "modlist.txt"), ["+A"]);
        File.WriteAllLines(Path.Combine(_dir, "plugins.txt"), ["*A.esp"]);
        File.WriteAllLines(Path.Combine(_dir, "loadorder.txt"), ["A.esp"]);

        ProfileFiles.WriteBackups(_dir, new DateTime(2026, 10, 22, 14, 30, 5));

        foreach (var name in new[] { "modlist.txt", "plugins.txt", "loadorder.txt", "lockedorder.txt" })
            Assert.True(File.Exists(Path.Combine(_dir, name + ".NAM 2026-10-22 14.30.05")), name);
    }
}
