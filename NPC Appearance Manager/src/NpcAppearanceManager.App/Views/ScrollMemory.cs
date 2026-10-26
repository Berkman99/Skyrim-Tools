using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NpcAppearanceManager.App.Views;

/// <summary>
/// Keeps a list's scroll position in a bound property: put back when the list is shown again (a page is made anew
/// each time you go to it), and written whenever you scroll.
/// </summary>
public static class ScrollMemory
{
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(ScrollMemory),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnOffsetChanged));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(ScrollMemory));

    public static double GetOffset(DependencyObject d) => (double)d.GetValue(OffsetProperty);
    public static void SetOffset(DependencyObject d, double value) => d.SetValue(OffsetProperty, value);

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || (bool)element.GetValue(HookedProperty)) return;
        element.SetValue(HookedProperty, true);
        element.Loaded += (_, _) =>
        {
            if (FindScrollViewer(element) is not { } viewer) return;
            bool restoring = true;
            viewer.ScrollChanged += (_, args) =>
            {
                if (!restoring && args.VerticalChange != 0) element.SetCurrentValue(OffsetProperty, args.VerticalOffset);
            };
            // After the list has its items laid out; scrolling before that is lost.
            element.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
            {
                if (GetOffset(element) is var offset && !double.IsNaN(offset)) viewer.ScrollToVerticalOffset(offset);
                restoring = false;
            });
        };
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }
}
