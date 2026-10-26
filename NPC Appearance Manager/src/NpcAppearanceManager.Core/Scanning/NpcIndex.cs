using Mutagen.Bethesda.Plugins;
using NpcAppearanceManager.Core.Environment;

namespace NpcAppearanceManager.Core.Scanning;

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

    /// <summary>The NPC's own look from where it comes from: Skyrim Special Edition or the mod that adds it (see <see cref="OriginalSources"/>).</summary>
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
    /// The NPC's own look: from Skyrim Special Edition (the game and Creation Club) or the mod that adds it. When that mod has a face for it this is one of
    /// <see cref="Looks"/>; null when its source has no face for it.
    /// </summary>
    public NpcLook? Original { get; init; }

    /// <summary>Where the NPC comes from: a mod's name or "Skyrim Special Edition".</summary>
    public required string OriginalName { get; init; }

    /// <summary>
    /// For an NPC that takes its look from another NPC (a template): that NPC's own face, used only for the
    /// picture of the original tile.
    /// </summary>
    public NpcLook? OriginalPicture { get; init; }

    /// <summary>The NPC's template is a leveled list: the game gives it one of several faces at random.</summary>
    public bool OriginalIsRandom { get; init; }

    /// <summary>The NPC whose look a templated NPC uses (its picture is that NPC's), when it isn't random.</summary>
    public FormKey? OriginalBorrowedFrom { get; init; }

    /// <summary>The look the game shows right now (null: none of the looks, so the original). Picks start here.</summary>
    public NpcLook? DefaultLook { get; init; }

    /// <summary>The original is one of the loaded looks (the NPC's own mod has a face for it).</summary>
    public bool OriginalIsLook => Original != null && Looks.Contains(Original);

    public string DisplayName => !string.IsNullOrWhiteSpace(Name) ? Name! : EditorId ?? FormKey.ToString();

    public override string ToString() => DisplayName;
}

/// <summary>Per-mod facts discovered while reading a mod's plugins and faces.</summary>
public sealed class ModDetails
{
    public required ModSummary Summary { get; init; }

    /// <summary>Plugins in this mod that add brand-new NPCs. Those can't be merged away.</summary>
    public List<string> PluginsAddingNewNpcs { get; } = [];

    /// <summary>NPCs skipped because they take their looks from a template NPC.</summary>
    public int TemplatedNpcsSkipped { get; set; }

    public int LookCount { get; set; }

    /// <summary>Overhaul, NPC mod or both (see <see cref="ModRoles"/>).</summary>
    public ModRole Role { get; set; }

    /// <summary>Looks for NPCs the mod adds itself, and for NPCs from elsewhere.</summary>
    public int OwnLooks { get; set; }
    public int OthersLooks { get; set; }

    public bool CanBeMergedAway => PluginsAddingNewNpcs.Count == 0;
}

public sealed class NpcIndex
{
    public required IReadOnlyList<NpcEntry> Npcs { get; init; }
    public required IReadOnlyDictionary<string, ModDetails> Mods { get; init; }
}
