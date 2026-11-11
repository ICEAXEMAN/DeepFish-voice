using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FishPet;

// 别的程序的置顶窗口（或被点过的任务栏以外的东西）跑到她上面时，她就被盖住了：WPF 的 Topmost 只在设置那一刻生效。
// 这里每隔一会儿看一眼：有没有别的进程的、可见的、会挡住她的置顶窗口排在她前面，有就把她（和气泡）重新放回最上面。
// 不抢焦点（SWP_NOACTIVATE）；穿透的覆盖层（NVIDIA、手写画布这类）、任务栏、0 大小的窗口不算。
internal static class ZOrder
{
    private const int GwHwndPrev = 3, ExTopmost = 0x8, ExTransparent = 0x20;
    private const uint NoSize = 1, NoMove = 2, NoActivate = 0x10;

    internal static bool CoveredByOthers(nint me)
    {
        if (me == 0 || !GetWindowRect(me, out var mine)) return false;
        for (var above = GetWindow(me, GwHwndPrev); above != 0; above = GetWindow(above, GwHwndPrev))
        {
            if (!IsWindowVisible(above)) continue;
            GetWindowThreadProcessId(above, out var id);
            if (id == Environment.ProcessId) continue;
            if (((long)GetWindowLongPtr(above, -20) & ExTransparent) != 0) continue;
            if (!GetWindowRect(above, out var r) || r.Right - r.Left < 8 || r.Bottom - r.Top < 8) continue;
            if (r.Right <= mine.Left || r.Left >= mine.Right || r.Bottom <= mine.Top || r.Top >= mine.Bottom) continue;
            var name = new StringBuilder(64);
            GetClassName(above, name, 64);
            var cls = name.ToString();
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") continue;
            return true;
        }
        return false;
    }

    internal static void Raise(nint window)
    {
        if (window != 0) SetWindowPos(window, -1, 0, 0, 0, 0, NoMove | NoSize | NoActivate);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out RawRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int cx, int cy, uint flags);
}
