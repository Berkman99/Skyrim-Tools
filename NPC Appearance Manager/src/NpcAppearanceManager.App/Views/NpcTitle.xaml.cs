using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NpcAppearanceManager.App.ViewModels;

namespace NpcAppearanceManager.App.Views;

public partial class NpcTitle : UserControl
{
    public NpcTitle()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ShowEditor(false); // another NPC selected
    }

    private void ShowEditor(bool show)
    {
        Editor.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        RenameButton.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StartRename(object sender, RoutedEventArgs e)
    {
        if (DataContext is not NpcRowViewModel row) return;
        NameBox.Text = row.CustomName ?? "";
        ShowEditor(true);
        NameBox.Focus();
        NameBox.SelectAll();
    }

    /// <summary>An empty box gives the NPC its own name back.</summary>
    private void Confirm(object sender, RoutedEventArgs e)
    {
        if (DataContext is NpcRowViewModel row)
            row.CustomName = string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim();
        ShowEditor(false);
    }

    private void Cancel(object sender, RoutedEventArgs e) => ShowEditor(false);

    private void NameBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Confirm(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape) { ShowEditor(false); e.Handled = true; }
    }
}
