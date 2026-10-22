using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Mugshots;
using NpcFaceManager.Core.Scanning;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.App.ViewModels;

public enum Page { Mods, Npcs, Build, Settings }

/// <summary>The app shell: holds the detected setup and moves between the three steps.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly string[] _args;
    private PluginCache? _plugins;

    public AppSettings Settings { get; } = AppSettings.Load();
    public MugshotService Mugshots { get; }
    public GameSetup? Setup { get; private set; }
    public NpcIndex? Index { get; private set; }
    public IReadOnlyList<ModSummary> PreScan { get; private set; } = [];

    public ModPickerViewModel ModPicker { get; }
    public SettingsViewModel SettingsPage { get; }
    [ObservableProperty] private NpcChooserViewModel? _chooser;
    [ObservableProperty] private BuildViewModel? _build;

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private Page _currentStep = Page.Mods;
    [ObservableProperty] private string? _busyMessage;
    [ObservableProperty] private string _setupDescription = "Looking for your game...";

    public MainViewModel(string[] args)
    {
        _args = args;
        Mugshots = new MugshotService(Settings);
        ModPicker = new ModPickerViewModel(this);
        SettingsPage = new SettingsViewModel(this);
    }

    public string ProfileKey => Setup == null ? "" :
        Setup.IsMo2 ? $"{Setup.Mo2!.IniPath}|{Setup.ProfileName}" : $"game|{Setup.GameFolder}";

    public ProfileSelections Selections => Settings.ForProfile(ProfileKey);

    public async Task InitializeAsync()
    {
        if (!TryDetectSetup(out var problem))
        {
            SetupDescription = problem;
            ShowPage(Page.Settings);
            return;
        }
        ShowPage(Page.Mods);
        await ModPicker.ScanAsync();
    }

    /// <summary>Finds the setup without asking: MO2 that launched us, then saved choices, then the plain game.</summary>
    public bool TryDetectSetup(out string problem)
    {
        problem = "";
        try
        {
            var detected = Mo2Detector.Detect(_args);
            if (detected != null)
            {
                Setup = GameSetup.FromMo2(detected.Instance, detected.Profile, detected.HowFound);
            }
            else if (!string.IsNullOrWhiteSpace(Settings.Mo2IniPath) && File.Exists(Settings.Mo2IniPath))
            {
                var instance = Mo2Instance.Load(Settings.Mo2IniPath);
                Setup = GameSetup.FromMo2(instance, Settings.Mo2Profile ?? instance.SelectedProfile, "Saved Mod Organizer 2 instance");
            }
            else
            {
                var game = !string.IsNullOrWhiteSpace(Settings.GameFolder) ? Settings.GameFolder : GameSetup.FindGameFolder();
                if (game == null || !File.Exists(Path.Combine(game, "Data", "Skyrim.esm")))
                {
                    problem = "Couldn't find Skyrim Special Edition. Open this program from Mod Organizer 2, or set your MO2 instance or game folder below.";
                    return false;
                }
                Setup = GameSetup.FromGameFolder(game, string.IsNullOrWhiteSpace(Settings.GameFolder) ? "Found Skyrim automatically" : "Saved game folder");
            }
        }
        catch (Exception ex)
        {
            problem = "Couldn't read your setup: " + ex.Message;
            Setup = null;
            return false;
        }

        SetupDescription = Setup.IsMo2
            ? $"Mod Organizer 2 · profile \"{Setup.ProfileName}\" · {Setup.AllMods.Count} mods ({Setup.AllMods.Count(m => m.IsEnabled)} enabled)  ({Setup.HowFound})"
            : $"Skyrim at {Setup.GameFolder} (no mod manager)  ({Setup.HowFound})";
        ResetPlugins();
        AdoptNpc2Folders();
        Mugshots.Generator = SettingsPage.CreateGenerator(Setup);
        return true;
    }

    /// <summary>
    /// If NPC Plugin Chooser 2 is in MO2's executables, reuse its mugshot folder and Portrait Creator
    /// so an existing picture collection works straight away. Only fills settings that are still empty.
    /// </summary>
    private void AdoptNpc2Folders()
    {
        var npc2 = Setup?.Mo2?.FindNpcPluginChooser2Folder();
        if (npc2 == null) return;
        bool changed = false;
        var mugshots = Path.Combine(npc2, "Mugshots");
        if (string.IsNullOrWhiteSpace(Settings.MugshotsFolder) && Directory.Exists(mugshots))
        {
            Settings.MugshotsFolder = mugshots;
            changed = true;
        }
        var portraitCreator = Path.Combine(npc2, "NPC Portrait Creator", "NPCPortraitCreator.exe");
        if (string.IsNullOrWhiteSpace(Settings.PortraitCreatorPath) && File.Exists(portraitCreator))
        {
            Settings.PortraitCreatorPath = portraitCreator;
            changed = true;
        }
        if (changed) SaveSettings();
    }

    public PluginCache Plugins => _plugins ??= new PluginCache(Setup!);

    public void ResetPlugins()
    {
        _plugins?.Dispose();
        _plugins = null;
        ArchiveHelper.ClearCache();
        Index = null;
        Chooser?.StopPictures();
        Chooser = null;
        Build = null;
    }

    /// <summary>Which mods depend on which (from the quick scan).</summary>
    public Dependents? Deps { get; private set; }

    public void SetPreScan(IReadOnlyList<ModSummary> mods)
    {
        PreScan = mods;
        Deps = new Dependents(mods);
    }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>Saves shortly after the last change (for sliders and search boxes that change on every keystroke).</summary>
    public void SaveSettingsSoon()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveSettings(); };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public async Task LoadNpcsAsync(IReadOnlyList<ModSummary> ticked)
    {
        if (Setup == null) return;
        var setup = Setup;
        Selections.TickedMods = ticked.Select(m => m.Mod.Name).ToList();
        SaveSettings();

        await RunBusyAsync("Reading the overhauls you picked...", async progress =>
        {
            var plugins = Plugins;
            var all = PreScan;
            Index = await Task.Run(() => NpcScanner.Scan(setup, ticked, all, plugins, progress));
        });
        if (Index == null) return;
        Chooser?.StopPictures();
        Chooser = new NpcChooserViewModel(this, Index);
        Build = new BuildViewModel(this);
        ShowPage(Page.Npcs);
    }

    public async Task RunBusyAsync(string message, Func<IProgress<string>, Task> work)
    {
        BusyMessage = message;
        var progress = new Progress<string>(m => BusyMessage = m);
        try { await work(progress); }
        catch (Exception ex)
        {
            MessageBox.Show(Application.Current.MainWindow!, ex.Message, AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { BusyMessage = null; }
    }

    public void SaveSettings()
    {
        try { Settings.Save(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("Saving settings failed: " + ex.Message); }
    }

    public void ShowPage(Page page)
    {
        CurrentStep = page;
        CurrentPage = page switch
        {
            Page.Mods => ModPicker,
            Page.Npcs => (object?)Chooser ?? ModPicker,
            Page.Build => (object?)Build ?? ModPicker,
            _ => SettingsPage,
        };
        if (page == Page.Settings) SettingsPage.Refresh();
        if (page == Page.Build) Build?.RefreshSummary();
    }

    [RelayCommand] private void GoToMods() => ShowPage(Page.Mods);
    [RelayCommand] private void GoToNpcs() { if (Chooser != null) ShowPage(Page.Npcs); }
    [RelayCommand] private void GoToBuild() { if (Build != null) ShowPage(Page.Build); }
    [RelayCommand] private void GoToSettings() => ShowPage(Page.Settings);

    public void Shutdown()
    {
        Chooser?.StopPictures();
        Chooser?.SaveChoices();
        SaveSettings();
        _plugins?.Dispose();
    }
}
