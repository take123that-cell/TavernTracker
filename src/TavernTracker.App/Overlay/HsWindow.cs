using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TavernTracker.App.Overlay;

/// <summary>Finds Hearthstone's window and where its game area is on screen.</summary>
public static class HsWindow
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    private static volatile IntPtr _handle;
    private static volatile uint _pid;
    private static DateTime _lookedUp = DateTime.MinValue;
    private static int _looking;

    /// <summary>
    /// Hearthstone's main window, or IntPtr.Zero if it isn't running. The process list is scanned on a
    /// background thread every few seconds (it's slow), so this never stalls the overlay.
    /// </summary>
    public static IntPtr Handle
    {
        get
        {
            if (DateTime.UtcNow - _lookedUp > TimeSpan.FromSeconds(3) && Interlocked.Exchange(ref _looking, 1) == 0)
            {
                _lookedUp = DateTime.UtcNow;
                Task.Run(() =>
                {
                    try { LookUp(); }
                    finally { Interlocked.Exchange(ref _looking, 0); }
                });
            }
            return _handle;
        }
    }

    private static void LookUp()
    {
        IntPtr found = IntPtr.Zero;
        uint pid = 0;
        try
        {
            foreach (var p in Process.GetProcessesByName("Hearthstone"))
            {
                using (p)
                {
                    if (p.MainWindowHandle != IntPtr.Zero)
                    {
                        found = p.MainWindowHandle;
                        pid = (uint)p.Id;
                    }
                }
            }
        }
        catch { /* not running */ }
        _handle = found;
        _pid = pid;
    }

    /// <summary>The game area in screen pixels, or null when Hearthstone isn't visible.</summary>
    public static (int X, int Y, int Width, int Height)? ClientArea()
    {
        var h = Handle;
        if (h == IntPtr.Zero || IsIconic(h)) return null;
        if (!GetClientRect(h, out var r)) return null;
        var origin = new POINT();
        if (!ClientToScreen(h, ref origin)) return null;
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w < 200 || ht < 200) return null;
        return (origin.X, origin.Y, w, ht);
    }

    /// <summary>True when Hearthstone (or one of our own windows) is the active window.</summary>
    public static bool IsActive(IntPtr ownWindow)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (fg == ownWindow || fg == Handle) return true;
        GetWindowThreadProcessId(fg, out var pid);
        return pid == (uint)Environment.ProcessId || (_pid != 0 && pid == _pid);
    }
}
