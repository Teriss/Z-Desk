using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ZDesk.Controls;
using ZDesk.Models;
using ZDesk.Services;

namespace ZDesk.Windows;

public sealed class DesktopGroupWindow : Window
{
    private const double MinimumVisibleWidth = 80;
    private const double MinimumVisibleHeight = 36;
    private bool _temporaryTopmost;
    private bool _restoringDesktopLayer;
    private nint _desktopBoundary;
    private HwndSource? _source;
    private bool _edgeHidden;
    private bool _suppressPlacementCapture;
    private int _edgeAnimationVersion;
    private System.Drawing.Rectangle _dockWorkingAreaPixels;
    private System.Drawing.Rectangle _dockExpandedBoundsPixels;
    private Point? _dockExpandedPosition;

    public GroupContainer Group { get; }

    public event EventHandler? LayoutChanged;
    public event EventHandler? RemoveRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ExitRequested;
    public event EventHandler? InteractionRequested;
    // Kept as a compatibility event for integrations; layout selection no
    // longer needs to synchronize Explorer for QuickLook.
    public event Action<IReadOnlyList<string>>? ShellSelectionRequested
    {
        add { }
        remove { }
    }

    public bool IsEdgeHidden => _edgeHidden;
    public bool IsTemporaryTopmost => _temporaryTopmost;
    public bool IsPointWithinWindow(System.Drawing.Point point)
    {
        var handle = new WindowInteropHelper(this).Handle;
        return handle != nint.Zero && GetWindowRect(handle, out var bounds) &&
            point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
    }
    public DockEdge DockEdge => Group.Definition.DockEdge;
    public bool IsInteractionBusy => Group.IsInteractionBusy;

    public DesktopGroupWindow(
        GroupDefinition definition,
        bool animationsEnabled = true,
        double containerOpacity = 0.92,
        double cornerRadius = 11,
        double iconSize = 88,
        double animationSpeed = 1.0)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        MinWidth = 220;
        MinHeight = 36;
        Width = definition.Width;
        Height = definition.IsCollapsed ? (definition.HasMultipleTabs ? 60 : 36) : definition.Height;

        Group = new GroupContainer(
            definition,
            desktopHosted: true,
            animationsEnabled,
            containerOpacity,
            cornerRadius,
            iconSize,
            animationSpeed);
        Group.LayoutChanged += (_, _) => LayoutChanged?.Invoke(this, EventArgs.Empty);
        Group.RemoveRequested += (_, _) => RemoveRequested?.Invoke(this, EventArgs.Empty);
        Group.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        Group.ExitRequested += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        Content = Group;

        var placement = GetSafePlacement(definition);
        Left = placement.X;
        Top = placement.Y;

        LocationChanged += (_, _) => CaptureDesktopPlacement();
        PreviewMouseDown += (_, _) => InteractionRequested?.Invoke(this, EventArgs.Empty);
        Activated += (_, _) => Dispatcher.BeginInvoke(
            () => InteractionRequested?.Invoke(this, EventArgs.Empty),
            System.Windows.Threading.DispatcherPriority.Input);
        Closed += (_, _) =>
        {
            _source?.RemoveHook(WindowMessageHook);
            _source = null;
            Group.Dispose();
        };
    }

    public void ShowAnimated()
    {
        if (!IsVisible)
        {
            Show();
        }

        Group.AnimateIn();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_temporaryTopmost) RestoreDesktopLayer();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public void SetInteractionMode(LayoutInteractionMode mode)
    {
        Group.SetEdgeHideMode(mode == LayoutInteractionMode.EdgeHide);
        if (mode != LayoutInteractionMode.Standard) return;

        // Leaving edge-hide mode is a state reset, not just a reveal request. This
        // also recovers windows whose animation/state flag was interrupted while
        // they were almost completely outside the working area.
        if (Group.Definition.DockEdge != DockEdge.None || _edgeHidden)
        {
            var expanded = GetExpandedPosition();
            _dockExpandedPosition = expanded;
            Group.Definition.DesktopX = expanded.X;
            Group.Definition.DesktopY = expanded.Y;
            _edgeHidden = false;
            // Settings can be applied twice (Apply and then window close). A
            // synchronous restore ensures the second application cannot cancel
            // an in-flight animation and leave the layout outside the screen.
            MoveEdgePosition(expanded, animate: false);
            LogService.Info($"Edge dock reset to standard mode | group={Group.Definition.Id}");
        }
        else
        {
            CancelEdgeAnimation();
        }

        Group.Definition.DockEdge = DockEdge.None;
        ClearDockMetrics();
        if (_temporaryTopmost) SetTemporaryTopmost(false);
    }

    public void RevealFromEdge(bool animate)
    {
        if (!_edgeHidden && Group.Definition.DockEdge == DockEdge.None) return;
        var expanded = GetExpandedPosition();
        _dockExpandedPosition = expanded;
        Group.Definition.DesktopX = expanded.X;
        Group.Definition.DesktopY = expanded.Y;
        _edgeHidden = false;
        MoveEdgePosition(expanded, animate);
        LogService.Info($"Edge dock revealed | group={Group.Definition.Id} | edge={Group.Definition.DockEdge}");
    }

    public void HideToEdge(bool animate)
    {
        if (Group.Definition.DockEdge == DockEdge.None || _edgeHidden) return;
        RememberExpandedPosition();
        CaptureDockMetrics();
        var target = GetHiddenPosition(Group.Definition.DockEdge);
        _edgeHidden = true;
        MoveEdgePosition(target, animate);
        LogService.Info($"Edge dock hidden | group={Group.Definition.Id} | edge={Group.Definition.DockEdge}");
    }

    public void UpdateDockFromCurrentPosition(LayoutInteractionMode mode)
    {
        if (mode != LayoutInteractionMode.EdgeHide)
        {
            Group.Definition.DockEdge = DockEdge.None;
            _edgeHidden = false;
            ClearDockMetrics();
            return;
        }

        // Applying unrelated settings while a window is hidden must not run
        // edge detection against its intentionally off-screen coordinates.
        if (_edgeHidden && Group.Definition.DockEdge != DockEdge.None) return;

        var edge = DetectDockEdge();
        Group.Definition.DockEdge = edge;
        _edgeHidden = false;
        if (edge == DockEdge.None)
        {
            ClearDockMetrics();
            LogService.Info($"Edge dock cleared | group={Group.Definition.Id}");
            return;
        }

        RememberExpandedPosition();
        CaptureDockMetrics();
        LogService.Info($"Edge dock captured | group={Group.Definition.Id} | edge={edge} | expanded={_dockExpandedPosition}");
    }

    public bool IsCursorInRevealZone(System.Drawing.Point cursorPixels)
    {
        if (Group.Definition.DockEdge == DockEdge.None) return false;
        EnsureDockMetrics();
        return EdgeDockGeometry.IsCursorInRevealZone(
            Group.Definition.DockEdge,
            _dockWorkingAreaPixels,
            _dockExpandedBoundsPixels,
            cursorPixels);
    }

    public bool IsCursorInExpandedBounds(System.Drawing.Point cursorPixels)
    {
        EnsureDockMetrics();
        return _dockExpandedBoundsPixels.Contains(cursorPixels);
    }

    private Point GetExpandedPosition()
    {
        if (_dockExpandedPosition is { } expanded) return expanded;

        // Compatibility recovery for positions persisted by builds that wrote a
        // hidden, off-screen location back into DesktopX/DesktopY. A stored dock
        // edge is enough to reconstruct the visible coordinate on that edge.
        if (Group.Definition.DockEdge != DockEdge.None)
        {
            var area = GetWorkingAreaLogical();
            return Group.Definition.DockEdge switch
            {
                DockEdge.Left => new Point(area.Left, Group.Definition.DesktopY ?? Top),
                DockEdge.Right => new Point(area.Right - ActualWidth, Group.Definition.DesktopY ?? Top),
                DockEdge.Top => new Point(Group.Definition.DesktopX ?? Left, area.Top),
                _ => new Point(Group.Definition.DesktopX ?? Left, Group.Definition.DesktopY ?? Top)
            };
        }

        return new Point(Group.Definition.DesktopX ?? Left, Group.Definition.DesktopY ?? Top);
    }

    private void RememberExpandedPosition()
    {
        _dockExpandedPosition = new Point(Left, Top);
        Group.Definition.DesktopX = Left;
        Group.Definition.DesktopY = Top;
    }

    private Point GetHiddenPosition(DockEdge edge)
    {
        var area = GetWorkingAreaLogical();
        const double strip = 3;
        return edge switch
        {
            DockEdge.Left => new Point(area.Left - ActualWidth + strip, Top),
            DockEdge.Right => new Point(area.Right - strip, Top),
            DockEdge.Top => new Point(Left, area.Top - ActualHeight + strip),
            _ => new Point(Left, Top)
        };
    }

    private void MoveEdgePosition(Point target, bool animate)
    {
        var version = ++_edgeAnimationVersion;
        _suppressPlacementCapture = true;
        if (!animate || !Group.AnimationsEnabled)
        {
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = target.X;
            Top = target.Y;
            _suppressPlacementCapture = false;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(180);
        var left = new DoubleAnimation(target.X, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var top = new DoubleAnimation(target.Y, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        top.Completed += (_, _) =>
        {
            if (version != _edgeAnimationVersion) return;
            BeginAnimation(LeftProperty, null);
            BeginAnimation(TopProperty, null);
            Left = target.X;
            Top = target.Y;
            _suppressPlacementCapture = false;
        };
        BeginAnimation(LeftProperty, left, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(TopProperty, top, HandoffBehavior.SnapshotAndReplace);
    }

    private void CancelEdgeAnimation()
    {
        _edgeAnimationVersion++;
        _suppressPlacementCapture = true;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        _suppressPlacementCapture = false;
    }

    private void CaptureDockMetrics()
    {
        if (_edgeHidden) return;
        RememberExpandedPosition();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !GetWindowRect(handle, out var bounds)) return;

        var screen = System.Windows.Forms.Screen.FromHandle(handle);
        _dockWorkingAreaPixels = screen.WorkingArea;
        _dockExpandedBoundsPixels = System.Drawing.Rectangle.FromLTRB(
            bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        Group.Definition.DisplayDeviceName = screen.DeviceName;
    }

    private void EnsureDockMetrics()
    {
        if (!_dockWorkingAreaPixels.IsEmpty && !_dockExpandedBoundsPixels.IsEmpty) return;
        CaptureDockMetrics();
    }

    private void ClearDockMetrics()
    {
        _dockWorkingAreaPixels = System.Drawing.Rectangle.Empty;
        _dockExpandedBoundsPixels = System.Drawing.Rectangle.Empty;
        _dockExpandedPosition = null;
    }

    private DockEdge DetectDockEdge()
    {
        var area = GetWorkingAreaLogical();
        const double threshold = 18;
        if (Math.Abs(Left - area.Left) <= threshold) return DockEdge.Left;
        if (Math.Abs(Left + ActualWidth - area.Right) <= threshold) return DockEdge.Right;
        if (Math.Abs(Top - area.Top) <= threshold) return DockEdge.Top;
        return DockEdge.None;
    }

    private Rect GetWorkingAreaLogical()
    {
        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
            string.Equals(candidate.DeviceName, Group.Definition.DisplayDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transform.Transform(new Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero) return;
        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = (style | WsExToolWindow) & ~WsExAppWindow;
        SetWindowLongPtr(handle, GwlExStyle, (nint)style);
        SetWindowPos(handle, nint.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowMessageHook);
        RestoreDesktopLayer();
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmWindowPosChanging || _temporaryTopmost || _restoringDesktopLayer ||
            _desktopBoundary == nint.Zero || lParam == nint.Zero)
            return nint.Zero;

        var position = System.Runtime.InteropServices.Marshal.PtrToStructure<WindowPosition>(lParam);
        if ((position.Flags & SwpNoZOrder) != 0) return nint.Zero;
        var insertAfter = GetDesktopInsertAfter(hwnd);
        if (insertAfter == nint.Zero) return nint.Zero;
        position.InsertAfter = insertAfter;
        System.Runtime.InteropServices.Marshal.StructureToPtr(position, lParam, fDeleteOld: false);
        return nint.Zero;
    }

    public void HideAnimated()
    {
        if (!IsVisible)
        {
            return;
        }

        Group.AnimateOut(() =>
        {
            if (IsVisible)
            {
                Hide();
            }
        });
    }

    public void SetTemporaryTopmost(bool enabled)
    {
        _temporaryTopmost = enabled;
        Topmost = enabled;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero) return;

        if (enabled)
        {
            SetWindowPos(handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            return;
        }

        RestoreDesktopLayer();
    }

    public void RestoreDesktopLayer()
    {
        if (_temporaryTopmost) return;
        _restoringDesktopLayer = true;
        try
        {
            Topmost = false;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == nint.Zero) return;
            EnsureDesktopBoundary();
            SetWindowLongPtr(handle, GwlHwndParent, nint.Zero);
            SetWindowPos(handle, HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            if ((style & WsExTopmost) != 0)
            {
                SetWindowLongPtr(handle, GwlExStyle, (nint)(style & ~WsExTopmost));
                SetWindowPos(handle, nint.Zero, 0, 0, 0, 0,
                    SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoZOrder | SwpFrameChanged);
            }
            var insertAfter = GetDesktopInsertAfter(handle);
            if (insertAfter != nint.Zero)
                SetWindowPos(handle, insertAfter, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
            if ((style & WsExTopmost) != 0)
                SetWindowLongPtr(handle, GwlExStyle, (nint)(style & ~WsExTopmost));
        }
        finally
        {
            _restoringDesktopLayer = false;
        }
    }

    public void BringToFrontWithin(IReadOnlyCollection<DesktopGroupWindow> layoutWindows)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == nint.Zero || !IsVisible) return;

        if (_temporaryTopmost)
        {
            // A normal layout activation must not demote a QQ-mode or hotkey-
            // revealed window back into the desktop Z-order band.
            SetTemporaryTopmost(true);
            return;
        }

        var topLayoutHandle = layoutWindows
            .Where(window => window.IsVisible)
            .Select(window => new WindowInteropHelper(window).Handle)
            .Where(candidate => candidate != nint.Zero)
            .OrderBy(GetZOrderIndex)
            .FirstOrDefault();
        if (topLayoutHandle == nint.Zero || topLayoutHandle == handle) return;

        var insertAfter = GetWindow(topLayoutHandle, GwHwndPrev);
        _restoringDesktopLayer = true;
        try
        {
            SetWindowPos(handle, insertAfter, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate);
        }
        finally
        {
            _restoringDesktopLayer = false;
        }
    }

    private static int GetZOrderIndex(nint target)
    {
        var current = GetTopWindow(nint.Zero);
        for (var index = 0; index < 10000 && current != nint.Zero; index++)
        {
            if (current == target) return index;
            current = GetWindow(current, GwHwndNext);
        }
        return int.MaxValue;
    }

    private void EnsureDesktopBoundary()
    {
        if (_desktopBoundary != nint.Zero && IsWindow(_desktopBoundary)) return;
        _desktopBoundary = WorkerWHostService.FindHost();
    }

    private nint GetDesktopInsertAfter(nint handle)
    {
        EnsureDesktopBoundary();
        if (_desktopBoundary == nint.Zero) return nint.Zero;
        var insertAfter = GetWindow(_desktopBoundary, GwHwndPrev);
        if (insertAfter == handle) insertAfter = GetWindow(handle, GwHwndPrev);
        return insertAfter;
    }

    public void EnsureVisibleOnCurrentDisplays()
    {
        var placement = GetSafePlacement(Group.Definition);
        if (Math.Abs(Left - placement.X) > 0.5 || Math.Abs(Top - placement.Y) > 0.5)
        {
            Left = placement.X;
            Top = placement.Y;
        }

        var virtualWidth = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        Width = Math.Min(Math.Max(MinWidth, Width), Math.Max(MinWidth, virtualWidth));
        Height = Math.Min(Math.Max(MinHeight, Height), Math.Max(MinHeight, virtualHeight));
    }

    public void RefreshContents() => Group.RefreshItems();

    public void ApplyDesktopFileChanges(IReadOnlyList<DesktopFileChange> changes) =>
        Group.ApplyDesktopFileChanges(changes);

    private System.Windows.Point GetSafePlacement(GroupDefinition definition)
    {
        var x = definition.DesktopX ?? SystemParameters.WorkArea.Left + definition.X;
        var y = definition.DesktopY ?? SystemParameters.WorkArea.Top + definition.Y;

        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        x = Math.Clamp(x, virtualLeft - definition.Width + MinimumVisibleWidth, virtualRight - MinimumVisibleWidth);
        y = Math.Clamp(y, virtualTop, virtualBottom - MinimumVisibleHeight);
        return new System.Windows.Point(x, y);
    }

    private void CaptureDesktopPlacement()
    {
        if (_suppressPlacementCapture || (_edgeHidden && Group.Definition.DockEdge != DockEdge.None)) return;
        Group.Definition.DesktopX = Left;
        Group.Definition.DesktopY = Top;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != nint.Zero)
        {
            Group.Definition.DisplayDeviceName = System.Windows.Forms.Screen.FromHandle(handle).DeviceName;
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNoTopmost = new(-2);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpNoZOrder = 0x0004;
    private const int GwlExStyle = -20;
    private const int GwlHwndParent = -8;
    private const int WmWindowPosChanging = 0x0046;
    private const uint GwHwndPrev = 3;
    private const uint GwHwndNext = 2;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExAppWindow = 0x00040000L;
    private const long WsExTopmost = 0x00000008L;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public nint Window;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

}
