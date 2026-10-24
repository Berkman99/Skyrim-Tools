using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NpcFaceManager.Core.Build;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.App.ViewModels;

/// <summary>One row of the "what happens to each mod" list.</summary>
public sealed record PlannedRow(string Name, string Status, PlanGroup Group, string? ToolTip);

/// <summary>A mod to install before building: picked looks come from it.</summary>
public sealed partial class MissingMod(string name, string? url, IReadOnlyList<string> npcs)
{
    public string Name { get; } = name;
    public string? Url { get; } = url;

    /// <summary>The NPCs you picked a look from this mod for.</summary>
    public IReadOnlyList<string> Npcs { get; } = npcs;
    public string Details => Npcs.Count == 1 ? "1 NPC" : $"{Npcs.Count:N0} NPCs";
    public string NpcList => string.Join(System.Environment.NewLine, Npcs);
    public bool HasUrl => Url != null;

    [RelayCommand]
    private void Open()
    {
        if (Url != null && Uri.TryCreate(Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

public partial class BuildViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    [ObservableProperty] private string _outputModName;
    [ObservableProperty] private string _outputPluginName;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _isBuilding;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private BuildReport? _report;
    [ObservableProperty] private string _resultHeadline = "";
    [ObservableProperty] private string _mo2Message = "";

    public ObservableCollection<string> SafeToDisable { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> StillNeeded { get; } = [];

    /// <summary>Mods you picked looks from but don't have installed; the build waits until they are.</summary>
    public ObservableCollection<MissingMod> ToInstall { get; } = [];
    public bool CanBuild => ToInstall.Count == 0 && !IsBuilding;

    partial void OnIsBuildingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanBuild));
        BuildCommand.NotifyCanExecuteChanged();
    }

    /// <summary>What the build will do with each overhaul (and the dependencies merged with them).</summary>
    public ObservableCollection<PlannedRow> Planned { get; } = [];
    [ObservableProperty] private string _plannedSummary = "";

    public BuildViewModel(MainViewModel main)
    {
        _main = main;
        _outputModName = main.Settings.OutputModName;
        _outputPluginName = main.Settings.OutputPluginName;
    }

    public bool IsMo2 => _main.Setup?.IsMo2 == true;

    /// <summary>Switch the merged mods/plugins off in MO2 right after building (saved immediately).</summary>
    public bool AutoDisable
    {
        get => _main.Settings.AutoDisableInMo2;
        set
        {
            if (_main.Settings.AutoDisableInMo2 == value) return;
            _main.Settings.AutoDisableInMo2 = value;
            _main.SaveSettings();
            OnPropertyChanged();
        }
    }

    public string OutputFolder
    {
        get
        {
            var setup = _main.Setup!;
            return setup.IsMo2
                ? Path.Combine(setup.Mo2!.ModsDirectory, OutputModName)
                : Path.Combine(AppSettings.AppFolder, "Output", OutputModName);
        }
    }

    partial void OnOutputModNameChanged(string value)
    {
        OnPropertyChanged(nameof(OutputFolder));
        if (string.IsNullOrWhiteSpace(value)) return;
        _main.Settings.OutputModName = value.Trim();
        _main.SaveSettings();
    }

    partial void OnOutputPluginNameChanged(string value)
    {
        // ".esp" is added by the tool; typing it just gets removed from the box.
        var name = AppSettings.PluginBaseName(value);
        if (name != value.Trim() && value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
        {
            OutputPluginName = name;
            return;
        }
        if (string.IsNullOrWhiteSpace(name)) return;
        _main.Settings.OutputPluginName = name;
        _main.SaveSettings();
    }

    private string PluginFileName => AppSettings.PluginBaseName(OutputPluginName) + ".esp";

    private int _previewRun;

    /// <summary>Updates the summary and the overhaul list from the current picks.</summary>
    public async void RefreshSummary()
    {
        var chooser = _main.Chooser;
        if (chooser == null || _main.Setup == null || _main.Index == null) return;
        var changed = chooser.Rows.Count(r => r.ChosenMod != NpcRowViewModel.DefaultChoice(r.Entry));
        Summary = $"{changed:N0} NPCs changed from what the game shows now";
        ToInstall.Clear();
        foreach (var (mod, npcs) in chooser.OnlinePicks()) ToInstall.Add(new MissingMod(mod.Name, mod.Url, npcs));
        OnPropertyChanged(nameof(CanBuild));
        BuildCommand.NotifyCanExecuteChanged();

        int run = ++_previewRun;
        PlannedSummary = _main.OverhaulFiles.IsCompleted ? "Working out what happens to each face mod..." : "Checking the face mods' files...";
        try
        {
            var request = MakeRequest(chooser, "", "", await CheckedFilesAsync());
            if (run != _previewRun) return;
            var planned = await Task.Run(() => MergeBuilder.Preview(request));
            if (run != _previewRun) return;
            Planned.Clear();
            foreach (var p in planned)
            {
                var status = p.Group switch
                {
                    PlanGroup.Merged => p.IsEnabled ? "Merged and switched off" : "Picked faces copied (stays disabled)",
                    _ => "Stays on",
                };
                Planned.Add(new PlannedRow(p.Name, status, p.Group, p.Reason));
            }
            PlannedSummary = $"{planned.Count(p => p.Group == PlanGroup.Merged)} merged · " +
                             $"{planned.Count(p => p.Group == PlanGroup.StaysOn)} stay on";
        }
        catch (Exception ex)
        {
            if (run == _previewRun) PlannedSummary = "Couldn't work it out: " + ex.Message;
        }
    }

    /// <summary>The background check of the overhauls' files (null if it failed: the build then checks them itself).</summary>
    private async Task<IReadOnlyDictionary<NpcFaceManager.Core.Scanning.ModSummary, string?>?> CheckedFilesAsync()
    {
        try { return await _main.OverhaulFiles; }
        catch (Exception) { return null; }
    }

    private BuildRequest MakeRequest(NpcChooserViewModel chooser, string outputFolder, string plugin,
        IReadOnlyDictionary<NpcFaceManager.Core.Scanning.ModSummary, string?>? checkedFiles) => new()
    {
        Setup = _main.Setup!,
        Index = _main.Index!,
        AllMods = _main.PreScan,
        Choices = chooser.GetChoices(),
        CheckedFiles = checkedFiles,
        OutputFolder = outputFolder,
        OutputPluginName = plugin,
    };

    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        var setup = _main.Setup;
        var chooser = _main.Chooser;
        if (setup == null || chooser == null || _main.Index == null || chooser.OnlinePicks().Count > 0) return;

        if (string.IsNullOrWhiteSpace(OutputPluginName)) OutputPluginName = _main.Settings.OutputPluginName;
        var plugin = PluginFileName;
        chooser.SaveChoices();

        IsBuilding = true;
        ProgressText = "Checking the face mods' files...";
        var request = MakeRequest(chooser, OutputFolder, plugin, await CheckedFilesAsync());
        Report = null;
        try
        {
            var progress = new Progress<string>(m => ProgressText = m);
            var report = await Task.Run(() => MergeBuilder.BuildAsync(request, progress));
            ShowReport(report);
            if (setup.IsMo2 && AutoDisable) SwitchOffInMo2(report);
            else if (setup.IsMo2)
                Mo2Message = "Not switched off in MO2 (turned off in the build options).";
        }
        catch (Exception ex)
        {
            ProgressText = "";
            MessageBox.Show(Application.Current.MainWindow!, "The build failed:\n\n" + ex.Message, AppSettings.AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsBuilding = false; }
    }

    private void ShowReport(BuildReport report)
    {
        Report = report;
        ResultHeadline = $"Done · {report.NpcsChanged + report.FaceOnlyNpcs:N0} faces · {report.RecordsCopied:N0} records · {report.FilesCopied:N0} files" +
                         (report.IsLightPlugin ? " · ESL" : "");
        Fill(SafeToDisable, report.ModsSafeToDisable.Concat(report.MergedDisabledMods));
        Fill(Warnings, report.Warnings);
        Fill(StillNeeded, report.Masters);
        Mo2Message = !_main.Setup!.IsMo2
            ? $"Install {report.OutputFolder} as a mod, then disable the merged mods."
            : "";
    }

    private void SwitchOffInMo2(BuildReport report)
    {
        var setup = _main.Setup!;
        try
        {
            ProfileFiles.ApplyMergeResult(setup.ProfileDirectory!,
                MergeChanges.From(report, setup.PreviousMarker, OutputModName, PluginFileName));
            Mo2Message = $"Merged mods disabled in \"{setup.ProfileName}\", \"{OutputModName}\" enabled. If MO2 is open, press F5 there after closing this tool.";
        }
        catch (Exception ex)
        {
            Mo2Message = "Couldn't change the MO2 profile: " + ex.Message;
        }
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    [RelayCommand]
    private void CopyList()
    {
        if (Report == null) return;
        Clipboard.SetText(string.Join(System.Environment.NewLine, SafeToDisable));
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (Directory.Exists(OutputFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{OutputFolder}\"") { UseShellExecute = true });
    }

    [RelayCommand] private void Back() => _main.ShowPage(Page.Npcs);
}

