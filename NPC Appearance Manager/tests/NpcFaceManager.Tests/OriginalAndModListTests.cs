using Mutagen.Bethesda.Plugins;
using NpcFaceManager.Core.Build;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.Tests;

public sealed class OriginalAndModListTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nam-tests-").FullName;
    private string Mods => Path.Combine(_dir, "mods");
    private string Data => Path.Combine(_dir, "game", "Data");

    public void Dispose() => Directory.Delete(_dir, true);

    private static readonly FormKey Lydia = FormKey.Factory("0A2C94:Skyrim.esm");
    private static readonly FormKey CcNpc = FormKey.Factory("000801:ccbgssse001-fish.esm");
    private static readonly FormKey ModdedNpc = FormKey.Factory("000D62:Followers.esp");

    /// <summary>A fake MO2 instance. modlist.txt lists mods highest priority first, as MO2 writes it.</summary>
    private GameSetup MakeSetup(string[] modlist, string[] plugins)
    {
        var profile = Path.Combine(_dir, "profiles", "Default");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(Data);
        foreach (var line in modlist)
            Directory.CreateDirectory(Path.Combine(Mods, line[1..]));
        File.WriteAllLines(Path.Combine(profile, "modlist.txt"), modlist);
        File.WriteAllLines(Path.Combine(profile, "plugins.txt"), plugins.Select(p => "*" + p));
        File.WriteAllText(Path.Combine(_dir, "ModOrganizer.ini"),
            $"[General]\ngamePath={Path.Combine(_dir, "game").Replace(@"\", @"\\")}\nselected_profile=Default\n");
        return GameSetup.FromMo2(Mo2Instance.Load(Path.Combine(_dir, "ModOrganizer.ini")), null, "test");
    }

    private static void Face(string folder, FormKey npc)
    {
        foreach (var rel in new[] { FaceGenPaths.NifPath(npc), FaceGenPaths.DdsPath(npc) })
        {
            var path = Path.Combine(folder, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
    }

    [Fact]
    public void ModList_KeepsSeparatorsInMo2Order()
    {
        var setup = MakeSetup(["+Faces Mod", "+Faces_separator", "-Old Mod", "+Base Mod", "+Basics_separator"], []);

        // MO2 shows the lowest priority first: Basics, Base Mod, Old Mod, Faces, Faces Mod.
        var shown = setup.AllMods.Select(m => (m.Priority, m.Name))
            .Concat(setup.Separators.Select(s => (s.Priority, "[" + s.Name + "]")))
            .OrderBy(x => x.Priority).Select(x => x.Item2);
        Assert.Equal(["[Basics]", "Base Mod", "Old Mod", "[Faces]", "Faces Mod"], shown);
        Assert.Equal("Basics", setup.SeparatorOf(setup.AllMods.Single(m => m.Name == "Old Mod"))!.Name);
        Assert.Equal("Faces", setup.SeparatorOf(setup.AllMods.Single(m => m.Name == "Faces Mod"))!.Name);
        Assert.DoesNotContain(setup.AllMods, m => m.Name.EndsWith("_separator"));
    }

    [Theory]
    [InlineData("NAM Merge", "NAM Merge")]
    [InlineData("NAM Merge.esp", "NAM Merge")]
    [InlineData("  My Faces.ESP ", "My Faces")]
    public void PluginName_IsTypedWithoutEsp(string typed, string expected) => Assert.Equal(expected, AppSettings.PluginBaseName(typed));

    [Fact]
    public void Settings_CarryOverFromTheOldFile()
    {
        var oldFile = Path.Combine(_dir, "NpcFaceManager.settings.json");
        File.WriteAllText(oldFile, """{ "OutputModName": "NAM Merge", "OutputPluginName": "NAM Merge.esp", "OnlyFaceMods": true }""");

        var settings = AppSettings.Load(Path.Combine(_dir, "NpcAppearanceManager.settings.json"), oldFile);

        Assert.Equal("NAM Merge", settings.OutputModName);
        Assert.Equal("NAM Merge", settings.OutputPluginName);
        Assert.True(settings.OnlyFaceMods);
        Assert.False(settings.ShowDisabledMods);
    }

    [Fact]
    public void OldBuilds_AreStillRecognized()
    {
        var folder = Path.Combine(_dir, "NAM Merge");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, MergeMarker.OldFileName), """{ "Plugin": "NAM Merge.esp" }""");

        Assert.True(MergeMarker.IsOutputFolder(folder));
        Assert.Equal("NAM Merge.esp", MergeMarker.TryRead(folder)!.Plugin);
    }

    [Fact]
    public void Original_IsWhereTheNpcComesFrom()
    {
        var addedByTicked = FormKey.Factory("000D63:Auri.esp");
        foreach (var (mod, plugin) in new[] { ("Followers", "Followers.esp"), ("Auri", "Auri.esp") })
        {
            Directory.CreateDirectory(Path.Combine(Mods, mod));
            File.WriteAllText(Path.Combine(Mods, mod, plugin), "");
        }
        var setup = MakeSetup(
            ["+Ticked Overhaul", "+Other Overhaul", "+Followers", "+Auri"],
            ["Followers.esp", "Auri.esp", "ccbgssse001-fish.esm"]);
        Face(Path.Combine(Mods, "Ticked Overhaul"), Lydia);
        Face(Path.Combine(Mods, "Other Overhaul"), Lydia);
        Face(Path.Combine(Mods, "Other Overhaul"), ModdedNpc);
        Face(Path.Combine(Mods, "Followers"), ModdedNpc);
        Face(Path.Combine(Mods, "Auri"), addedByTicked);
        Face(Data, Lydia);
        Face(Data, CcNpc);
        var all = ModPreScanner.Scan(setup);
        var ticked = all.Where(m => m.Mod.Name is "Ticked Overhaul" or "Auri").ToList();

        var originals = OriginalSources.Build(setup, all, ticked);

        // Vanilla NPC: overhauls never count, ticked or not.
        var look = originals.Find(Lydia, [], out var name);
        Assert.Equal("Skyrim", name);
        Assert.StartsWith(Data, look!.Faces.NifLoosePath);
        Assert.NotNull(originals.Find(CcNpc, [], out name));
        Assert.Equal("Creation Club", name);
        // Modded NPC: the mod that adds it, even though another overhaul changes it.
        look = originals.Find(ModdedNpc, [], out name);
        Assert.Equal("Followers", name);
        Assert.True(look!.IsOriginal);
        Assert.StartsWith(Path.Combine(Mods, "Followers"), look.Faces.NifLoosePath);
        Assert.Contains(ModKey.FromFileName("Followers.esp"), look.RecordFrom!);
        // Added by a ticked mod: its own look is the original.
        var auriLook = new NpcLook { Source = ticked.Single(t => t.Mod.Name == "Auri"), Npc = addedByTicked, SourcePlugin = ModKey.FromFileName("Auri.esp"), Faces = new FaceFiles() };
        Assert.Same(auriLook, originals.Find(addedByTicked, [auriLook], out name));
        Assert.Equal("Auri", name);
        // Its source has no face for it: still named after its source.
        Assert.Null(originals.Find(FormKey.Factory("000123:Skyrim.esm"), [], out name));
        Assert.Equal("Skyrim", name);
    }

    [Fact]
    public void DisabledMods_ListTheirDisabledPatches()
    {
        Mo2Summary("Main", false, ("Main.esp", ["Skyrim.esm"]), out var main);
        Mo2Summary("Main Patch", false, ("MainPatch.esp", ["Main.esp"]), out var patch);
        Mo2Summary("Enabled Mod", true, ("Enabled.esp", ["Skyrim.esm"]), out var enabled);
        Mo2Summary("Old Patch", false, ("OldPatch.esp", ["Enabled.esp"]), out var oldPatch);

        var deps = new Dependents([main, patch, enabled, oldPatch]);

        Assert.Equal([patch], deps.Of(main));
        Assert.Empty(deps.Of(enabled)); // an enabled mod only lists enabled mods that need it
    }

    [Fact]
    public void DisabledPlugins_GoRightAfterTheirMasters()
    {
        Mo2Summary("Main", false, ("Main.esp", ["Skyrim.esm", "Lib.esp"]), out var main);
        Mo2Summary("Patch", false, ("Patch.esp", ["Main.esp"]), out var patch);
        var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Lib.esp", "Other.esp" }.Select(p => ModKey.FromFileName(p)).ToList();

        var order = MergeBuilder.WithDisabledPlugins(loadOrder, [patch, main]).Select(k => k.FileName.String);

        Assert.Equal(["Skyrim.esm", "Update.esm", "Lib.esp", "Main.esp", "Patch.esp", "Other.esp"], order);
    }

    private static void Mo2Summary(string name, bool enabled, (string Plugin, string[] Masters) plugin, out ModSummary summary) => summary = new()
    {
        Mod = new ModFolder { Name = name, Path = name, Priority = 0, IsEnabled = enabled, TopLevelFiles = [plugin.Plugin] },
        Kind = ModKind.Other,
        Plugins = [new PluginSummary(plugin.Plugin, enabled, false, plugin.Masters, [])],
        FaceFileCount = 0,
        FacesInArchives = false,
        FacePlugins = [],
    };
}
