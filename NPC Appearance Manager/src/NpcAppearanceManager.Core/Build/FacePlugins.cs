using System.Collections.Concurrent;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Core.Build;

/// <summary>
/// For a look that ships only face files: the plugin in your load order whose record the face was made for (for
/// example PAN_AIO_small.esp for Pandorable's Serana, whose own plugin is hidden). The same check the build uses
/// (<see cref="FaceMeshParts"/>), so the look list and the build agree.
/// </summary>
public sealed class FacePlugins
{
    private readonly ILinkCache<ISkyrimMod, ISkyrimModGetter> _cache;
    private readonly ConcurrentDictionary<(FormKey, string), ModKey?> _results = new();

    public FacePlugins(GameSetup setup, PluginCache plugins)
    {
        var mods = setup.LoadOrder.Select(k => plugins.Get(k.FileName.String))
            .OfType<ISkyrimModGetter>()
            .Where(m => !MergeBuilder.IsBuiltByThisTool(m)) // the last build's own copy would always "fit"
            .ToList();
        _cache = mods.ToImmutableLinkCache<ISkyrimMod, ISkyrimModGetter>();
    }

    /// <summary>
    /// The plugin the face was made for, or null when it fits the NPC's own record (Skyrim's, or the mod's that
    /// adds the NPC) or nothing fits.
    /// </summary>
    public ModKey? PluginFor(NpcLook look) =>
        _results.GetOrAdd((look.Npc, look.Source.Mod.Path), _ => Find(look));

    private ModKey? Find(NpcLook look)
    {
        var shapes = ReadShapes(look);
        if (shapes == null) return (ModKey?)null;
        var contexts = _cache.ResolveAllContexts<INpc, INpcGetter>(look.Npc).ToList();
        if (contexts.Count == 0) return null;
        var best = FaceMeshParts.Best(contexts, c => c.Record, shapes, _cache);
        if (best == null) return null;
        var own = contexts[^1].Record; // the record from the plugin that adds the NPC
        bool sameParts = best.Record.HeadParts.Select(h => h.FormKey).ToHashSet().SetEquals(own.HeadParts.Select(h => h.FormKey));
        return sameParts ? (ModKey?)null : best.ModKey;
    }

    private static HashSet<string>? ReadShapes(NpcLook look)
    {
        if (look.Faces.NifLoosePath is { } loose) return FaceMeshParts.ShapeNames(loose);
        if (look.Faces.NifArchive is not { } archive) return null;
        var entry = ArchiveHelper.Find(archive, FaceGenPaths.NifPath(look.Npc));
        if (entry == null) return null;
        var temp = Path.Combine(Path.GetTempPath(), "NAM faces", Guid.NewGuid().ToString("N") + ".nif");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            using (var src = entry.AsStream())
            using (var dst = File.Create(temp))
                src.CopyTo(dst);
            return FaceMeshParts.ShapeNames(temp);
        }
        catch (IOException) { return null; }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }
}
