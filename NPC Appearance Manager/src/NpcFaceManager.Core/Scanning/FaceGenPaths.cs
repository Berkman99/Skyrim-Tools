using System.Globalization;
using Mutagen.Bethesda.Plugins;

namespace NpcFaceManager.Core.Scanning;

/// <summary>
/// The game looks for an NPC's baked face at
/// meshes\actors\character\facegendata\facegeom\&lt;Plugin&gt;\&lt;FormID:X8&gt;.nif (+ the matching facetint .dds),
/// where Plugin is the plugin that originally defines the NPC.
/// </summary>
public static class FaceGenPaths
{
    public const string GeomFolder = @"meshes\actors\character\facegendata\facegeom";
    public const string TintFolder = @"textures\actors\character\facegendata\facetint";

    public static string NifPath(FormKey npc) => $@"{GeomFolder}\{npc.ModKey.FileName}\{npc.ID:X8}.nif";
    public static string DdsPath(FormKey npc) => $@"{TintFolder}\{npc.ModKey.FileName}\{npc.ID:X8}.dds";

    /// <summary>Lower-case key "plugin.esp|0001A2B3" used to match face files to NPC records.</summary>
    public static string Key(FormKey npc) => $"{npc.ModKey.FileName.String.ToLowerInvariant()}|{npc.ID:X8}";

    public static string Key(string plugin, string id8) => $"{plugin.ToLowerInvariant()}|{id8.ToUpperInvariant()}";

    public static bool TryParseId(string fileNameWithoutExtension, out uint id)
    {
        id = 0;
        return fileNameWithoutExtension.Length == 8 &&
               uint.TryParse(fileNameWithoutExtension, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>Builds the NPC FormKey a face file belongs to from its plugin folder and file name.</summary>
    public static FormKey? ToFormKey(string plugin, string id8)
    {
        if (!TryParseId(id8, out var id)) return null;
        try { return new FormKey(ModKey.FromFileName(plugin), id & 0x00FFFFFF); }
        catch { return null; }
    }

    /// <summary>
    /// Splits an archive/loose relative path into (plugin, id8, isNif) if it is a face file.
    /// </summary>
    public static bool TryParse(string relativePath, out string plugin, out string id8, out bool isNif)
    {
        plugin = id8 = "";
        isNif = false;
        var p = relativePath.Replace('/', '\\').TrimStart('\\');
        string prefix;
        if (p.StartsWith(GeomFolder + @"\", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
        {
            prefix = GeomFolder;
            isNif = true;
        }
        else if (p.StartsWith(TintFolder + @"\", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            prefix = TintFolder;
        }
        else return false;

        var rest = p[(prefix.Length + 1)..].Split('\\');
        if (rest.Length != 2) return false;
        var name = Path.GetFileNameWithoutExtension(rest[1]);
        if (!TryParseId(name, out _)) return false;
        plugin = rest[0];
        id8 = name.ToUpperInvariant();
        return true;
    }
}
