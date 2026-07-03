using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using ActivityManagement.Core;
using Microsoft.Win32;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Velopack;
using Velopack.Sources;
using Windows.Graphics;
using WinRT.Interop;

namespace ActivityManagement.App;

internal static class WindowPlacement
{
    public static void ConfigureFlyout(Window window, int width, int height)
    {
        var appWindow = GetAppWindow(window);
        appWindow.IsShownInSwitchers = false;
        appWindow.Resize(new SizeInt32(width, height));
        NativeWindowStyles.HideFromTaskbar(window, noActivate: false);
    }

    public static void HideInsteadOfClose(Window window, Func<bool> shouldHide)
    {
        GetAppWindow(window).Closing += (_, args) =>
        {
            if (!shouldHide())
            {
                return;
            }

            args.Cancel = true;
            NativeWindowStyles.Hide(window);
        };
    }

    public static void MoveNearTray(Window window, int width, int height, int yOffset)
    {
        var workingArea = GetWorkArea();
        GetAppWindow(window).Move(new PointInt32(
            workingArea.Right - width - 8,
            workingArea.Bottom - height - yOffset));
    }

    public static void MoveAboveAnchor(Window window, RectInt32 anchor, int width, int height, int margin)
    {
        var workingArea = GetWorkArea();
        var x = anchor.X + (anchor.Width / 2) - (width / 2);
        var minX = workingArea.Left + 8;
        var maxX = workingArea.Right - width - 8;
        x = maxX < minX ? minX : Math.Clamp(x, minX, maxX);

        var y = anchor.Y - height - margin;
        if (y < workingArea.Top)
        {
            y = anchor.Y + anchor.Height + margin;
        }

        var minY = workingArea.Top + 8;
        var maxY = workingArea.Bottom - height - 8;
        y = maxY < minY ? minY : Math.Clamp(y, minY, maxY);
        GetAppWindow(window).Move(new PointInt32(x, y));
    }

    private static AppWindow GetAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(windowId);
    }

    private static Rect GetWorkArea()
    {
        if (!SystemParametersInfo(0x0030, 0, out var rect, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return rect;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out Rect pvParam, uint fWinIni);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

internal sealed class TaskbarWidgetHost : IDisposable
{
    // Native shell window classes we locate to reparent our widget into the taskbar band.
    private const string TaskbarClassName = "Shell_TrayWnd";
    private const string RebarClassName = "ReBarWindow32";
    private const string TrayNotifyClassName = "TrayNotifyWnd";
    // TaskbarQuota treats DynamicContent2 as part of the native taskbar lane, which keeps both widgets from
    // repeatedly pushing each other across the left side of the taskbar.
    private const string HostClassName = "DynamicContent2";
    private const int WidgetGap = 0;
    // WS_POPUP: a borderless top-level window, reparented into the taskbar so it draws in-band.
    private const uint WsPopup = 0x80000000;
    // WS_EX_LAYERED: lets the XAML content bridge composite with alpha over the taskbar.
    private const uint WsExLayered = 0x00080000;
    private readonly FrameworkElement _content;
    private readonly int _width;
    private readonly WidgetWindowProcedure _windowProcedure;
    private DesktopWindowXamlSource? _xamlSource;
    private IntPtr _taskbarHwnd;
    private IntPtr _trayHwnd;
    private IntPtr _rebarHwnd;
    private IntPtr _hwnd;
    private bool _classRegistered;
    private bool _attached;

    public TaskbarWidgetHost(FrameworkElement content, int width)
    {
        _content = content;
        _width = width;
        _windowProcedure = WndProc;
    }

    public string? Error { get; private set; }

    public RectInt32? ScreenBounds
    {
        get
        {
            if (_hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                return null;
            }

            return new RectInt32(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
    }

    public void Attach()
    {
        try
        {
            FindTaskbarWindows();

            if (_taskbarHwnd == IntPtr.Zero)
            {
                Error = "Windows taskbar window was not found.";
                return;
            }

            _hwnd = CreateHostWindow();
            _xamlSource = new DesktopWindowXamlSource();
            var windowId = Win32Interop.GetWindowIdFromWindow(_hwnd);
            AppWindow.GetFromWindowId(windowId).IsShownInSwitchers = false;
            _xamlSource.Initialize(windowId);
            _xamlSource.SiteBridge.ResizePolicy = ContentSizePolicy.ResizeContentToParentWindow;
            _xamlSource.Content = _content;
            SetParent(_hwnd, _taskbarHwnd);

            _attached = true;
            UpdatePosition();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    public void UpdatePosition()
    {
        if (!_attached || _hwnd == IntPtr.Zero)
        {
            return;
        }

        if (_taskbarHwnd == IntPtr.Zero || !IsWindow(_taskbarHwnd))
        {
            FindTaskbarWindows();
        }

        if (_taskbarHwnd == IntPtr.Zero || !GetWindowRect(_taskbarHwnd, out var taskbarRect))
        {
            return;
        }

        var bandRect = GetBandRect(taskbarRect);
        var taskbarWidth = taskbarRect.Right - taskbarRect.Left;
        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

        if (taskbarWidth >= taskbarHeight)
        {
            var height = Math.Max(32, bandRect.Bottom - bandRect.Top);
            var y = Math.Max(0, bandRect.Top - taskbarRect.Top);
            var x = GetLeftWidgetAnchor(taskbarRect, bandRect);
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, _width, height, SetWindowPosFlags.NoZOrder | SetWindowPosFlags.NoActivate | SetWindowPosFlags.ShowWindow);
            return;
        }

        var trayRect = GetNotificationAreaRect(taskbarRect);
        var verticalHeight = Math.Min(140, Math.Max(72, taskbarHeight / 4));
        var verticalY = Math.Max(0, trayRect.Top - taskbarRect.Top - verticalHeight - WidgetGap);
        SetWindowPos(_hwnd, IntPtr.Zero, 0, verticalY, taskbarWidth, verticalHeight, SetWindowPosFlags.NoZOrder | SetWindowPosFlags.NoActivate | SetWindowPosFlags.ShowWindow);
    }

    public void Dispose()
    {
        _attached = false;
        _xamlSource?.Dispose();
        _xamlSource = null;

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_classRegistered)
        {
            UnregisterClass(HostClassName, GetModuleHandle(null));
            _classRegistered = false;
        }
    }

    // Finds the right edge of the nearest sibling widget to the left of our slot, so we place ourselves
    // immediately to its right without overlapping it or native taskbar elements.
    private int GetLeftWidgetAnchor(Rect taskbarRect, Rect bandRect)
    {
        var maxRight = taskbarRect.Left;
        var gc = GCHandle.Alloc(new LeftWidgetScan(_hwnd, taskbarRect, bandRect.Left));
        try
        {
            EnumChildWindows(_taskbarHwnd, EnumLeftWidgets, GCHandle.ToIntPtr(gc));
            maxRight = ((LeftWidgetScan)gc.Target!).MaxRight;
        }
        finally
        {
            gc.Free();
        }

        maxRight = Math.Max(maxRight, GetRightEdgeOfKnownLeftWidget("TaskbarQuotaWidgetWinRT", taskbarRect, bandRect.Left));

        var x = maxRight <= taskbarRect.Left
            ? 0
            : maxRight - taskbarRect.Left + WidgetGap;

        var maxX = Math.Max(0, bandRect.Left - taskbarRect.Left - _width - WidgetGap);
        return Math.Clamp(x, 0, maxX);
    }

    private void FindTaskbarWindows()
    {
        _taskbarHwnd = FindWindow(TaskbarClassName, null);
        _trayHwnd = _taskbarHwnd == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(_taskbarHwnd, IntPtr.Zero, TrayNotifyClassName, null);
        _rebarHwnd = _taskbarHwnd == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(_taskbarHwnd, IntPtr.Zero, RebarClassName, null);
    }

    private Rect GetNotificationAreaRect(Rect taskbarRect)
    {
        var result = _trayHwnd != IntPtr.Zero && GetWindowRect(_trayHwnd, out var trayRect)
            ? trayRect
            : taskbarRect;

        IncludeChildBounds("ClockButton", taskbarRect, ref result);
        IncludeChildBounds("TrayClockWClass", taskbarRect, ref result);
        IncludeChildBounds("TrayShowDesktopButtonWClass", taskbarRect, ref result);
        return result;
    }

    private Rect GetBandRect(Rect taskbarRect)
    {
        return _rebarHwnd != IntPtr.Zero && GetWindowRect(_rebarHwnd, out var rebarRect)
            ? rebarRect
            : taskbarRect;
    }

    private void IncludeChildBounds(string className, Rect taskbarRect, ref Rect result)
    {
        for (var child = FindWindowEx(_taskbarHwnd, IntPtr.Zero, className, null);
             child != IntPtr.Zero;
             child = FindWindowEx(_taskbarHwnd, child, className, null))
        {
            if (!GetWindowRect(child, out var bounds)
                || bounds.Right <= bounds.Left
                || bounds.Bottom <= bounds.Top
                || !Intersects(bounds, taskbarRect))
            {
                continue;
            }

            result = Union(result, bounds);
        }
    }

    private int GetRightEdgeOfKnownLeftWidget(string className, Rect taskbarRect, int bandLeft)
    {
        var maxRight = taskbarRect.Left;
        for (var child = FindWindowEx(_taskbarHwnd, IntPtr.Zero, className, null);
             child != IntPtr.Zero;
             child = FindWindowEx(_taskbarHwnd, child, className, null))
        {
            if (child == _hwnd
                || !GetWindowRect(child, out var bounds)
                || bounds.Right <= bounds.Left
                || bounds.Right > bandLeft
                || !Intersects(bounds, taskbarRect))
            {
                continue;
            }

            maxRight = Math.Max(maxRight, bounds.Right);
        }

        return maxRight;
    }


    private static bool Intersects(Rect a, Rect b)
    {
        return a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;
    }

    private static Rect Union(Rect a, Rect b)
    {
        return new Rect
        {
            Left = Math.Min(a.Left, b.Left),
            Top = Math.Min(a.Top, b.Top),
            Right = Math.Max(a.Right, b.Right),
            Bottom = Math.Max(a.Bottom, b.Bottom)
        };
    }

    private static bool EnumLeftWidgets(IntPtr hwnd, IntPtr lParam)
    {
        var scan = (LeftWidgetScan)GCHandle.FromIntPtr(lParam).Target!;
        if (hwnd == scan.OwnWindow || !GetWindowRect(hwnd, out var bounds))
        {
            return true;
        }

        if (bounds.Right <= bounds.Left
            || bounds.Bottom <= bounds.Top
            || bounds.Left < scan.TaskbarRect.Left
            || bounds.Right > scan.BandLeft
            || !Intersects(bounds, scan.TaskbarRect))
        {
            return true;
        }

        var className = NativeWindowClassName(hwnd);
        if (IsSystemTaskbarClass(className))
        {
            return true;
        }

        scan.MaxRight = Math.Max(scan.MaxRight, bounds.Right);
        return true;
    }

    private static string NativeWindowClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        var length = GetClassName(hwnd, buffer, buffer.Capacity);
        return length <= 0 ? string.Empty : buffer.ToString();
    }

    // Shell-owned window classes, excluded when scanning for sibling widgets so we only account for
    // other injected/custom widgets (e.g. the quota widget) and not native taskbar parts.
    private static bool IsSystemTaskbarClass(string className)
    {
        return className is "Start"
            or "TrayDummySearchControl"
            or "ReBarWindow32"
            or "MSTaskSwWClass"
            or "MSTaskListWClass"
            or "TrayNotifyWnd"
            or "TrayButton"
            or "ClockButton"
            or "TrayClockWClass"
            or "TrayShowDesktopButtonWClass"
            or "Windows.UI.Core.CoreWindow"
            or "Windows.UI.Composition.DesktopWindowContentBridge"
            or "Microsoft.UI.Content.DesktopChildSiteBridge"
            or "InputNonClientPointerSource"
            or "InputSiteWindowClass"
            or "Windows.UI.Input.InputSite.WindowClass";
    }

    private IntPtr CreateHostWindow()
    {
        RegisterHostClass();
        var hwnd = CreateWindowEx(
            WsExLayered,
            HostClassName,
            "Activity",
            WsPopup,
            0,
            0,
            0,
            0,
            _taskbarHwnd,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return hwnd;
    }

    private void RegisterHostClass()
    {
        var windowClass = new WidgetWindowClass
        {
            Size = (uint)Marshal.SizeOf<WidgetWindowClass>(),
            Instance = GetModuleHandle(null),
            ClassName = HostClassName,
            WindowProcedure = _windowProcedure
        };

        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            var error = Marshal.GetLastWin32Error();
            const int classAlreadyExists = 1410;
            if (error != classAlreadyExists)
            {
                throw new Win32Exception(error);
            }
        }
        else
        {
            _classRegistered = true;
        }
    }

    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr childWindow, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, SetWindowPosFlags flags);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WidgetWindowClass windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentWindow,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr lParam);

    private delegate IntPtr WidgetWindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WidgetWindowClass
    {
        public uint Size;
        public uint Style;
        public WidgetWindowProcedure WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    private sealed class LeftWidgetScan
    {
        public LeftWidgetScan(IntPtr ownWindow, Rect taskbarRect, int bandLeft)
        {
            OwnWindow = ownWindow;
            TaskbarRect = taskbarRect;
            BandLeft = bandLeft;
            MaxRight = taskbarRect.Left;
        }

        public IntPtr OwnWindow { get; }

        public Rect TaskbarRect { get; }

        public int BandLeft { get; }

        public int MaxRight { get; set; }
    }
}

internal static class NativeWindowStyles
{
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const byte VkMenu = 0x12;
    private const uint KeyeventfKeyup = 0x0002;

    public static void HideFromTaskbar(Window window, bool noActivate)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var exStyle = GetWindowLong(hwnd, GwlExStyle);
        exStyle &= ~WsExAppWindow;
        exStyle |= WsExToolWindow;
        if (noActivate)
        {
            exStyle |= WsExNoActivate;
        }

        SetWindowLong(hwnd, GwlExStyle, exStyle);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SetWindowPosFlags.NoMove | SetWindowPosFlags.NoSize | SetWindowPosFlags.NoZOrder | SetWindowPosFlags.FrameChanged | SetWindowPosFlags.NoActivate);
    }

    public static void Hide(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (!ShowWindow(hwnd, ShowWindowCommand.Hide))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public static void BringToForeground(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        ShowWindow(hwnd, ShowWindowCommand.Restore);
        if (SetForegroundWindow(hwnd))
        {
            return;
        }

        // SetForegroundWindow is denied when the app was activated by a global hotkey rather than a click on
        // one of its own windows. A synthesized ALT keypress resets the foreground lock so the window can
        // take focus, which is the established workaround for hotkey-launched windows.
        keybd_event(VkMenu, 0, 0, IntPtr.Zero);
        keybd_event(VkMenu, 0, KeyeventfKeyup, IntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    private static long GetWindowLong(IntPtr hwnd, int index)
    {
        return IntPtr.Size == 8
            ? GetWindowLongPtr(hwnd, index).ToInt64()
            : GetWindowLong32(hwnd, index);
    }

    private static void SetWindowLong(IntPtr hwnd, int index, long value)
    {
        if (IntPtr.Size == 8)
        {
            SetWindowLongPtr(hwnd, index, new IntPtr(value));
        }
        else
        {
            SetWindowLong32(hwnd, index, unchecked((int)value));
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int newLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, SetWindowPosFlags flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hwnd, ShowWindowCommand command);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte hardwareScanCode, uint flags, IntPtr extraInfo);
}

internal enum ShowWindowCommand
{
    Hide = 0,
    Restore = 9
}

[Flags]
internal enum SetWindowPosFlags : uint
{
    NoSize = 0x0001,
    NoMove = 0x0002,
    NoZOrder = 0x0004,
    NoActivate = 0x0010,
    ShowWindow = 0x0040,
    FrameChanged = 0x0020
}

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ActivityManagement";

    public static void Enable()
    {
        var executablePath = GetStableExecutablePath();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        key?.SetValue(ValueName, $"\"{executablePath}\"", RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static string? GetStableExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(directory)
            && string.Equals(Path.GetFileName(directory), "current", StringComparison.OrdinalIgnoreCase)
            && Directory.GetParent(directory) is { } rootDirectory)
        {
            var stubPath = Path.Combine(rootDirectory.FullName, Path.GetFileName(executablePath));
            if (File.Exists(stubPath))
            {
                return stubPath;
            }
        }

        return executablePath;
    }
}

internal static class ExternalReferences
{
    public static string? GetOpenableUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri.ToString();
        }

        return null;
    }
}

internal sealed class ReleaseUpdater
{
    private const string RepositoryUrl = "https://github.com/schalk-conradie/activity-management";

    public string? Error { get; private set; }

    public async void CheckForUpdates()
    {
        try
        {
            var source = new GithubSource(RepositoryUrl, accessToken: null, prerelease: false);
            var manager = new UpdateManager(source);
            if (!manager.IsInstalled)
            {
                return;
            }

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return;
            }

            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }
}

internal sealed class ShellIntegrationHost : IDisposable
{
    private const int HotkeyId = 1;
    private const int WmHotkey = 0x0312;
    private const int WmNull = 0x0000;
    private const int WmTrayIcon = 0x8001;
    private const int WmCommand = 0x0111;
    private const int WmLeftButtonUp = 0x0202;
    private const int WmRightButtonUp = 0x0205;
    private const int NinBalloonUserClick = 0x0405;
    private const uint MfString = 0x0000;
    private const uint TpmRightButton = 0x0002;
    private const int MenuOpen = 1001;
    private const int MenuQuickAdd = 1002;
    private const int MenuExit = 1003;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint TrayIconId = 1;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 1;
    private const uint NifIcon = 2;
    private const uint NifTip = 4;
    private const uint NifInfo = 16;
    private const uint NiifInfo = 1;
    private const uint NotifyIconVersion4 = 4;
    private const uint ImageIcon = 1;
    private const uint LrDefaultSize = 0x00000040;
    private const int MaxBalloonTextLength = 255;
    private const int MaxBalloonTitleLength = 63;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly IntPtr IdiApplication = new(32512);

    private readonly Action _showFlyout;
    private readonly Action _quickCreate;
    private readonly Action _exitApplication;
    private readonly WindowProcedure _windowProcedure;
    private readonly string _className = $"ActivityManagementShellHost-{Guid.NewGuid():N}";
    private readonly IntPtr _hwnd;
    private IntPtr _trayIcon;
    private bool _hotkeyRegistered;
    private bool _trayRegistered;

    public ShellIntegrationHost(Action showFlyout, Action quickCreate, Action exitApplication)
    {
        _showFlyout = showFlyout;
        _quickCreate = quickCreate;
        _exitApplication = exitApplication;
        _windowProcedure = WndProc;
        _hwnd = CreateMessageWindow();

        RegisterTrayIcon();
        RegisterQuickCreateHotkey();
    }

    public string? TrayError { get; private set; }

    public string? HotkeyError { get; private set; }

    public string? ShowReminderBalloon(string title, string message)
    {
        if (!_trayRegistered)
        {
            return TrayError ?? "Tray icon is not registered.";
        }

        var data = CreateNotifyIconData();
        data.Flags = NifInfo;
        data.InfoTitle = title.Length > MaxBalloonTitleLength ? title[..MaxBalloonTitleLength] : title;
        data.Info = message.Length > MaxBalloonTextLength ? message[..MaxBalloonTextLength] : message;
        data.InfoFlags = NiifInfo;

        return Shell_NotifyIcon(NimModify, ref data)
            ? null
            : new Win32Exception(Marshal.GetLastWin32Error()).Message;
    }

    public void Dispose()
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _hotkeyRegistered = false;
        }

        if (_trayRegistered)
        {
            var data = CreateNotifyIconData();
            Shell_NotifyIcon(NimDelete, ref data);
            _trayRegistered = false;
        }

        if (_trayIcon != IntPtr.Zero)
        {
            DestroyIcon(_trayIcon);
            _trayIcon = IntPtr.Zero;
        }

        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
        }
    }

    private IntPtr CreateMessageWindow()
    {
        var instance = GetModuleHandle(null);
        var windowClass = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(),
            Instance = instance,
            ClassName = _className,
            WindowProcedure = _windowProcedure
        };

        var atom = RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var hwnd = CreateWindowEx(0, _className, _className, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return hwnd;
    }

    private void RegisterTrayIcon()
    {
        var data = CreateNotifyIconData();
        data.Flags = NifMessage | NifIcon | NifTip;
        data.CallbackMessage = WmTrayIcon;
        data.Icon = LoadTrayIcon();
        if (data.Icon == IntPtr.Zero)
        {
            return;
        }

        data.Tip = "Activity Management";

        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            TrayError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return;
        }

        _trayRegistered = true;
        data.Version = NotifyIconVersion4;
        Shell_NotifyIcon(NimSetVersion, ref data);
    }

    private IntPtr LoadTrayIcon()
    {
        var icon = LoadImage(GetModuleHandle(null), IdiApplication, ImageIcon, 0, 0, LrDefaultSize);
        if (icon != IntPtr.Zero)
        {
            _trayIcon = icon;
            return icon;
        }

        var appIconError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        icon = LoadIcon(IntPtr.Zero, IdiApplication);
        if (icon == IntPtr.Zero)
        {
            TrayError = $"App icon unavailable: {appIconError}; system icon unavailable: {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
            return IntPtr.Zero;
        }

        TrayError = $"App icon unavailable, using system icon: {appIconError}";
        return icon;
    }

    private void RegisterQuickCreateHotkey()
    {
        _hotkeyRegistered = RegisterHotKey(_hwnd, HotkeyId, ModControl | ModShift, 'K');
        if (!_hotkeyRegistered)
        {
            HotkeyError = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        }
    }

    private NotifyIconData CreateNotifyIconData()
    {
        return new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            WindowHandle = _hwnd,
            Id = TrayIconId
        };
    }

    private IntPtr WndProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam)
    {
        switch ((int)message)
        {
            case WmHotkey:
                _quickCreate();
                return IntPtr.Zero;
            case WmTrayIcon:
                HandleTrayNotification(lParam);
                return IntPtr.Zero;
            case WmCommand:
                HandleMenuCommand((int)wParam & 0xffff);
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, message, wParam, lParam);
        }
    }

    private void HandleTrayNotification(IntPtr lParam)
    {
        // Version 4 tray notifications pack the mouse message in the low word of lParam and the icon ID
        // in the high word. Masking the low word keeps this working for pre-v4 icons too.
        switch (lParam.ToInt32() & 0xffff)
        {
            case WmLeftButtonUp:
            case NinBalloonUserClick:
                _showFlyout();
                break;
            case WmRightButtonUp:
                ShowTrayMenu();
                break;
        }
    }

    private void ShowTrayMenu()
    {
        if (!GetCursorPos(out var point))
        {
            return;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, MenuOpen, "Open tasks");
            AppendMenu(menu, MfString, MenuQuickAdd, "Quick add");
            AppendMenu(menu, MfString, MenuExit, "Exit");
            SetForegroundWindow(_hwnd);
            TrackPopupMenu(menu, TpmRightButton, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleMenuCommand(int command)
    {
        switch (command)
        {
            case MenuOpen:
                _showFlyout();
                break;
            case MenuQuickAdd:
                _quickCreate();
                break;
            case MenuExit:
                _exitApplication();
                break;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int newItemId, string newItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WindowClass windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentWindow,
        IntPtr menu,
        IntPtr instance,
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int width, int height, uint load);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    private delegate IntPtr WindowProcedure(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr Icon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint Version;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid GuidItem;
        public IntPtr BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}

internal sealed class ReminderNotifications : IDisposable
{
    public const string ArgumentAction = "action";
    public const string ArgumentTaskId = "taskId";
    public const string ActionOpen = "open";
    public const string ActionMarkDone = "mark_done";
    public const string ActionSnooze = "snooze_15";
    public const string ActionDismiss = "dismiss";
    public const string ActionOpenSource = "open_source";

    private const int RegdbClassNotRegistered = unchecked((int)0x80040154);
    private static readonly TimeSpan ReminderThrottle = TimeSpan.FromMinutes(30);
    private readonly TaskStore _store;
    private readonly DispatcherQueueTimer _timer;
    private readonly Action<string, long> _handleAction;
    private readonly Action _tasksChanged;
    private readonly Func<string, string, string?> _showFallbackNotification;
    private bool _registered;

    public ReminderNotifications(
        TaskStore store,
        DispatcherQueue dispatcherQueue,
        Action<string, long> handleAction,
        Action tasksChanged,
        Func<string, string, string?> showFallbackNotification)
    {
        _store = store;
        _handleAction = handleAction;
        _tasksChanged = tasksChanged;
        _showFallbackNotification = showFallbackNotification;
        _timer = dispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        _timer.Tick += (_, _) => SendDueNotifications();

        var canUseAppNotifications = TryRegisterAppNotifications();
        if (!canUseAppNotifications && string.IsNullOrWhiteSpace(Error))
        {
            Error = "Notification actions unavailable: Windows app notifications are not available for this installation; using tray reminders.";
        }

        _timer.Start();
        SendDueNotifications();
    }

    public string? Error { get; private set; }

    public void Dispose()
    {
        _timer.Stop();
        if (_registered)
        {
            AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }

    private bool TryRegisterAppNotifications()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                return false;
            }

            AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
            return true;
        }
        catch (COMException ex) when (ex.HResult == RegdbClassNotRegistered)
        {
            return false;
        }
        catch (Exception ex)
        {
            Error = $"Notification actions unavailable: Windows app notifications could not be registered: {ex.Message}";
            return false;
        }
    }

    private void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var values = args.Arguments;
        if (!values.ContainsKey(ArgumentTaskId) || !long.TryParse(values[ArgumentTaskId], out var taskId))
        {
            return;
        }

        var action = values.ContainsKey(ArgumentAction) ? values[ArgumentAction] : ActionOpen;
        _handleAction(action, taskId);
    }

    private void SendDueNotifications()
    {
        try
        {
            foreach (var task in _store.ListReminderCandidates(DateTimeOffset.UtcNow, ReminderThrottle))
            {
                try
                {
                    var fallbackError = ShowReminder(task);
                    if (fallbackError is not null)
                    {
                        Error = $"Reminder notification for task {task.Id} failed: {fallbackError}";
                        continue;
                    }

                    _store.RecordNotified(task.Id);
                }
                catch (Exception ex)
                {
                    Error = $"Reminder notification for task {task.Id} failed: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            Error = $"Reminder candidates could not be loaded: {ex.Message}";
        }

        _tasksChanged();
    }

    private string? ShowReminder(ActivityTask task)
    {
        if (_registered)
        {
            try
            {
                AppNotificationManager.Default.Show(BuildNotification(task));
                return null;
            }
            catch (COMException ex) when (ex.HResult == RegdbClassNotRegistered)
            {
                _registered = false;
                Error = "Notification actions unavailable: Windows app notifications are no longer available; using tray reminders.";
            }
        }

        return _showFallbackNotification(task.Title, BuildNotificationDetail(task));
    }

    private static AppNotification BuildNotification(ActivityTask task)
    {
        var taskId = task.Id.ToString(CultureInfo.InvariantCulture);
        var builder = new AppNotificationBuilder()
            .AddArgument(ArgumentAction, ActionOpen)
            .AddArgument(ArgumentTaskId, taskId)
            .AddText(task.Title)
            .AddText(BuildNotificationDetail(task))
            .AddButton(new AppNotificationButton("Done")
                .AddArgument(ArgumentAction, ActionMarkDone)
                .AddArgument(ArgumentTaskId, taskId))
            .AddButton(new AppNotificationButton("Snooze 15m")
                .AddArgument(ArgumentAction, ActionSnooze)
                .AddArgument(ArgumentTaskId, taskId))
            .AddButton(new AppNotificationButton("Dismiss")
                .AddArgument(ArgumentAction, ActionDismiss)
                .AddArgument(ArgumentTaskId, taskId));

        if (!string.IsNullOrWhiteSpace(task.ExternalReference))
        {
            builder.AddButton(new AppNotificationButton("Open source")
                .AddArgument(ArgumentAction, ActionOpenSource)
                .AddArgument(ArgumentTaskId, taskId));
        }

        return builder.BuildNotification();
    }

    private static string BuildNotificationDetail(ActivityTask task)
    {
        var parts = new List<string>();
        if (task.DueAt is not null)
        {
            parts.Add(task.DueAt.Value.ToLocalTime() < DateTimeOffset.Now
                ? $"Overdue {task.DueAt.Value.ToLocalTime():g}"
                : $"Due {task.DueAt.Value.ToLocalTime():g}");
        }

        parts.Add($"Priority: {task.Priority}");
        return string.Join(" - ", parts);
    }
}
