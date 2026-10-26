using System.Collections.ObjectModel;
using System.Windows;
using NpcAppearanceManager.App.ViewModels;

namespace NpcAppearanceManager.App.Views;

/// <summary>"Reset names": every renamed NPC in name order; its x gives it its own name back, without asking.</summary>
public partial class RenamedNpcsWindow : Window
{
    private readonly ObservableCollection<NpcRowViewModel> _renamed;

    public RenamedNpcsWindow(NpcChooserViewModel chooser)
    {
        InitializeComponent();
        _renamed = new(chooser.Rows.Where(r => r.CustomName != null)
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        List.ItemsSource = _renamed;
        UpdateEmpty();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    private void ResetName(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not NpcRowViewModel row) return;
        row.CustomName = null;
        _renamed.Remove(row);
        UpdateEmpty();
    }

    private void UpdateEmpty() => EmptyText.Visibility = _renamed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
}
