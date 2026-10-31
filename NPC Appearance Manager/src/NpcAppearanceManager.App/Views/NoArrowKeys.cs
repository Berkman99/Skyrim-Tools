using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NpcAppearanceManager.App.Views;

/// <summary>
/// For a filter dropdown that sits above a list: Up and Down don't change the dropdown (they'd pick another filter by accident),
/// and once something is picked in it the keyboard goes back to the list.
/// </summary>
public static class NoArrowKeys
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(NoArrowKeys),
        new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        combo.PreviewKeyDown -= OnPreviewKeyDown;
        combo.DropDownClosed -= OnDropDownClosed;
        if (!(bool)e.NewValue) return;
        combo.PreviewKeyDown += OnPreviewKeyDown;
        combo.DropDownClosed += OnDropDownClosed;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Up or Key.Down) e.Handled = true;
    }

    private static void OnDropDownClosed(object? sender, EventArgs e)
    {
        if (sender is ComboBox combo) FindNearestList(combo)?.Focus();
    }

    /// <summary>The list in the same column: the first one in the closest parent that has one.</summary>
    private static ListBox? FindNearestList(DependencyObject start)
    {
        for (var parent = VisualTreeHelper.GetParent(start); parent != null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (FindList(parent) is { } list) return list;
        }
        return null;
    }

    private static ListBox? FindList(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ListBox list) return list;
            if (FindList(child) is { } found) return found;
        }
        return null;
    }
}
