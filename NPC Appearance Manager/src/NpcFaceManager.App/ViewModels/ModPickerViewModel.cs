using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Scanning;

namespace NpcFaceManager.App.ViewModels;

/// <summary>A dependency shown under a ticked mod. Its checkbox is the dependency's own tick in the main list.</summary>
public sealed class DependencyItem
{
    public required ModRowViewModel Row { get; init; }
    public required int MasterCount { get; init; }
    public bool TooManyMasters => MasterCount > Dependents.MaxMasters;
    public string ToolTip => TooManyMasters
        ? $"Too many masters ({MasterCount})"
        : $"{Row.Name}\nNeeds this mod. Masters: {MasterCount}";
}

/// <summary>An MO2 separator heading the mods below it. Click to fold.</summary>
public partial class SeparatorRowViewModel : ObservableObject
{
    private readonly ModPickerViewModel _owner;
    public string Name { get; }
    public List<ModRowViewModel> Mods { get; } = [];
    [ObservableProperty] private bool _isCollapsed;

    public SeparatorRowViewModel(ModPickerViewModel owner, string name, bool collapsed)
    {
        _owner = owner;
        Name = name;
        _isCollapsed = collapsed;
    }

    [RelayCommand] private void Toggle() => _owner.ToggleSeparator(this);
}

public partial class ModRowViewModel : ObservableObject
{
    public ModSummary Summary { get; }
    public SeparatorRowViewModel? Separator { get; set; }
    [ObservableProperty] private bool _isTicked;

    /// <summary>Ticked by hand (not just because a ticked mod needed it).</summary>
    public bool TickedByHand { get; set; }

    /// <summary>Ticked mods that auto-ticked this one.</summary>
    public HashSet<ModRowViewModel> AutoTickedBy { get; } = [];

    public ObservableCollection<DependencyItem> Dependencies { get; } = [];
    [ObservableProperty] private bool _showDependencies;

    public ModRowViewModel(ModSummary summary) => Summary = summary;

    public string Name => Summary.Mod.Name;
    public ModKind Kind => Summary.Kind;

    /// <summary>"facegen + plugin" / "facegen"; empty for mods that aren't NPC overhauls.</summary>
    public string Badge => Summary.Kind switch
    {
        ModKind.FacesAndPlugin => "facegen + plugin",
        ModKind.FacesOnly => "facegen",
        _ => "",
    };

    public string BadgeToolTip => Summary.Kind switch
    {
        ModKind.FacesAndPlugin => $"{Summary.FaceFileCount} faces, with a plugin",
        ModKind.FacesOnly => $"{Summary.FaceFileCount} faces, no plugin",
        _ => "",
    };

    public string PluginsText => string.Join(", ", Summary.Plugins.Select(p => p.FileName));
    public bool IsDisabledInMo2 => !Summary.Mod.CountsAsEnabled;
    public string StateText => Summary.Mod.DisabledByMerge ? "merged" : IsDisabledInMo2 ? "disabled" : "";
}

public partial class ModPickerViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _loading;
    private bool _autoChange;
    private readonly Dictionary<ModSummary, ModRowViewModel> _rowOf = [];

    /// <summary>Every mod, in MO2's order.</summary>
    public List<ModRowViewModel> Rows { get; } = [];

    /// <summary>Mods and separators in MO2's order (top of MO2's list first).</summary>
    public ObservableCollection<object> Items { get; } = [];
    public ICollectionView View { get; }

    [ObservableProperty] private string _searchText;
    [ObservableProperty] private bool _onlyFaceMods;
    [ObservableProperty] private bool _showDisabledMods;
    [ObservableProperty] private int _tickedCount;
    [ObservableProperty] private string _status = "";

    public ModPickerViewModel(MainViewModel main)
    {
        _main = main;
        _searchText = main.Settings.ModSearch;
        _onlyFaceMods = main.Settings.OnlyFaceMods;
        _showDisabledMods = main.Settings.ShowDisabledMods;
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => o switch
        {
            ModRowViewModel r => r.Separator?.IsCollapsed != true && PassesFilters(r),
            SeparatorRowViewModel s => s.Mods.Any(PassesFilters),
            _ => false,
        };
    }

    /// <summary>Filters and search; ticked mods always pass the filters.</summary>
    private bool PassesFilters(ModRowViewModel r) =>
        (!OnlyFaceMods || r.Summary.IsAppearanceMod || r.IsTicked) &&
        (ShowDisabledMods || !r.IsDisabledInMo2 || r.IsTicked) &&
        (SearchText.Length == 0 || r.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
         r.PluginsText.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

    public void ToggleSeparator(SeparatorRowViewModel separator)
    {
        separator.IsCollapsed = !separator.IsCollapsed;
        if (separator.IsCollapsed) _main.Settings.CollapsedSeparators.Add(separator.Name);
        else _main.Settings.CollapsedSeparators.Remove(separator.Name);
        _main.SaveSettings();
        View.Refresh();
    }

    public bool MergeDependents
    {
        get => _main.Settings.MergeDependents;
        set
        {
            if (_main.Settings.MergeDependents == value) return;
            _main.Settings.MergeDependents = value;
            _main.SaveSettings();
            OnPropertyChanged();
            RefreshDependencyLists();
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        View.Refresh();
        _main.Settings.ModSearch = value;
        _main.SaveSettingsSoon();
    }

    partial void OnOnlyFaceModsChanged(bool value)
    {
        View.Refresh();
        _main.Settings.OnlyFaceMods = value;
        _main.SaveSettings();
    }

    partial void OnShowDisabledModsChanged(bool value)
    {
        View.Refresh();
        _main.Settings.ShowDisabledMods = value;
        _main.SaveSettings();
    }

    public async Task ScanAsync()
    {
        var setup = _main.Setup;
        if (setup == null) return;
        _loading = true;
        Rows.Clear();
        Items.Clear();
        _rowOf.Clear();
        IReadOnlyList<ModSummary> found = [];
        var started = DateTime.Now;
        await _main.RunBusyAsync("Checking your mods...", async progress =>
        {
            found = await Task.Run(() => ModPreScanner.Scan(setup, progress));
        });
        _main.SetPreScan(found);

        // Only the user's own last picks are ticked; nothing is ticked otherwise.
        var ticked = _main.Selections.TickedMods.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var separators = setup.Separators.ToDictionary(s => s,
            s => new SeparatorRowViewModel(this, s.Name, _main.Settings.CollapsedSeparators.Contains(s.Name)));

        // MO2 shows its lowest priority mod at the top; separators sit above their mods.
        var ordered = found.Select(s => (Priority: s.Mod.Priority, Item: (object)s))
            .Concat(setup.Separators.Select(s => (s.Priority, Item: (object)s)))
            .OrderBy(x => x.Priority);
        foreach (var (_, item) in ordered)
        {
            if (item is Separator sep) { Items.Add(separators[sep]); continue; }
            var summary = (ModSummary)item;
            var row = new ModRowViewModel(summary) { IsTicked = ticked.Contains(summary.Mod.Name) };
            row.TickedByHand = row.IsTicked;
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ModRowViewModel.IsTicked)) OnTickChanged(row); };
            if (setup.SeparatorOf(summary.Mod) is { } owner)
            {
                row.Separator = separators[owner];
                row.Separator.Mods.Add(row);
            }
            _rowOf[summary] = row;
            Rows.Add(row);
            Items.Add(row);
        }
        BuildDependencyLists();
        View.Refresh(); // separators were filtered before their mods were added
        _loading = false;
        UpdateCount();
        RefreshDependencyLists();
        Status = $"{found.Count} mods, {found.Count(f => f.IsAppearanceMod)} with facegen · {(DateTime.Now - started).TotalSeconds:0.0}s";
    }

    /// <summary>Each mod's dependency list: the mods that need it, each with its own checkbox.</summary>
    private void BuildDependencyLists()
    {
        var deps = _main.Deps;
        if (deps == null) return;
        foreach (var row in Rows)
        {
            row.Dependencies.Clear();
            foreach (var d in deps.Of(row.Summary))
                if (_rowOf.TryGetValue(d, out var depRow))
                    row.Dependencies.Add(new DependencyItem { Row = depRow, MasterCount = deps.MasterCount(d) });
        }
    }

    private void RefreshDependencyLists()
    {
        foreach (var row in Rows) row.ShowDependencies = MergeDependents && row.IsTicked && row.Dependencies.Count > 0;
    }

    private void OnTickChanged(ModRowViewModel row)
    {
        if (_loading) return;
        if (!_autoChange) row.TickedByHand = row.IsTicked;

        if (MergeDependents)
        {
            bool outer = !_autoChange;
            _autoChange = true;
            try
            {
                if (row.IsTicked)
                {
                    // Tick what needs this mod, except mods with too many masters (those stay up to you).
                    foreach (var d in row.Dependencies.Where(d => !d.TooManyMasters))
                    {
                        d.Row.AutoTickedBy.Add(row);
                        if (!d.Row.IsTicked) d.Row.IsTicked = true;
                    }
                }
                else
                {
                    // Untick what this mod auto-ticked, unless you ticked it or another ticked mod still needs it.
                    foreach (var d in row.Dependencies)
                    {
                        d.Row.AutoTickedBy.Remove(row);
                        if (d.Row.IsTicked && !d.Row.TickedByHand && d.Row.AutoTickedBy.Count == 0) d.Row.IsTicked = false;
                    }
                }
            }
            finally { if (outer) _autoChange = false; }
        }

        if (_autoChange) return; // the outermost change saves once
        UpdateCount();
        RefreshDependencyLists();
        _main.Selections.TickedMods = Rows.Where(r => r.IsTicked).Select(r => r.Name).ToList();
        _main.SaveSettings();
    }

    private void UpdateCount()
    {
        TickedCount = Rows.Count(r => r.IsTicked);
        LoadSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ClearAll()
    {
        _loading = true;
        foreach (var r in Rows)
        {
            r.IsTicked = false;
            r.TickedByHand = false;
            r.AutoTickedBy.Clear();
        }
        _loading = false;
        UpdateCount();
        RefreshDependencyLists();
        _main.Selections.TickedMods = [];
        _main.SaveSettings();
    }

    [RelayCommand]
    private Task Rescan() => ScanAsync();

    private bool CanLoad() => TickedCount > 0;

    [RelayCommand(CanExecute = nameof(CanLoad))]
    private Task LoadSelected() =>
        _main.LoadNpcsAsync(Rows.Where(r => r.IsTicked).Select(r => r.Summary).ToList());
}
