using System.Text;

namespace NpcAppearanceManager.Core.Scanning;

/// <summary>
/// Plugins are read as Western text, but translated mods (Russian, Chinese, ...) often store names as UTF-8,
/// which then shows as "ÐšÐ°Ð¿..." garbage. Names are only re-read for display; plugins themselves aren't touched,
/// so the merge still copies their bytes exactly.
/// </summary>
public static class NameText
{
    private static readonly Encoding Western;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    static NameText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Western = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
    }

    public static string? Fix(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.All(c => c < 0x80)) return name;
        try { return StrictUtf8.GetString(Western.GetBytes(name)); }
        catch { return name; } // not UTF-8 after all (e.g. a French name with accents): it was right already
    }

    /// <summary>
    /// The other way round, for a name the merge writes: one that doesn't fit Western text (e.g. typed in Russian)
    /// is stored as UTF-8, the way translated mods store theirs.
    /// </summary>
    public static string ForPlugin(string name)
    {
        try { Western.GetBytes(name); return name; }
        catch { return Western.GetString(Encoding.UTF8.GetBytes(name)); }
    }
}
