using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

namespace FishPet;

// Whether the window in front fills its whole screen (a game, a video, a slide show): not one of her own, and
// not the desktop. The same test as the BigBlueFish pet's, without Windows Forms.
internal static class Fullscreen
{
    internal static bool InFront()
    {
        var window = GetForegroundWindow();
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out var id);
        if (id == Environment.ProcessId || !GetWindowRect(window, out var rect)) return false;
        try { if (Process.GetProcessById((int)id).ProcessName == "explorer") return false; } catch { }
        return FillsMonitor(window, rect);
    }

    // 截图、录屏这类“盖满整屏的临时浮层”在最前（本机加，2026-11-13）：她不该盖在它上面，不管“全屏时藏起来”开没开。
    // 认法：最前的窗口铺满整个屏幕，并且是工具窗口（不进任务栏的浮层，截图框、录制条基本都是）或进程名是常见的截图/录屏软件。
    // 游戏、视频这类正经全屏程序不算，那个照旧由“全屏时藏起来”那个开关管。
    internal static bool OverlayInFront(out string who)
    {
        who = "";
        var window = GetForegroundWindow();
        if (window == 0) return false;
        GetWindowThreadProcessId(window, out var id);
        if (id == Environment.ProcessId || !GetWindowRect(window, out var rect) || !FillsMonitor(window, rect)) return false;
        string name;
        try { name = Process.GetProcessById((int)id).ProcessName; } catch { return false; }
        if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IsToolWindow(window) && !IsCaptureTool(name)) return false;
        who = name;
        return true;
    }

    internal static bool IsCaptureTool(string name) =>
        CaptureTools.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] CaptureTools = { "screenshot", "screenclip", "snip", "screensketch", "gamebar", "recorder", "capture" };

    // 不进任务栏的浮层窗口（WS_EX_TOOLWINDOW，没带 WS_EX_APPWINDOW）。
    internal static bool IsToolWindow(nint window)
    {
        long style = (long)GetWindowLongPtr(window, -20);
        return (style & 0x80) != 0 && (style & 0x40000) == 0;
    }

    // 这个窗口是不是铺满了它所在的整个屏幕。
    internal static bool FillsMonitor(nint window)
        => GetWindowRect(window, out var rect) && FillsMonitor(window, rect);

    private static bool FillsMonitor(nint window, NativeRect rect)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return false;
        var screen = info.Monitor;
        return rect.Left <= screen.Left && rect.Top <= screen.Top && rect.Right >= screen.Right && rect.Bottom >= screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
