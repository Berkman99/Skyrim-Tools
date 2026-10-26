using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.App.ViewModels;

/// <summary>
/// "Copy faces" (optional): gives an NPC (NPC 1) one of another NPC's (NPC 2) faces. NPC 1's list uses the same search
/// and filters as Pick faces; NPC 2's has its own. NPCs changed in Pick faces are locked as NPC 1.
/// </summary>
public partial class FaceCopyViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private CancellationTokenSource? _tileLoading;
    private NpcRowViewModel? _amberSource;

    public NpcChooserViewModel Chooser { get; }
    public ListCollectionView TargetView { get; }
    public ListCollectionView SourceView { get; }
    public ObservableCollection<LookTileViewModel> Tiles { get; } = [];
    public ObservableCollection<string> SourceModFilters { get; } = [];

    /// <summary>NPC 1: gets the face.</summary>
    [ObservableProperty] private NpcRowViewModel? _target;

    /// <summary>NPC 2: the face comes from it.</summary>
    [ObservableProperty] private NpcRowViewModel? _source;

    [ObservableProperty] private string _sourceSearch;
    [ObservableProperty] private string _sourceModFilter;
    [ObservableProperty] private bool _sourceShowSingleLook;
    [ObservableProperty] private bool _sourceShowDisabled;
    [ObservableProperty] private string _summary = "";

    public FaceCopyViewModel(MainViewModel main, NpcChooserViewModel chooser)
    {
        _main = main;
        Chooser = chooser;
        var s = main.Settings;
        _sourceSearch = s.CopySearch;
        _sourceShowSingleLook = s.CopyShowSingleLookNpcs;
        _sourceShowDisabled = s.CopyShowDisabledOverhauls;
        chooser.FillModFilters(SourceModFilters, _sourceShowDisabled);
        _sourceModFilter = s.CopyModFilter is { } f && SourceModFilters.Contains(f) ? f : NpcChooserViewModel.AllModsFilter;

        TargetView = chooser.NewView(o => o is NpcRowViewModel r && NpcChooserViewModel.Matches(r, chooser.SearchText, chooser.ModFilter, chooser.ShowSingleLook));
        SourceView = chooser.NewView(o => o is NpcRowViewModel r && NpcChooserViewModel.Matches(r, SourceSearch, SourceModFilter, SourceShowSingleLook));
        chooser.FilterChanged += () => TargetView.Refresh();

        // The NPC 1 and NPC 2 you had last time.
        var selections = main.Selections;
        Target = chooser.Rows.FirstOrDefault(r => r.Entry.FormKey.ToString() == selections.LastCopyTarget && !r.IsLocked);
        if (chooser.Rows.FirstOrDefault(r => r.Entry.FormKey.ToString() == selections.LastCopySource) is { } source && source != Target)
            Source = source;
        UpdateSummary();
    }

    /// <summary>The left side's width (the divider you drag), kept.</summary>
    public GridLength ListWidth
    {
        get => new(Math.Max(400, _main.Settings.CopyListsWidth));
        set { _main.Settings.CopyListsWidth = value.Value; _main.SaveSettingsSoon(); }
    }

    /// <summary>How far down each list is scrolled, kept.</summary>
    public double TargetScroll
    {
        get => _main.Settings.CopyTargetScroll;
        set { _main.Settings.CopyTargetScroll = value; _main.SaveSettingsSoon(); }
    }

    public double SourceScroll
    {
        get => _main.Settings.CopySourceScroll;
        set { _main.Settings.CopySourceScroll = value; _main.SaveSettingsSoon(); }
    }

    /// <summary>Opening the step: as you left it, unless NPC 1 got locked by a pick in Pick faces meanwhile.</summary>
    public void Start()
    {
        if (Target is { IsLocked: true }) Target = null;
        ShowCopySource(); // a copy may have been removed in Pick faces
        TargetView.Refresh();
        SourceView.Refresh();
        ShowTiles();
        UpdateSummary();
    }

    partial void OnTargetChanged(NpcRowViewModel? oldValue, NpcRowViewModel? newValue)
    {
        // A locked NPC can't be NPC 1 (also when reached with the keyboard).
        if (newValue is { IsLocked: true })
        {
            Application.Current.Dispatcher.BeginInvoke(() => Target = oldValue is { IsLocked: false } ? oldValue : null);
            return;
        }
        if (oldValue != null) oldValue.IsCopyTarget = false;
        if (newValue != null) newValue.IsCopyTarget = true;
        // NPC 1 with a copied face: the NPC it came from is picked as NPC 2, showing that face. Otherwise no faces
        // show until NPC 2 is picked.
        Source = newValue?.Copy?.From;
        ShowCopySource();
        ShowTiles();
        ResetPickCommand.NotifyCanExecuteChanged();
        _main.Selections.LastCopyTarget = newValue?.Entry.FormKey.ToString();
        _main.SaveSettingsSoon();
    }

    partial void OnSourceChanged(NpcRowViewModel? oldValue, NpcRowViewModel? newValue)
    {
        // NPC 1 itself can't be NPC 2: giving an NPC its own face is what Pick faces is for.
        if (newValue != null && newValue == Target)
        {
            Application.Current.Dispatcher.BeginInvoke(() => Source = oldValue != Target ? oldValue : null);
            return;
        }
        ShowTiles();
        _main.Selections.LastCopySource = newValue?.Entry.FormKey.ToString();
        _main.SaveSettingsSoon();
    }

    /// <summary>The NPC whose face the selected NPC 1 has is amber in NPC 2's list.</summary>
    private void ShowCopySource()
    {
        var source = Target?.Copy?.From;
        if (source == _amberSource) return;
        if (_amberSource != null) _amberSource.IsCopySource = false;
        if (source != null) source.IsCopySource = true;
        _amberSource = source;
    }

    private void ShowTiles()
    {
        _tileLoading?.Cancel();
        Tiles.Clear();
        if (Source == null) return;
        _tileLoading = new CancellationTokenSource();
        var chosen = Target?.Copy is { } copy && copy.From == Source ? copy.Face : null;
        foreach (var tile in Chooser.MakeTiles(Source, chosen, CopyFace)) Tiles.Add(tile);
        Chooser.LoadTiles(Tiles.ToList(), _tileLoading.Token);
    }

    /// <summary>A click on one of NPC 2's faces: NPC 1 gets it.</summary>
    private void CopyFace(LookTileViewModel tile)
    {
        if (Target == null || Source == null) return;
        var owner = Application.Current.MainWindow!;
        if (tile.IsUnavailable)
        {
            MessageBox.Show(owner, tile.UnavailableMessage, AppSettings.AppName);
            return;
        }
        var key = tile.IsOriginal ? "" : tile.OnlineLook?.ChoiceKey ?? tile.ModName;
        if (tile.OnlineLook == null && Source.CopyableLook(key) == null)
        {
            MessageBox.Show(owner, "In the game this NPC gets a random face each time it appears, so there's no single face to copy.", AppSettings.AppName);
            return;
        }
        Chooser.SetCopy(Target, (Source, key));
        foreach (var t in Tiles) t.IsSelected = t == tile;
        ShowCopySource();
        ResetPickCommand.NotifyCanExecuteChanged();
        UpdateSummary();
    }

    partial void OnSourceSearchChanged(string value)
    {
        SourceView.Refresh();
        _main.Settings.CopySearch = value;
        _main.SaveSettingsSoon();
    }

    partial void OnSourceModFilterChanged(string value)
    {
        SourceView.Refresh();
        _main.Settings.CopyModFilter = value;
        _main.SaveSettings();
    }

    partial void OnSourceShowSingleLookChanged(bool value)
    {
        SourceView.Refresh();
        _main.Settings.CopyShowSingleLookNpcs = value;
        _main.SaveSettings();
    }

    partial void OnSourceShowDisabledChanged(bool value)
    {
        var current = SourceModFilter;
        Chooser.FillModFilters(SourceModFilters, value);
        SourceModFilter = SourceModFilters.Contains(current) ? current : NpcChooserViewModel.AllModsFilter;
        _main.Settings.CopyShowDisabledOverhauls = value;
        _main.SaveSettings();
    }

    private void UpdateSummary()
    {
        var copied = Chooser.CopiedCount;
        Summary = copied == 1 ? "1 face copied" : $"{copied:N0} faces copied";
    }

    /// <summary>Takes the copied face off NPC 1 (its pick in Pick faces applies again).</summary>
    [RelayCommand(CanExecute = nameof(CanResetPick))]
    private void ResetPick()
    {
        if (Target == null) return;
        Chooser.SetCopy(Target, null);
        foreach (var t in Tiles) t.IsSelected = false;
        ShowCopySource();
        ResetPickCommand.NotifyCanExecuteChanged();
        UpdateSummary();
    }

    private bool CanResetPick() => Target?.HasCopy == true;

    /// <summary>Takes every copied face off (after asking) and starts the step afresh. Picks in Pick faces and names stay.</summary>
    [RelayCommand]
    private void ResetPicks()
    {
        var copied = Chooser.Rows.Where(r => r.HasCopy).ToList();
        var answer = MessageBox.Show(Application.Current.MainWindow!,
            $"Are you sure? This takes off every copied face ({copied.Count:N0} NPCs). Your picks in step 1 and your names stay.",
            AppSettings.AppName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;
        foreach (var row in copied) Chooser.SetCopy(row, null);
        Target = null;
        Source = null;
        ShowCopySource();
        ResetPickCommand.NotifyCanExecuteChanged();
        UpdateSummary();
    }

    [RelayCommand] private void Next() => _main.ShowPage(Page.Build);
}
