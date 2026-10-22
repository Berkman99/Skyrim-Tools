using nifly;

namespace NpcFaceManager.Core.Build;

public static class NifTextures
{
    /// <summary>Texture paths referenced inside a .nif, normalised to "textures\...".</summary>
    public static HashSet<string> Read(string nifPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var nif = new NifFile();
            if (nif.Load(nifPath) != 0) return result;
            var header = nif.GetHeader();
            var blockCount = header.GetNumBlocks();
            for (uint id = 0; id < blockCount; id++)
            {
                if (header.GetBlockById(id) is not BSShaderTextureSet textureSet) continue;
                using var array = textureSet.textures;
                using var items = array.items();
                foreach (NiString tex in items)
                {
                    var path = tex?.get();
                    if (!string.IsNullOrWhiteSpace(path)) result.Add(Normalize(path));
                }
            }
        }
        catch
        {
            // A broken or unusual .nif shouldn't stop the build; its textures just aren't followed.
        }
        return result;
    }

    internal static string Normalize(string texturePath)
    {
        var p = texturePath.Replace('/', '\\').TrimStart('\\');
        var dataIdx = p.IndexOf(@"data\textures\", StringComparison.OrdinalIgnoreCase);
        if (dataIdx >= 0) p = p[(dataIdx + 5)..];
        return p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p : @"textures\" + p;
    }
}
