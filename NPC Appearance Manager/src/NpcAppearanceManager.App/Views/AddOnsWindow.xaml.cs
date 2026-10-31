using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NpcAppearanceManager.Core.Build;

namespace NpcAppearanceManager.App.Views;

public sealed partial class AddOnOption(string key, string name, bool isTicked) : ObservableObject
{
    public string Key { get; } = key;
    public string Name { get; } = name;
    [ObservableProperty] private bool _isTicked = isTicked;
}

public sealed class AddOnGroupView(string faceMod, IReadOnlyList<AddOnOption> options)
{
    public string FaceMod { get; } = faceMod;
    public IReadOnlyList<AddOnOption> Options { get; } = options;

    public string Heading => $"Other mods change {FaceMod}{(FaceMod.EndsWith('s') ? "'" : "'s")} faces, use them too?";
}

/// <summary>Save's question: mods that change the faces you picked, which NAM couldn't link to their face mod. Tick the ones to use.</summary>
public partial class AddOnsWindow : Window
{
    private readonly List<AddOnGroupView> _groups;

    public AddOnsWindow(IReadOnlyList<AddOnGroup> asked, IReadOnlyCollection<string> ticked)
    {
        InitializeComponent();
        _groups = asked.Select(g => new AddOnGroupView(g.FaceMod, g.AddOns
            .Select(a => new AddOnOption(AddOnScan.Key(g.FaceMod, a), a, ticked.Contains(AddOnScan.Key(g.FaceMod, a)))).ToList())).ToList();
        Groups.ItemsSource = _groups;
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    /// <summary>The add-ons ticked, each as <see cref="AddOnScan.Key"/>.</summary>
    public IEnumerable<string> Ticked => _groups.SelectMany(g => g.Options).Where(o => o.IsTicked).Select(o => o.Key);

    private void Save(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
