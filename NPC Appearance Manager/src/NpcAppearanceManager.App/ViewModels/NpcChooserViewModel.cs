using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Mutagen.Bethesda.Plugins;
using NpcAppearanceManager.Core.Build;
using NpcAppearanceManager.Core.Mugshots;
using NpcAppearanceManager.Core.Scanning;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.App.ViewModels;

public partial class NpcRowViewModel : ObservableObject
{
    public NpcEntry Entry { get; }

    /// <summary>Chosen mod name, or "" for the original look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoiceText))]
    [NotifyPropertyChangedFor(nameof(IsChanged))]
    [NotifyPropertyChangedFor(nameof(ShowCopyBar))]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    [NotifyPropertyChangedFor(nameof(LookCount))]
    [NotifyPropertyChangedFor(nameof(LookCountText))]
    private string _chosenMod;

    /// <summary>The name you gave it (null: its own). Shown as "Own name (New name)"; the game shows the new one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private string? _customName;

    /// <summary>"Copy faces": the NPC whose face it gets, and which of its faces (a pick key, like <see cref="ChosenMod"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCopy))]
    [NotifyPropertyChangedFor(nameof(ShowCopyBar))]
    [NotifyPropertyChangedFor(nameof(CopyText))]
    [NotifyPropertyChangedFor(nameof(IsLocked))]
    private (NpcRowViewModel From, string Face)? _copy;

    public bool HasCopy => Copy != null;

    /// <summary>Pick faces' "copied face" bar: only when the pick there differs from what the game shows (it waits on the copy).</summary>
    public bool ShowCopyBar => HasCopy && IsChanged;

    /// <summary>Copy faces' NPC 1 list: where the copied face comes from (null: nothing copied).</summary>
    public string? CopyText => Copy is not { } c ? null
        : c.Face.Length == 0 ? c.From.Entry.OriginalName
        : c.From.Online.FirstOrDefault(o => o.ChoiceKey == c.Face) is { } online ? $"{online.Mod.Name} (not installed)"
        : c.Face;

    /// <summary>Amber in Copy faces' NPC 2 list: the selected NPC 1 has its face.</summary>
    [ObservableProperty] private bool _isCopySource;

    /// <summary>Its face was changed in Pick faces (away from what the game shows now).</summary>
    public bool IsChanged => ChosenMod != DefaultChoice(Entry);

    /// <summary>Locked in Copy faces' NPC 1 list: changed in Pick faces, with no face copied onto it.</summary>
    public bool IsLocked => IsChanged && Copy == null;

    /// <summary>Greyed out in Copy faces' NPC 2 list: it's the NPC 1 selected there.</summary>
    [ObservableProperty] private bool _isCopyTarget;

    /// <summary>One of its faces by pick key ("" the original), as a face to copy; null when there's none to copy.</summary>
    public NpcLook? CopyableLook(string key) =>
        key.Length == 0 ? Entry.Original ?? Entry.OriginalPicture : Entry.Looks.FirstOrDefault(l => l.ModName == key);

    public NpcRowViewModel(NpcEntry entry, string chosenMod)
    {
        Entry = entry;
        _chosenMod = chosenMod;
    }

    /// <summary>Faces from FaceFinder of mods you don't have installed.</summary>
    public IReadOnlyList<OnlineLook> Online { get; private set; } = [];

    /// <summary>A saved pick of a FaceFinder face ("ff:id") waiting for FaceFinder's list to be loaded.</summary>
    public string? PendingOnlinePick { get; set; }

    public void SetOnline(IReadOnlyList<OnlineLook> online)
    {
        Online = online;
        OnPropertyChanged(nameof(LookCount));
        OnPropertyChanged(nameof(LookCountText));
        OnPropertyChanged(nameof(ChoiceText));
    }

    public void RefreshLookCount()
    {
        OnPropertyChanged(nameof(LookCount));
        OnPropertyChanged(nameof(LookCountText));
    }

    public string DisplayName => CustomName == null ? Entry.DisplayName : $"{Entry.DisplayName} ({CustomName})";
    public string Details => Entry.EditorId == null ? Entry.FormKey.ToString() : $"{Entry.EditorId}  ·  {Entry.FormKey}";
    public string ChoiceText => ChosenMod.Length == 0 ? Entry.OriginalName
        : ChosenOnline is { } online ? $"{online.Mod.Name} (not installed)"
        : ChosenMod;
    /// <summary>"Hide mods not installed" (Settings): FaceFinder faces of mods you don't have aren't shown, except the picked one.</summary>
    public static bool HideNotInstalled { get; set; }

    /// <summary>The FaceFinder faces shown for it (all, or only the picked one with "Hide mods not installed").</summary>
    public IEnumerable<OnlineLook> ShownOnline => HideNotInstalled ? Online.Where(o => o.ChoiceKey == ChosenMod) : Online;

    public int LookCount => Entry.Looks.Count + ShownOnline.Count();
    public string LookCountText => LookCount == 1 ? "1 face" : $"{LookCount} faces";

    public NpcLook? ChosenLook => Entry.Looks.FirstOrDefault(l => l.ModName == ChosenMod);
    public OnlineLook? ChosenOnline => Online.FirstOrDefault(o => o.ChoiceKey == ChosenMod);

    /// <summary>Where a pick starts: the look the game shows right now ("" when that's the original).</summary>
    public static string DefaultChoice(NpcEntry entry) => entry.DefaultLook?.ModName ?? "";
}

public enum TileKind
{
    /// <summary>A look from a loaded mod.</summary>
    Look,
    /// <summary>The NPC's own look from where it comes from, named after its source.</summary>
    Original,
    /// <summary>A mugshot of a mod that isn't loaded; it can't be picked.</summary>
    Unavailable,
    /// <summary>A FaceFinder face of a mod that isn't installed: it can be picked, and built once the mod is installed.</summary>
    Online,
}

public partial class LookTileViewModel : ObservableObject
{
    private readonly NpcChooserViewModel _owner;
    public string ModName { get; }
    public NpcLook? Look { get; }
    public FormKey Npc { get; }
    public TileKind Kind { get; }

    /// <summary>For an unavailable tile: its mugshot, and why it can't be picked.</summary>
    public string? MugshotPath { get; init; }
    public string? UnavailableReason { get; init; }
    public string? UnavailableMessage { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private string _pictureStatus = "Loading...";

    public LookTileViewModel(NpcChooserViewModel owner, string modName, NpcLook? look, FormKey npc, TileKind kind)
    {
        _owner = owner;
        ModName = modName;
        Look = look;
        Npc = npc;
        Kind = kind;
    }

    /// <summary>For an online tile: the FaceFinder face.</summary>
    public OnlineLook? OnlineLook { get; init; }

    /// <summary>What a click does instead of picking the face for the NPC (Copy faces copies it).</summary>
    public Action<LookTileViewModel>? OnClick { get; init; }

    /// <summary>"Use this mod for all its NPCs" only makes sense in Pick faces.</summary>
    public bool CanUseForAll => OnClick == null;

    public bool IsOriginal => Kind == TileKind.Original;
    public bool IsUnavailable => Kind == TileKind.Unavailable;
    public bool IsOnline => Kind == TileKind.Online;

    public string ToolTip => OnlineLook is { } o
        ? $"{o.Mod.Name} isn't installed. You can still pick it, but install the mod before building."
        : FacePlugin is { } p ? $"{ModName}: only face files, made for {p}'s record (the build uses that record)"
        : OriginalNoteToolTip ?? ModName;

    /// <summary>A ticked mod's look that is also the NPC's original (the mod adds the NPC).</summary>
    public bool IsOwnModLook { get; init; }

    /// <summary>For a pluginless look: the plugin in your load order whose record its face was made for (found in the background).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption))]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string? _facePlugin;

    /// <summary>Replaces the caption (and the tooltip) of an original tile, e.g. for a random face.</summary>
    public string? OriginalNote { get; init; }
    public string? OriginalNoteToolTip { get; init; }

    /// <summary>Shown instead of "No mugshot" when there is none (e.g. a random face).</summary>
    public string? NoPictureText { get; init; }

    public string Caption => Kind switch
    {
        TileKind.Original => OriginalNote ?? "original",
        TileKind.Unavailable => UnavailableReason ?? "",
        TileKind.Online => "Not installed",
        _ when IsOwnModLook => "original",
        _ => Look!.IsFacesOnly ? FacePlugin ?? "Pluginless" : Look.SourcePlugin?.FileName.String ?? "",
    };

    public async Task LoadImageAsync(CancellationToken ct)
    {
        try
        {
            // Pictures already on disk (your mugshots, saved FaceFinder pictures, earlier renders) show at once:
            // no "Loading...", just the time to read the file.
            var saved = MugshotPath ?? (OnlineLook != null ? _owner.FindSavedOnline(OnlineLook, Npc) : _owner.FindSaved(ModName, Look, Npc));
            if (saved != null)
            {
                PictureStatus = "";
                await ShowAsync(saved, ct);
                return;
            }

            var task = OnlineLook != null ? _owner.GetOnlinePicture(OnlineLook, Npc) : _owner.GetPicture(ModName, Look, Npc);
            var path = task.IsCompletedSuccessfully ? task.Result : await task;
            if (ct.IsCancellationRequested) return;

            // A FaceFinder picture not saved yet is downloaded when its tile is shown, then saved.
            if (path != null && MugshotService.IsWebPicture(path))
            {
                var image = await _owner.LoadWebBitmapAsync(path, ct);
                if (ct.IsCancellationRequested) return;
                Image = image;
                PictureStatus = image == null ? "No mugshot" : "";
                return;
            }
            await ShowAsync(path, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MugshotService.Log($"Picture for {Npc} ({ModName}) failed: {ex}");
            PictureStatus = "No mugshot";
        }
    }

    private async Task ShowAsync(string? path, CancellationToken ct)
    {
        if (path == null) { PictureStatus = NoPictureText ?? (_owner.RenderingWaits ? "Waiting for FaceFinder" : "No mugshot"); return; }
        var image = await _owner.LoadBitmapAsync(path);
        if (ct.IsCancellationRequested) return;
        Image = image;
        PictureStatus = image == null ? "Mugshot couldn't be read" : "";
    }

    [RelayCommand] private void Select() => (OnClick ?? _owner.Choose)(this);
    [RelayCommand] private void UseForAll() => _owner.UseModForAll(this);
    [RelayCommand] private void DontUseForAll() => _owner.DontUseModForAll(this);
    [RelayCommand] private void LinkMugshotFolder() => _owner.LinkMugshotFolder(ModName);

    [RelayCommand]
    private void OpenModPage()
    {
        if (OnlineLook?.Mod.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

public partial class NpcChooserViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _tileLoading;
    private CancellationTokenSource? _pictureJob;
    private readonly ConcurrentDictionary<(FormKey, string), Task<string?>> _pictures = new();
    private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private int _picturesDone, _picturesTotal;
    public const string AllModsFilter = "All mods";

    // Decoded pictures of recently shown NPCs, so going back to an NPC shows them instantly.
    private const int BitmapCacheSize = 300;
    private readonly Dictionary<string, LinkedListNode<(string Path, BitmapImage Image)>> _bitmaps = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<(string Path, BitmapImage Image)> _bitmapOrder = new();

    public ObservableCollection<NpcRowViewModel> Rows { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<LookTileViewModel> Tiles { get; } = [];
    public ObservableCollection<string> ModFilters { get; } = [];
    private readonly NpcIndex _index;

    [ObservableProperty] private string _searchText;
    [ObservableProperty] private string _modFilter;
    [ObservableProperty] private bool _showSingleLook;
    [ObservableProperty] private NpcRowViewModel? _selectedNpc;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private double _pictureSize;
    [ObservableProperty] private string _pictureProgress = "";

    public NpcChooserViewModel(MainViewModel main, NpcIndex index)
    {
        _main = main;
        var settings = main.Settings;
        NpcRowViewModel.HideNotInstalled = settings.UseFaceFinder && settings.HideNotInstalled;
        var saved = main.Selections.Choices;
        foreach (var entry in index.Npcs)
        {
            saved.TryGetValue(entry.FormKey.ToString(), out var c);
            var choice = c != null && (c.Length == 0 || entry.Looks.Any(l => l.ModName == c))
                ? c
                : NpcRowViewModel.DefaultChoice(entry); // the look the game shows right now
            AddRow(new NpcRowViewModel(entry, choice) { PendingOnlinePick = c != null && OnlineLook.ModIdOf(c) != null ? c : null });
        }
        _index = index;
        _showDisabledOverhauls = settings.ShowDisabledOverhauls;
        FillModFilters();
        if (main.Catalog != null) AddOnlineLooks(main.Catalog);
        RestoreCopiesAndNames();

        _searchText = settings.NpcSearch;
        _modFilter = settings.NpcModFilter is { } f && ModFilters.Contains(f) ? f : AllModsFilter;
        _showSingleLook = settings.ShowSingleLookNpcs;
        _pictureSize = Math.Clamp(settings.PictureSize, 120, 400);

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = Filter;
        // NPCs only FaceFinder has looks for are added at the end; the list stays in name order.
        if (View is ListCollectionView list)
            list.CustomSort = Comparer<object>.Create((a, b) =>
                StringComparer.CurrentCultureIgnoreCase.Compare(((NpcRowViewModel)a).DisplayName, ((NpcRowViewModel)b).DisplayName));
        var last = main.Selections.LastSelectedNpc;
        SelectedNpc = View.Cast<NpcRowViewModel>().FirstOrDefault(r => r.Entry.FormKey.ToString() == last)
                      ?? View.Cast<NpcRowViewModel>().FirstOrDefault();
        UpdateSummary();

        _progressTimer.Tick += (_, _) => UpdatePictureProgress();
        StartPictureJob();
    }

    private bool Filter(object o) => o is NpcRowViewModel r && Matches(r, SearchText, ModFilter, ShowSingleLook);

    /// <summary>Whether an NPC list with this search and these filters shows the NPC.</summary>
    public static bool Matches(NpcRowViewModel r, string search, string modFilter, bool showSingleLook)
    {
        // An NPC only FaceFinder has faces for, with those hidden, has nothing to show.
        if (r.Entry.Looks.Count == 0 && r.Online.Count > 0 && !r.ShownOnline.Any()) return false;
        if (!showSingleLook && r.LookCount < 2) return false;
        if (modFilter != AllModsFilter && !r.Entry.Looks.Any(l => l.ModName == modFilter)) return false;
        if (search.Length == 0) return true;
        return r.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
               (r.Entry.EditorId?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
               r.Entry.FormKey.ToString().Contains(search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A list over the same NPCs (Copy faces has two), in name order, filtered by <paramref name="filter"/>.</summary>
    public ListCollectionView NewView(Predicate<object> filter) => new(Rows)
    {
        Filter = filter,
        CustomSort = Comparer<object>.Create((a, b) =>
            StringComparer.CurrentCultureIgnoreCase.Compare(((NpcRowViewModel)a).DisplayName, ((NpcRowViewModel)b).DisplayName)),
    };

    /// <summary>The search or a filter changed (Copy faces' NPC 1 list uses the same ones).</summary>
    public event Action? FilterChanged;

    /// <summary>Shows the NPCs that match the search and filters now (in every list using them).</summary>
    public void RefreshLists()
    {
        View.Refresh();
        FilterChanged?.Invoke();
    }

    partial void OnSearchTextChanged(string value)
    {
        RefreshLists();
        _main.Settings.NpcSearch = value;
        _main.SaveSettingsSoon();
    }

    partial void OnModFilterChanged(string value)
    {
        RefreshLists();
        _main.Settings.NpcModFilter = value;
        _main.SaveSettings();
    }

    /// <summary>
    /// The filter lists the mods that change NPCs' looks: overhauls (also ones with other content) and mods that add
    /// NPCs and change others. Disabled ones only with "Show disabled".
    /// </summary>
    private void FillModFilters() => FillModFilters(ModFilters, ShowDisabledOverhauls);

    public void FillModFilters(ObservableCollection<string> target, bool showDisabled)
    {
        var names = _index.Mods.Values
            .Where(d => d.Role is ModRole.Overhaul or ModRole.Both && (showDisabled || d.Summary.Mod.CountsAsEnabled))
            .Select(d => d.Summary.Mod.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase);
        target.Clear();
        target.Add(AllModsFilter);
        foreach (var n in names) target.Add(n);
    }

    [ObservableProperty] private bool _showDisabledOverhauls;

    partial void OnShowDisabledOverhaulsChanged(bool value)
    {
        var current = ModFilter;
        FillModFilters();
        ModFilter = ModFilters.Contains(current) ? current : AllModsFilter;
        _main.Settings.ShowDisabledOverhauls = value;
        _main.SaveSettings();
    }

    partial void OnShowSingleLookChanged(bool value)
    {
        RefreshLists();
        _main.Settings.ShowSingleLookNpcs = value;
        _main.SaveSettings();
    }

    /// <summary>The NPC list's width (the divider you drag), kept.</summary>
    public GridLength ListWidth
    {
        get => new(Math.Max(240, _main.Settings.NpcListWidth));
        set { _main.Settings.NpcListWidth = value.Value; _main.SaveSettingsSoon(); }
    }

    /// <summary>How far down the NPC list is scrolled, kept.</summary>
    public double ListScroll
    {
        get => _main.Settings.NpcListScroll;
        set { _main.Settings.NpcListScroll = value; _main.SaveSettingsSoon(); }
    }

    partial void OnPictureSizeChanged(double value)
    {
        _main.Settings.PictureSize = value;
        _main.SaveSettingsSoon();
    }

    partial void OnSelectedNpcChanged(NpcRowViewModel? value)
    {
        ShowTiles(value);
        ResetPickCommand.NotifyCanExecuteChanged();
        RemoveCopyCommand.NotifyCanExecuteChanged();
        if (value == null) return;
        _main.Selections.LastSelectedNpc = value.Entry.FormKey.ToString();
        _main.SaveSettingsSoon();
    }

    /// <summary>
    /// The picture for one look. Each one is looked up (or downloaded/generated) once; the background job and the
    /// tiles share the result, so clicking an NPC that isn't done yet simply starts its pictures right away.
    /// </summary>
    public Task<string?> GetPicture(string modName, NpcLook? look, FormKey npc)
    {
        var key = (npc, modName);
        // Until FaceFinder's list is there, nothing is rendered: FaceFinder may have this face. The look is looked
        // up again once the list arrives (the tiles are shown again then).
        if (RenderingWaits) return Task.Run(() => _main.Mugshots.GetAsync(modName, look, npc, CancellationToken.None, render: false));
        var task = _pictures.GetOrAdd(key, _ => Task.Run(() => _main.Mugshots.GetAsync(modName, look, npc, CancellationToken.None)));
        if (task.IsFaulted || task.IsCanceled) _pictures.TryRemove(key, out _);
        return task;
    }

    /// <summary>A renderer is on, but FaceFinder's list isn't there yet.</summary>
    public bool RenderingWaits => _main.Mugshots.Generator != null && _main.Settings.Generator != MugshotGeneratorKind.Off && _main.WaitingForCatalog;

    /// <summary>The picture of a FaceFinder face (from your mugshots folder if you have one, else downloaded).</summary>
    public Task<string?> GetOnlinePicture(OnlineLook online, FormKey npc)
    {
        var key = (npc, online.ChoiceKey);
        return _pictures.GetOrAdd(key, _ => Task.FromResult(_main.Mugshots.GetOnline(online.Face, npc)));
    }

    // FaceFinder pictures being downloaded, so two tiles (or the NPC opened twice) share one download.
    private readonly ConcurrentDictionary<string, Task<string?>> _webSaves = new();

    /// <summary>
    /// A FaceFinder picture not saved yet: downloaded and saved (like NPC Plugin Chooser 2), so from then on it
    /// shows straight away, also after restarting.
    /// </summary>
    public async Task<BitmapImage?> LoadWebBitmapAsync(string url, CancellationToken ct)
    {
        var save = _webSaves.GetOrAdd(url, u => Task.Run(() => _main.Mugshots.SaveWebPictureAsync(u, CancellationToken.None)));
        var path = await save;
        if (path == null) _webSaves.TryRemove(url, out _); // try again next time
        if (path == null || ct.IsCancellationRequested) return null;
        return await LoadBitmapAsync(path);
    }

    /// <summary>A picture already on disk for this look, found without waiting (null if there's none yet).</summary>
    public string? FindSaved(string modName, NpcLook? look, FormKey npc) => _main.Mugshots.FindSaved(modName, look, npc);

    public string? FindSavedOnline(OnlineLook online, FormKey npc) => _main.Mugshots.FindSavedOnline(online.Face, npc);

    /// <summary>
    /// Reads a picture without holding up the window: one shown recently comes from memory straight away; others are
    /// read on a background thread (many at once), so opening an NPC with 100 looks doesn't stutter.
    /// </summary>
    public async Task<BitmapImage?> LoadBitmapAsync(string path)
    {
        if (_bitmaps.ContainsKey(path)) return LoadBitmap(path);
        var bmp = await Task.Run(() => ReadBitmap(path));
        if (bmp != null) Remember(path, bmp);
        return bmp;
    }

    private static BitmapImage? ReadBitmap(string path)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 400;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>After FaceFinder's list is downloaded: adds its faces and shows them.</summary>
    public void ApplyCatalog(FaceFinderCatalog catalog)
    {
        AddOnlineLooks(catalog);
        RestoreCopiesAndNames(); // NPCs only FaceFinder has faces for are there now
        _pictures.Clear(); // looks FaceFinder has faces for now use those
        _webSaves.Clear(); // pictures FaceFinder changed were deleted and are downloaded again
        _bitmaps.Clear();
        _bitmapOrder.Clear();
        RefreshLists();
        ShowTiles(SelectedNpc);
        UpdateSummary();
        SaveChoices();
        StartPictureJob(); // renders only what neither your mugshots nor FaceFinder have
    }

    /// <summary>Starts rendering after FaceFinder's list couldn't be downloaded.</summary>
    public void ResumePictures()
    {
        ShowTiles(SelectedNpc); // its missing mugshots can be rendered now
        StartPictureJob();
    }

    /// <summary>
    /// Adds FaceFinder's faces from mods you don't have installed as looks, also for NPCs no installed mod changes
    /// (as long as the NPC's plugin is in your game). Saved picks of such faces are restored; when the mod has been
    /// installed since, the pick moves to its real look.
    /// </summary>
    private void AddOnlineLooks(FaceFinderCatalog catalog)
    {
        var setup = _main.Setup!;
        var installed = setup.AllMods;
        var installedById = catalog.Mods.ToDictionary(m => m.Id, m => FaceFinderCatalog.InstalledMod(m, installed));
        List<OnlineLook> OnlineFor(IEnumerable<FaceFinderFace> faces) => faces
            .Where(f => installedById.TryGetValue(f.ModId, out var folder) && folder == null && !FaceFinderCatalog.IsBaseGame(catalog.Mod(f.ModId)!))
            .GroupBy(f => f.ModId).Select(g => new OnlineLook(catalog.Mod(g.Key)!, g.First()))
            .OrderBy(o => o.Mod.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var known = new HashSet<string>();
        foreach (var row in Rows)
        {
            known.Add(FaceFinderCatalog.NpcKey(row.Entry.FormKey));
            row.SetOnline(OnlineFor(catalog.FacesFor(row.Entry.FormKey)));
        }

        // NPCs only FaceFinder has looks for. Their plugin must be in the game, or the NPC doesn't exist there.
        var loadOrder = setup.LoadOrder.ToDictionary(k => k.FileName.String, StringComparer.OrdinalIgnoreCase);
        var saved = _main.Selections.Choices;
        foreach (var (key, faces) in catalog.FacesByNpc)
        {
            if (known.Contains(key)) continue;
            var colon = key.IndexOf(':');
            if (!loadOrder.TryGetValue(key[(colon + 1)..], out var plugin)) continue;
            var online = OnlineFor(faces);
            if (online.Count == 0) continue;
            var formKey = new FormKey(plugin, uint.Parse(key[..colon], System.Globalization.NumberStyles.HexNumber));
            // The game's own record gives the name (FaceFinder's copy can hold stray line breaks); no record means
            // the NPC doesn't exist in this game.
            if (NpcScanner.FindRecord(_main.Plugins, formKey) is not { } record) continue;
            string? recordName;
            try { recordName = NameText.Fix(record.Name?.String); } catch { recordName = null; }
            var entry = new NpcEntry
            {
                FormKey = formKey,
                EditorId = record.EditorID,
                Name = CleanName(recordName) ?? CleanName(faces.Select(f => f.NpcName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))),
                Looks = [],
                OriginalName = OriginalNameOf(plugin.FileName.String),
            };
            saved.TryGetValue(formKey.ToString(), out var c);
            var row = new NpcRowViewModel(entry, "") { PendingOnlinePick = c != null && OnlineLook.ModIdOf(c) != null ? c : null };
            row.SetOnline(online);
            AddRow(row);
        }

        foreach (var row in Rows.Where(r => r.PendingOnlinePick != null))
        {
            var pick = row.PendingOnlinePick!;
            row.PendingOnlinePick = null;
            var modId = OnlineLook.ModIdOf(pick)!.Value;
            if (row.Online.Any(o => o.Mod.Id == modId)) { row.ChosenMod = pick; continue; }
            // Installed since it was picked: use its real look.
            if (installedById.GetValueOrDefault(modId) is { } folder &&
                row.Entry.Looks.FirstOrDefault(l => l.Source.Mod.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase)) is { } look)
                row.ChosenMod = look.ModName;
        }
    }

    /// <summary>A name on one line: line breaks and repeated spaces become one space.</summary>
    private static string? CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return string.Join(' ', name.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    private string OriginalNameOf(string plugin) =>
        _main.Setup!.PluginOwner(plugin)?.Name
        ?? (OriginalSources.IsGamePlugin(plugin) ? OriginalSources.BaseGameName : plugin);

    /// <summary>Reads a picture (or takes it from the recently shown ones).</summary>
    public BitmapImage? LoadBitmap(string path)
    {
        if (_bitmaps.TryGetValue(path, out var node))
        {
            _bitmapOrder.Remove(node);
            _bitmapOrder.AddFirst(node);
            return node.Value.Image;
        }
        if (ReadBitmap(path) is not { } bmp) return null;
        Remember(path, bmp);
        return bmp;
    }

    private void Remember(string key, BitmapImage bmp)
    {
        if (_bitmaps.ContainsKey(key)) return;
        _bitmaps[key] = _bitmapOrder.AddFirst((key, bmp));
        while (_bitmapOrder.Count > BitmapCacheSize)
        {
            _bitmaps.Remove(_bitmapOrder.Last!.Value.Path);
            _bitmapOrder.RemoveLast();
        }
    }

    /// <summary>
    /// With "Render all NPCs" on: renders, in the background, the pictures of looks that have none yet: no mugshot,
    /// no FaceFinder face and not rendered before. Otherwise only the NPC you open is rendered (by its tiles).
    /// </summary>
    private void StartPictureJob()
    {
        _pictureJob?.Cancel();
        var cts = _pictureJob = new CancellationTokenSource();
        var visibleFirst = View.Cast<NpcRowViewModel>().Concat(Rows).Distinct().ToList();
        var mugshots = _main.Mugshots;
        _picturesTotal = 0;
        _picturesDone = 0;
        PictureProgress = "";
        if (mugshots.Generator == null || _main.Settings.Generator == MugshotGeneratorKind.Off || !_main.Settings.RenderAllNpcs) return;
        // Until FaceFinder's list is there, it isn't known which looks it has faces for, and those are never rendered.
        if (_main.WaitingForCatalog) { PictureProgress = "Rendering waits for FaceFinder's list"; return; }

        _ = Task.Run(async () =>
        {
            try
            {
                // Checking what's missing only looks at files and FaceFinder's list, so it's quick.
                var items = visibleFirst.SelectMany(r =>
                {
                    var looks = r.Entry.Looks.Select(l => (ModName: l.ModName, Look: (NpcLook?)l, r.Entry.FormKey));
                    if (r.Entry.Original == null)
                        // A templated NPC's original is the face of the NPC it borrows its look from.
                        return r.Entry.OriginalPicture is { } p ? looks.Append((ModName: r.Entry.OriginalName, Look: (NpcLook?)p, FormKey: p.Npc)) : looks;
                    return r.Entry.OriginalIsLook ? looks : looks.Append((ModName: r.Entry.OriginalName, Look: r.Entry.Original, r.Entry.FormKey));
                }).Where(i => i.Look != null && !mugshots.HasPicture(i.ModName, i.Look, i.FormKey)).ToList();
                if (cts.IsCancellationRequested) return;
                _picturesTotal = items.Count;
                Application.Current.Dispatcher.Invoke(() => { if (!cts.IsCancellationRequested) { _progressTimer.Start(); UpdatePictureProgress(); } });

                await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = cts.Token },
                    async (item, _) =>
                    {
                        try { await GetPicture(item.ModName, item.Look, item.FormKey); }
                        catch { /* logged by the picture service */ }
                        Interlocked.Increment(ref _picturesDone);
                    });
            }
            catch (OperationCanceledException) { }
        });
    }

    private void UpdatePictureProgress()
    {
        if (_picturesDone >= _picturesTotal)
        {
            _progressTimer.Stop();
            PictureProgress = _picturesTotal == 0 ? "" : $"Rendered {_picturesTotal:N0} missing mugshots";
            return;
        }
        PictureProgress = $"Rendering missing mugshots: {_picturesDone:N0} / {_picturesTotal:N0}";
    }

    /// <summary>"Hide mods not installed" (or "Use FaceFinder") changed: shows or hides FaceFinder faces of mods you don't have.</summary>
    public void RefreshNotInstalled()
    {
        NpcRowViewModel.HideNotInstalled = _main.Settings.UseFaceFinder && _main.Settings.HideNotInstalled;
        foreach (var row in Rows) row.RefreshLookCount();
        RefreshLists();
        ShowTiles(SelectedNpc);
    }

    /// <summary>"Render all NPCs" changed: starts or stops rendering in the background.</summary>
    public void RestartPictureJob() => StartPictureJob();

    /// <summary>After switching renderer (or picture settings), look everything up again.</summary>
    public void RestartPictures()
    {
        _pictureJob?.Cancel();
        _pictures.Clear();
        _webSaves.Clear(); // saved FaceFinder pictures may have been removed
        _bitmaps.Clear(); // a picture made again keeps its file name, so don't show the old one from memory
        _bitmapOrder.Clear();
        ShowTiles(SelectedNpc);
        StartPictureJob();
    }

    private void ShowTiles(NpcRowViewModel? row)
    {
        _tileLoading?.Cancel();
        Tiles.Clear();
        if (row == null) return;
        _tileLoading = new CancellationTokenSource();
        foreach (var tile in MakeTiles(row, row.ChosenMod, null)) Tiles.Add(tile);
        LoadTiles(Tiles.ToList(), _tileLoading.Token);
    }

    /// <summary>
    /// The tiles of an NPC's faces, with the one of <paramref name="chosen"/> (a pick key) selected (none when null).
    /// A click picks the face for the NPC, or runs <paramref name="onClick"/> when given.
    /// </summary>
    public List<LookTileViewModel> MakeTiles(NpcRowViewModel row, string? chosen, Action<LookTileViewModel>? onClick)
    {
        var tiles = new List<LookTileViewModel>();
        var entry = row.Entry;
        foreach (var look in entry.Looks)
        {
            bool own = entry.OriginalIsLook && look == entry.Original;
            tiles.Add(new LookTileViewModel(this, look.ModName, look, entry.FormKey, TileKind.Look)
            {
                IsOwnModLook = own,
                IsSelected = chosen == look.ModName || (own && chosen?.Length == 0),
                OnClick = onClick,
            });
        }
        if (!entry.OriginalIsLook)
            tiles.Add(OriginalTile(row, chosen, onClick));
        foreach (var online in row.Online.Where(o => !NpcRowViewModel.HideNotInstalled || o.ChoiceKey == row.ChosenMod || o.ChoiceKey == chosen))
            tiles.Add(new LookTileViewModel(this, online.Mod.Name, null, entry.FormKey, TileKind.Online)
            {
                OnlineLook = online,
                IsSelected = chosen == online.ChoiceKey,
                OnClick = onClick,
            });
        tiles.AddRange(UnavailableTiles(row, onClick));
        return tiles;
    }

    /// <summary>Starts loading the tiles' pictures (and finding pluginless faces' plugins).</summary>
    public void LoadTiles(IReadOnlyList<LookTileViewModel> tiles, CancellationToken ct)
    {
        foreach (var tile in tiles)
            _ = tile.LoadImageAsync(ct);
        _ = FindFacePluginsAsync(tiles.Where(t => t.Kind == TileKind.Look && t.Look is { IsFacesOnly: true }).ToList(), ct);
    }

    /// <summary>Finds, for each pluginless look shown, the plugin its face was made for (see <see cref="FacePlugins"/>).</summary>
    private async Task FindFacePluginsAsync(List<LookTileViewModel> tiles, CancellationToken ct)
    {
        if (tiles.Count == 0) return;
        try
        {
            var finder = await _main.FacePlugins;
            foreach (var tile in tiles)
            {
                var plugin = await Task.Run(() => finder.PluginFor(tile.Look!), ct);
                if (ct.IsCancellationRequested) return;
                tile.FacePlugin = plugin?.FileName.String;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { MugshotService.Log("Finding face plugins failed: " + ex); }
    }

    /// <summary>
    /// The tile for the NPC's own look. An NPC whose look comes from another NPC (a template) shows that NPC's face;
    /// one whose template is a leveled list gets a random face in game, so there's no single face to show.
    /// </summary>
    private LookTileViewModel OriginalTile(NpcRowViewModel row, string? chosen, Action<LookTileViewModel>? onClick)
    {
        var entry = row.Entry;
        var (borrowed, random) = (entry.OriginalBorrowedFrom, entry.OriginalIsRandom);
        if (entry.Original == null && borrowed == null && !random && entry.Looks.Count == 0 &&
            NpcScanner.FindRecord(_main.Plugins, entry.FormKey) is { } record && NpcScanner.IsTemplatedForLooks(record))
            (borrowed, random) = NpcScanner.LookSource(_main.Plugins, record); // an NPC only FaceFinder has looks for
        var pictureNpc = entry.Original == null ? entry.OriginalPicture?.Npc ?? borrowed ?? entry.FormKey : entry.FormKey;
        return new LookTileViewModel(this, entry.OriginalName, entry.Original ?? entry.OriginalPicture, pictureNpc, TileKind.Original)
        {
            IsSelected = chosen?.Length == 0,
            OnClick = onClick,
            OriginalNote = random ? "original · random face" : null,
            NoPictureText = random ? "Random face in game" : null,
            OriginalNoteToolTip = random
                ? "In the game this NPC gets one of several faces at random each time it appears, so there's no single face to show."
                : borrowed != null ? "In the game this NPC uses another NPC's face, so that face is shown here." : null,
        };
    }

    /// <summary>Mugshots in your mugshot folder of mods that aren't loaded: shown like the others, but can't be picked.</summary>
    private IEnumerable<LookTileViewModel> UnavailableTiles(NpcRowViewModel row, Action<LookTileViewModel>? onClick)
    {
        var entry = row.Entry;
        var mugshots = _main.Mugshots;
        // Skyrim Special Edition (and NPC2's "Skyrim" and "Creation Club") isn't a mod: its folders are only ever the original look.
        var shown = entry.Looks.Select(l => mugshots.MugshotFolderOf(l.ModName))
            .Append(mugshots.MugshotFolderOf(entry.OriginalName))
            .Append(OriginalSources.BaseGameName).Concat(OriginalSources.OlderBaseGameFolders)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A FaceFinder tile already shows that mod (and uses this mugshot as its picture).
        var online = row.Online.Select(o => FaceFinderCatalog.Normalize(o.Mod.Name)).ToHashSet();
        shown.UnionWith(mugshots.FindAllMugshots(entry.FormKey).Select(m => m.Folder).Where(f => online.Contains(FaceFinderCatalog.Normalize(f))));
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _main.Setup?.AllMods ?? []) installed.TryAdd(mugshots.MugshotFolderOf(mod.Name), mod.Name);
        foreach (var (folder, picture) in mugshots.FindAllMugshots(entry.FormKey))
        {
            if (shown.Contains(folder)) continue;
            // Every installed mod with faces is loaded, so an installed one here just has no face for this NPC.
            var (reason, message) = installed.TryGetValue(folder, out var mod)
                ? ("No face for this NPC", $"{mod} has no face for this NPC.")
                : ("Not installed", $"{folder} isn't installed.");
            yield return new LookTileViewModel(this, folder, null, entry.FormKey, TileKind.Unavailable)
            {
                MugshotPath = picture,
                UnavailableReason = reason,
                UnavailableMessage = message,
                OnClick = onClick,
            };
        }
    }

    public void Choose(LookTileViewModel tile)
    {
        if (SelectedNpc == null) return;
        if (tile.IsUnavailable)
        {
            MessageBox.Show(Application.Current.MainWindow!, tile.UnavailableMessage, AppSettings.AppName);
            return;
        }
        var wasOnline = SelectedNpc.ChosenOnline != null;
        SelectedNpc.ChosenMod = tile.IsOriginal ? "" : tile.OnlineLook?.ChoiceKey ?? tile.ModName;
        foreach (var t in Tiles) t.IsSelected = t == tile;
        if (wasOnline && NpcRowViewModel.HideNotInstalled) ShowTiles(SelectedNpc); // the face picked before hides now
        UpdateSummary();
        SaveChoices();
    }

    public void UseModForAll(LookTileViewModel tile)
    {
        if (tile.OnlineLook is { } online)
        {
            foreach (var row in Rows.Where(r => r.Online.Any(o => o.Mod.Id == online.Mod.Id)))
                row.ChosenMod = online.ChoiceKey;
        }
        else if (tile.Kind == TileKind.Look)
        {
            foreach (var row in Rows.Where(r => r.Entry.Looks.Any(l => l.ModName == tile.ModName)))
                row.ChosenMod = tile.ModName;
        }
        else return;
        ShowTiles(SelectedNpc);
        UpdateSummary();
        SaveChoices();
    }

    /// <summary>
    /// The opposite of <see cref="UseModForAll"/>: every NPC set to this face's mod gets the face it would have
    /// without it (the next mod in line, or its original).
    /// </summary>
    public void DontUseModForAll(LookTileViewModel tile)
    {
        var key = tile.OnlineLook?.ChoiceKey ?? (tile.Kind == TileKind.Look ? tile.ModName : null);
        if (key == null || _main.Setup == null) return;
        var ranking = new LookRanking(_main.Setup);
        foreach (var row in Rows.Where(r => r.ChosenMod == key))
            row.ChosenMod = ranking.WinnerWithout(row.Entry, key)?.ModName ?? "";
        ShowTiles(SelectedNpc);
        UpdateSummary();
        SaveChoices();
    }

    public void LinkMugshotFolder(string modName)
    {
        var settings = _main.Settings;
        if (string.IsNullOrWhiteSpace(settings.MugshotsFolder) || !Directory.Exists(settings.MugshotsFolder))
        {
            MessageBox.Show(Application.Current.MainWindow!, "Set your mugshots folder in Settings first.", AppSettings.AppName);
            return;
        }
        var dialog = new OpenFolderDialog { Title = $"Mugshot folder for {modName}", InitialDirectory = settings.MugshotsFolder };
        if (dialog.ShowDialog() != true) return;
        settings.MugshotFolderLinks[modName] = Path.GetFileName(dialog.FolderName);
        _main.SaveSettings();
        foreach (var key in _pictures.Keys.Where(k => k.Item2 == modName).ToList()) _pictures.TryRemove(key, out _);
        ShowTiles(SelectedNpc);
    }

    private void UpdateSummary()
    {
        var changed = Rows.Count(r => r.ChosenMod != NpcRowViewModel.DefaultChoice(r.Entry));
        Summary = $"{Rows.Count:N0} NPCs · {changed:N0} changed";
        ResetPickCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Picked looks from mods you don't have installed, by mod, with how many NPCs wait on each.</summary>
    public List<(FaceFinderMod Mod, List<string> Npcs)> OnlinePicks() => Rows
        .Select(r => (Row: r, Online: r.Copy is { } copy ? copy.From.Online.FirstOrDefault(o => o.ChoiceKey == copy.Face) : r.ChosenOnline))
        .Where(p => p.Online != null)
        .GroupBy(p => p.Online!.Mod.Id)
        .Select(g => (g.First().Online!.Mod, g.Select(p => p.Row.DisplayName).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList()))
        .OrderBy(p => p.Mod.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>
    /// The picks as the build step needs them. An NPC with a face copied onto it gets that face (a look of the other
    /// NPC); its pick here waits until the copy is removed.
    /// </summary>
    public Dictionary<FormKey, NpcLook> GetChoices()
    {
        var choices = new Dictionary<FormKey, NpcLook>();
        foreach (var row in Rows)
        {
            var look = row.Copy is { } copy ? copy.From.CopyableLook(copy.Face) : row.ChosenLook;
            if (look != null) choices[row.Entry.FormKey] = look;
        }
        return choices;
    }

    /// <summary>The names you gave NPCs, as the build step needs them.</summary>
    public Dictionary<FormKey, string> GetNames() =>
        Rows.Where(r => r.CustomName != null).ToDictionary(r => r.Entry.FormKey, r => r.CustomName!);

    public int CopiedCount => Rows.Count(r => r.HasCopy);
    public int RenamedCount => Rows.Count(r => r.CustomName != null);
    private bool AnyRenamed() => Rows.Any(r => r.CustomName != null);

    private void AddRow(NpcRowViewModel row)
    {
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NpcRowViewModel.CustomName)) SaveName(row);
        };
        Rows.Add(row);
    }

    /// <summary>Puts back saved copies and names, for NPCs (and copied-from NPCs) that are in the list now.</summary>
    private void RestoreCopiesAndNames()
    {
        var byKey = Rows.ToDictionary(r => r.Entry.FormKey.ToString());
        var selections = _main.Selections;
        foreach (var (key, copy) in selections.Copies.ToList())
        {
            if (!byKey.TryGetValue(key, out var row) || row.Copy != null || !byKey.TryGetValue(copy.From, out var from)) continue;
            // A face of a mod that's gone (FaceFinder faces are checked when building).
            if (OnlineLook.ModIdOf(copy.Face) == null && from.CopyableLook(copy.Face) == null) continue;
            row.Copy = (from, copy.Face);
        }
        // A FaceFinder face of a mod installed since it was copied: the copy moves to the mod's real face.
        foreach (var row in Rows.ToList())
            if (row.Copy is { } c && InstalledFace(c.From, c.Face) is { } installed) SetCopy(row, (c.From, installed));
        foreach (var (key, name) in selections.Names)
            if (byKey.TryGetValue(key, out var row) && row.CustomName == null) row.CustomName = name;
        ResetNamesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>For a FaceFinder face ("ff:id") of a mod that's installed now: that mod's face for the NPC (its pick key).</summary>
    private string? InstalledFace(NpcRowViewModel from, string face)
    {
        if (OnlineLook.ModIdOf(face) is not { } modId || _main.Catalog is not { } catalog) return null;
        if (from.Online.Any(o => o.Mod.Id == modId) || catalog.Mod(modId) is not { } mod) return null;
        var folder = FaceFinderCatalog.InstalledMod(mod, _main.Setup!.AllMods);
        return folder == null ? null
            : from.Entry.Looks.FirstOrDefault(l => l.Source.Mod.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase))?.ModName;
    }

    /// <summary>Gives an NPC another NPC's face (null: takes the copy off), saved at once.</summary>
    public void SetCopy(NpcRowViewModel row, (NpcRowViewModel From, string Face)? copy)
    {
        row.Copy = copy;
        var key = row.Entry.FormKey.ToString();
        if (copy is { } c) _main.Selections.Copies[key] = new FaceCopy { From = c.From.Entry.FormKey.ToString(), Face = c.Face };
        else _main.Selections.Copies.Remove(key);
        _main.SaveSettings();
        RemoveCopyCommand.NotifyCanExecuteChanged();
    }

    private void SaveName(NpcRowViewModel row)
    {
        var key = row.Entry.FormKey.ToString();
        if (row.CustomName == null) _main.Selections.Names.Remove(key);
        else _main.Selections.Names[key] = row.CustomName;
        _main.SaveSettings();
        ResetNamesCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Step 1's "Remove copied face": the NPC's own pick here applies again.</summary>
    [RelayCommand(CanExecute = nameof(CanRemoveCopy))]
    private void RemoveCopy()
    {
        if (SelectedNpc is { } row) SetCopy(row, null);
    }

    private bool CanRemoveCopy() => SelectedNpc?.HasCopy == true;

    /// <summary>"Reset names": a small window listing every renamed NPC, each with an x that gives its own name back.</summary>
    [RelayCommand(CanExecute = nameof(AnyRenamed))]
    private void ResetNames()
    {
        new Views.RenamedNpcsWindow(this) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    /// <summary>Only picks that differ from the default are stored, so new or reordered mods still apply.</summary>
    public void SaveChoices()
    {
        var choices = _main.Selections.Choices;
        foreach (var row in Rows)
        {
            var key = row.Entry.FormKey.ToString();
            if (row.PendingOnlinePick != null) continue; // FaceFinder's list isn't loaded yet: keep the saved pick
            if (row.ChosenMod == NpcRowViewModel.DefaultChoice(row.Entry)) choices.Remove(key);
            else choices[key] = row.ChosenMod;
        }
        _main.SaveSettings();
    }

    public void StopPictures()
    {
        _pictureJob?.Cancel();
        _progressTimer.Stop();
    }

    [RelayCommand]
    private void Next()
    {
        SaveChoices();
        _main.ShowPage(Page.Copy);
    }

    /// <summary>Puts the selected NPC back on the look the game shows right now.</summary>
    [RelayCommand(CanExecute = nameof(CanResetPick))]
    private void ResetPick()
    {
        if (SelectedNpc is not { } row) return;
        row.ChosenMod = NpcRowViewModel.DefaultChoice(row.Entry);
        row.PendingOnlinePick = null;
        ShowTiles(row);
        UpdateSummary();
        SaveChoices();
    }

    private bool CanResetPick() => SelectedNpc is { } row && row.ChosenMod != NpcRowViewModel.DefaultChoice(row.Entry);

    /// <summary>Puts every NPC back on the look the game shows right now, as before any picks (after asking).</summary>
    [RelayCommand]
    private void ResetPicks()
    {
        var changed = Rows.Count(r => r.ChosenMod != NpcRowViewModel.DefaultChoice(r.Entry));
        var answer = MessageBox.Show(Application.Current.MainWindow!,
            $"Are you sure? This undoes all your picks ({changed:N0} NPCs changed) and puts every NPC back on the face the game shows now.",
            AppSettings.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var row in Rows)
        {
            row.ChosenMod = NpcRowViewModel.DefaultChoice(row.Entry);
            row.PendingOnlinePick = null;
        }
        _main.Selections.Choices.Clear();
        SaveChoices();
        ShowTiles(SelectedNpc);
        UpdateSummary();
    }
}
