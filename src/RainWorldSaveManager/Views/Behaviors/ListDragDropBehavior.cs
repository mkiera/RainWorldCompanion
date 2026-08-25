// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists in the referenced assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace RainWorldSaveManager.Views.Behaviors;

/// <summary>
/// A list whose order can be changed by dragging a row onto another one.
///
/// The view model decides what a move means, because the order on screen is a view of an order in
/// the save and the two have to be changed together.
/// </summary>
public interface IReorderable
{
    /// <summary>Moves the row at one position to another. Out of range does nothing.</summary>
    void MoveTo(int from, int to);
}

/// <summary>
/// Reordering a list by dragging a row onto another one.
///
/// Attached to the ItemsControl rather than written into the rows, so the rows stay a template over
/// a view model with no drag code in them. The behaviour reports the move as two positions and
/// leaves the list itself alone: what a drop means is the view model's to decide, and doing it here
/// as well would mean the list on screen and the list in the save could disagree.
///
/// Dragging is not the only way to move a row. Every list this is attached to also carries buttons,
/// because a drag cannot be done from the keyboard and a row that can only be moved with a mouse is
/// a row some people cannot move.
/// </summary>
public static class ListDragDropBehavior
{
    /// <summary>
    /// Where a drop is reported. Bind this to the view model behind the list and the ItemsControl
    /// becomes reorderable.
    /// </summary>
    public static readonly DependencyProperty MoveProperty = DependencyProperty.RegisterAttached(
        "Move",
        typeof(IReorderable),
        typeof(ListDragDropBehavior),
        new PropertyMetadata(null, OnMoveChanged));

    /// <summary>The row the pointer is over during a drag, so the template can show where it lands.</summary>
    private static readonly DependencyPropertyKey IsDropTargetKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsDropTarget",
        typeof(bool),
        typeof(ListDragDropBehavior),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsDropTargetProperty = IsDropTargetKey.DependencyProperty;

    /// <summary>How far the pointer moves before a press counts as a drag rather than a click.</summary>
    private static readonly double Threshold = SystemParameters.MinimumHorizontalDragDistance;

    private const string Format = "RainWorldSaveManager.ListRow";

    private static Point _pressedAt;
    private static object? _pressedItem;

    public static void SetMove(DependencyObject element, IReorderable? value)
        => element.SetValue(MoveProperty, value);

    public static IReorderable? GetMove(DependencyObject element)
        => (IReorderable?)element.GetValue(MoveProperty);

    public static bool GetIsDropTarget(DependencyObject element)
        => (bool)element.GetValue(IsDropTargetProperty);

    private static void OnMoveChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ItemsControl list)
        {
            return;
        }

        list.PreviewMouseLeftButtonDown -= OnPressed;
        list.PreviewMouseMove -= OnMoved;
        list.Drop -= OnDropped;
        list.DragOver -= OnDragOver;
        list.DragLeave -= OnDragLeave;

        if (args.NewValue is null)
        {
            list.AllowDrop = false;
            return;
        }

        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += OnPressed;
        list.PreviewMouseMove += OnMoved;
        list.Drop += OnDropped;
        list.DragOver += OnDragOver;
        list.DragLeave += OnDragLeave;
    }

    private static void OnPressed(object sender, MouseButtonEventArgs args)
    {
        _pressedAt = args.GetPosition(null);
        _pressedItem = ItemUnder(args.OriginalSource as DependencyObject, (ItemsControl)sender);
    }

    private static void OnMoved(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || _pressedItem is null)
        {
            return;
        }

        Point now = args.GetPosition(null);

        if (Math.Abs(now.X - _pressedAt.X) < Threshold && Math.Abs(now.Y - _pressedAt.Y) < Threshold)
        {
            return;
        }

        var list = (ItemsControl)sender;
        object item = _pressedItem;
        _pressedItem = null;

        // A drag that starts inside a text box or a slider is that control's, not the list's.
        if (args.OriginalSource is DependencyObject source && WantsTheMouse(source, list))
        {
            return;
        }

        try
        {
            DragDrop.DoDragDrop(list, new DataObject(Format, item), DragDropEffects.Move);
        }
        finally
        {
            ClearDropTarget(list);
        }
    }

    private static void OnDragOver(object sender, DragEventArgs args)
    {
        var list = (ItemsControl)sender;

        if (!args.Data.GetDataPresent(Format))
        {
            args.Effects = DragDropEffects.None;
            args.Handled = true;
            return;
        }

        object? over = ItemUnder(args.OriginalSource as DependencyObject, list);

        ClearDropTarget(list);

        if (over is not null && list.ItemContainerGenerator.ContainerFromItem(over) is DependencyObject container)
        {
            container.SetValue(IsDropTargetKey, true);
        }

        args.Effects = DragDropEffects.Move;
        args.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs args) => ClearDropTarget((ItemsControl)sender);

    private static void OnDropped(object sender, DragEventArgs args)
    {
        var list = (ItemsControl)sender;
        ClearDropTarget(list);

        if (GetMove(list) is not { } target || !args.Data.GetDataPresent(Format))
        {
            return;
        }

        object dragged = args.Data.GetData(Format)!;
        object? onto = ItemUnder(args.OriginalSource as DependencyObject, list);

        int from = list.Items.IndexOf(dragged);

        // Dropping past the last row means the end of the list, which is what the pointer being
        // below every row looks like.
        int to = onto is null ? list.Items.Count - 1 : list.Items.IndexOf(onto);

        if (from >= 0 && to >= 0 && from != to)
        {
            target.MoveTo(from, to);
        }

        args.Handled = true;
    }

    private static void ClearDropTarget(ItemsControl list)
    {
        foreach (object item in list.Items)
        {
            if (list.ItemContainerGenerator.ContainerFromItem(item) is { } container)
            {
                container.SetValue(IsDropTargetKey, false);
            }
        }
    }

    /// <summary>
    /// Walks up from whatever was clicked to the item of the list it sits in. Returns null for a
    /// click on the list's own background rather than on a row.
    /// </summary>
    private static object? ItemUnder(DependencyObject? source, ItemsControl list)
    {
        while (source is not null && source != list)
        {
            object item = list.ItemContainerGenerator.ItemFromContainer(source);

            if (item != DependencyProperty.UnsetValue)
            {
                return item;
            }

            source = Parent(source);
        }

        return null;
    }

    /// <summary>
    /// Whether the thing under the pointer handles dragging itself. Dragging inside a text box
    /// selects text and dragging a slider moves it, and taking that away to reorder a row would
    /// break the controls the row is made of.
    /// </summary>
    private static bool WantsTheMouse(DependencyObject source, ItemsControl list)
    {
        DependencyObject? walk = source;

        while (walk is not null && walk != list)
        {
            if (walk is TextBoxBase or Slider or ComboBox or ToggleButton or ButtonBase)
            {
                return true;
            }

            walk = Parent(walk);
        }

        return false;
    }

    /// <summary>
    /// The next thing up. The visual tree first, then the logical one, because a combo box's list
    /// sits in a popup that hangs off the logical tree rather than the visual one.
    /// </summary>
    private static DependencyObject? Parent(DependencyObject source)
    {
        if (source is Visual or System.Windows.Media.Media3D.Visual3D
            && VisualTreeHelper.GetParent(source) is { } visual)
        {
            return visual;
        }

        return source switch
        {
            FrameworkElement element => element.Parent,
            FrameworkContentElement content => content.Parent,
            _ => null,
        };
    }
}
