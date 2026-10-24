using nifly;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.Core.Build;

public static class NifTextures
{
    /// <summary>
    /// Files a .nif uses: its textures, normalised to "textures\...", and the physics configs (.xml) it names,
    /// such as SMP hair settings.
    /// </summary>
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
            // Physics configs are named in string extra data, which lives in the file's string table.
            for (uint i = 0; i < header.GetStringCount(); i++)
            {
                var s = header.GetStringById(i);
                if (s != null && s.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) result.Add(NormalizeDataPath(s));
            }
        }
        catch
        {
            // A broken or unusual .nif shouldn't stop the build; its files just aren't followed.
        }
        return result;
    }

    /// <summary>Same as <see cref="Read(string)"/> for a .nif inside an archive.</summary>
    public static HashSet<string> ReadFromArchive(string archivePath, string relativePath)
    {
        var entry = ArchiveHelper.Find(archivePath, relativePath);
        if (entry == null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var temp = Path.Combine(Path.GetTempPath(), "NAM faces", Guid.NewGuid().ToString("N") + ".nif");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            using (var src = entry.AsStream())
            using (var dst = File.Create(temp))
                src.CopyTo(dst);
            return Read(temp);
        }
        catch (IOException) { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    internal static string Normalize(string texturePath)
    {
        var p = NormalizeDataPath(texturePath);
        return p.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? p : @"textures\" + p;
    }

    /// <summary>A path relative to the Data folder, with backslashes ("C:\...\Data\meshes\x.nif" → "meshes\x.nif").</summary>
    internal static string NormalizeDataPath(string path)
    {
        var p = path.Replace('/', '\\').TrimStart('\\');
        var dataIdx = p.IndexOf(@"data\", StringComparison.OrdinalIgnoreCase);
        if (dataIdx >= 0 && (dataIdx == 0 || p[dataIdx - 1] == '\\')) p = p[(dataIdx + 5)..];
        return p;
    }
}
