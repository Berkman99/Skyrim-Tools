using nifly;

namespace NpcAppearanceManager.Core.Mugshots;

/// <summary>Checks on a face mesh before it's rendered.</summary>
public static class FaceMeshCheck
{
    /// <summary>
    /// Taller than any part of a head can be (a whole head is about 25 units; hair down to the waist about 70).
    /// </summary>
    private const float MaxShapeHeight = 90f;

    /// <summary>
    /// True when a shape in the face mesh has vertices far away from the rest (some meshes ship eyelashes or brows
    /// with stray vertices near the feet). Framing the picture around such a shape makes the head tiny.
    /// </summary>
    public static bool HasStrayVertices(string nifPath)
    {
        try
        {
            using var nif = new NifFile();
            if (nif.Load(nifPath) != 0) return false;
            foreach (var shape in nif.GetShapes())
            {
                using var verts = nif.GetVertsForShape(shape);
                if (verts.Count == 0) continue;
                float min = float.MaxValue, max = float.MinValue;
                foreach (var v in verts)
                {
                    min = Math.Min(min, v.z);
                    max = Math.Max(max, v.z);
                }
                if (max - min > MaxShapeHeight) return true;
            }
        }
        catch
        {
            // An unreadable mesh is left to the renderer.
        }
        return false;
    }
}
