using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FlowBoard.Domain;

namespace FlowBoard.Views.DragDrop;

/// <summary>
/// The XAML surface of the drag system. Templates declare intent —
/// <c>dd:Drag.CardSource="True"</c> — and never touch the controller.
///
/// Every hookup here also unhooks on Unloaded. Card containers are virtualized and
/// recycled: without the teardown, the controller would accumulate registrations pointing
/// at containers that have long since been reused for a different card, and drops would
/// start landing in the wrong lane after enough scrolling.
/// </summary>
public static class Drag
{
    // ---------------------------------------------------------------- controller

    public static readonly DependencyProperty ControllerProperty =
        DependencyProperty.RegisterAttached("Controller", typeof(DragController), typeof(Drag));

    public static DragController? GetController(DependencyObject d)
    {
        var window = d as Window ?? Window.GetWindow(d);
        if (window is null) return null;

        if (window.GetValue(ControllerProperty) is DragController existing) return existing;

        var created = new DragController(window);
        window.SetValue(ControllerProperty, created);
        return created;
    }

    // ---------------------------------------------------------------- card source

    public static readonly DependencyProperty CardSourceProperty =
        DependencyProperty.RegisterAttached("CardSource", typeof(bool), typeof(Drag),
            new PropertyMetadata(false, OnCardSourceChanged));

    public static bool GetCardSource(DependencyObject d) => (bool)d.GetValue(CardSourceProperty);
    public static void SetCardSource(DependencyObject d, bool v) => d.SetValue(CardSourceProperty, v);

    private static void OnCardSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => HookSource(d, e, DragKind.Card);

    // ---------------------------------------------------------------- lane source

    public static readonly DependencyProperty LaneSourceProperty =
        DependencyProperty.RegisterAttached("LaneSource", typeof(bool), typeof(Drag),
            new PropertyMetadata(false, OnLaneSourceChanged));

    public static bool GetLaneSource(DependencyObject d) => (bool)d.GetValue(LaneSourceProperty);
    public static void SetLaneSource(DependencyObject d, bool v) => d.SetValue(LaneSourceProperty, v);

    private static void OnLaneSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => HookSource(d, e, DragKind.Lane);

    private static void HookSource(DependencyObject d, DependencyPropertyChangedEventArgs e, DragKind kind)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;

        fe.PreviewMouseLeftButtonDown += (_, args) =>
        {
            // The drag source is the container, but the press may land on a button inside
            // it. Let the inner control have the click.
            if (args.OriginalSource is DependencyObject o && IsInteractive(o)) return;

            // A lane drags by its header; a card drags by its whole face. The grabbed
            // element and the element that *moves* are not the same thing for lanes.
            var mover = kind == DragKind.Lane ? FindLaneRoot(fe) ?? fe : fe;
            GetController(fe)?.NotePress(mover, kind, args);
        };

        fe.MouseMove += (_, args) => GetController(fe)?.NoteMove(args);
        fe.PreviewMouseLeftButtonUp += (_, args) => GetController(fe)?.NoteRelease();
    }

    /// <summary>Did the press land on something that wants the click itself?
    /// Walks via TreeWalk, because OriginalSource is a Run whenever the press lands on
    /// text, and VisualTreeHelper.GetParent throws on ContentElements rather than
    /// returning null.</summary>
    private static bool IsInteractive(DependencyObject o)
    {
        for (var node = o; node is not null; node = TreeWalk.ParentOf(node))
        {
            if (node is ButtonBase or TextBoxBase or Thumb) return true;
            if (node is ItemsControl) return false;
        }
        return false;
    }

    /// <summary>Walk up from the header to the element marked Drag.LaneRoot. A lane is
    /// grabbed by its header but the whole lane is what lifts.</summary>
    private static FrameworkElement? FindLaneRoot(DependencyObject from)
    {
        for (var node = from; node is not null; node = TreeWalk.ParentOf(node))
            if (node is FrameworkElement fe && GetLaneRoot(fe))
                return fe;
        return null;
    }

    // ---------------------------------------------------------------- lane root marker

    public static readonly DependencyProperty LaneRootProperty =
        DependencyProperty.RegisterAttached("LaneRoot", typeof(bool), typeof(Drag),
            new PropertyMetadata(false));

    public static bool GetLaneRoot(DependencyObject d) => (bool)d.GetValue(LaneRootProperty);
    public static void SetLaneRoot(DependencyObject d, bool v) => d.SetValue(LaneRootProperty, v);

    // ---------------------------------------------------------------- card drop zone

    public static readonly DependencyProperty CardZoneProperty =
        DependencyProperty.RegisterAttached("CardZone", typeof(bool), typeof(Drag),
            new PropertyMetadata(false, OnCardZoneChanged));

    public static bool GetCardZone(DependencyObject d) => (bool)d.GetValue(CardZoneProperty);
    public static void SetCardZone(DependencyObject d, bool v) => d.SetValue(CardZoneProperty, v);

    private static void OnCardZoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items || !(bool)e.NewValue) return;

        void Register(object? _, EventArgs __)
        {
            if (items.DataContext is not Board board) return;
            var panel = GetItemsPanel(items);
            if (panel is null) return;
            GetController(items)?.RegisterCardZone(items, board, panel);
        }

        items.Loaded += Register;
        items.DataContextChanged += (_, _) => Register(null, EventArgs.Empty);
        items.Unloaded += (_, _) => GetController(items)?.Unregister(items);
    }

    // ---------------------------------------------------------------- lane drop zone

    public static readonly DependencyProperty LaneZoneProperty =
        DependencyProperty.RegisterAttached("LaneZone", typeof(bool), typeof(Drag),
            new PropertyMetadata(false, OnLaneZoneChanged));

    public static bool GetLaneZone(DependencyObject d) => (bool)d.GetValue(LaneZoneProperty);
    public static void SetLaneZone(DependencyObject d, bool v) => d.SetValue(LaneZoneProperty, v);

    private static void OnLaneZoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items || !(bool)e.NewValue) return;

        items.Loaded += (_, _) =>
        {
            var panel = GetItemsPanel(items);
            if (panel is not null) GetController(items)?.RegisterLaneZone(items, panel);
        };
        items.Unloaded += (_, _) => GetController(items)?.Unregister(items);
    }

    // ---------------------------------------------------------------- workspace target

    public static readonly DependencyProperty WorkspaceZoneProperty =
        DependencyProperty.RegisterAttached("WorkspaceZone", typeof(bool), typeof(Drag),
            new PropertyMetadata(false, OnWorkspaceZoneChanged));

    public static bool GetWorkspaceZone(DependencyObject d) => (bool)d.GetValue(WorkspaceZoneProperty);
    public static void SetWorkspaceZone(DependencyObject d, bool v) => d.SetValue(WorkspaceZoneProperty, v);

    private static void OnWorkspaceZoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;

        void Register(object? _, EventArgs __)
        {
            if (fe.DataContext is Workspace ws) GetController(fe)?.RegisterWorkspaceZone(fe, ws);
        }

        fe.Loaded += Register;
        fe.DataContextChanged += (_, _) => Register(null, EventArgs.Empty);
        fe.Unloaded += (_, _) => GetController(fe)?.Unregister(fe);
    }

    // ---------------------------------------------------------------- archive target

    public static readonly DependencyProperty ArchiveZoneProperty =
        DependencyProperty.RegisterAttached("ArchiveZone", typeof(bool), typeof(Drag),
            new PropertyMetadata(false, OnArchiveZoneChanged));

    public static bool GetArchiveZone(DependencyObject d) => (bool)d.GetValue(ArchiveZoneProperty);
    public static void SetArchiveZone(DependencyObject d, bool v) => d.SetValue(ArchiveZoneProperty, v);

    private static void OnArchiveZoneChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || !(bool)e.NewValue) return;

        fe.Loaded += (_, _) => GetController(fe)?.RegisterArchiveZone(fe);
        fe.Unloaded += (_, _) => GetController(fe)?.Unregister(fe);
    }

    // ---------------------------------------------------------------- scrollers

    public static readonly DependencyProperty AutoScrollProperty =
        DependencyProperty.RegisterAttached("AutoScroll", typeof(Orientation?), typeof(Drag),
            new PropertyMetadata(null, OnAutoScrollChanged));

    public static Orientation? GetAutoScroll(DependencyObject d) => (Orientation?)d.GetValue(AutoScrollProperty);
    public static void SetAutoScroll(DependencyObject d, Orientation? v) => d.SetValue(AutoScrollProperty, v);

    private static void OnAutoScrollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer view || e.NewValue is not Orientation axis) return;

        view.Loaded += (_, _) => GetController(view)?.RegisterScroller(view, axis);
        view.Unloaded += (_, _) => GetController(view)?.UnregisterScroller(view);
    }

    // ---------------------------------------------------------------- window plumbing

    /// <summary>Set on the window. The controller captures the mouse at window level, so
    /// move and release must be observed there — the pointer spends most of a drag
    /// nowhere near the element it started on.</summary>
    public static readonly DependencyProperty HostProperty =
        DependencyProperty.RegisterAttached("Host", typeof(bool), typeof(Drag),
            new PropertyMetadata(false, OnHostChanged));

    public static bool GetHost(DependencyObject d) => (bool)d.GetValue(HostProperty);
    public static void SetHost(DependencyObject d, bool v) => d.SetValue(HostProperty, v);

    private static void OnHostChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || !(bool)e.NewValue) return;

        window.PreviewMouseMove += (_, args) => GetController(window)?.NoteMove(args);
        window.PreviewMouseLeftButtonUp += (_, args) => GetController(window)?.NoteDrop(args);

        // Losing capture means something stole the pointer — an alt-tab, a UAC prompt, a
        // crashing shell extension. Treat it exactly like ESC: abandon quietly rather than
        // leaving a ghost stuck to the adorner layer forever.
        window.LostMouseCapture += (_, args) =>
        {
            if (GetController(window) is { IsDragging: true }) args.Handled = false;
        };
    }

    private static Panel? GetItemsPanel(ItemsControl items)
    {
        if (items.ItemsPanel is null) return null;

        var presenter = FindChild<ItemsPresenter>(items);
        if (presenter is null) return null;

        return VisualTreeHelper.GetChildrenCount(presenter) > 0
            ? VisualTreeHelper.GetChild(presenter, 0) as Panel
            : null;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
