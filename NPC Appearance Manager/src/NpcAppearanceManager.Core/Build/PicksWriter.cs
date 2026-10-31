using System.Globalization;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Logging;
using NpcAppearanceManager.Core.Scanning;

namespace NpcAppearanceManager.Core.Build;

public sealed class PicksRequest
{
    public required GameSetup Setup { get; init; }
    public required NpcIndex Index { get; init; }

    /// <summary>The look picked for each NPC. A look of another NPC (<see cref="NpcLook.Npc"/> differs) is a copied face.</summary>
    public required IReadOnlyDictionary<FormKey, NpcLook> Choices { get; init; }

    public IReadOnlyDictionary<FormKey, string> Names { get; init; } = new Dictionary<FormKey, string>();

    /// <summary>The folder NAM keeps its saved files in: the picks list and NAM.log go there.</summary>
    public required string SavedFolder { get; init; }

    /// <summary>The add-ons of the picked faces (null: none), and the ones you ticked for Save's question (<see cref="AddOnScan.Key"/>).</summary>
    public AddOnScan? AddOns { get; init; }
    public IReadOnlySet<string> AddOnTicks { get; init; } = new HashSet<string>();
}

public sealed class PicksReport
{
    public int NpcsChanged { get; set; }
    public int FacesCopied { get; set; }
    public int NpcsRenamed { get; set; }
    public int FilesServed { get; set; }
    public int FormsMade { get; set; }
    public List<string> Warnings { get; } = [];

    /// <summary>How long each part of Save took, for NAM.log.</summary>
    public List<(string Step, double Seconds)> Timings { get; } = [];
}

/// <summary>
/// Writes the list of picks NAM's game part applies when Skyrim starts. For each picked NPC the
/// list holds the face files to load (straight from the picked mod: its loose files, or files inside its archives, which
/// the game part reads from there) and the look values of the record that goes with that face. Head parts, skins and the like that only exist in a disabled mod are described too, so the game
/// part can make them. Nothing is written for an NPC whose pick is what the game shows anyway.
/// </summary>
public static class PicksWriter
{
    public const string ListFile = "Picks.toml";

    public static PicksReport Write(PicksRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var report = new PicksReport();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        void Took(string step)
        {
            report.Timings.Add((step, clock.Elapsed.TotalSeconds));
            clock.Restart();
        }
        var setup = request.Setup;
        Directory.CreateDirectory(request.SavedFolder);

        // NPCs to write: picks that differ from what the game shows, copies, and renames.
        var entries = request.Index.Npcs.ToDictionary(n => n.FormKey);
        var picks = PicksToWrite(request.Index, request.Choices, report.Warnings);
        Took("picks");

        // Plugins: the load order, plus those of disabled mods faces are picked from (only read, for their records).
        progress?.Report("Opening the load order...");
        var disabledMods = picks.Values.Select(l => l.Source).Where(s => !s.Mod.IsEnabled).Distinct().ToList();
        var order = WithDisabledPlugins(setup.LoadOrder, disabledMods);
        var disabledPaths = disabledMods.SelectMany(m => m.Plugins.Select(p => (Key: ModKey.FromFileName(p.FileName), Path: Path.Combine(m.Mod.Path, p.FileName))))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Path);
        using var cache = new PluginCache(setup);
        var mods = new List<ISkyrimModGetter>();
        foreach (var key in order)
        {
            var mod = disabledPaths.TryGetValue(key, out var path) ? cache.GetByPath(path) : cache.Get(key.FileName);
            if (mod == null || IsBuiltByThisTool(mod)) continue;
            mods.Add(mod);
        }
        var linkCache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        bool InGame(ModKey plugin) => setup.IsActive(plugin.FileName);
        Took("load order");

        // Each face file also names its mod by Nexus ID, so the game part finds it after the mod is renamed.
        var modFolders = setup.AllMods.Where(m => !m.IsGameDataFolder).OrderByDescending(m => m.Path.Length).ToList();
        var writer = new ListWriter
        {
            Log = Path.Combine(request.SavedFolder, NamLog.FileName),
            ModOf = path => modFolders.FirstOrDefault(m => path.StartsWith(m.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)),
        };
        // Files the game has without the picked mods: in its own archives, Data, or an enabled mod.
        var gameArchives = GameArchiveFiles(setup);
        var enabledFolders = setup.Mods.Where(m => !m.IsGameDataFolder && !m.TopLevelFiles.Any(OutputMarker.IsMarkerFile)).Select(m => m.Path)
            .Append(setup.DataFolder).ToList();
        bool GameHas(string path) => gameArchives.Contains(path) || enabledFolders.Any(f => File.Exists(Path.Combine(f, path)));
        var files = new Files(writer, GameHas);
        var forms = new FormList(linkCache, InGame, writer, report);
        Took("game archives");

        int n = 0;
        foreach (var (npcKey, look) in picks)
        {
            ct.ThrowIfCancellationRequested();
            if (++n % 50 == 0) progress?.Report($"Saving NPC {n} of {picks.Count}...");
            var name = entries.TryGetValue(npcKey, out var entry) ? entry.DisplayName : npcKey.ToString();
            var copy = look.Npc != npcKey;

            var targets = linkCache.ResolveAllContexts<INpc, INpcGetter>(npcKey).Where(c => InGame(c.ModKey)).ToList();
            if (targets.Count == 0)
            {
                report.Warnings.Add($"Skipped {name} ({npcKey}): its record isn't in the game.");
                continue;
            }

            // Face files, at the paths the game uses for this NPC: the picked mod's, with its add-ons in.
            var faceFiles = request.AddOns?.Apply(look, request.AddOnTicks) ?? look.Faces;
            var nif = files.Face(FaceGenPaths.NifPath(look.Npc), faceFiles.NifLoosePath, faceFiles.NifArchive, FaceGenPaths.NifPath(npcKey));
            if (nif == null)
            {
                report.Warnings.Add($"Skipped {name} ({npcKey}) from {look.ModName}: its face mesh couldn't be read.");
                continue;
            }
            var dds = files.Face(FaceGenPaths.DdsPath(look.Npc), faceFiles.DdsLoosePath, faceFiles.DdsArchive, FaceGenPaths.DdsPath(npcKey));
            if (dds == null)
                report.Warnings.Add($"{name} ({npcKey}) from {look.ModName} has no face tint file, so the face may look darker in game.");

            var source = Appearance(look, nif, linkCache, InGame, disabledPaths.Keys.ToHashSet(), report, name);
            if (source == null) continue;

            // An opposite-sex copy keeps the NPC's own voice on its record, so the game picks the same lines; the game
            // part plays each line from the new sex's voice instead, or unvoiced when that voice has no recording of it.
            // A copied face brings its NPC's body; one without a body of its own gets its race's, as in the game. The
            // NPC's own body may be made for another race or sex, and then has no pieces the game can draw.
            writer.Look(npcKey, source, forms,
                raceBody: UsesRaceBody(copy, source),
                faces: dds == null ? [FaceGenPaths.NifPath(npcKey)] : [FaceGenPaths.NifPath(npcKey), FaceGenPaths.DdsPath(npcKey)],
                speaksAs: copy && IsFemale(source) != IsFemale(targets[0].Record) ? source.Voice.FormKey : null,
                name: request.Names.GetValueOrDefault(npcKey));

            // A disabled mod's files aren't in the game: what this face uses comes from its folder.
            if (!look.Source.Mod.IsEnabled)
            {
                foreach (var asset in forms.AssetsOf(source))
                    files.FromMod(look.Source, asset);
                files.TexturesOf(look.Source, nif);
            }

            if (copy) report.FacesCopied++;
            else report.NpcsChanged++;
        }

        Took($"{picks.Count} faces");

        // Renames of NPCs whose face stays.
        foreach (var (npcKey, newName) in request.Names.Where(x => !picks.ContainsKey(x.Key)))
        {
            if (!linkCache.ResolveAllContexts<INpc, INpcGetter>(npcKey).Any(c => InGame(c.ModKey)))
            {
                report.Warnings.Add($"Couldn't rename {npcKey} to {newName}: its record isn't in the game.");
                continue;
            }
            writer.Rename(npcKey, newName);
        }
        report.NpcsRenamed = request.Names.Count;

        forms.WriteDefinitions();

        progress?.Report("Writing the picks list...");
        var listPath = Path.Combine(request.SavedFolder, ListFile);
        var temp = listPath + ".tmp"; // the game never sees half a list
        File.WriteAllText(temp, writer.ToString(), new UTF8Encoding(false));
        File.Move(temp, listPath, overwrite: true);
        report.FilesServed = files.Count;
        report.FormsMade = forms.Count;
        Took("writing");
        progress?.Report("Done.");
        return report;
    }

    /// <summary>The faces Save writes: picks that differ from what the game shows now, copies, and the original of an NPC left out of the choices.</summary>
    public static Dictionary<FormKey, NpcLook> PicksToWrite(NpcIndex index, IReadOnlyDictionary<FormKey, NpcLook> choices, List<string>? warnings = null)
    {
        var entries = index.Npcs.ToDictionary(n => n.FormKey);
        var picks = new Dictionary<FormKey, NpcLook>();
        foreach (var (key, look) in choices)
        {
            if (look.Npc == key && entries.TryGetValue(key, out var e) && e.DefaultLook == look) continue;
            if (look.Npc == key && entries.TryGetValue(key, out var e2) && e2.DefaultLook == null && look == e2.Original) continue;
            picks[key] = look;
        }
        // An NPC missing from the choices has its original picked: written when the game shows another face now.
        foreach (var entry in index.Npcs.Where(n => n.DefaultLook != null && !choices.ContainsKey(n.FormKey)))
        {
            if (entry.Original != null) picks[entry.FormKey] = entry.Original;
            else warnings?.Add($"{entry.DisplayName} ({entry.FormKey}): its original has no face file, so {entry.DefaultLook!.ModName} still shows.");
        }
        return picks;
    }

    private static bool IsFemale(INpcGetter npc) => npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female);

    /// <summary>A copied face whose NPC has no body of its own: the race's body, not the target's own.</summary>
    internal static bool UsesRaceBody(bool copy, INpcGetter source) => copy && source.WornArmor.IsNull;

    /// <summary>
    /// The record version that goes with a look's face: an original's own source, the
    /// look's plugin, or for face files only the version whose head parts the face mesh was made with.
    /// </summary>
    private static INpcGetter? Appearance(NpcLook look, Source nif, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Func<ModKey, bool> inGame, IReadOnlySet<ModKey> disabledPlugins, PicksReport report, string name)
    {
        var all = linkCache.ResolveAllContexts<INpc, INpcGetter>(look.Npc).ToList();
        if (look.RecordFrom is { } from)
        {
            var own = all.FirstOrDefault(c => from.Contains(c.ModKey));
            if (own == null) report.Warnings.Add($"Skipped {name} ({look.Npc}): its original record isn't readable.");
            return own?.Record;
        }
        if (look.SourcePlugin is { } plugin)
        {
            var src = all.FirstOrDefault(c => c.ModKey == plugin);
            if (src == null) report.Warnings.Add($"Skipped {name} ({look.Npc}): {plugin} isn't readable.");
            return src?.Record;
        }

        // Face files only: the version whose head parts the face mesh was made with. Versions in the game come
        // first, then ones from disabled mods faces are picked from.
        var inGameVersions = all.Where(c => inGame(c.ModKey)).ToList();
        var shapes = nif.Read(FaceMeshParts.ShapeNames);
        var best = shapes == null ? null
            : FaceMeshParts.Best(inGameVersions.Concat(all.Where(c => disabledPlugins.Contains(c.ModKey))), c => c.Record, shapes, linkCache);
        if (best == null)
        {
            report.Warnings.Add($"{name} ({look.Npc}): no plugin has the head parts {look.ModName}'s face was made with, so hair or brows may not match the face.");
            best = inGameVersions.FirstOrDefault();
        }
        return best?.Record;
    }

    /// <summary>"0x0001327E~Skyrim.esm", the way the game part reads a form.</summary>
    internal static string Key(FormKey key) => $"0x{key.ID:X6}~{key.ModKey.FileName}";

    // Plugins earlier versions of NAM built (the NAM merge): never read as a source of faces.
    private const string MergeSignature = "Built by NPC Appearance Manager";
    private const string OldMergeSignature = "Built by NPC Face Manager";

    public static bool IsBuiltByThisTool(ISkyrimModGetter mod) => mod.ModHeader.Description is MergeSignature or OldMergeSignature;

    /// <summary>The load order with the plugins of disabled mods added, each right after the last of its masters (only read, for their records).</summary>
    internal static List<ModKey> WithDisabledPlugins(IReadOnlyList<ModKey> loadOrder, IReadOnlyList<ModSummary> disabledMods)
    {
        var order = loadOrder.ToList();
        var pending = disabledMods.OrderBy(m => m.Mod.Priority).SelectMany(m => m.Plugins)
            .Where(p => !order.Contains(ModKey.FromFileName(p.FileName))).ToList();
        // Masters first: keep placing plugins whose masters are all placed (or missing anyway).
        while (pending.Count > 0)
        {
            var pendingNames = pending.Select(p => p.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var next = pending.FirstOrDefault(p => !p.Masters.Any(pendingNames.Contains)) ?? pending[0];
            pending.Remove(next);
            var after = next.Masters.Select(m => order.FindIndex(k => k.FileName.String.Equals(m, StringComparison.OrdinalIgnoreCase))).DefaultIfEmpty(-1).Max();
            var baseMasters = order.TakeWhile(k => PluginNames.IsBaseMaster(k.FileName)).Count();
            order.Insert(Math.Max(after + 1, baseMasters), ModKey.FromFileName(next.FileName));
        }
        return order;
    }

    private static readonly Dictionary<string, HashSet<string>> GameArchiveCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every file in the archives of the Data folder (Skyrim's own, the DLCs, Creation Club, and mods' when NAM runs in Mod Organizer).</summary>
    private static HashSet<string> GameArchiveFiles(GameSetup setup)
    {
        lock (GameArchiveCache)
        {
            if (GameArchiveCache.TryGetValue(setup.DataFolder, out var cached)) return cached;
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(setup.DataFolder))
                foreach (var archive in Directory.EnumerateFiles(setup.DataFolder, "*.bsa"))
                    if (ArchiveHelper.Open(archive) is { } reader)
                        foreach (var f in reader.Files) files.Add(f.Path.Replace('/', '\\').TrimStart('\\'));
            return GameArchiveCache[setup.DataFolder] = files;
        }
    }

    /// <summary>Where a file the game part serves is: a loose file, or a file inside an archive (read from there in game).</summary>
    internal sealed record Source(string? Loose, string? Archive, string Entry)
    {
        /// <summary>Runs <paramref name="read"/> on the file; one inside an archive is read from a temporary copy that's deleted at once.</summary>
        public T? Read<T>(Func<string, T?> read)
        {
            if (Loose != null) return read(Loose);
            var entry = ArchiveHelper.Find(Archive!, Entry);
            if (entry == null) return default;
            var temp = Path.Combine(Path.GetTempPath(), "NAM faces", Guid.NewGuid().ToString("N") + Path.GetExtension(Entry));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                using (var src = entry.AsStream())
                using (var dst = File.Create(temp))
                    src.CopyTo(dst);
                return read(temp);
            }
            catch (IOException) { return default; }
            finally
            {
                try { File.Delete(temp); } catch (IOException) { }
            }
        }
    }

    /// <summary>The face files and other files the game part serves, each straight from the mod it comes from.</summary>
    private sealed class Files(ListWriter writer, Func<string, bool> inGame)
    {
        private readonly HashSet<string> _served = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Source> _read = [];

        public int Count => _served.Count;

        /// <summary>Serves a face file at <paramref name="gamePath"/>. Returns where it is, or null.</summary>
        public Source? Face(string entry, string? loose, string? archive, string gamePath)
        {
            var source = loose != null && File.Exists(loose) ? new Source(loose, null, entry)
                : archive != null && ArchiveHelper.Find(archive, entry) != null ? new Source(null, archive, entry)
                : null;
            if (source != null) Serve(gamePath, source);
            return source;
        }

        /// <summary>
        /// Serves a file a disabled mod's face uses, from that mod, if the mod has it. A file the game already has
        /// (a vanilla texture the mod replaces, kept by your other mods) is left alone: the path is shared by every NPC,
        /// so serving it would change NPCs other than the picked ones.
        /// </summary>
        public void FromMod(ModSummary mod, string dataPath)
        {
            dataPath = NifTextures.NormalizeDataPath(dataPath);
            if (_served.Contains(dataPath) || inGame(dataPath)) return;
            var loose = Path.Combine(mod.Mod.Path, dataPath);
            Source? source = !mod.Mod.IsGameDataFolder && File.Exists(loose) ? new Source(loose, null, dataPath) : null;
            if (source == null)
                foreach (var archive in ArchiveHelper.LoadedArchives(mod.Mod.Archives, mod.Plugins.Select(p => p.FileName).ToList()))
                {
                    var path = Path.Combine(mod.Mod.Path, archive);
                    if (ArchiveHelper.Find(path, dataPath) != null) { source = new Source(null, path, dataPath); break; }
                }
            if (source == null) return;
            Serve(dataPath, source);
            if (dataPath.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)) TexturesOf(mod, source);
        }

        /// <summary>Serves what a mesh names (textures, physics settings) from the mod, and so on for meshes it names.</summary>
        public void TexturesOf(ModSummary mod, Source nif)
        {
            if (!_read.Add(nif)) return;
            var files = nif.Loose != null ? NifTextures.Read(nif.Loose) : NifTextures.ReadFromArchive(nif.Archive!, nif.Entry);
            foreach (var file in files)
                FromMod(mod, file);
        }

        private void Serve(string gamePath, Source source)
        {
            if (_served.Add(gamePath)) writer.File(gamePath, source);
        }
    }

    /// <summary>
    /// Records a look uses that aren't in the game (they come from a disabled mod's plugin): head parts, texture sets,
    /// hair colours, form lists and skins, described for the game part to make.
    /// </summary>
    internal sealed class FormList(ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache, Func<ModKey, bool> inGame, ListWriter writer, PicksReport report)
    {
        private readonly Dictionary<FormKey, IMajorRecordGetter> _needed = [];

        public int Count => _needed.Count;

        /// <summary>Notes a form a look uses; returns its key for the list.</summary>
        public string Use(FormKey key)
        {
            Need(key);
            return Key(key);
        }

        private void Need(FormKey key)
        {
            if (key.IsNull || inGame(key.ModKey) || _needed.ContainsKey(key)) return;
            if (!linkCache.TryResolve(key, out var record))
            {
                report.Warnings.Add($"Couldn't find {key} in its plugin.");
                return;
            }
            if (!IsFacePart(record))
            {
                report.Warnings.Add($"{record.EditorID ?? key.ToString()} ({key}) only exists in a disabled mod, and NAM can't make that kind of record in game.");
                return;
            }
            _needed[key] = record;
            foreach (var link in record.EnumerateFormLinks()) Need(link.FormKey);
        }

        /// <summary>The kinds of record a face is made of, and the only ones NAM makes in game.</summary>
        private static bool IsFacePart(IMajorRecordGetter record) =>
            record is IHeadPartGetter or ITextureSetGetter or IColorRecordGetter or IFormListGetter or IArmorGetter or IArmorAddonGetter;

        private static readonly HashSet<Type> FacePartLinks =
            [typeof(IHeadPartGetter), typeof(ITextureSetGetter), typeof(IColorRecordGetter), typeof(IFormListGetter), typeof(IArmorGetter), typeof(IArmorAddonGetter)];

        /// <summary>
        /// Every file a face's records use: its head parts, skin and textures, and the face parts those name. Races and
        /// other records they link to are left out; those link on to much of the game, and are never a face's own.
        /// </summary>
        public IEnumerable<string> AssetsOf(INpcGetter npc)
        {
            var seen = new HashSet<FormKey>();
            var stack = new Stack<IFormLinkGetter>(npc.HeadParts.Cast<IFormLinkGetter>().Append(npc.WornArmor).Append(npc.HeadTexture));
            while (stack.Count > 0)
            {
                var link = stack.Pop();
                if (link.IsNull || !seen.Add(link.FormKey) || !linkCache.TryResolve(link.FormKey, link.Type, out var record) || !IsFacePart(record)) continue;
                foreach (var asset in record.EnumerateAssetLinks(AssetLinkQuery.Listed))
                    yield return asset.DataRelativePath.Path;
                // Looked up only when it's a face part: the first look-up of each other kind of record reads every plugin.
                foreach (var child in record.EnumerateFormLinks())
                    if (FacePartLinks.Contains(child.Type)) stack.Push(child);
            }
        }

        public void WriteDefinitions()
        {
            foreach (var record in _needed.Values) writer.Form(record);
        }
    }

    /// <summary>The picks list, in the format NAM's game part reads.</summary>
    internal sealed class ListWriter
    {
        private readonly StringBuilder _files = new();
        private readonly StringBuilder _looks = new();
        private readonly StringBuilder _forms = new();

        /// <summary>NAM.log in NAM's saved-files folder, which the game part adds to.</summary>
        public string? Log { get; set; }

        /// <summary>The mod folder a served file is in (null: not in a mod, e.g. the game's Data folder).</summary>
        public Func<string, ModFolder?>? ModOf { get; init; }

        public override string ToString() =>
            "# Written by NPC Appearance Manager. NAM's game part applies these picks when Skyrim starts.\n\n" +
            (Log == null ? "" : $"log = {Str(Log)}\n\n") +
            _files + _looks + _forms;

        public void File(string gamePath, Source source)
        {
            _files.Append("[[files]]\ngame = ").Append(Str(gamePath)).Append("\nfrom = ").Append(Str(source.Loose ?? source.Archive!)).Append('\n');
            if (source.Archive != null) _files.Append("entry = ").Append(Str(source.Entry)).Append('\n');
            var path = source.Loose ?? source.Archive!;
            if (ModOf?.Invoke(path) is { NexusId: { } id } mod)
                _files.Append("mod_id = ").Append(id).Append("\nin_mod = ").Append(Str(Path.GetRelativePath(mod.Path, path))).Append('\n');
            _files.Append('\n');
        }

        public void Look(FormKey target, INpcGetter source, FormList forms, bool raceBody, IReadOnlyList<string> faces, FormKey? speaksAs, string? name)
        {
            var s = _looks.Append("[[looks]]\ntarget = ").Append(Str(Key(target))).Append('\n');
            // Its face files: the game part leaves the NPC as it is when one of them can't be found.
            s.Append("faces = [").Append(string.Join(", ", faces.Select(Str))).Append("]\n");
            s.Append("head_parts = [").Append(string.Join(", ", source.HeadParts.Where(h => !h.IsNull).Select(h => Str(forms.Use(h.FormKey))))).Append("]\n");
            Ref(s, "hair_color", source.HairColor.FormKey, forms);
            if (raceBody) s.Append("skin = \"none\"\n");
            else Ref(s, "skin", source.WornArmor.FormKey, forms);
            Ref(s, "head_texture", source.HeadTexture.FormKey, forms);
            Ref(s, "race", source.Race.FormKey, forms);
            if (speaksAs is { } v) Ref(s, "speaks_as", v, forms);
            s.Append("height = ").Append(Num(source.Height)).Append('\n');
            s.Append("weight = ").Append(Num(source.Weight)).Append('\n');
            s.Append("female = ").Append(IsFemale(source) ? "true" : "false").Append('\n');
            if (source.TextureLighting is { } c) s.Append($"body_tint = [{c.R}, {c.G}, {c.B}]\n");
            if (name != null) s.Append("name = ").Append(Str(name)).Append('\n');
            s.Append('\n');
        }

        public void Rename(FormKey target, string name) =>
            _looks.Append("[[looks]]\ntarget = ").Append(Str(Key(target))).Append("\nname = ").Append(Str(name)).Append("\n\n");

        public void Form(IMajorRecordGetter record)
        {
            var s = _forms.Append("[[forms]]\nkey = ").Append(Str(Key(record.FormKey))).Append('\n');
            // Face meshes name each shape after its head part, so a made head part needs its own name.
            if (record.EditorID is { Length: > 0 } editorId) s.Append("editor_id = ").Append(Str(editorId)).Append('\n');
            switch (record)
            {
                case IHeadPartGetter part:
                    s.Append("type = \"HDPT\"\n");
                    s.Append("model = ").Append(Str(part.Model?.File.GivenPath ?? "")).Append('\n');
                    s.Append("part_type = ").Append((int)(part.Type ?? HeadPart.TypeEnum.Misc)).Append('\n');
                    s.Append("flags = ").Append((int)part.Flags).Append('\n');
                    Link(s, "texture_set", part.TextureSet.FormKey);
                    Link(s, "color", part.Color.FormKey);
                    Link(s, "valid_races", part.ValidRaces.FormKey);
                    var tris = new string[3];
                    foreach (var p in part.Parts)
                    {
                        var index = p.PartType switch { Part.PartTypeEnum.RaceMorph => 0, Part.PartTypeEnum.Tri => 1, Part.PartTypeEnum.ChargenMorph => 2, _ => -1 };
                        if (index >= 0) tris[index] = p.FileName?.GivenPath ?? "";
                    }
                    s.Append("tris = [").Append(string.Join(", ", tris.Select(t => Str(t ?? "")))).Append("]\n");
                    s.Append("extra_parts = [").Append(string.Join(", ", part.ExtraParts.Where(x => !x.IsNull).Select(x => Str(Key(x.FormKey))))).Append("]\n");
                    break;
                case ITextureSetGetter set:
                    s.Append("type = \"TXST\"\n");
                    s.Append("textures = [").Append(string.Join(", ", new[]
                    {
                        set.Diffuse, set.NormalOrGloss, set.EnvironmentMaskOrSubsurfaceTint, set.GlowOrDetailMap,
                        set.Height, set.Environment, set.Multilayer, set.BacklightMaskOrSpecular,
                    }.Select(t => Str(t?.GivenPath ?? "")))).Append("]\n");
                    s.Append("flags = ").Append((int)set.Flags).Append('\n');
                    break;
                case IColorRecordGetter color:
                    s.Append("type = \"CLFM\"\n");
                    s.Append($"rgb = [{color.Color.R}, {color.Color.G}, {color.Color.B}]\n");
                    s.Append("flags = ").Append(color.Playable ? 1 : 0).Append('\n');
                    break;
                case IFormListGetter list:
                    s.Append("type = \"FLST\"\n");
                    s.Append("forms = [").Append(string.Join(", ", list.Items.Where(x => !x.IsNull).Select(x => Str(Key(x.FormKey))))).Append("]\n");
                    break;
                case IArmorGetter armor:
                    s.Append("type = \"ARMO\"\n");
                    Link(s, "race", armor.Race.FormKey);
                    s.Append("slots = ").Append((uint)(armor.BodyTemplate?.FirstPersonFlags ?? 0)).Append('\n');
                    s.Append("armor_type = ").Append((int)(armor.BodyTemplate?.ArmorType ?? ArmorType.Clothing)).Append('\n');
                    s.Append("addons = [").Append(string.Join(", ", armor.Armature.Where(x => !x.IsNull).Select(x => Str(Key(x.FormKey))))).Append("]\n");
                    break;
                case IArmorAddonGetter addon:
                    s.Append("type = \"ARMA\"\n");
                    Link(s, "race", addon.Race.FormKey);
                    s.Append("slots = ").Append((uint)(addon.BodyTemplate?.FirstPersonFlags ?? 0)).Append('\n');
                    s.Append("models = [").Append(Str(addon.WorldModel?.Male?.File.GivenPath ?? "")).Append(", ").Append(Str(addon.WorldModel?.Female?.File.GivenPath ?? "")).Append("]\n");
                    s.Append("models_1st = [").Append(Str(addon.FirstPersonModel?.Male?.File.GivenPath ?? "")).Append(", ").Append(Str(addon.FirstPersonModel?.Female?.File.GivenPath ?? "")).Append("]\n");
                    s.Append("skins = [").Append(Str(KeyOrEmpty(addon.SkinTexture?.Male?.FormKey))).Append(", ").Append(Str(KeyOrEmpty(addon.SkinTexture?.Female?.FormKey))).Append("]\n");
                    s.Append($"priorities = [{addon.Priority.Male}, {addon.Priority.Female}]\n");
                    // Whether the body blends between its thin and heavy meshes by the NPC's weight.
                    s.Append($"weight_slider = [{(addon.WeightSliderEnabled.Male ? "true" : "false")}, {(addon.WeightSliderEnabled.Female ? "true" : "false")}]\n");
                    s.Append("detection_sound = ").Append(addon.DetectionSoundValue).Append('\n');
                    s.Append("weapon_adjust = ").Append(Num(addon.WeaponAdjust)).Append('\n');
                    s.Append("additional_races = [").Append(string.Join(", ", addon.AdditionalRaces.Where(x => !x.IsNull).Select(x => Str(Key(x.FormKey))))).Append("]\n");
                    break;
            }
            s.Append('\n');
        }

        private static string KeyOrEmpty(FormKey? key) => key is { IsNull: false } k ? Key(k) : "";

        private static void Ref(StringBuilder s, string field, FormKey key, FormList forms)
        {
            if (!key.IsNull) s.Append(field).Append(" = ").Append(Str(forms.Use(key))).Append('\n');
        }

        private static void Link(StringBuilder s, string field, FormKey key)
        {
            if (!key.IsNull) s.Append(field).Append(" = ").Append(Str(Key(key))).Append('\n');
        }

        private static string Num(float value) => value.ToString("0.0#####", CultureInfo.InvariantCulture);

        private static string Str(string value)
        {
            var b = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': b.Append(@"\\"); break;
                    case '"': b.Append("\\\""); break;
                    case < ' ': b.Append($"\\u{(int)c:X4}"); break;
                    default: b.Append(c); break;
                }
            }
            return b.Append('"').ToString();
        }
    }
}
