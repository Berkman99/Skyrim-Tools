using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using nifly;

namespace NpcFaceManager.Core.Build;

/// <summary>
/// Which record a face mesh was made for. The Creation Kit names every shape in a face mesh after the head part it
/// came from (hair, brows, eyes, the head and their extra parts), so the record whose head parts are the mesh's
/// shapes is the one the face belongs to. Placeholders without a model (an empty scar) leave no shape and don't count; the best
/// match wins rather than requiring every part.
/// </summary>
public static class FaceMeshParts
{
    /// <summary>The shape names in a face mesh, or null if it can't be read.</summary>
    public static HashSet<string>? ShapeNames(string nifPath)
    {
        try
        {
            if (!File.Exists(nifPath)) return null;
            using var nif = new NifFile();
            if (nif.Load(nifPath) != 0) return null;
            var names = nif.GetShapes().Select(s => s.name.get()).Where(n => !string.IsNullOrEmpty(n));
            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>How well a record fits a face mesh: its head parts that are shapes, minus those that aren't.</summary>
    public static int Score(INpcGetter npc, IReadOnlySet<string> shapes, ILinkCache linkCache)
    {
        int score = 0;
        foreach (var link in npc.HeadParts)
        {
            if (link.IsNull) continue;
            if (!linkCache.TryResolve<IHeadPartGetter>(link.FormKey, out var part)) { score--; continue; }
            // Placeholders like "no scar" or "no beard" have no model, so they never become a shape.
            if (string.IsNullOrEmpty(part.Model?.File.DataRelativePath.Path)) continue;
            score += part.EditorID is { } id && shapes.Contains(id) ? 1 : -1;
        }
        return score;
    }

    /// <summary>
    /// The candidate that fits the face best (the first one on a tie, so put the preferred versions first), or
    /// default when every one has more head parts missing from the face than in it. A record with no head parts
    /// of its own (it uses its race's) fits a face made from those defaults.
    /// </summary>
    public static T? Best<T>(IEnumerable<T> candidates, Func<T, INpcGetter> record, IReadOnlySet<string> shapes, ILinkCache linkCache)
    {
        T? best = default;
        int bestScore = -1;
        foreach (var c in candidates)
        {
            var score = Score(record(c), shapes, linkCache);
            if (score > bestScore) { best = c; bestScore = score; }
        }
        return best;
    }
}
