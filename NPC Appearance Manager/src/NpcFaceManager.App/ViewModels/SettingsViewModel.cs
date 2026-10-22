using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Mugshots;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.App.ViewModels;

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
    [ObservableProperty] private GeneratorOption _generator;
    [ObservableProperty] private string? _portraitCreatorPath;
    [ObservableProperty] private string _status = "";

    public ObservableCollection<string> Profiles { get; } = [];

    public IReadOnlyList<GeneratorOption> GeneratorOptions { get; } =
    [
        new(MugshotGeneratorKind.Off, "Off"),
        new(MugshotGeneratorKind.InternalRenderer, "Internal renderer"),
        new(MugshotGeneratorKind.PortraitCreator, "Portrait Creator"),
    ];

    public bool FaceFinderAvailable => MugshotService.FaceFinderAvailable;
    public string FaceFinderNote => FaceFinderAvailable
        ? "Downloads missing pictures from npcfacefinder.com"
        : "Not available yet";

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
    partial void OnUseFaceFinderChanged(bool value) => Persist(picturesChanged: true);
    partial void OnGeneratorChanged(GeneratorOption value) => Persist(generatorChanged: true, picturesChanged: true);
    partial void OnPortraitCreatorPathChanged(string? value) => Persist(generatorChanged: true, picturesChanged: true);
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
        s.PortraitCreatorPath = string.IsNullOrWhiteSpace(PortraitCreatorPath) ? null : PortraitCreatorPath;
        _main.SaveSettings();
        if (generatorChanged && _main.Setup != null) _main.Mugshots.Generator = CreateGenerator(_main.Setup);
        if (picturesChanged) _main.Chooser?.RestartPictures();
    }

    private void RefreshCore()
    {
        var s = _main.Settings;
        Mo2IniPath = _main.Setup?.Mo2?.IniPath ?? s.Mo2IniPath;
        GameFolder = s.GameFolder;
        MugshotsFolder = s.MugshotsFolder;
        UseFaceFinder = s.UseFaceFinder && FaceFinderAvailable;
        Generator = GeneratorOptions.First(g => g.Kind == s.Generator);
        PortraitCreatorPath = s.PortraitCreatorPath;
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
        MugshotGeneratorKind.PortraitCreator when File.Exists(_main.Settings.PortraitCreatorPath) =>
            new PortraitCreatorGenerator(_main.Settings.PortraitCreatorPath!, setup),
        MugshotGeneratorKind.InternalRenderer => InternalRendererFactory.TryCreate(setup, _main.Plugins),
        _ => null,
    };

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

    [RelayCommand]
    private void BrowsePortraitCreator()
    {
        var d = new OpenFileDialog { Title = "Pick NPCPortraitCreator.exe", Filter = "Programs|*.exe" };
        if (d.ShowDialog() == true) PortraitCreatorPath = d.FileName;
    }

    /// <summary>Re-reads the setup after changing the MO2 instance, profile or game folder (settings are already saved).</summary>
    [RelayCommand]
    private async Task ReloadSetupAsync()
    {
        Persist();
        await _main.InitializeAsync();
    }
}

