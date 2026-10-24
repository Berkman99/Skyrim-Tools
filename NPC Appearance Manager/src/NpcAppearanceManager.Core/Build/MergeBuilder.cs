using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Core.Build;

public sealed class BuildRequest
{
    public required GameSetup Setup { get; init; }

    /// <summary>Every mod with faces, what each is (overhaul, NPC mod, both), and the NPC looks found in them.</summary>
    public required NpcIndex Index { get; init; }

    /// <summary>Every installed mod from the quick scan (to know which plugins still need a merged mod).</summary>
    public required IReadOnlyList<ModSummary> AllMods { get; init; }

    /// <summary>The look picked for each NPC. NPCs missing from here keep their original look.</summary>
    public required IReadOnlyDictionary<FormKey, NpcLook> Choices { get; init; }

    /// <summary>
    /// Overhauls whose files were already checked (<see cref="ModContents.CheckOverhauls"/>): a file each replaces that
    /// none of its faces use, or null. Those with one stay on; ones missing here are checked when building.
    /// </summary>
    public IReadOnlyDictionary<ModSummary, string?>? CheckedFiles { get; init; }

    public string OutputFolder { get; init; } = "";
    public string OutputPluginName { get; init; } = "";
}

/// <summary>What the build does with a mod, in the order the Build page lists them.</summary>
public enum PlanGroup { Merged, StaysOn }

/// <summary>What the build will do with one mod, for the list on the Build page.</summary>
public sealed record PlannedMod(string Name, PlanGroup Group, bool IsEnabled, string? Reason)
{
    public bool Merged => Group == PlanGroup.Merged;
}

public sealed class BuildReport
{
    public required string OutputFolder { get; init; }
    public required string PluginPath { get; init; }
    public int NpcsChanged { get; set; }
    public int FaceOnlyNpcs { get; set; }
    public int OriginalLooksKept { get; set; }
    public int RecordsCopied { get; set; }
    public int FilesCopied { get; set; }
    public bool IsLightPlugin { get; set; }

    /// <summary>Mods that were merged and can be switched off.</summary>
    public List<string> ModsSafeToDisable { get; } = [];

    /// <summary>Mods that were disabled in MO2 and got merged anyway (they stay disabled).</summary>
    public List<string> MergedDisabledMods { get; } = [];

    /// <summary>Overhauls (and dependencies) that stay on, with the reason.</summary>
    public List<(string Mod, string Reason)> ModsToKeep { get; } = [];

    /// <summary>Why each merged mod is merged.</summary>
    public Dictionary<string, string> MergeReasons { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plugins of the merged mods.</summary>
    public List<string> PluginsToDeactivate { get; } = [];

    /// <summary>Other plugins the new plugin depends on (they stay enabled).</summary>
    public List<string> Masters { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>What to write into the marker / restore on the next build.</summary>
    public List<DeactivatedPlugin> Deactivated { get; } = [];
}

/// <summary>
/// Builds one mod (folder + plugin) so the overhauls can be switched off.
///
/// - Each enabled overhaul (a mod that only changes other mods' or Skyrim's NPCs) is merged and switched off, or left
///   on, whichever leaves fewer NPCs in the new mod. Merging takes only the faces you keep from it, the files they
///   use, and its edits of other things (outfits, races). From disabled overhauls only the faces picked from them
///   come along; their plugins are placed in the load order right after their masters. NPC mods are never merged.
/// - An NPC is written only when its pick differs from what the game shows once the merged mods are off. Its
///   record and face then always come from the picked look; the record the game currently uses is kept (AI,
///   voice and stat edits from all mods stay) and only the look is swapped in.
/// Records from mods that stay on are just referenced.
/// </summary>
public static class MergeBuilder
{
    public const string PluginSignature = "Built by NPC Appearance Manager";
    private const string OldPluginSignature = "Built by NPC Face Manager";

    public static bool IsBuiltByThisTool(ISkyrimModGetter mod) =>
        mod.ModHeader.Description is PluginSignature or OldPluginSignature;

    private sealed class Plan
    {
        public readonly HashSet<ModKey> Full = [];        // merged whole
        public readonly List<ModSummary> FullMods = [];

        /// <summary>Active plugins plus the merged plugins of disabled mods, each placed right after its masters.</summary>
        public List<ModKey> LoadOrder = [];
    }

    public static async Task<BuildReport> BuildAsync(BuildRequest request, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var setup = request.Setup;
        var report = new BuildReport
        {
            OutputFolder = request.OutputFolder,
            PluginPath = Path.Combine(request.OutputFolder, request.OutputPluginName),
        };
        var outputKey = ModKey.FromFileName(request.OutputPluginName);

        using var cache = new PluginCache(setup);

        progress?.Report("Working out what can be merged...");
        var plan = PlanWithFileCheck(request, report, cache, progress);
        var mergeable = plan.Full;
        var activeSet = plan.LoadOrder.ToHashSet();
        PrepareOutputFolder(request.OutputFolder);

        progress?.Report("Opening the load order...");
        var disabledPaths = plan.FullMods.Where(m => !m.Mod.CountsAsEnabled)
            .SelectMany(m => m.Plugins.Select(p => (Key: ModKey.FromFileName(p.FileName), Path: Path.Combine(m.Mod.Path, p.FileName))))
            .ToDictionary(x => x.Key, x => x.Path);
        var mods = new List<ISkyrimModGetter>();
        foreach (var key in plan.LoadOrder)
        {
            if (key == outputKey) continue;
            var mod = disabledPaths.TryGetValue(key, out var path) ? cache.GetByPath(path) : cache.Get(key.FileName);
            if (mod == null) { report.Warnings.Add($"Couldn't open {key.FileName}, so it was skipped."); continue; }
            if (IsBuiltByThisTool(mod)) continue;
            mods.Add(mod);
        }
        var linkCache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        var output = new SkyrimMod(outputKey, GameSetup.Release);
        output.ModHeader.Description = PluginSignature;
        var mapping = new Dictionary<FormKey, FormKey>();
        var visited = new HashSet<FormKey>();

        var mergeableRecordKeys = mods.Where(m => mergeable.Contains(m.ModKey))
            .SelectMany(m => m.EnumerateMajorRecords()).Select(r => r.FormKey).ToHashSet();

        // 1. What the overhauls that go off change besides faces (outfits, races, armor), with what that uses.
        MergeEdits(plan, request, mods, linkCache, output, mapping, visited, mergeableRecordKeys, report, progress, ct);
        var mergedNpcs = output.Npcs.ToDictionary(n => n.FormKey);

        // 2. NPC looks.
        var mergedMods = plan.FullMods.ToHashSet();
        var switchedOff = plan.FullMods.Where(m => m.Mod.CountsAsEnabled)
            .SelectMany(m => m.Plugins.Select(p => ModKey.FromFileName(p.FileName))).ToHashSet();
        var disabledPlugins = plan.FullMods.Where(m => !m.Mod.CountsAsEnabled)
            .SelectMany(m => m.Plugins.Select(p => ModKey.FromFileName(p.FileName))).ToHashSet();
        var ranking = new LookRanking(setup);

        var copier = new AssetCopier(request.OutputFolder, plan.FullMods.OrderByDescending(m => m.Mod.Priority).ToList(), report.Warnings);
        var newFacePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var entry in request.Index.Npcs)
        {
            ct.ThrowIfCancellationRequested();
            if (++n % 50 == 0) progress?.Report($"Writing NPC {n} of {request.Index.Npcs.Count}...");
            var npcKey = entry.FormKey;
            request.Choices.TryGetValue(npcKey, out var look);

            var all = linkCache.ResolveAllContexts<INpc, INpcGetter>(npcKey).ToList();
            var contexts = all.Where(c => activeSet.Contains(c.ModKey)).ToList();
            if (contexts.Count == 0)
            {
                if (look != null) report.Warnings.Add($"Skipped {entry.DisplayName} ({npcKey}): its record isn't in the load order.");
                continue;
            }

            // An NPC added by a merged mod now lives in the new plugin under a new ID, so its face files move too.
            if (mapping.TryGetValue(npcKey, out var newKey) && mergedNpcs.TryGetValue(newKey, out var movedNpc))
            {
                var faceLook = look ?? entry.Looks.FirstOrDefault(l => l.SourcePlugin == npcKey.ModKey) ?? entry.Looks[0];
                if (!CopyFaceFiles(copier, npcKey, newKey, faceLook, report)) continue;
                newFacePaths.Add(FaceGenPaths.NifPath(newKey));
                if (look?.SourcePlugin is { } plugin && plugin != contexts[0].ModKey && all.FirstOrDefault(c => c.ModKey == plugin) is { } src)
                    CopyAppearance(src.Record, movedNpc);
                report.NpcsChanged++;
                foreach (var link in movedNpc.EnumerateFormLinks().ToList())
                    PullInDependency(link, linkCache, mergeable, mergeableRecordKeys, output, mapping, visited, report);
                continue;
            }

            // The record the game uses now. A disabled mod's version doesn't count: only the faces picked from it
            // come along, not its other edits.
            var winner = contexts.FirstOrDefault(c => !disabledPlugins.Contains(c.ModKey)) ?? contexts[0];

            // What the game shows for this NPC once the merged mods are off: the winning look among mods that stay.
            // When the pick is exactly that, the NPC's look is left alone, so its record and face both keep coming
            // from that mod.
            var shown = ranking.Winner(entry.Looks.Where(l => !mergedMods.Contains(l.Source) && l.Source.Mod.CountsAsEnabled));
            var wanted = look ?? (entry.OriginalIsLook ? entry.Original : null);
            if (wanted == shown)
            {
                output.Npcs.Remove(npcKey);
                if (look == null) report.OriginalLooksKept++;
                // An overhaul that goes off may change more than the look in this NPC's record (a combat style, for
                // example). That edit is kept, with the look of the record the game will use underneath.
                if (switchedOff.Contains(winner.ModKey) &&
                    contexts.FirstOrDefault(c => !mergeable.Contains(c.ModKey)) is { } under &&
                    !OnlyLookDiffers(winner.Record, under.Record))
                {
                    var kept = winner.GetOrAddAsOverride(output);
                    CopyAppearance(under.Record, kept);
                    foreach (var link in kept.EnumerateFormLinks().ToList())
                        PullInDependency(link, linkCache, mergeable, mergeableRecordKeys, output, mapping, visited, report);
                }
                continue;
            }
            if (wanted == null)
            {
                // The original, over an overhaul that stays on: it has to be written, or the overhaul keeps showing.
                if (entry.Original == null)
                {
                    report.Warnings.Add($"{entry.DisplayName} ({npcKey}): its original has no face file, so {shown!.ModName} still shows.");
                    output.Npcs.Remove(npcKey);
                    continue;
                }
                wanted = entry.Original;
                report.OriginalLooksKept++;
            }

            if (!CopyFaceFiles(copier, npcKey, npcKey, wanted, report)) continue;
            INpcGetter appearance;
            if (wanted.RecordFrom is { } from)
            {
                // An original: the record version its own source has (the one its face was made for).
                var own = all.FirstOrDefault(c => from.Contains(c.ModKey));
                if (own == null)
                {
                    report.Warnings.Add($"Skipped {entry.DisplayName} ({npcKey}): its original record isn't readable.");
                    continue;
                }
                appearance = own.Record;
            }
            else if (wanted.SourcePlugin is { } sourcePlugin)
            {
                var source = all.FirstOrDefault(c => c.ModKey == sourcePlugin);
                if (source == null)
                {
                    report.Warnings.Add($"Skipped {entry.DisplayName} ({npcKey}): {sourcePlugin} isn't readable.");
                    continue;
                }
                appearance = source.Record;
                if (look != null) report.NpcsChanged++;
            }
            else
            {
                // Face files only: the record that goes with them is the one whose head parts the face mesh was made
                // with (each shape in a face mesh is named after a head part). That's Skyrim's own record for a face
                // made for it, or a merge like PAN_AIO for a face whose own plugin is hidden. The latest version that
                // fits is used, so fixes and AI edits stay.
                report.FaceOnlyNpcs++;
                var shapes = FaceMeshParts.ShapeNames(Path.Combine(request.OutputFolder, FaceGenPaths.NifPath(npcKey)));
                // Versions in the game come first (the winning one first), then ones from disabled mods being merged
                // (anything else would point at a plugin that isn't in the game).
                var baseRecord = shapes == null ? null
                    : FaceMeshParts.Best(contexts.Concat(all.Where(c => !contexts.Contains(c) && mergeable.Contains(c.ModKey))), c => c.Record, shapes, linkCache);
                if (baseRecord == null)
                {
                    // Nothing fits: keep the NPC's own head parts, as before, and say so.
                    var own = contexts.LastOrDefault(c => c.ModKey == npcKey.ModKey) ?? contexts[^1];
                    var ownParts = HeadPartKeys(own.Record);
                    baseRecord = contexts.FirstOrDefault(c => HeadPartKeys(c.Record).SetEquals(ownParts)) ?? own;
                    report.Warnings.Add($"{entry.DisplayName} ({npcKey}): no plugin has the head parts {wanted.ModName}'s face was made with, so hair or brows may not match the face.");
                }
                if (winner == baseRecord && !mergeable.Contains(winner.ModKey)) continue;
                appearance = baseRecord.Record;
            }

            var npc = winner.GetOrAddAsOverride(output); // returns the copy from step 1 if there is one
            if (!ReferenceEquals(appearance, winner.Record)) CopyAppearance(appearance, npc);

            foreach (var link in npc.EnumerateFormLinks().ToList())
                PullInDependency(link, linkCache, mergeable, mergeableRecordKeys, output, mapping, visited, report);
        }

        // 3. Point everything at the copies made inside the new plugin.
        progress?.Report("Linking copied records...");
        if (mapping.Count > 0) output.RemapLinks(mapping);
        report.RecordsCopied = output.EnumerateMajorRecords().Count();

        // A record or link left under a merged plugin's ID would point at a plugin that gets switched off.
        var dangling = output.EnumerateMajorRecords().Select(r => r.FormKey)
            .Concat(output.EnumerateFormLinks().Select(l => l.FormKey))
            .Where(k => mergeable.Contains(k.ModKey)).Distinct().Take(5).ToList();
        if (dangling.Count > 0)
            throw new InvalidOperationException(
                $"Some records couldn't be merged, and the NAM merge would still need them: {string.Join(", ", dangling)}.\n\n" +
                string.Join("\n", report.Warnings.Where(w => w.StartsWith("Couldn't merge")).Take(5)));

        // 4. Files.
        progress?.Report("Copying meshes and textures...");
        CopyFiles(output, copier, request, progress);

        // 5. Save.
        progress?.Report("Saving the plugin...");
        int newRecords = output.EnumerateMajorRecords().Count(r => r.FormKey.ModKey == outputKey);
        if (newRecords <= 2048)
        {
            output.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
            report.IsLightPlugin = true;
        }
        // The opened plugins tell Mutagen each master's ESM/ESL style, which decides how FormIDs are written.
        var loadOrderForMasters = mods.Where(m => activeSet.Contains(m.ModKey) && !mergeable.Contains(m.ModKey)).ToList();
        await output.BeginWrite.ToPath(report.PluginPath).WithLoadOrder(loadOrderForMasters).WriteAsync();

        report.Masters.AddRange(PluginHeader.Read(report.PluginPath).Masters);
        report.FilesCopied = copier.FilesCopied;
        RecordDeactivations(setup, plan, report);
        new MergeMarker
        {
            BuiltAt = DateTime.Now,
            Plugin = request.OutputPluginName,
            Overhauls = plan.FullMods.Select(m => m.Mod.Name).OrderBy(x => x).ToList(),
            DisabledMods = report.ModsSafeToDisable.ToList(),
            DeactivatedPlugins = report.Deactivated.ToList(),
            Npcs = request.Choices.ToDictionary(c => c.Key.ToString(), c => c.Value.ModName),
        }.Write(request.OutputFolder);
        progress?.Report("Done.");
        return report;
    }

    /// <summary>
    /// The plan, after checking the files of every overhaul about to be switched off: one that replaces files none of
    /// its faces or records use (a body texture, another mod's hair physics) stays on, since switching it off would
    /// change those.
    /// </summary>
    private static Plan PlanWithFileCheck(BuildRequest request, BuildReport report, PluginCache cache, IProgress<string>? progress)
    {
        var pinned = new Dictionary<ModSummary, string>();
        var checkedMods = new Dictionary<ModSummary, string?>(request.CheckedFiles ?? new Dictionary<ModSummary, string?>());
        while (true)
        {
            var trial = new BuildReport { OutputFolder = "", PluginPath = "" };
            var plan = MakePlan(request, trial, pinned);
            bool again = false;
            foreach (var mod in plan.FullMods.Where(m => m.Mod.CountsAsEnabled))
            {
                if (!checkedMods.TryGetValue(mod, out var unused))
                {
                    progress?.Report($"Checking the files of {mod.Mod.Name}...");
                    var plugins = mod.Plugins.Where(p => p.Active).Select(p => cache.Get(p.FileName)).OfType<ISkyrimModGetter>().ToList();
                    checkedMods[mod] = unused = ModContents.ReplacedFile(mod, plugins, request.Setup);
                }
                if (unused == null) continue;
                pinned[mod] = ReplacesReason(unused);
                again = true;
            }
            if (again) continue;

            report.ModsToKeep.AddRange(trial.ModsToKeep);
            foreach (var (mod, reason) in trial.MergeReasons) report.MergeReasons[mod] = reason;
            report.ModsSafeToDisable.AddRange(trial.ModsSafeToDisable);
            report.MergedDisabledMods.AddRange(trial.MergedDisabledMods);
            report.PluginsToDeactivate.AddRange(trial.PluginsToDeactivate);
            return plan;
        }
    }

    private static string ReplacesReason(string file) =>
        $"It replaces files none of its faces use (such as {file}), so it stays on. Faces picked from it go into the NAM merge.";

    /// <summary>
    /// What the build will do with the mods it touches, without building anything: merged first, then the ones that
    /// stay on. Mods the build doesn't touch aren't listed.
    /// </summary>
    public static List<PlannedMod> Preview(BuildRequest request)
    {
        var report = new BuildReport { OutputFolder = "", PluginPath = "" };
        var plan = MakePlan(request, report);
        return PlannedMods(request, plan, report);
    }

    private static List<PlannedMod> PlannedMods(BuildRequest request, Plan plan, BuildReport report)
    {
        var result = plan.FullMods.Select(m => new PlannedMod(m.Mod.Name, PlanGroup.Merged, m.Mod.CountsAsEnabled,
            report.MergeReasons.GetValueOrDefault(m.Mod.Name))).ToList();
        var byName = request.AllMods.ToDictionary(m => m.Mod.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, reason) in report.ModsToKeep)
            result.Add(new PlannedMod(name, PlanGroup.StaysOn, byName.TryGetValue(name, out var m) && m.Mod.CountsAsEnabled, reason));
        return result.OrderBy(p => p.Group).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// The mods to merge. Each enabled overhaul (a mod whose plugins hold nothing but NPC looks) is merged and switched
    /// off, or left on, whichever leaves fewer NPCs in the new mod (<see cref="MergeDecision"/>); the overhauls that
    /// need it are decided with it. Disabled overhauls you picked from give only those faces. NPC mods and mods with
    /// more than looks are never merged. One stays on when a plugin that isn't merged still needs it (the game
    /// wouldn't start without it), when it needs a plugin that isn't in the game, when merging it would go over
    /// Skyrim's 254 masters, or when <paramref name="pinned"/> says so.
    /// </summary>
    private static Plan MakePlan(BuildRequest request, BuildReport report, IReadOnlyDictionary<ModSummary, string>? pinned = null)
    {
        var setup = request.Setup;
        var plan = new Plan();
        var kept = new Dictionary<ModSummary, string>(pinned ?? new Dictionary<ModSummary, string>());
        // Overhauls already known to replace files their faces don't use stay on; they're listed only when the new
        // mod holds NPCs changed away from them (otherwise the build doesn't touch them).
        var fileKept = (request.CheckedFiles ?? new Dictionary<ModSummary, string?>())
            .Where(c => c.Value != null && !kept.ContainsKey(c.Key)).ToDictionary(c => c.Key, c => c.Value!);
        var deps = new Dependents(request.AllMods);

        // A pick is a look other than the one the game shows now.
        var defaults = request.Index.Npcs.ToDictionary(n => n.FormKey, n => n.DefaultLook);
        var picked = request.Choices.Where(c => defaults.GetValueOrDefault(c.Key) != c.Value).Select(c => c.Value.Source).ToHashSet();
        var roles = request.Index.Mods.Values.ToDictionary(d => d.Summary, d => d.Role);

        // An enabled mod is only merged when its plugins hold nothing but NPC looks. Mods like USSEP fix a few faces
        // too, but merging them would take their quests, scripts and every patch for them along.
        bool IsOverhaul(ModSummary m) => roles.GetValueOrDefault(m) == ModRole.Overhaul;
        foreach (var mod in picked.Where(m => m.Mod.CountsAsEnabled && !kept.ContainsKey(m)))
        {
            if (roles.GetValueOrDefault(mod) == ModRole.Both)
                kept[mod] = "It adds NPCs of its own, so it stays on. Faces picked from it go into the NAM merge.";
            else if (IsOverhaul(mod) && !OnlyLooks(mod))
                kept[mod] = "It has more than NPC faces (quests, scripts or other content), so it stays on. Faces picked from it go into the NAM merge.";
        }
        var candidates = roles.Keys.Where(m => m.Mod.CountsAsEnabled && IsOverhaul(m) && OnlyLooks(m))
            .OrderBy(m => m.Mod.Name, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var mod in candidates.Where(m => m.Mod.IsGameDataFolder && picked.Contains(m)))
            kept.TryAdd(mod, "Without Mod Organizer 2, mods can't be switched off one by one.");
        candidates.RemoveAll(m => m.Mod.IsGameDataFolder);
        var disabledPicked = picked.Where(m => !m.Mod.CountsAsEnabled && IsOverhaul(m))
            .OrderBy(m => m.Mod.Name, StringComparer.OrdinalIgnoreCase).ToList();

        var decision = new MergeDecision(request);
        var masters = ActiveMasters(setup, request.AllMods);
        List<IReadOnlyList<ModSummary>> groups;
        HashSet<ModSummary> decided;
        while (true)
        {
            // Overhauls that need another overhaul go with it: switched off together, or both left on.
            var open = candidates.Where(m => !kept.ContainsKey(m) && !fileKept.ContainsKey(m)).ToHashSet();
            // A patch that only changes NPC looks but has no faces of its own (an "optional replacer" plugin)
            // goes with the overhaul it needs too.
            bool IsLookPatch(ModSummary d) => d.Mod.CountsAsEnabled && !d.Mod.IsGameDataFolder && !roles.ContainsKey(d) &&
                                              d.Plugins.Count > 0 && OnlyLooks(d) && !kept.ContainsKey(d);
            groups = Groups(open, m => deps.WithDependencies([m], d => !open.Contains(d) && !IsLookPatch(d)));
            decided = decision.Decide(groups);
            var active = decided.Concat(disabledPicked.Where(m => !kept.ContainsKey(m))).ToList();
            var merged = new Dictionary<string, ModSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in active)
                foreach (var p in m.Plugins) merged[p.FileName] = m;

            var blocker = masters.Where(p => !merged.ContainsKey(p.Plugin))
                .Select(p => (p.Plugin, Master: p.Masters.FirstOrDefault(merged.ContainsKey)))
                .FirstOrDefault(p => p.Master != null);
            if (blocker.Master != null)
            {
                var owner = setup.PluginOwner(blocker.Plugin)?.Name;
                kept[merged[blocker.Master]] = $"{blocker.Plugin}{(owner == null ? "" : $" ({owner})")} needs it.";
                continue;
            }

            var loadOrder = WithDisabledPlugins(setup.LoadOrder, active.Where(m => !m.Mod.CountsAsEnabled).ToList());

            // Everything a merged plugin needs must be in the game, or merged too.
            var inGame = loadOrder.Select(k => k.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mergedPlugins = active.SelectMany(m => m.Plugins.Select(p => (Mod: m, Plugin: p)))
                .Where(x => inGame.Contains(x.Plugin.FileName)).ToList();
            var missing = mergedPlugins.SelectMany(x => x.Plugin.Masters.Where(m => !inGame.Contains(m)).Select(m => (x.Mod, Master: m))).FirstOrDefault();
            if (missing.Master != null)
            {
                kept[missing.Mod] = $"It needs {missing.Master}, which isn't enabled.";
                continue;
            }

            // Skyrim allows at most 254 masters per plugin: the mod with the most masters stays on until it fits.
            var needed = mergedPlugins.SelectMany(x => x.Plugin.Masters)
                .Where(m => !merged.ContainsKey(m)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (needed.Count > Dependents.MaxMasters)
            {
                var biggest = active.MaxBy(deps.MasterCount)!;
                kept[biggest] = $"Too many masters ({deps.MasterCount(biggest)}).";
                continue;
            }

            plan.FullMods.AddRange(active);
            foreach (var plugin in merged.Keys) plan.Full.Add(ModKey.FromFileName(plugin));
            plan.LoadOrder = loadOrder;
            break;
        }

        foreach (var (mod, reason) in kept) report.ModsToKeep.Add((mod.Mod.Name, reason));
        foreach (var (mod, file) in fileKept)
            if (decision.Reason(mod, decided) != null) report.ModsToKeep.Add((mod.Mod.Name, ReplacesReason(file)));
        foreach (var group in groups)
        foreach (var mod in group)
        {
            var reason = roles.ContainsKey(mod) ? decision.Reason(mod, decided)
                : decided.Contains(mod) ? $"It only changes NPC faces and needs {string.Join(", ", group.Where(roles.ContainsKey).Select(m => m.Mod.Name))}, so it goes with it."
                : null;
            if (decided.Contains(mod)) report.MergeReasons[mod.Mod.Name] = reason!;
            else if (reason != null) report.ModsToKeep.Add((mod.Mod.Name, reason));
        }
        foreach (var mod in plan.FullMods.Where(m => !m.Mod.CountsAsEnabled))
            report.MergeReasons[mod.Mod.Name] = "It's disabled, so only the faces you picked from it go into the NAM merge.";
        // Mods that were disabled already stay disabled; only the enabled ones need switching off.
        report.ModsSafeToDisable.AddRange(plan.FullMods.Where(m => m.Mod.CountsAsEnabled).Select(m => m.Mod.Name));
        report.MergedDisabledMods.AddRange(plan.FullMods.Where(m => !m.Mod.CountsAsEnabled).Select(m => m.Mod.Name));
        report.PluginsToDeactivate.AddRange(plan.Full.Where(setup.LoadOrder.Contains).Select(k => k.FileName.String));
        return plan;
    }

    /// <summary>Whether a mod's plugins hold nothing but NPC looks (so it can be merged away).</summary>
    internal static bool OnlyLooks(ModSummary m) => m.Plugins.All(p => p.RecordTypes.All(PluginHeader.AppearanceRecordTypes.Contains));

    /// <summary>Each mod with what has to go along with it; groups that share a mod become one.</summary>
    private static List<IReadOnlyList<ModSummary>> Groups(IReadOnlySet<ModSummary> mods, Func<ModSummary, IEnumerable<ModSummary>> along)
    {
        var groups = new List<HashSet<ModSummary>>();
        foreach (var mod in mods.OrderBy(m => m.Mod.Name, StringComparer.OrdinalIgnoreCase))
        {
            var group = along(mod).Append(mod).ToHashSet();
            foreach (var overlapping in groups.Where(g => g.Overlaps(group)).ToList())
            {
                group.UnionWith(overlapping);
                groups.Remove(overlapping);
            }
            groups.Add(group);
        }
        return groups.Select(g => (IReadOnlyList<ModSummary>)g.ToList()).ToList();
    }

    /// <summary>The load order with the plugins of merged disabled mods added, each right after the last of its masters.</summary>
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

    /// <summary>Masters of every active plugin (from the quick scan, or read from the Data folder).</summary>
    private static List<(string Plugin, IReadOnlyList<string> Masters)> ActiveMasters(GameSetup setup, IReadOnlyList<ModSummary> allMods)
    {
        var byPlugin = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in allMods.Where(m => m.Mod.CountsAsEnabled))
            foreach (var p in mod.Plugins)
                if (setup.PluginOwner(p.FileName) == mod.Mod) byPlugin[p.FileName] = p.Masters;
        var result = new List<(string, IReadOnlyList<string>)>();
        foreach (var key in setup.LoadOrder)
        {
            var name = key.FileName.String;
            if (!byPlugin.TryGetValue(name, out var m))
            {
                var path = setup.ResolvePluginPath(name);
                m = path == null ? [] : PluginHeader.TryRead(path)?.Masters ?? [];
            }
            result.Add((name, m));
        }
        return result;
    }

    /// <summary>
    /// Copies what the enabled overhauls that go off change besides faces: their edits of other plugins' records
    /// (outfits, races, armor, lists), as the game currently sees them, unless a plugin that stays enabled overrides
    /// them later anyway. Their NPC records are left to the NPC step (only faces you keep go in); records they add
    /// themselves come along only when something copied uses them.
    /// </summary>
    private static void MergeEdits(Plan plan, BuildRequest request, List<ISkyrimModGetter> mods, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        SkyrimMod output, Dictionary<FormKey, FormKey> mapping, HashSet<FormKey> visited, HashSet<FormKey> mergeableRecordKeys,
        BuildReport report, IProgress<string>? progress, CancellationToken ct)
    {
        var switchedOff = plan.FullMods.Where(m => m.Mod.CountsAsEnabled)
            .SelectMany(m => m.Plugins.Select(p => ModKey.FromFileName(p.FileName))).ToHashSet();
        if (switchedOff.Count == 0) return;
        var npcsWithLooks = request.Index.Npcs.Select(n => n.FormKey).ToHashSet();
        var done = new HashSet<FormKey>();
        int failures = 0;

        foreach (var plugin in mods.Where(m => switchedOff.Contains(m.ModKey)))
        {
            progress?.Report($"Merging {plugin.ModKey.FileName}...");
            foreach (var record in plugin.EnumerateMajorRecords())
            {
                ct.ThrowIfCancellationRequested();
                if (plan.Full.Contains(record.FormKey.ModKey) || npcsWithLooks.Contains(record.FormKey) || !done.Add(record.FormKey)) continue;
                try
                {
                    if (!linkCache.TryResolveContext(record.FormKey, record.Registration.GetterType, out var winner)) continue;
                    if (!plan.Full.Contains(winner.ModKey)) continue;
                    var copy = winner.GetOrAddAsOverride(output);
                    foreach (var link in copy.EnumerateFormLinks().ToList())
                        PullInDependency(link, linkCache, plan.Full, mergeableRecordKeys, output, mapping, visited, report);
                }
                catch (Exception ex)
                {
                    if (++failures <= 10) report.Warnings.Add($"Couldn't merge {record.FormKey} ({record.EditorID}) from {plugin.ModKey.FileName}: {ex.Message}");
                }
            }
        }
        if (failures > 10) report.Warnings.Add($"...and {failures - 10} more records couldn't be merged.");
    }

    /// <summary>Copies a look's face files to where the game will look for <paramref name="destKey"/>.</summary>
    private static bool CopyFaceFiles(AssetCopier copier, FormKey sourceKey, FormKey destKey, NpcLook look, BuildReport report)
    {
        var srcNif = FaceGenPaths.NifPath(sourceKey);
        var srcDds = FaceGenPaths.DdsPath(sourceKey);
        if (!copier.CopyExact(FaceGenPaths.NifPath(destKey), look.Faces.NifLoosePath, look.Faces.NifArchive, look.ModName, archiveEntry: srcNif))
        {
            report.Warnings.Add($"Skipped {sourceKey} from {look.ModName}: its face mesh could not be read.");
            return false;
        }
        if (!copier.CopyExact(FaceGenPaths.DdsPath(destKey), look.Faces.DdsLoosePath, look.Faces.DdsArchive, look.ModName, archiveEntry: srcDds))
            report.Warnings.Add($"{sourceKey} from {look.ModName} has no face tint file, so the face may look darker in game.");
        return true;
    }

    private static HashSet<FormKey> HeadPartKeys(INpcGetter npc) =>
        npc.HeadParts.Where(h => !h.IsNull).Select(h => h.FormKey).ToHashSet();

    /// <summary>Whether two versions of an NPC record differ only in the look (and the record's version stamps).</summary>
    internal static bool OnlyLookDiffers(INpcGetter a, INpcGetter b)
    {
        var copy = (Npc)a.DeepCopy();
        CopyAppearance(b, copy);
        copy.Configuration.TemplateFlags = b.Configuration.TemplateFlags;
        copy.VersionControl = b.VersionControl;
        copy.Version2 = b.Version2;
        copy.FormVersion = b.FormVersion;
        return copy.Equals(b);
    }

    /// <summary>Copies the fields that make up an NPC's look. Everything else stays from the base record.</summary>
    internal static void CopyAppearance(INpcGetter source, INpc target)
    {
        target.FaceMorph = source.FaceMorph?.DeepCopy();
        target.FaceParts = source.FaceParts?.DeepCopy();
        target.Height = source.Height;
        target.Weight = source.Weight;
        target.TextureLighting = source.TextureLighting;
        target.TintLayers.Clear();
        target.TintLayers.AddRange(source.TintLayers.Select(t => t.DeepCopy()));

        target.Race.SetTo(source.Race.FormKey);
        target.HeadTexture.SetTo(source.HeadTexture.FormKey);
        target.HairColor.SetTo(source.HairColor.FormKey);
        target.WornArmor.SetTo(source.WornArmor.FormKey);
        target.FarAwayModel.SetTo(source.FarAwayModel.FormKey);
        target.HeadParts.Clear();
        foreach (var hp in source.HeadParts.Where(h => !h.IsNull))
            target.HeadParts.Add(hp.FormKey);

        if (source.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female))
            target.Configuration.Flags |= NpcConfiguration.Flag.Female;
        else
            target.Configuration.Flags &= ~NpcConfiguration.Flag.Female;

        // If the base record borrowed its looks from a template but the chosen look doesn't, it must stop
        // borrowing, or the game would ignore the face we just copied.
        if (target.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !NpcScanner.IsTemplatedForLooks(source))
            target.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Traits;
    }

    /// <summary>
    /// Follows a link from a look. If the target is defined by a merged plugin it is copied in as a new record;
    /// if it's an existing record whose winning version comes from a merged plugin, that version is copied in.
    /// Anything else is a record from a mod that stays enabled, so it's left as a plain reference.
    /// </summary>
    private static void PullInDependency(IFormLinkGetter root, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        HashSet<ModKey> mergeable, HashSet<FormKey> mergeableRecordKeys, SkyrimMod output, Dictionary<FormKey, FormKey> mapping,
        HashSet<FormKey> visited, BuildReport report)
    {
        var stack = new Stack<IFormLinkGetter>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var link = stack.Pop();
            if (link.FormKey.IsNull || !visited.Add(link.FormKey)) continue;
            if (mapping.ContainsKey(link.FormKey) || !mergeableRecordKeys.Contains(link.FormKey)) continue;

            IModContext<ISkyrimMod, ISkyrimModGetter, IMajorRecord, IMajorRecordGetter>? winner;
            try
            {
                if (!linkCache.TryResolveContext(link.FormKey, link.Type, out winner)) winner = null;
            }
            catch { winner = null; }
            if (winner == null)
            {
                if (mergeable.Contains(link.FormKey.ModKey))
                    report.Warnings.Add($"Could not find {link.FormKey} in its plugin.");
                continue;
            }
            if (!mergeable.Contains(winner.ModKey)) continue;

            IMajorRecordGetter added;
            if (mergeable.Contains(link.FormKey.ModKey))
            {
                var dup = winner.DuplicateIntoAsNewRecord(output, winner.Record.EditorID);
                mapping[link.FormKey] = dup.FormKey;
                added = dup;
            }
            else
            {
                added = winner.GetOrAddAsOverride(output);
            }

            foreach (var child in added.EnumerateFormLinks())
                if (!child.FormKey.IsNull && !visited.Contains(child.FormKey))
                    stack.Push(child);
        }
    }

    /// <summary>
    /// Copies the files the new mod uses from the merged mods: whatever its records name (head part meshes and
    /// their .tri files, texture sets, armor), then everything named inside each copied mesh (textures, physics
    /// settings), and so on. Face files are already in place from the NPC step; files of faces nobody keeps stay out.
    /// </summary>
    private static void CopyFiles(SkyrimMod output, AssetCopier copier, BuildRequest request, IProgress<string>? progress)
    {
        foreach (var record in output.EnumerateMajorRecords())
            foreach (var asset in record.EnumerateAssetLinks(AssetLinkQuery.Listed))
                copier.CopyFromOverhauls(NifTextures.NormalizeDataPath(asset.DataRelativePath.Path), null);

        // A face mesh's textures come from the mod its face was picked from first.
        var preferredByNpc = request.Choices.ToDictionary(c => c.Key, c => c.Value.Source);
        var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (copier.CopiedNifs.Where(n => !read.Contains(n)).ToList() is { Count: > 0 } nifs)
        {
            progress?.Report($"Copying meshes and textures... ({copier.FilesCopied:N0} files)");
            foreach (var nif in nifs)
            {
                read.Add(nif);
                ModSummary? preferred = null;
                if (FaceGenPaths.TryParse(nif, out var plugin, out var id8, out _) &&
                    FaceGenPaths.ToFormKey(plugin, id8) is { } key && preferredByNpc.TryGetValue(key, out var src))
                    preferred = src;
                foreach (var file in NifTextures.Read(copier.OutputPath(nif)))
                    copier.CopyFromOverhauls(file, preferred);
            }
        }
    }

    /// <summary>Remembers where each switched-off plugin sat, so the next build can put it back.</summary>
    private static void RecordDeactivations(GameSetup setup, Plan plan, BuildReport report)
    {
        string? previous = null;
        foreach (var key in setup.LoadOrder)
        {
            var name = key.FileName.String;
            if (plan.Full.Contains(key)) report.Deactivated.Add(new DeactivatedPlugin(name, previous));
            else previous = name;
        }
    }

    private static void PrepareOutputFolder(string folder)
    {
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
        {
            if (!MergeMarker.IsOutputFolder(folder))
                throw new InvalidOperationException(
                    $"The output folder already exists and wasn't made by this tool: {folder}. Pick another output name.");
            foreach (var dir in Directory.GetDirectories(folder)) Directory.Delete(dir, true);
            // The old marker stays until the new build finishes, so a failed build still remembers what the
            // previous one switched off.
            foreach (var file in Directory.GetFiles(folder))
                if (!Path.GetFileName(file).Equals("meta.ini", StringComparison.OrdinalIgnoreCase) &&
                    !MergeMarker.IsMarkerFile(Path.GetFileName(file)))
                    File.Delete(file);
        }
        Directory.CreateDirectory(folder);
        if (!MergeMarker.IsOutputFolder(folder)) new MergeMarker().Write(folder);
    }
}
