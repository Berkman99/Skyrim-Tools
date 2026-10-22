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
using NpcFaceManager.Core.Mugshots;
using NpcFaceManager.Core.Scanning;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.App.ViewModels;

public partial class NpcRowViewModel : ObservableObject
{
    public NpcEntry Entry { get; }

    /// <summary>Chosen mod name, or "" for the original look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChoiceText))]
    private string _chosenMod;

    public NpcRowViewModel(NpcEntry entry, string chosenMod)
    {
        Entry = entry;
        _chosenMod = chosenMod;
    }

    public string DisplayName => Entry.DisplayName;
    public string Details => $"{Entry.EditorId}  ·  {Entry.FormKey}";
    public string ChoiceText => ChosenMod.Length == 0 ? Entry.OriginalName : ChosenMod;
    public int LookCount => Entry.Looks.Count;
    public string LookCountText => LookCount == 1 ? "1 look" : $"{LookCount} looks";

    public NpcLook? ChosenLook => Entry.Looks.FirstOrDefault(l => l.ModName == ChosenMod);
}

public enum TileKind
{
    /// <summary>A look from a ticked mod.</summary>
    Look,
    /// <summary>The look the NPC keeps without the ticked mods, named after its source.</summary>
    Original,
    /// <summary>A mugshot of a mod that isn't loaded; it can't be picked.</summary>
    Unavailable,
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

    public bool IsOriginal => Kind == TileKind.Original;
    public bool IsUnavailable => Kind == TileKind.Unavailable;

    /// <summary>A ticked mod's look that is also the NPC's original (the mod adds the NPC).</summary>
    public bool IsOwnModLook { get; init; }

    public string Caption => Kind switch
    {
        TileKind.Original => "original",
        TileKind.Unavailable => UnavailableReason ?? "",
        _ when IsOwnModLook => "original",
        _ => Look!.IsFacesOnly ? "facegen only" : Look.SourcePlugin?.FileName.String ?? "",
    };

    public async Task LoadImageAsync(CancellationToken ct)
    {
        try
        {
            if (MugshotPath != null) { Show(MugshotPath); return; }

            // Pictures that are already found, downloaded or generated show straight away.
            var task = _owner.GetPicture(ModName, Look, Npc);
            if (task.IsCompletedSuccessfully) { Show(task.Result); return; }

            var path = await task;
            if (ct.IsCancellationRequested) return;
            Show(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            MugshotService.Log($"Picture for {Npc} ({ModName}) failed: {ex}");
            PictureStatus = "No picture";
        }
    }

    private void Show(string? path)
    {
        if (path == null) { PictureStatus = "No picture"; return; }
        Image = _owner.LoadBitmap(path);
        PictureStatus = Image == null ? "Picture couldn't be read" : "";
    }

    [RelayCommand] private void Select() => _owner.Choose(this);
    [RelayCommand] private void UseForAll() => _owner.UseModForAll(this);
    [RelayCommand] private void LinkMugshotFolder() => _owner.LinkMugshotFolder(ModName);
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
    public List<string> ModFilters { get; }

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
        var saved = main.Selections.Choices;
        foreach (var entry in index.Npcs)
        {
            var choice = saved.TryGetValue(entry.FormKey.ToString(), out var c) && (c.Length == 0 || entry.Looks.Any(l => l.ModName == c))
                ? c
                : entry.Looks[0].ModName; // highest priority mod by default
            Rows.Add(new NpcRowViewModel(entry, choice));
        }
        ModFilters = [AllModsFilter, .. index.Mods.Keys.OrderBy(x => x)];

        _searchText = settings.NpcSearch;
        _modFilter = settings.NpcModFilter is { } f && ModFilters.Contains(f) ? f : AllModsFilter;
        _showSingleLook = settings.ShowSingleLookNpcs;
        _pictureSize = Math.Clamp(settings.PictureSize, 120, 400);

        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = Filter;
        var last = main.Selections.LastSelectedNpc;
        SelectedNpc = View.Cast<NpcRowViewModel>().FirstOrDefault(r => r.Entry.FormKey.ToString() == last)
                      ?? View.Cast<NpcRowViewModel>().FirstOrDefault();
        UpdateSummary();

        _progressTimer.Tick += (_, _) => UpdatePictureProgress();
        StartPictureJob();
    }

    private bool Filter(object o)
    {
        if (o is not NpcRowViewModel r) return false;
        if (!ShowSingleLook && r.LookCount < 2) return false;
        if (ModFilter != AllModsFilter && !r.Entry.Looks.Any(l => l.ModName == ModFilter)) return false;
        if (SearchText.Length == 0) return true;
        return r.DisplayName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
               (r.Entry.EditorId?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
               r.Entry.FormKey.ToString().Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    partial void OnSearchTextChanged(string value)
    {
        View.Refresh();
        _main.Settings.NpcSearch = value;
        _main.SaveSettingsSoon();
    }

    partial void OnModFilterChanged(string value)
    {
        View.Refresh();
        _main.Settings.NpcModFilter = value;
        _main.SaveSettings();
    }

    partial void OnShowSingleLookChanged(bool value)
    {
        View.Refresh();
        _main.Settings.ShowSingleLookNpcs = value;
        _main.SaveSettings();
    }

    partial void OnPictureSizeChanged(double value)
    {
        _main.Settings.PictureSize = value;
        _main.SaveSettingsSoon();
    }

    partial void OnSelectedNpcChanged(NpcRowViewModel? value)
    {
        ShowTiles(value);
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
        var task = _pictures.GetOrAdd(key, _ => Task.Run(() => _main.Mugshots.GetAsync(modName, look, npc, CancellationToken.None)));
        if (task.IsFaulted || task.IsCanceled) _pictures.TryRemove(key, out _);
        return task;
    }

    /// <summary>Reads a picture (or takes it from the recently shown ones).</summary>
    public BitmapImage? LoadBitmap(string path)
    {
        if (_bitmaps.TryGetValue(path, out var node))
        {
            _bitmapOrder.Remove(node);
            _bitmapOrder.AddFirst(node);
            return node.Value.Image;
        }
        BitmapImage bmp;
        try
        {
            bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = 400;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
        }
        catch { return null; }
        _bitmaps[path] = _bitmapOrder.AddFirst((path, bmp));
        while (_bitmapOrder.Count > BitmapCacheSize)
        {
            _bitmaps.Remove(_bitmapOrder.Last!.Value.Path);
            _bitmapOrder.RemoveLast();
        }
        return bmp;
    }

    /// <summary>Finds, downloads or generates pictures for every loaded NPC, starting right after the list opens.</summary>
    private void StartPictureJob()
    {
        _pictureJob?.Cancel();
        var cts = _pictureJob = new CancellationTokenSource();
        var visibleFirst = View.Cast<NpcRowViewModel>().Concat(Rows).Distinct().ToList();
        var items = visibleFirst.SelectMany(r =>
        {
            var looks = r.Entry.Looks.Select(l => (ModName: l.ModName, Look: (NpcLook?)l, r.Entry.FormKey));
            return r.Entry.Original == null || r.Entry.OriginalIsLook ? looks : looks.Append((ModName: r.Entry.OriginalName, Look: r.Entry.Original, r.Entry.FormKey));
        }).ToList();
        _picturesTotal = items.Count;
        _picturesDone = 0;
        _progressTimer.Start();
        UpdatePictureProgress();

        _ = Task.Run(async () =>
        {
            try
            {
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
            PictureProgress = _picturesTotal == 0 ? "" : $"Pictures: {_picturesTotal:N0} loaded";
            return;
        }
        PictureProgress = $"Pictures: {_picturesDone:N0} / {_picturesTotal:N0}";
    }

    /// <summary>After switching renderer (or picture settings), look everything up again.</summary>
    public void RestartPictures()
    {
        _pictureJob?.Cancel();
        _pictures.Clear();
        ShowTiles(SelectedNpc);
        StartPictureJob();
    }

    private void ShowTiles(NpcRowViewModel? row)
    {
        _tileLoading?.Cancel();
        Tiles.Clear();
        if (row == null) return;
        _tileLoading = new CancellationTokenSource();
        var ct = _tileLoading.Token;
        var entry = row.Entry;

        foreach (var look in entry.Looks)
        {
            bool own = entry.OriginalIsLook && look == entry.Original;
            Tiles.Add(new LookTileViewModel(this, look.ModName, look, entry.FormKey, TileKind.Look)
            {
                IsOwnModLook = own,
                IsSelected = row.ChosenMod == look.ModName || (own && row.ChosenMod.Length == 0),
            });
        }
        if (!entry.OriginalIsLook)
            Tiles.Add(new LookTileViewModel(this, entry.OriginalName, entry.Original, entry.FormKey, TileKind.Original) { IsSelected = row.ChosenMod.Length == 0 });
        foreach (var tile in UnavailableTiles(entry)) Tiles.Add(tile);

        foreach (var tile in Tiles)
            _ = tile.LoadImageAsync(ct);
    }

    /// <summary>Mugshots in your mugshot folder of mods that aren't loaded: shown like the others, but can't be picked.</summary>
    private IEnumerable<LookTileViewModel> UnavailableTiles(NpcEntry entry)
    {
        var mugshots = _main.Mugshots;
        // Skyrim and Creation Club aren't mods: their folders are only ever the original look.
        var shown = entry.Looks.Select(l => mugshots.MugshotFolderOf(l.ModName))
            .Append(mugshots.MugshotFolderOf(entry.OriginalName))
            .Append(OriginalSources.SkyrimName).Append(OriginalSources.CreationClubName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in _main.Setup?.AllMods ?? []) installed.TryAdd(mugshots.MugshotFolderOf(mod.Name), mod.Name);
        var ticked = _main.Index?.Mods.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        foreach (var (folder, picture) in mugshots.FindAllMugshots(entry.FormKey))
        {
            if (shown.Contains(folder)) continue;
            string reason, message;
            if (!installed.TryGetValue(folder, out var mod))
                (reason, message) = ("Not installed", $"{folder} isn't installed.");
            else if (!ticked.Contains(mod))
                (reason, message) = ("Not loaded", $"Tick {mod} in Step 1 to use this look.");
            else
                (reason, message) = ("No face for this NPC", $"{mod} has no face for this NPC.");
            yield return new LookTileViewModel(this, folder, null, entry.FormKey, TileKind.Unavailable)
            {
                MugshotPath = picture,
                UnavailableReason = reason,
                UnavailableMessage = message,
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
        SelectedNpc.ChosenMod = tile.IsOriginal ? "" : tile.ModName;
        foreach (var t in Tiles) t.IsSelected = t == tile;
        UpdateSummary();
        SaveChoices();
    }

    public void UseModForAll(LookTileViewModel tile)
    {
        if (tile.Kind != TileKind.Look) return;
        foreach (var row in Rows.Where(r => r.Entry.Looks.Any(l => l.ModName == tile.ModName)))
            row.ChosenMod = tile.ModName;
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
        var changed = Rows.Count(r => r.ChosenMod.Length > 0);
        Summary = $"{Rows.Count:N0} NPCs · {changed:N0} changed";
    }

    /// <summary>The picks as the build step needs them.</summary>
    public Dictionary<FormKey, NpcLook> GetChoices() =>
        Rows.Where(r => r.ChosenLook != null).ToDictionary(r => r.Entry.FormKey, r => r.ChosenLook!);

    /// <summary>Only picks that differ from the default are stored, so new or reordered mods still apply.</summary>
    public void SaveChoices()
    {
        var choices = _main.Selections.Choices;
        foreach (var row in Rows)
        {
            var key = row.Entry.FormKey.ToString();
            if (row.ChosenMod == row.Entry.Looks[0].ModName) choices.Remove(key);
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
        _main.ShowPage(Page.Build);
    }

    [RelayCommand]
    private void Back()
    {
        SaveChoices();
        _main.ShowPage(Page.Mods);
    }
}
