using Mutagen.Bethesda.Plugins;
using NpcFaceManager.Core.Environment;

namespace NpcFaceManager.Core.Scanning;

/// <summary>Where one NPC's face mesh and face tint live inside one mod.</summary>
public sealed class FaceFiles
{
    public string? NifLoosePath { get; set; }
    public string? DdsLoosePath { get; set; }
    public string? NifArchive { get; set; }
    public string? DdsArchive { get; set; }

    public bool HasNif => NifLoosePath != null || NifArchive != null;
    public bool HasDds => DdsLoosePath != null || DdsArchive != null;
}

/// <summary>One available look for an NPC: "this NPC as it appears in this mod".</summary>
public sealed class NpcLook
{
    public required ModSummary Source { get; init; }
    public required FormKey Npc { get; init; }

    /// <summary>The mod's plugin that edits this NPC, or null when the mod only ships face files.</summary>
    public ModKey? SourcePlugin { get; init; }

    public required FaceFiles Faces { get; init; }

    /// <summary>The NPC's own look from where it comes from: Skyrim, Creation Club or the mod that adds it (see <see cref="OriginalSources"/>).</summary>
    public bool IsOriginal { get; init; }

    /// <summary>For an original look: the plugins whose (latest) version of the NPC record goes with its face.</summary>
    public IReadOnlySet<ModKey>? RecordFrom { get; init; }

    public string ModName => Source.Mod.Name;
    public bool IsFacesOnly => SourcePlugin == null;
}

public sealed class NpcEntry
{
    public required FormKey FormKey { get; init; }
    public string? EditorId { get; init; }
    public string? Name { get; init; }
    public bool IsFemale { get; init; }

    /// <summary>Available looks, highest mod priority first.</summary>
    public required List<NpcLook> Looks { get; init; }

    /// <summary>
    /// The NPC's own look: from Skyrim, Creation Club or the mod that adds it. When that mod is ticked this is one of
    /// <see cref="Looks"/>; null when its source has no face for it.
    /// </summary>
    public NpcLook? Original { get; init; }

    /// <summary>Where the NPC comes from: a mod's name, "Skyrim" or "Creation Club".</summary>
    public required string OriginalName { get; init; }

    /// <summary>The original is one of the ticked mods' looks (the NPC is added by a ticked mod).</summary>
    public bool OriginalIsLook => Original != null && Looks.Contains(Original);

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name! : EditorId ?? FormKey.ToString();

    public override string ToString() => DisplayName;
}

/// <summary>Per-mod facts discovered while reading a ticked mod's plugins.</summary>
public sealed class ModDetails
{
    public required ModSummary Summary { get; init; }

    /// <summary>Plugins in this mod that add brand-new NPCs. Those can't be merged away.</summary>
    public List<string> PluginsAddingNewNpcs { get; } = [];

    /// <summary>NPCs skipped because they take their looks from a template NPC.</summary>
    public int TemplatedNpcsSkipped { get; set; }

    public int LookCount { get; set; }

    public bool CanBeMergedAway => PluginsAddingNewNpcs.Count == 0;
}

public sealed class NpcIndex
{
    public required IReadOnlyList<NpcEntry> Npcs { get; init; }
    public required IReadOnlyDictionary<string, ModDetails> Mods { get; init; }
}
