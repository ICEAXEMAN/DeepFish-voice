using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FishTray;

// 和桌宠说话：走她自带的本机接口（api.json 里的地址和口令），不经过 Claude，不花 token。
internal static class Pet
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    internal static string Root { get; private set; } = "";
    internal static string Exe => Path.Combine(Root, "bin", "FishPet.exe");
    internal static bool Running => Process.GetProcessesByName("FishPet").Length > 0;

    // 插件目录：先看控制台文件夹里的 config.json，不行就从正在跑的桌宠推出来。
    internal static void Init()
    {
        try
        {
            var config = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "config.json"));
            if (File.Exists(config))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(config, Encoding.UTF8));
                if (doc.RootElement.TryGetProperty("PluginRoot", out var root) && root.GetString() is { Length: > 0 } path && Directory.Exists(path)) { Root = path; return; }
            }
        }
        catch { }
        // 编在插件里的（插件\tray\app\FishTray.exe）：往上两层就是插件
        var plugin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        if (File.Exists(Path.Combine(plugin, "setup.ps1")) && Directory.Exists(Path.Combine(plugin, "src", "FishPet"))) { Root = plugin; return; }
        try
        {
            foreach (var process in Process.GetProcessesByName("FishPet"))
                if (process.MainModule?.FileName is { } file && Path.GetDirectoryName(Path.GetDirectoryName(file)) is { } dir) { Root = dir; return; }
        }
        catch { }
    }

    private static (string Url, string Token)? Api()
    {
        try
        {
            var file = Path.Combine(Root, "api.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file, Encoding.UTF8));
            return (doc.RootElement.GetProperty("url").GetString()!, doc.RootElement.GetProperty("token").GetString()!);
        }
        catch { return null; }
    }

    // 调一个工具。返回里的 JSON；连不上或工具报错返回 null。
    internal static async Task<JsonElement?> Call(string tool, object? args = null)
    {
        if (Api() is not { } api) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, api.Url + "/call/" + tool)
            { Content = new StringContent(JsonSerializer.Serialize(args ?? new { }), Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
            request.Headers.Add("X-FishTray", "1");
            using var response = await Http.SendAsync(request);
            using var outer = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (outer.RootElement.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True) return null;
            var text = outer.RootElement.GetProperty("content")[0].GetProperty("text").GetString();
            using var inner = JsonDocument.Parse(text ?? "{}");
            return inner.RootElement.Clone();
        }
        catch { return null; }
    }

    // 控制台专用的互动接口（桌宠里的隐藏工具 panel）：做表情、做动作、恢复平常。
    internal static Task<JsonElement?> Panel(object args) => Call("panel", args);

    internal static async Task Say(string text)
    {
        if (Api() is not { } api) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, api.Url + "/say") { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Token);
            request.Headers.Add("X-FishTray", "1");
            using var response = await Http.SendAsync(request);
        }
        catch { }
    }

    internal static void StartPet()
    {
        try
        {
            if (Running || !File.Exists(Exe)) return;
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Exe)! })?.Dispose();
        }
        catch { }
    }
}
