using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NpcAppearanceManager.App.Views;

/// <summary>Moves an NPC list's scrollbar to an NPC, but only when the NPC isn't on screen already.</summary>
public static class ListScrolling
{
    /// <summary>
    /// Scrolls so <paramref name="item"/> is the first row, unless it's in view now (then the scrollbar stays). Done once
    /// the list has caught up with what just changed (a selection, a filter that now lets the NPC show).
    /// </summary>
    public static void ShowFirstIfHidden(ListBox list, object? item)
    {
        if (item == null) return;
        list.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (!list.IsLoaded || Scroller(list) is not { } viewer) return;
            var index = list.Items.IndexOf(item);
            if (index < 0) return;
            if (!NeedsScroll(index, viewer.VerticalOffset, viewer.ViewportHeight)) return;
            viewer.ScrollToVerticalOffset(index);
        });
    }

    /// <summary>The row at <paramref name="index"/> isn't among the rows on screen (the last one, cut off, doesn't count).</summary>
    public static bool NeedsScroll(int index, double offset, double viewport) => index < offset || index >= offset + viewport - 1;

    private static ScrollViewer? Scroller(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (Scroller(child) is { } found) return found;
        }
        return null;
    }
}
