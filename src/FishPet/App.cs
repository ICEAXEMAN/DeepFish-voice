using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;

namespace FishPet;

// Starts her once per user: a second start (the MCP bridge starting her while she is already up) just ends.
//   --model <大肥鱼.model3.json>  the model somewhere else than model\
//   --test --pipe <name> --settings <file> --api <file>  out of sight, on a pipe, settings and HTTP port of its own (FishPet.Mcp --check)
internal static class Program
{
    // 她已经开着（多半是收着待命）：从管道递一句「fishpet/wake」让她出来。不走 MCP 握手，所以不算一个 Agent 连上（否则会触发「Agent 都走了就退场」）。
    private static void WakeRunning()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Tools.PipeName, PipeDirection.Out, PipeOptions.None);
            client.Connect(1500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            writer.WriteLine("{\"jsonrpc\":\"2.0\",\"method\":\"fishpet/wake\"}");
        }
        catch (Exception error) { Places.Log("叫醒已开着的她没成功：" + error.Message); }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        string? Option(string name) { int at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
        bool test = args.Contains("--test");
        bool manual = args.Contains("--manual"); // 桌面图标 / 启动桌宠.bat 双击：手动叫她出来（不管「启动时先收起」，她已经在收着就叫醒）
        if (Option("--settings") is { } file) Places.SettingsFile = file;
        if (Option("--api") is { } apiFile) Places.ApiFile = apiFile;
        using var single = new Mutex(true, "FishPet.Single." + Tools.PipeName, out bool first);
        if (!first && !test)
        {
            if (manual) WakeRunning();
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { Places.Log("出错了：" + e.Exception); e.Handled = true; };
        new PetWindow(Settings.Load(), Places.Model(Option("--model")), test, Option("--pipe"), manual).Show();
        return app.Run();
    }
}
