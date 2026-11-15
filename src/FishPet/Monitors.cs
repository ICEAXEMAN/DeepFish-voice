using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace FishPet;

// 多显示器（issue #3）：她在哪块屏上，就按哪块屏的大小和任务栏来摆。SystemParameters.WorkArea 只认主屏，副屏上用它，
// 气泡会被夹回主屏、KeepOnScreen 会把她拽回主屏、哄睡跑到主屏正中、副屏的任务栏也不裁。
// 坐标换算照这个窗口自己的 DPI（和拖动、视线那几处一样）：WPF 摆窗口时就是用这个系数把 Left、Top 换成像素的。
internal static class Monitors
{
    // 这个窗口现在的缩放系数（100% 是 1，150% 是 1.5）。
    internal static double Scale(Visual window) => VisualTreeHelper.GetDpi(window).DpiScaleX;

    // 这一点（DIP）所在的那块屏，不在任何屏上就取离它最近的：整块屏和去掉任务栏的可用区域，都是 DIP。
    // 系统接口失败时退回主屏（就是以前的算法）。
    internal static (Rect Screen, Rect Work) At(double x, double y, double scale)
    {
        if (scale <= 0) scale = 1;
        var point = new NativePoint { X = (int)Math.Round(x * scale), Y = (int)Math.Round(y * scale) };
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromPoint(point, DefaultToNearest), ref info))
            return (new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight), SystemParameters.WorkArea);
        return (ToDip(info.Monitor, scale), ToDip(info.Work, scale));
    }

    internal static Rect ToDip(NativeRect rect, double scale) =>
        new(rect.Left / scale, rect.Top / scale, (rect.Right - rect.Left) / scale, (rect.Bottom - rect.Top) / scale);

    private const uint DefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
