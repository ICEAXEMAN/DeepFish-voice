using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace FishTray;

// 让每个窗口在任务栏上各占一个按钮。
// 同一个程序的几个窗口，Windows 默认会叠成一个按钮；给每个窗口各设一个“应用 ID”，它们就分开了。
// 光设 ID 的话任务栏会用 exe 名字（FishTray）当标题、图标也不一定对，所以启动时再在 HKCU 里给这两个 ID
// 登记一个名字和图标（图标直接指 FishTray.exe 自己，exe 的图标就是那张贴纸）。
internal static class Taskbar
{
    internal const string PanelId = "DaFeiYu.Panel";
    internal const string ContextId = "DaFeiYu.ContextChat";

    // 控制面板和「识别当前窗口并闲聊」各自的名字（任务栏按钮和 Alt+Tab 上都显示这个）
    internal static void Register()
    {
        Name(PanelId, "大肥鱼 · 控制台");
        Name(ContextId, "大肥鱼 · 闲聊");
    }

    private static void Name(string id, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + id);
            if (key is null) return;
            key.SetValue("DisplayName", name, RegistryValueKind.String);
            key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "FishTray.exe"), RegistryValueKind.String);
        }
        catch (Exception error) { App.Log("任务栏名字没设上：" + error.Message); }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey { public Guid Fmtid; public uint Pid; }

    // PROPVARIANT：64 位下是 24 字节；只用到“字符串”这一种
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant { public ushort Vt, R1, R2, R3; public IntPtr P1, P2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private static readonly Guid AppUserModel = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const ushort VtLpwstr = 31;

    // 窗口句柄一建好就调用（SourceInitialized 里）。失败了也不要紧：只是几个窗口会叠在同一个任务栏按钮里。
    internal static void SetId(Window window, string id)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return;
            try
            {
                var key = new PropertyKey { Fmtid = AppUserModel, Pid = 5 }; // System.AppUserModel.ID
                // 本机修（2026-11-13）：propvarutil.h 的 InitPropVariantFromString 在 propsys.dll 里没有导出
                // （跑起来报 “Unable to find an entry point”），几个窗口就永远分不成两个按钮。
                // 自己组一个 VT_LPWSTR 的 PROPVARIANT，字符串用 CoTaskMem 分配，交给 PropVariantClear 释放。
                var value = new PropVariant { Vt = VtLpwstr, P1 = Marshal.StringToCoTaskMemUni(id) };
                try
                {
                    var hr = store.SetValue(ref key, ref value);
                    if (hr != 0) { App.Log("任务栏分组设置失败：SetValue 0x" + hr.ToString("X8")); return; }
                    store.Commit();
                }
                finally { PropVariantClear(ref value); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch (Exception error) { App.Log("任务栏分组设置失败：" + error.Message); }
    }
}
