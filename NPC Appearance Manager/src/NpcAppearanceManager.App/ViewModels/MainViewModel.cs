using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NpcAppearanceManager.Core.Build;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Mugshots;
using NpcAppearanceManager.Core.Scanning;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.App.ViewModels;

public enum Page { Npcs, Copy, Build, Settings }

/// <summary>The app shell: holds the detected setup, reads every mod with faces, and moves between the steps.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly string[] _args;
    private PluginCache? _plugins;

    public AppSettings Settings { get; } = AppSettings.Load();
    public MugshotService Mugshots { get; }
    public GameSetup? Setup { get; private set; }
    public NpcIndex? Index { get; private set; }
    public IReadOnlyList<ModSummary> PreScan { get; private set; } = [];

    /// <summary>FaceFinder's whole list (null until it's been downloaded once).</summary>
    public FaceFinderCatalog? Catalog { get; private set; }

    /// <summary>Which plugin a pluginless face was made for; set up in the background after the NPCs are read.</summary>
    public Task<FacePlugins> FacePlugins { get; private set; } = Task.FromException<FacePlugins>(new InvalidOperationException("Not loaded yet."));

    /// <summary>
    /// Started after loading: for each overhaul, a file it replaces that none of its faces use (it then stays on),
    /// so the Build page knows before anyone presses Build.
    /// </summary>
    public Task<Dictionary<ModSummary, string?>> OverhaulFiles { get; private set; } = Task.FromResult(new Dictionary<ModSummary, string?>());
    [ObservableProperty] private string _faceFinderStatus = "";
    private Task? _catalogDownload;
    private bool _downloadingCatalog; // progress reports arriving after the end are dropped
    /// <summary>FaceFinder is on but its list isn't there yet (being downloaded, or about to be).</summary>
    public bool WaitingForCatalog => Settings.UseFaceFinder && (_downloadingCatalog || (Catalog == null && !_catalogFailed));
    private bool _catalogFailed;
    [ObservableProperty] private string _faceFinderPicturesStatus = "";
    private CancellationTokenSource? _downloadAll;

    public SettingsViewModel SettingsPage { get; }
    [ObservableProperty] private NpcChooserViewModel? _chooser;
    [ObservableProperty] private FaceCopyViewModel? _copyFaces;
    [ObservableProperty] private BuildViewModel? _build;

    [ObservableProperty] private object? _currentPage;
    [ObservableProperty] private Page _currentStep = Page.Npcs;
    [ObservableProperty] private string? _busyMessage;
    [ObservableProperty] private string _setupDescription = "Looking for your game...";

    public MainViewModel(string[] args)
    {
        _args = args;
        Mugshots = new MugshotService(Settings);
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
        await LoadNpcsAsync();
        if (Chooser == null) ShowPage(Page.Settings);
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
        UseOwnMugshotsFolder();
        Mugshots.Generator = SettingsPage.CreateGenerator(Setup);
        return true;
    }

    /// <summary>With no mugshot folder set (a fresh copy of the tool), the tool's own one in Mugshot Cache is used.</summary>
    private void UseOwnMugshotsFolder()
    {
        if (!string.IsNullOrWhiteSpace(Settings.MugshotsFolder)) return;
        Directory.CreateDirectory(AppSettings.DefaultMugshotsFolder);
        Settings.MugshotsFolder = AppSettings.DefaultMugshotsFolder;
        SaveSettings();
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
        CopyFaces = null;
        Build = null;
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

    /// <summary>Reads every installed mod with faces (enabled and disabled) and opens Pick looks.</summary>
    public async Task LoadNpcsAsync()
    {
        if (Setup == null) return;
        var setup = Setup;
        await RunBusyAsync("Checking your mods...", async progress =>
        {
            var plugins = Plugins;
            PreScan = await Task.Run(() => ModPreScanner.Scan(setup, progress));
            var withFaces = PreScan.Where(m => m.IsAppearanceMod).ToList();
            var all = PreScan;
            Index = await Task.Run(() => NpcScanner.Scan(setup, withFaces, all, plugins, progress));
            if (Catalog == null && Settings.UseFaceFinder) { Catalog = await Task.Run(FaceFinderCatalog.Load); Mugshots.Catalog = Catalog; }
        });
        if (Index == null) return;
        if (ArchiveHelper.UnreadableArchives is { Count: > 0 } broken)
            MessageBox.Show(Application.Current.MainWindow!,
                string.Join("\n", broken.Select(a => $"Couldn't read {Path.GetFileName(a)} (damaged archive). Faces inside it are skipped.")),
                AppSettings.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        Chooser?.StopPictures();
        var plugins = Plugins;
        FacePlugins = Task.Run(() => new FacePlugins(setup, plugins));
        var index = Index;
        OverhaulFiles = Task.Run(() => ModContents.CheckOverhauls(setup, index, plugins));
        Chooser = new NpcChooserViewModel(this, Index);
        CopyFaces = new FaceCopyViewModel(this, Chooser);
        Build = new BuildViewModel(this);
        ShowPage(Page.Npcs);
        if (Catalog != null) FaceFinderStatus = CatalogText(Catalog);
        else if (Settings.UseFaceFinder) UpdateFaceFinder();
        StartDownloadAll();
    }

    private static string CatalogText(FaceFinderCatalog c) =>
        $"FaceFinder: {c.Faces.Count:N0} faces from {c.Mods.Count:N0} mods (downloaded {c.DownloadedAt:d})";

    /// <summary>After switching FaceFinder on: uses the list already downloaded, or downloads it.</summary>
    public void StartFaceFinder()
    {
        var saved = FaceFinderCatalog.Load();
        if (saved == null) { UpdateFaceFinder(); return; }
        Catalog = saved;
        Mugshots.Catalog = saved;
        FaceFinderStatus = CatalogText(saved);
        Chooser?.ApplyCatalog(saved);
        StartDownloadAll();
    }

    /// <summary>
    /// Downloads FaceFinder's whole list in the background (first launch, or "Update FaceFinder" in Settings), then
    /// adds its faces to Pick looks. Only one download runs at a time.
    /// </summary>
    public void UpdateFaceFinder()
    {
        if (_catalogDownload is { IsCompleted: false }) return;
        FaceFinderStatus = "FaceFinder: getting the list of faces...";
        _catalogFailed = false;
        _downloadingCatalog = true;
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            if (_downloadingCatalog) FaceFinderStatus = $"FaceFinder: getting the list of faces ({p.Done:N0} / {p.Total:N0} mods)...";
        });
        _catalogDownload = DownloadCatalogAsync(progress);
    }

    private async Task DownloadCatalogAsync(IProgress<(int, int)> progress)
    {
        try
        {
            var catalog = await Task.Run(() => FaceFinderCatalog.DownloadAsync(progress, CancellationToken.None));
            // Saved mugshots of faces FaceFinder changed since the last list are deleted, so they're downloaded again.
            if (Catalog is { } older)
            {
                var changed = FaceFinderCatalog.ChangedSince(older, catalog);
                await Task.Run(() => MugshotService.DeleteSavedPictures(changed));
            }
            _downloadingCatalog = false;
            Catalog = catalog;
            Mugshots.Catalog = catalog;
            FaceFinderStatus = CatalogText(catalog);
            Chooser?.ApplyCatalog(catalog);
            StartDownloadAll();
        }
        catch (Exception ex)
        {
            _downloadingCatalog = false;
            _catalogFailed = true;
            FaceFinderStatus = "FaceFinder: couldn't get the list of faces. " + ex.Message;
            Chooser?.ResumePictures();
        }
    }

    /// <summary>
    /// "Download all FaceFinder mugshots": saves every picture in FaceFinder's list in the background, skipping the
    /// ones already saved, so even the first click on an NPC is instant. Runs while the option is on.
    /// </summary>
    public void StartDownloadAll()
    {
        var catalog = Catalog;
        if (!Settings.UseFaceFinder || !Settings.DownloadAllFaceFinder || catalog == null || _downloadAll != null) return;
        var cts = _downloadAll = new CancellationTokenSource();
        var faces = catalog.Faces.Where(f => f.NpcKey != null).ToList();
        int done = 0, failed = 0;
        FaceFinderPicturesStatus = $"FaceFinder mugshots: checking {faces.Count:N0}...";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => FaceFinderPicturesStatus = $"FaceFinder mugshots: {done:N0} / {faces.Count:N0}" + (failed > 0 ? $" ({failed:N0} couldn't be downloaded)" : "");
        timer.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(faces, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cts.Token }, async (face, token) =>
                {
                    var path = MugshotService.FaceFinderPicturePath(face);
                    if (path != null && !await MugshotService.SaveFaceFinderPictureAsync(face, path, token)) Interlocked.Increment(ref failed);
                    Interlocked.Increment(ref done);
                });
            }
            catch (OperationCanceledException) { }
            Application.Current.Dispatcher.Invoke(() =>
            {
                timer.Stop();
                if (_downloadAll == cts) _downloadAll = null;
                FaceFinderPicturesStatus = cts.IsCancellationRequested
                    ? $"FaceFinder mugshots: stopped at {done:N0} / {faces.Count:N0}. What's saved stays."
                    : $"FaceFinder mugshots: all {faces.Count - failed:N0} saved" + (failed > 0 ? $" ({failed:N0} couldn't be downloaded)" : "");
            });
        });
    }

    /// <summary>Stops "Download all FaceFinder mugshots"; the pictures saved so far stay.</summary>
    public void StopDownloadAll()
    {
        _downloadAll?.Cancel();
        _downloadAll = null;
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
            Page.Npcs => (object?)Chooser ?? SettingsPage,
            Page.Copy => (object?)CopyFaces ?? SettingsPage,
            Page.Build => (object?)Build ?? SettingsPage,
            _ => SettingsPage,
        };
        if (page == Page.Settings) SettingsPage.Refresh();
        if (page == Page.Copy) CopyFaces?.Start();
        if (page == Page.Build) Build?.RefreshSummary();
    }

    [RelayCommand] private void GoToNpcs() { if (Chooser != null) ShowPage(Page.Npcs); }
    [RelayCommand] private void GoToCopy() { if (CopyFaces != null) ShowPage(Page.Copy); }
    [RelayCommand] private void GoToBuild() { if (Build != null) ShowPage(Page.Build); }
    [RelayCommand] private void GoToSettings() => ShowPage(Page.Settings);

    public void Shutdown()
    {
        StopDownloadAll();
        Chooser?.StopPictures();
        Chooser?.SaveChoices();
        SaveSettings();
        _plugins?.Dispose();
    }
}
