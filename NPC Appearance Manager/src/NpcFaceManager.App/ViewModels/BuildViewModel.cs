using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NpcFaceManager.Core.Build;
using NpcFaceManager.Core.Environment;
using NpcFaceManager.Core.Settings;

namespace NpcFaceManager.App.ViewModels;

public sealed record KeptMod(string Name, string Reason);

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
    public ObservableCollection<KeptMod> MustStay { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<string> StillNeeded { get; } = [];

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

    public void RefreshSummary()
    {
        var choices = _main.Chooser?.GetChoices() ?? [];
        var mods = choices.Values.Select(l => l.ModName).Distinct().Count();
        Summary = $"{choices.Count:N0} looks from {mods} mod(s)";
    }

    [RelayCommand]
    private async Task BuildAsync()
    {
        var setup = _main.Setup;
        var chooser = _main.Chooser;
        if (setup == null || chooser == null || _main.Index == null) return;

        if (string.IsNullOrWhiteSpace(OutputPluginName)) OutputPluginName = _main.Settings.OutputPluginName;
        var plugin = PluginFileName;
        chooser.SaveChoices();

        var request = new BuildRequest
        {
            Setup = setup,
            Index = _main.Index,
            AllMods = _main.PreScan,
            Choices = chooser.GetChoices(),
            OutputFolder = OutputFolder,
            OutputPluginName = plugin,
        };

        IsBuilding = true;
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
        ResultHeadline = $"Done · {report.NpcsChanged + report.FaceOnlyNpcs:N0} looks · {report.RecordsCopied:N0} records · {report.FilesCopied:N0} files" +
                         (report.IsLightPlugin ? " · ESL" : "");
        Fill(SafeToDisable, report.ModsSafeToDisable.Concat(report.MergedDisabledMods));
        MustStay.Clear();
        foreach (var (mod, reason) in report.ModsToKeep) MustStay.Add(new KeptMod(mod, reason));
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

