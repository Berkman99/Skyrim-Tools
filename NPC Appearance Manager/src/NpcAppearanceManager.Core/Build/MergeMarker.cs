using System.Text.Json;

namespace NpcFaceManager.Core.Build;

/// <summary>A plugin the last build switched off, and the plugin it sat after in the load order.</summary>
public sealed record DeactivatedPlugin(string Plugin, string? After);

/// <summary>
/// Written into the output mod folder. Marks the folder as made by this tool, and remembers which mods and
/// plugins the build switched off, so the next build can treat them as they were before the merge.
/// </summary>
public sealed class MergeMarker
{
    public const string FileName = "NpcAppearanceManager.output.json";

    /// <summary>The marker's name from before the rename; builds made then are still recognized.</summary>
    public const string OldFileName = "NpcFaceManager.output.json";

    public static bool IsMarkerFile(string fileName) =>
        fileName.Equals(FileName, StringComparison.OrdinalIgnoreCase) || fileName.Equals(OldFileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a mod folder made by this tool.</summary>
    public static bool IsOutputFolder(string folder) =>
        File.Exists(Path.Combine(folder, FileName)) || File.Exists(Path.Combine(folder, OldFileName));

    public DateTime BuiltAt { get; set; }
    public string Plugin { get; set; } = "";
    public List<string> Overhauls { get; set; } = [];
    public List<string> DisabledMods { get; set; } = [];
    public List<DeactivatedPlugin> DeactivatedPlugins { get; set; } = [];
    public Dictionary<string, string> Npcs { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static MergeMarker? TryRead(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) path = Path.Combine(folder, OldFileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<MergeMarker>(File.ReadAllText(path), Options) : null;
        }
        catch { return null; }
    }

    public void Write(string folder)
    {
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options));
        File.Delete(Path.Combine(folder, OldFileName));
    }
}
