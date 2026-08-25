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
/// Somewhere a dragged row can be dropped.
///
/// The view model decides what a drop means, because what is on screen is a view of something in
/// the save and the two have to change together. For a flat list that is a reorder; for a tree it
/// is one thing moving inside another.
/// </summary>
public interface IReorderable
{
    /// <summary>The row dragged, and the row it was dropped on.</summary>
    void MoveOnto(object moved, object target);
}

/// <summary>
/// Dragging a row onto another one.
///
/// Rows are found by a flag on the template rather than by asking the list which item is where, so
/// a tree works the same as a flat list: a row nested four levels down inside four ItemsControls is
/// still just the nearest element marked as a row. Nothing here touches the collection. It reports
/// the two rows and lets the view model decide.
///
/// Dragging is never the only way to move something. Every list this is attached to also carries
/// buttons, because a drag cannot be done from the keyboard and a row that can only be moved with a
/// mouse is a row some people cannot move.
/// </summary>
public static class ListDragDropBehavior
{
    /// <summary>Where a drop is reported. Bind this on the control holding the rows.</summary>
    public static readonly DependencyProperty MoveProperty = DependencyProperty.RegisterAttached(
        "Move",
        typeof(IReorderable),
        typeof(ListDragDropBehavior),
        new PropertyMetadata(null, OnMoveChanged));

    /// <summary>
    /// Set on the root element of a row's template. That element is what a press, a drag and a drop
    /// all resolve to, and its DataContext is the row.
    /// </summary>
    public static readonly DependencyProperty IsRowProperty = DependencyProperty.RegisterAttached(
        "IsRow",
        typeof(bool),
        typeof(ListDragDropBehavior),
        new PropertyMetadata(false));

    /// <summary>True on the row the pointer is over during a drag, so the template can show it.</summary>
    private static readonly DependencyPropertyKey IsDropTargetKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsDropTarget",
        typeof(bool),
        typeof(ListDragDropBehavior),
        new PropertyMetadata(false));

    public static readonly DependencyProperty IsDropTargetProperty = IsDropTargetKey.DependencyProperty;

    /// <summary>How far the pointer moves before a press counts as a drag rather than a click.</summary>
    private static readonly double Threshold = SystemParameters.MinimumHorizontalDragDistance;

    private const string Format = "RainWorldSaveManager.Row";

    private static Point _pressedAt;
    private static object? _pressedRow;
    private static DependencyObject? _highlighted;

    public static void SetMove(DependencyObject element, IReorderable? value)
        => element.SetValue(MoveProperty, value);

    public static IReorderable? GetMove(DependencyObject element)
        => (IReorderable?)element.GetValue(MoveProperty);

    public static void SetIsRow(DependencyObject element, bool value)
        => element.SetValue(IsRowProperty, value);

    public static bool GetIsRow(DependencyObject element)
        => (bool)element.GetValue(IsRowProperty);

    public static bool GetIsDropTarget(DependencyObject element)
        => (bool)element.GetValue(IsDropTargetProperty);

    private static void OnMoveChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not UIElement host)
        {
            return;
        }

        host.PreviewMouseLeftButtonDown -= OnPressed;
        host.PreviewMouseMove -= OnMoved;
        host.Drop -= OnDropped;
        host.DragOver -= OnDragOver;
        host.DragLeave -= OnDragLeave;

        if (args.NewValue is null)
        {
            host.AllowDrop = false;
            return;
        }

        host.AllowDrop = true;
        host.PreviewMouseLeftButtonDown += OnPressed;
        host.PreviewMouseMove += OnMoved;
        host.Drop += OnDropped;
        host.DragOver += OnDragOver;
        host.DragLeave += OnDragLeave;
    }

    private static void OnPressed(object sender, MouseButtonEventArgs args)
    {
        _pressedAt = args.GetPosition(null);
        _pressedRow = RowUnder(args.OriginalSource as DependencyObject);
    }

    private static void OnMoved(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || _pressedRow is null)
        {
            return;
        }

        Point now = args.GetPosition(null);

        if (Math.Abs(now.X - _pressedAt.X) < Threshold && Math.Abs(now.Y - _pressedAt.Y) < Threshold)
        {
            return;
        }

        object row = _pressedRow;
        _pressedRow = null;

        // A drag that starts inside a text box or a slider belongs to that control, not to the row.
        if (args.OriginalSource is DependencyObject source && WantsTheMouse(source))
        {
            return;
        }

        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(Format, row), DragDropEffects.Move);
        }
        finally
        {
            ClearHighlight();
        }
    }

    private static void OnDragOver(object sender, DragEventArgs args)
    {
        args.Handled = true;

        if (!args.Data.GetDataPresent(Format))
        {
            args.Effects = DragDropEffects.None;
            return;
        }

        DependencyObject? over = RowElementUnder(args.OriginalSource as DependencyObject);
        object dragged = args.Data.GetData(Format)!;

        ClearHighlight();

        // Dropping something on itself is not a move, so it does not light up as one.
        if (over is null || ReferenceEquals(DataOf(over), dragged))
        {
            args.Effects = DragDropEffects.None;
            return;
        }

        over.SetValue(IsDropTargetKey, true);
        _highlighted = over;
        args.Effects = DragDropEffects.Move;
    }

    private static void OnDragLeave(object sender, DragEventArgs args) => ClearHighlight();

    private static void OnDropped(object sender, DragEventArgs args)
    {
        ClearHighlight();
        args.Handled = true;

        if (GetMove((DependencyObject)sender) is not { } target || !args.Data.GetDataPresent(Format))
        {
            return;
        }

        object dragged = args.Data.GetData(Format)!;
        object? onto = RowUnder(args.OriginalSource as DependencyObject);

        if (onto is not null && !ReferenceEquals(onto, dragged))
        {
            target.MoveOnto(dragged, onto);
        }
    }

    private static void ClearHighlight()
    {
        _highlighted?.SetValue(IsDropTargetKey, false);
        _highlighted = null;
    }

    private static object? RowUnder(DependencyObject? source) => DataOf(RowElementUnder(source));

    private static object? DataOf(DependencyObject? element)
        => element is FrameworkElement { DataContext: { } data } ? data : null;

    /// <summary>
    /// The nearest element above this one that a template marked as a row. Null for a press on
    /// something that is not inside a row at all.
    /// </summary>
    private static DependencyObject? RowElementUnder(DependencyObject? source)
    {
        while (source is not null)
        {
            if (GetIsRow(source))
            {
                return source;
            }

            source = Parent(source);
        }

        return null;
    }

    /// <summary>
    /// Whether the thing under the pointer handles dragging itself. Dragging inside a text box
    /// selects text and dragging a slider moves it, and taking that away to move a row would break
    /// the controls the row is made of.
    /// </summary>
    private static bool WantsTheMouse(DependencyObject source)
    {
        DependencyObject? walk = source;

        while (walk is not null)
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
