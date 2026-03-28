using System.Runtime.InteropServices;

namespace MonitorTool.Services;

/// <summary>
/// Registers and manages a global hotkey (Ctrl+Shift+M) via Win32
/// RegisterHotKey / UnregisterHotKey.
///
/// Usage:
///   1. Call <see cref="Register"/> with the HWND of the window that will
///      receive WM_HOTKEY messages.
///   2. From the window's WndProc, call <see cref="ProcessMessage"/> for
///      every message; it fires <see cref="HotkeyPressed"/> when matched.
///   3. Dispose when the window is closed.
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // ── Win32 declarations ───────────────────────────────────────────────────
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── Constants ────────────────────────────────────────────────────────────
    private const int    HotkeyId   = 9000;
    private const uint   ModControl = 0x0002;
    private const uint   ModShift   = 0x0004;
    private const uint   VkM        = 0x4D;  // 'M'
    private const uint   WmHotkey   = 0x0312;

    // ── State ────────────────────────────────────────────────────────────────
    private IntPtr _hwnd;
    private bool   _registered;
    private bool   _disposed;

    // ── Public surface ───────────────────────────────────────────────────────

    /// <summary>Raised on the thread that called <see cref="ProcessMessage"/>
    /// (normally the UI thread) when Ctrl+Shift+M is pressed.</summary>
    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// Registers the Ctrl+Shift+M hotkey for the given window handle.
    /// Returns <c>true</c> on success.
    /// </summary>
    public bool Register(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _registered = RegisterHotKey(hwnd, HotkeyId, ModControl | ModShift, VkM);
        return _registered;
    }

    /// <summary>
    /// Call this from the window's WndProc for every message.
    /// Fires <see cref="HotkeyPressed"/> when Ctrl+Shift+M is detected.
    /// </summary>
    public void ProcessMessage(uint msg, IntPtr wParam)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_registered && _hwnd != IntPtr.Zero)
            UnregisterHotKey(_hwnd, HotkeyId);
    }
}
