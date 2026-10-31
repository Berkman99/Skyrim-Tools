using System.Text.Json;

namespace NpcAppearanceManager.Core.Build;

/// <summary>
/// Written into NAM Output. Marks the folder as made by NAM (so it's never read as a mod with faces) and records when
/// it was saved and which NPCs it changes.
/// </summary>
public sealed class OutputMarker
{
    public const string FileName = "NpcAppearanceManager.output.json";

    public static bool IsMarkerFile(string fileName) => fileName.Equals(FileName, StringComparison.OrdinalIgnoreCase);

    /// <summary>True for a mod folder made by NAM (NAM Output, or a NAM merge from an earlier version).</summary>
    public static bool IsOutputFolder(string folder) => File.Exists(Path.Combine(folder, FileName));

    public DateTime BuiltAt { get; set; }
    public Dictionary<string, string> Npcs { get; set; } = [];

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public void Write(string folder) => File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Options));
}
