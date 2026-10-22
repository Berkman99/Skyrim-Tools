using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Core.Build;

public sealed class BuildRequest
{
    public required GameSetup Setup { get; init; }

    /// <summary>The ticked mods (including ticked dependencies) and the NPC looks found in them.</summary>
    public required NpcIndex Index { get; init; }

    /// <summary>Every installed mod from the quick scan (to know which plugins still need a merged mod).</summary>
    public required IReadOnlyList<ModSummary> AllMods { get; init; }

    /// <summary>The look picked for each NPC. NPCs missing from here keep their original look.</summary>
    public required IReadOnlyDictionary<FormKey, NpcLook> Choices { get; init; }

    public required string OutputFolder { get; init; }
    public required string OutputPluginName { get; init; }
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

    /// <summary>Ticked mods that were merged and can be switched off.</summary>
    public List<string> ModsSafeToDisable { get; } = [];

    /// <summary>Ticked mods that were disabled in MO2 and got merged anyway (they stay disabled).</summary>
    public List<string> MergedDisabledMods { get; } = [];

    /// <summary>Ticked mods that stay on, with the reason.</summary>
    public List<(string Mod, string Reason)> ModsToKeep { get; } = [];

    /// <summary>Plugins of the merged mods.</summary>
    public List<string> PluginsToDeactivate { get; } = [];

    /// <summary>Other plugins the new plugin depends on (they stay enabled).</summary>
    public List<string> Masters { get; } = [];

    public List<string> Warnings { get; } = [];

    /// <summary>What to write into the marker / restore on the next build.</summary>
    public List<DeactivatedPlugin> Deactivated { get; } = [];
}

/// <summary>
/// Builds one mod (folder + plugin) from the ticked mods so they can be switched off.
///
/// - Ticked mods are merged whole: every record and every file. Disabled ones are placed in the load order right
///   after their masters.
/// - For every NPC with a pick, the record the game currently uses is kept (AI, voice and stat edits from all
///   mods stay) and only the look is swapped in.
/// Records from mods that aren't ticked are just referenced.
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
        var overhauls = request.Index.Mods.Values.Select(d => d.Summary).OrderByDescending(s => s.Mod.Priority).ToList();

        using var cache = new PluginCache(setup);

        progress?.Report("Working out what can be merged...");
        var plan = MakePlan(request, report);
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
            if (mod == null) { report.Warnings.Add($"Could not open {key.FileName}; it was skipped."); continue; }
            if (IsBuiltByThisTool(mod)) continue;
            mods.Add(mod);
        }
        var linkCache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
        var output = new SkyrimMod(outputKey, GameSetup.Release);
        output.ModHeader.Description = PluginSignature;
        var mapping = new Dictionary<FormKey, FormKey>();
        var visited = new HashSet<FormKey>();

        // 1. Merge the ticked enabled mods, record by record.
        MergeFull(plan, mods, linkCache, output, mapping, report, progress, ct);
        var mergedNpcs = output.Npcs.ToDictionary(n => n.FormKey);

        // 2. NPC looks.
        var tickedPlugins = overhauls.SelectMany(o => o.Plugins).Select(p => ModKey.FromFileName(p.FileName)).ToHashSet();
        var mergeableRecordKeys = mods.Where(m => mergeable.Contains(m.ModKey))
            .SelectMany(m => m.EnumerateMajorRecords()).Select(r => r.FormKey).ToHashSet();

        var copier = new AssetCopier(request.OutputFolder, overhauls, report.Warnings);
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

            var winner = contexts[0];
            var notMerged = contexts.FirstOrDefault(c => !mergeable.Contains(c.ModKey)) ?? contexts[^1];
            var notTicked = contexts.FirstOrDefault(c => !tickedPlugins.Contains(c.ModKey)) ?? notMerged;

            INpcGetter appearance;
            if (look == null)
            {
                // Original look: the new plugin leaves this NPC out (a merged mod's edit of it is dropped), so with
                // the merged mods switched off the game shows the NPC without them. Its face files aren't copied either.
                output.Npcs.Remove(npcKey);
                report.OriginalLooksKept++;
                continue;
            }
            else
            {
                if (!CopyFaceFiles(copier, npcKey, npcKey, look, report)) continue;
                if (look.SourcePlugin is { } sourcePlugin)
                {
                    var source = all.FirstOrDefault(c => c.ModKey == sourcePlugin);
                    if (source == null)
                    {
                        report.Warnings.Add($"Skipped {entry.DisplayName} ({npcKey}): {sourcePlugin} isn't readable.");
                        continue;
                    }
                    appearance = source.Record;
                    report.NpcsChanged++;
                }
                else
                {
                    // Face files only: they were made for the record without any of the ticked mods.
                    report.FaceOnlyNpcs++;
                    if (winner == notTicked && !mergeable.Contains(winner.ModKey)) continue;
                    appearance = notTicked.Record;
                }
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
                $"Some records couldn't be merged, and the new plugin would still need them: {string.Join(", ", dangling)}.\n\n" +
                string.Join("\n", report.Warnings.Where(w => w.StartsWith("Couldn't merge")).Take(5)));

        // 4. Files.
        progress?.Report("Copying meshes and textures...");
        CopyFiles(plan, copier, request, progress);

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
            Overhauls = request.Index.Mods.Keys.OrderBy(x => x).ToList(),
            DisabledMods = report.ModsSafeToDisable.ToList(),
            DeactivatedPlugins = report.Deactivated.ToList(),
            Npcs = request.Choices.ToDictionary(c => c.Key.ToString(), c => c.Value.ModName),
        }.Write(request.OutputFolder);
        progress?.Report("Done.");
        return report;
    }

    /// <summary>
    /// Ticked mods are merged whole, enabled or not. A merged mod stays on only when a plugin that isn't merged
    /// still needs it (the game wouldn't start without it).
    /// </summary>
    private static Plan MakePlan(BuildRequest request, BuildReport report)
    {
        var setup = request.Setup;
        var plan = new Plan();
        var kept = new Dictionary<ModSummary, string>();
        var ticked = request.Index.Mods.Values.Select(d => d.Summary).ToList();

        foreach (var mod in ticked.Where(m => m.Mod.IsGameDataFolder))
            kept[mod] = "Without Mod Organizer 2, mods can't be switched off one by one";

        var full = ticked.Where(m => !m.Mod.IsGameDataFolder).ToList();
        var masters = ActiveMasters(setup, request.AllMods);
        while (true)
        {
            var merged = new Dictionary<string, ModSummary>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in full.Where(m => !kept.ContainsKey(m)))
                foreach (var p in m.Plugins) merged[p.FileName] = m;

            var blocker = masters.Where(p => !merged.ContainsKey(p.Plugin))
                .Select(p => (p.Plugin, Master: p.Masters.FirstOrDefault(merged.ContainsKey)))
                .FirstOrDefault(p => p.Master != null);
            if (blocker.Master != null)
            {
                var owner = setup.PluginOwner(blocker.Plugin)?.Name;
                kept[merged[blocker.Master]] = $"{blocker.Plugin}{(owner == null ? "" : $" ({owner})")} needs {blocker.Master}. Tick it to merge it too.";
                continue;
            }

            plan.FullMods.AddRange(full.Where(m => !kept.ContainsKey(m)));
            foreach (var plugin in merged.Keys) plan.Full.Add(ModKey.FromFileName(plugin));
            plan.LoadOrder = WithDisabledPlugins(setup.LoadOrder, plan.FullMods.Where(m => !m.Mod.CountsAsEnabled).ToList());

            // Everything a merged plugin needs must be in the game, or merged too.
            var inGame = plan.LoadOrder.Select(k => k.FileName.String).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mergedPlugins = plan.FullMods.SelectMany(m => m.Plugins)
                .Where(p => inGame.Contains(p.FileName)).ToList();
            var missing = mergedPlugins.SelectMany(p => p.Masters.Where(m => !inGame.Contains(m)).Select(m => (p.FileName, Master: m))).FirstOrDefault();
            if (missing.Master != null)
                throw new InvalidOperationException(
                    $"{missing.FileName} ({setup.PluginOwner(missing.FileName)?.Name}) needs {missing.Master}, which isn't enabled or ticked.");

            // Skyrim allows at most 254 masters per plugin.
            var needed = mergedPlugins.SelectMany(p => p.Masters)
                .Where(m => !merged.ContainsKey(m)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (needed.Count > Dependents.MaxMasters)
            {
                var deps = new Dependents(request.AllMods);
                var biggest = plan.FullMods.OrderByDescending(deps.MasterCount).Take(3)
                    .Select(m => $"{m.Mod.Name} ({deps.MasterCount(m)})");
                throw new InvalidOperationException(
                    $"Too many masters: merging everything ticked needs {needed.Count}, and Skyrim allows {Dependents.MaxMasters}. " +
                    $"Mods with the most masters: {string.Join(", ", biggest)}.");
            }
            break;
        }

        foreach (var (mod, reason) in kept) report.ModsToKeep.Add((mod.Mod.Name, reason));
        // Mods that were disabled already stay disabled; only the enabled ones need switching off.
        report.ModsSafeToDisable.AddRange(plan.FullMods.Where(m => m.Mod.CountsAsEnabled).Select(m => m.Mod.Name));
        report.MergedDisabledMods.AddRange(plan.FullMods.Where(m => !m.Mod.CountsAsEnabled).Select(m => m.Mod.Name));
        report.PluginsToDeactivate.AddRange(plan.Full.Where(setup.LoadOrder.Contains).Select(k => k.FileName.String));
        return plan;
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
    /// Copies every record of the merged plugins as the game currently sees it: new records become new records in
    /// the output (remapped later), edits to other plugins' records are kept as edits, unless a plugin that stays
    /// enabled overrides them later anyway.
    /// </summary>
    private static void MergeFull(Plan plan, List<ISkyrimModGetter> mods, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        SkyrimMod output, Dictionary<FormKey, FormKey> mapping, BuildReport report, IProgress<string>? progress, CancellationToken ct)
    {
        if (plan.Full.Count == 0) return;
        var done = new HashSet<FormKey>();
        int failures = 0, count = 0;

        // Dialogue lines of topics a merged mod creates are copied together with their topic. Copied on their own,
        // a line would drag in a second copy of its topic under the old ID.
        var linesOfNewTopics = mods.Where(m => plan.Full.Contains(m.ModKey))
            .SelectMany(m => m.DialogTopics).Where(t => plan.Full.Contains(t.FormKey.ModKey))
            .SelectMany(t => t.Responses).Select(r => r.FormKey).ToHashSet();

        foreach (var plugin in mods.Where(m => plan.Full.Contains(m.ModKey)))
        {
            progress?.Report($"Merging {plugin.ModKey.FileName}...");
            foreach (var record in plugin.EnumerateMajorRecords())
            {
                ct.ThrowIfCancellationRequested();
                if (record is IDialogResponsesGetter && linesOfNewTopics.Contains(record.FormKey)) continue;
                if (!done.Add(record.FormKey)) continue;
                try
                {
                    if (!linkCache.TryResolveContext(record.FormKey, record.Registration.GetterType, out var winner)) continue;
                    if (plan.Full.Contains(record.FormKey.ModKey))
                    {
                        // A record this mod creates. Take the latest merged version of it.
                        var ctx = plan.Full.Contains(winner.ModKey) ? winner
                            : linkCache.ResolveAllContexts(record.FormKey, record.Registration.GetterType).First(c => plan.Full.Contains(c.ModKey));
                        var dup = ctx.DuplicateIntoAsNewRecord(output, ctx.Record.EditorID);
                        mapping[record.FormKey] = dup.FormKey;
                        if (dup is DialogTopic topic) CopyTopicLines(topic, record.FormKey, plan, linkCache, output, mapping);
                    }
                    else if (plan.Full.Contains(winner.ModKey))
                    {
                        winner.GetOrAddAsOverride(output);
                    }
                    if (++count % 5000 == 0) progress?.Report($"Merging {plugin.ModKey.FileName}... ({count} records)");
                }
                catch (Exception ex)
                {
                    if (++failures <= 10) report.Warnings.Add($"Couldn't merge {record.FormKey} ({record.EditorID}) from {plugin.ModKey.FileName}: {ex.Message}");
                }
            }
        }
        if (failures > 10) report.Warnings.Add($"...and {failures - 10} more records couldn't be merged.");
    }

    /// <summary>
    /// Fills a newly copied topic with the lines every merged plugin gives it (latest merged version of each).
    /// Lines the merged mods create get new IDs, like every other new record.
    /// </summary>
    private static void CopyTopicLines(DialogTopic topic, FormKey oldTopic, Plan plan, ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        SkyrimMod output, Dictionary<FormKey, FormKey> mapping)
    {
        var lineKeys = topic.Responses.Select(r => r.FormKey)
            .Concat(linkCache.ResolveAllContexts<IDialogTopic, IDialogTopicGetter>(oldTopic)
                .Where(c => plan.Full.Contains(c.ModKey)).SelectMany(c => c.Record.Responses.Select(r => r.FormKey)))
            .Distinct().ToList();
        topic.Responses.Clear();
        foreach (var key in lineKeys)
        {
            var line = linkCache.ResolveAllContexts<IDialogResponses, IDialogResponsesGetter>(key)
                .FirstOrDefault(c => plan.Full.Contains(c.ModKey))?.Record;
            if (line == null) continue;
            if (plan.Full.Contains(key.ModKey))
            {
                var copy = (DialogResponses)line.Duplicate(output.GetNextFormKey());
                mapping[key] = copy.FormKey;
                topic.Responses.Add(copy);
            }
            else topic.Responses.Add(line.DeepCopy());
        }
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
            report.Warnings.Add($"{sourceKey} from {look.ModName} has no face tint file; the face may look darker in game.");
        return true;
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

    private static void CopyFiles(Plan plan, AssetCopier copier, BuildRequest request, IProgress<string>? progress)
    {
        // Face files of NPCs that have looks are decided by the picks (already copied), never by bulk copies,
        // so an NPC set to "Original look" doesn't end up with a merged mod's face.
        var pickedFaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var npc in request.Index.Npcs)
        {
            pickedFaces.Add(FaceGenPaths.NifPath(npc.FormKey));
            pickedFaces.Add(FaceGenPaths.DdsPath(npc.FormKey));
        }
        bool Skip(string rel) => pickedFaces.Contains(rel);

        // Whole mods that get switched off: all their files, highest priority first (first copy wins).
        foreach (var mod in plan.FullMods.OrderByDescending(m => m.Mod.Priority))
        {
            progress?.Report($"Copying files of {mod.Mod.Name}...");
            copier.CopyWholeFolder(mod.Mod.Path, mod.Mod.Name, Skip);
            foreach (var archive in ArchiveHelper.LoadedArchives(mod.Mod.Archives, mod.Plugins.Select(p => p.FileName).ToList()))
                copier.ExtractArchive(Path.Combine(mod.Mod.Path, archive), mod.Mod.Name, Skip);
        }

        // Files the picked looks reference (mainly for disabled mods), and textures used inside copied face meshes.
        var preferredByNpc = request.Choices.ToDictionary(c => c.Key, c => c.Value.Source);
        foreach (var nif in copier.CopiedNifs.Where(n => n.StartsWith(FaceGenPaths.GeomFolder, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ModSummary? preferred = null;
            if (FaceGenPaths.TryParse(nif, out var plugin, out var id8, out _) &&
                FaceGenPaths.ToFormKey(plugin, id8) is { } key && preferredByNpc.TryGetValue(key, out var src))
                preferred = src;
            foreach (var texture in NifTextures.Read(copier.OutputPath(nif)))
                copier.CopyFromOverhauls(texture, preferred);
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
