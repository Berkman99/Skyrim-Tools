using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NpcAppearanceManager.Core.Environment;
using NpcAppearanceManager.Core.Mugshots;
using NpcAppearanceManager.Core.Settings;

namespace NpcAppearanceManager.App.ViewModels;

public sealed record GeneratorOption(MugshotGeneratorKind Kind, string Label)
{
    public override string ToString() => Label; // what the closed dropdown shows
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private string? _mo2IniPath;
    [ObservableProperty] private string? _mo2Profile;
    [ObservableProperty] private string? _gameFolder;
    [ObservableProperty] private string? _mugshotsFolder;
    [ObservableProperty] private bool _useFaceFinder;
    [ObservableProperty] private bool _downloadAllFaceFinder;
    [ObservableProperty] private bool _hideNotInstalled;
    [ObservableProperty] private bool _renderAllNpcs;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RendererOn))]
    private GeneratorOption _generator;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<string> Profiles { get; } = [];

    public IReadOnlyList<GeneratorOption> GeneratorOptions { get; } =
    [
        new(MugshotGeneratorKind.Off, "Off"),
        new(MugshotGeneratorKind.InternalRenderer, "Internal renderer (GPU)"),
        new(MugshotGeneratorKind.PortraitCreator, "Portrait Creator"),
    ];

    public MainViewModel Main => _main;

    public bool RendererOn => Generator.Kind != MugshotGeneratorKind.Off;

    public bool LaunchedFromMo2 => _main.Setup?.HowFound == "Launched from Mod Organizer 2";

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _generator = GeneratorOptions[0];
    }

    private bool _refreshing;

    public void Refresh()
    {
        _refreshing = true;
        try { RefreshCore(); }
        finally { _refreshing = false; }
    }

    // Every change is saved straight away.
    partial void OnMugshotsFolderChanged(string? value) => Persist(picturesChanged: true);
    partial void OnUseFaceFinderChanged(bool value)
    {
        Persist(picturesChanged: true);
        if (!_refreshing) _main.Chooser?.RefreshNotInstalled(); // "Hide mods not installed" only counts with FaceFinder on
    }

    partial void OnDownloadAllFaceFinderChanged(bool value)
    {
        if (_refreshing) return;
        _main.Settings.DownloadAllFaceFinder = value;
        _main.SaveSettings();
        if (value) _main.StartDownloadAll();
        else _main.StopDownloadAll(); // what's saved stays; "Remove downloaded mugshots" deletes it
    }
    partial void OnGeneratorChanged(GeneratorOption value) => Persist(generatorChanged: true, picturesChanged: true);

    partial void OnHideNotInstalledChanged(bool value)
    {
        if (_refreshing) return;
        _main.Settings.HideNotInstalled = value;
        _main.SaveSettings();
        _main.Chooser?.RefreshNotInstalled();
    }

    partial void OnRenderAllNpcsChanged(bool value)
    {
        if (_refreshing) return;
        _main.Settings.RenderAllNpcs = value;
        _main.SaveSettings();
        _main.Chooser?.RestartPictureJob();
    }

    partial void OnMo2ProfileChanged(string? value) => Persist();
    partial void OnGameFolderChanged(string? value) => Persist();

    private void Persist(bool generatorChanged = false, bool picturesChanged = false)
    {
        if (_refreshing) return;
        var s = _main.Settings;
        if (!LaunchedFromMo2)
        {
            s.Mo2IniPath = string.IsNullOrWhiteSpace(Mo2IniPath) ? null : Mo2IniPath;
            s.Mo2Profile = string.IsNullOrWhiteSpace(Mo2Profile) ? null : Mo2Profile;
            s.GameFolder = string.IsNullOrWhiteSpace(GameFolder) ? null : GameFolder;
        }
        s.MugshotsFolder = string.IsNullOrWhiteSpace(MugshotsFolder) ? null : MugshotsFolder;
        s.UseFaceFinder = UseFaceFinder;
        s.Generator = Generator.Kind;
        _main.SaveSettings();
        if (generatorChanged && _main.Setup != null) _main.Mugshots.Generator = CreateGenerator(_main.Setup);
        if (picturesChanged) _main.Chooser?.RestartPictures();
        if (UseFaceFinder && _main.Catalog == null && _main.Chooser != null) _main.StartFaceFinder();
        if (!UseFaceFinder) _main.StopDownloadAll();
    }

    private void RefreshCore()
    {
        var s = _main.Settings;
        Mo2IniPath = _main.Setup?.Mo2?.IniPath ?? s.Mo2IniPath;
        GameFolder = s.GameFolder;
        MugshotsFolder = s.MugshotsFolder;
        UseFaceFinder = s.UseFaceFinder;
        DownloadAllFaceFinder = s.DownloadAllFaceFinder;
        HideNotInstalled = s.HideNotInstalled;
        RenderAllNpcs = s.RenderAllNpcs;
        Generator = GeneratorOptions.First(g => g.Kind == s.Generator);
        LoadProfiles();
        Mo2Profile = _main.Setup?.ProfileName ?? s.Mo2Profile ?? Profiles.FirstOrDefault();
        Status = _main.SetupDescription;
        OnPropertyChanged(nameof(LaunchedFromMo2));
    }

    partial void OnMo2IniPathChanged(string? value)
    {
        LoadProfiles();
        Persist();
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        try
        {
            if (!string.IsNullOrWhiteSpace(Mo2IniPath) && File.Exists(Mo2IniPath))
                foreach (var p in Mo2Instance.Load(Mo2IniPath).ListProfiles()) Profiles.Add(p);
        }
        catch { /* unreadable ini: leave empty */ }
    }

    public IMugshotGenerator? CreateGenerator(GameSetup setup) => _main.Settings.Generator switch
    {
        MugshotGeneratorKind.PortraitCreator when File.Exists(AppSettings.PortraitCreatorExe) =>
            new PortraitCreatorGenerator(AppSettings.PortraitCreatorExe, setup),
        MugshotGeneratorKind.InternalRenderer => InternalRendererFactory.TryCreate(setup, _main.Plugins),
        _ => null,
    };

    /// <summary>Downloads FaceFinder's list of faces again (new mods and updated faces).</summary>
    [RelayCommand]
    private void UpdateFaceFinder() => _main.UpdateFaceFinder();

    /// <summary>Deletes the saved FaceFinder pictures after a warning (FaceFinder's list of faces stays).</summary>
    [RelayCommand]
    private void RemoveFaceFinderPictures()
    {
        var (files, bytes) = MugshotService.SavedFaceFinderPictures();
        var owner = System.Windows.Application.Current.MainWindow!;
        if (files == 0)
        {
            System.Windows.MessageBox.Show(owner, "No FaceFinder mugshots are saved on this PC.", AppSettings.AppName);
            return;
        }
        var answer = System.Windows.MessageBox.Show(owner,
            $"Delete the {files:N0} FaceFinder mugshots saved on this PC ({bytes / 1024.0 / 1024:N0} MB)?\n\n" +
            "They're downloaded again when you open an NPC" + (DownloadAllFaceFinder ? ", and \"Download all FaceFinder mugshots\" is switched off" : "") + ". " +
            "Your mugshots folder and rendered mugshots aren't touched.",
            AppSettings.AppName, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        DownloadAllFaceFinder = false; // otherwise it would start downloading them all again
        _main.StopDownloadAll();
        _main.Chooser?.StopPictures();
        _main.Mugshots.DeleteSavedFaceFinderPictures();
        _main.FaceFinderPicturesStatus = "";
        _main.Chooser?.RestartPictures();
    }

    /// <summary>Deletes the current renderer's pictures and makes them again (mugshots and FaceFinder pictures stay).</summary>
    [RelayCommand]
    private void RegeneratePictures()
    {
        if (Generator.Kind == MugshotGeneratorKind.Off) return;
        var count = _main.Mugshots.GeneratedCount;
        if (count == 0)
        {
            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow!,
                $"{Generator.Label} hasn't made any mugshots yet.", AppSettings.AppName);
            return;
        }
        var answer = System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow!,
            $"Delete the {count:N0} mugshots {Generator.Label} made and make them again?\n\n" +
            "Mugshots from your mugshots folder and FaceFinder aren't touched.",
            AppSettings.AppName, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        _main.Chooser?.StopPictures(); // don't make pictures while they're being deleted
        try { _main.Mugshots.ClearGenerated(); }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow!,
                "Couldn't delete all the mugshots: " + ex.Message, AppSettings.AppName);
        }
        _main.Chooser?.RestartPictures(); // makes whatever is missing again

    }

    [RelayCommand]
    private void BrowseMo2()
    {
        var d = new OpenFileDialog { Title = "Pick ModOrganizer.ini", Filter = "ModOrganizer.ini|ModOrganizer.ini" };
        if (d.ShowDialog() == true) Mo2IniPath = d.FileName;
    }

    [RelayCommand]
    private void BrowseGame()
    {
        var d = new OpenFolderDialog { Title = "Pick your Skyrim Special Edition folder (the one with SkyrimSE.exe)" };
        if (d.ShowDialog() == true) GameFolder = d.FolderName;
    }

    [RelayCommand]
    private void BrowseMugshots()
    {
        var d = new OpenFolderDialog { Title = "Pick your mugshots folder" };
        if (d.ShowDialog() == true) MugshotsFolder = d.FolderName;
    }

    /// <summary>Re-reads the setup after changing the MO2 instance, profile or game folder (settings are already saved).</summary>
    [RelayCommand]
    private async Task ReloadSetupAsync()
    {
        Persist();
        await _main.InitializeAsync();
    }
}

