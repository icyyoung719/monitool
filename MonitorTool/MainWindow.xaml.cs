using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using MonitorTool.Models;
using MonitorTool.Services;
using Windows.Graphics;
using WinRT.Interop;

namespace MonitorTool;

/// <summary>
/// Compact always-on-top overlay window that displays real-time system metrics.
///
/// Features
/// ────────
/// • Fluent Design: Mica backdrop, rounded corners, system font, accent bars.
/// • Always-on-top via Win32 SetWindowPos + OverlappedPresenter.IsAlwaysOnTop.
/// • Draggable anywhere on its surface via WM_NCLBUTTONDOWN trick.
/// • Global hotkey Ctrl+Shift+M toggles visibility; implemented by subclassing
///   the window's WndProc so WM_HOTKEY messages are intercepted.
/// • Metrics polled every second on a thread-pool thread; UI updated on the
///   dispatcher thread to keep resource usage negligible.
/// </summary>
public sealed partial class MainWindow : Window
{
    // ── Win32 P/Invokes ──────────────────────────────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute,
        ref int pvAttribute, int cbAttribute);

    // ── Win32 constants ──────────────────────────────────────────────────────

    private static readonly IntPtr HwndTopmost    = new(-1);
    private const uint             SwpNoMove      = 0x0002;
    private const uint             SwpNoSize      = 0x0001;
    private const uint             SwpNoActivate  = 0x0010;
    private const int              GwlpWndProc    = -4;
    private const uint             WmNcLButtonDown = 0x00A1;
    private const int              HtCaption      = 2;
    private const uint             DwmwaWindowCornerPreference = 33;
    private const int              DwmwcpRound    = 2;   // large rounded corners

    // ── Fields ───────────────────────────────────────────────────────────────

    private IntPtr                _hwnd;
    private AppWindow             _appWindow = null!;
    private IntPtr                _oldWndProc;
    // Keep a hard reference to the delegate so the GC does not collect it while
    // the native window still has a pointer to it.
    private WndProcDelegate?      _newWndProcDelegate;

    private readonly SystemMetricsService  _metricsService;
    private readonly GlobalHotkeyService   _hotkeyService;
    private readonly DispatcherTimer       _timer;
    // Guard against overlapping async metric reads (in case WMI takes > 1 s).
    private bool _metricsRunning;

    // ── Constructor ──────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();

        _metricsService = new SystemMetricsService();
        _hotkeyService  = new GlobalHotkeyService();
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;

        _timer          = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick    += OnTimerTick;

        Closed += OnWindowClosed;

        ConfigureWindow();

        _timer.Start();
    }

    // ── Window configuration ─────────────────────────────────────────────────

    private void ConfigureWindow()
    {
        _hwnd      = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        // ── Mica backdrop (Windows 11 Fluent Design) ─────────────────────────
        if (MicaBackdrop.IsSupported())
        {
            SystemBackdrop = new MicaBackdrop();
        }
        else if (DesktopAcrylicBackdrop.IsSupported())
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        // ── Hide / collapse the system title bar so content fills the window ─
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            var tb = _appWindow.TitleBar;
            tb.ExtendsContentIntoTitleBar     = true;
            tb.PreferredHeightOption          = TitleBarHeightOption.Collapsed;
            tb.ButtonBackgroundColor          = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor  = Colors.Transparent;
            tb.ButtonHoverBackgroundColor     = Colors.Transparent;
            tb.ButtonPressedBackgroundColor   = Colors.Transparent;
            tb.ButtonForegroundColor          = Colors.Transparent;
            tb.ButtonInactiveForegroundColor  = Colors.Transparent;
        }

        // ── Presenter: no maximise / minimise, always-on-top ─────────────────
        var presenter = _appWindow.Presenter as OverlappedPresenter
                        ?? OverlappedPresenter.Create();
        presenter.IsMaximizable  = false;
        presenter.IsMinimizable  = false;
        presenter.IsResizable    = false;
        presenter.IsAlwaysOnTop  = true;
        _appWindow.SetPresenter(presenter);

        // ── Also enforce topmost at Win32 level (belt-and-braces) ─────────────
        SetWindowPos(_hwnd, HwndTopmost, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);

        // ── Windows 11 large rounded corners via DWM ──────────────────────────
        int roundPref = DwmwcpRound;
        DwmSetWindowAttribute(_hwnd, DwmwaWindowCornerPreference,
            ref roundPref, sizeof(int));

        // ── Compact initial size and position (top-right corner) ─────────────
        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest);
        int workWidth   = displayArea.WorkArea.Width;
        const int W = 280, H = 190;
        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            workWidth - W - 20, 20, W, H));

        // ── Subclass WndProc to receive WM_HOTKEY ─────────────────────────────
        _newWndProcDelegate = NewWndProc;
        _oldWndProc = SetWindowLongPtr(_hwnd, GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_newWndProcDelegate));

        // Register global hotkey Ctrl+Shift+M
        _hotkeyService.Register(_hwnd);
    }

    // ── WndProc subclass ─────────────────────────────────────────────────────

    private IntPtr NewWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        _hotkeyService.ProcessMessage(msg, wParam);
        return CallWindowProc(_oldWndProc, hWnd, msg, wParam, lParam);
    }

    // ── Hotkey handler ───────────────────────────────────────────────────────

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        if (_appWindow.IsVisible)
            _appWindow.Hide();
        else
            _appWindow.Show();
    }

    // ── Drag support ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sends WM_NCLBUTTONDOWN with HTCAPTION to the window so Windows handles
    /// the drag natively, giving smooth movement without any custom pointer tracking.
    /// Clicks on interactive controls (buttons etc.) are excluded.
    /// </summary>
    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // If the event originates from an interactive child control, let it
        // handle the press normally.
        if (e.OriginalSource is Button)
            return;

        // Release WinUI capture so the OS can take over the drag.
        RootGrid.ReleasePointerCapture(e.Pointer);

        SendMessage(_hwnd, WmNcLButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
    }

    // ── Metrics timer ────────────────────────────────────────────────────────

    private async void OnTimerTick(object? sender, object e)
    {
        if (_metricsRunning) return;
        _metricsRunning = true;
        try
        {
            var metrics = await _metricsService.GetMetricsAsync();
            UpdateUi(metrics);
        }
        catch { /* swallow transient WMI / counter errors */ }
        finally
        {
            _metricsRunning = false;
        }
    }

    private void UpdateUi(SystemMetrics m)
    {
        // CPU
        CpuBar.Value  = m.CpuUsage;
        CpuText.Text  = $"{m.CpuUsage,5:F1}%";

        // Memory – prefer "X.X/Y.YG" when total is known
        MemBar.Value  = m.MemoryUsage;
        MemText.Text  = m.MemoryTotalGb > 0
            ? $"{m.MemoryUsedGb:F1}G"
            : $"{m.MemoryUsage,5:F1}%";

        // GPU
        GpuBar.Value  = m.GpuUsage;
        GpuText.Text  = $"{m.GpuUsage,5:F1}%";

        // Temperature (0 = unavailable)
        if (m.Temperature > 0f)
        {
            TempBar.Value = Math.Min(m.Temperature, 100f);
            TempText.Text = $"{m.Temperature:F0}°C";
        }
        else
        {
            TempBar.Value = 0;
            TempText.Text = "N/A";
        }

        StatusText.Text = $"Updated {DateTime.Now:HH:mm:ss}";
    }

    // ── Cleanup ──────────────────────────────────────────────────────────────

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _timer.Stop();
        _hotkeyService.Dispose();
        _metricsService.Dispose();
    }
}
