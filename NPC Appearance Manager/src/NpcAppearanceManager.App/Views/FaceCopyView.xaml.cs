using System.Windows.Controls;

namespace NpcAppearanceManager.App.Views;

public partial class FaceCopyView : UserControl
{
    public FaceCopyView() => InitializeComponent();

    /// <summary>NPC 2 picked for you (NPC 1's copied face comes from it) scrolls into view.</summary>
    private void ShowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item, IsLoaded: true } list) list.ScrollIntoView(item);
    }
}
