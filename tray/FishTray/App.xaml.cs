using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Threading;

namespace FishTray;

// 任务栏右下角的大肥鱼图标：左键开调控面板，右键菜单（出场/收起、打开面板、退出控制台）。
// 跟着桌宠走：桌宠一起来它就在，桌宠退场（Claude 断开约 15 秒后）它也退。
public partial class App : System.Windows.Application
{
    private const string SingleName = "FishTray.Single.7c1f";
    private const string ShowSignalName = "FishTray.ShowPanel.7c1f";

    private Mutex? single;
    private EventWaitHandle? showSignal;
    private NotifyIcon? tray;
    private MainWindow? panel;
    private ToolStripMenuItem? toggleItem;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime startedAt = DateTime.UtcNow;
    private DateTime? petGoneSince;
    private bool petSeen;

    internal static void Log(string text)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "..", "tray.log"), DateTime.Now.ToString("HH:mm:ss") + " " + text + "\n"); } catch { }
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);
        Pet.Init();

        // 画图标预览：FishTray.exe --preview 目标.png
        var args = e.Args;
        int preview = Array.IndexOf(args, "--preview");
        if (preview >= 0 && preview + 1 < args.Length)
        {
            using (var big = Whale.Draw(256)) big.Save(args[preview + 1]);
            using (var small = Whale.Draw(24)) small.Save(Path.ChangeExtension(args[preview + 1], ".24.png"));
            Shutdown();
            return;
        }

        // 截图预览：FishTray.exe --shot 目标.png [--more]（不进托盘、不占“只留一个”，渲染面板成图后退出，给开发自检用）
        int shot = Array.IndexOf(args, "--shot");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            var target = args[shot + 1];
            bool more = Array.IndexOf(args, "--more") >= 0, open = Array.IndexOf(args, "--open") >= 0, scroll = Array.IndexOf(args, "--scroll") >= 0;
            int find = Array.IndexOf(args, "--search");
            string search = find >= 0 && find + 1 < args.Length ? args[find + 1] : "";
            panel = new MainWindow();
            panel.Open();
            var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            wait.Tick += (_, _) =>
            {
                wait.Stop();
                try { panel.Snapshot(target, more, open, scroll, search); } catch (Exception error) { File.WriteAllText(target + ".err.txt", error.ToString()); }
                Shutdown();
            };
            wait.Start();
            return;
        }

        bool wantPanel = Array.IndexOf(args, "--show") >= 0;

        // 自检：FishTray.exe --selftest 日志.txt（点各个控件，核对桌宠状态，写日志后退出）
        int self = Array.IndexOf(args, "--selftest");
        if (self >= 0 && self + 1 < args.Length)
        {
            var logPath = args[self + 1];
            panel = new MainWindow();
            panel.Open();
            _ = Dispatcher.InvokeAsync(async () => { await System.Threading.Tasks.Task.Delay(1500); await panel.SelfTest(logPath); Shutdown(); });
            return;
        }
        single = new Mutex(true, SingleName, out bool first);
        if (!first)
        {
            // 已经有一个在跑：要开面板就通知它，然后自己退出
            if (wantPanel) { try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { } }
            Shutdown();
            return;
        }
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        var waiter = new Thread(() =>
        {
            while (showSignal.WaitOne()) Dispatcher.BeginInvoke(new Action(OpenPanel));
        }) { IsBackground = true };
        waiter.Start();

        panel = new MainWindow();
        BuildTray();
        poll.Tick += async (_, _) => { try { await Tick(); } catch (Exception error) { Log("ERR " + error); } };
        poll.Start();

        // 自己打开（start.bat 之类）而桌宠没开：把桌宠也带起来（她默认收着）
        if (wantPanel) { Pet.StartPet(); OpenPanel(); }
    }

    private void BuildTray()
    {
        toggleItem = new ToolStripMenuItem("出场");
        toggleItem.Click += async (_, _) =>
        {
            bool? hidden = panel?.Hidden;
            if (hidden is null) return;
            await Pet.Call(hidden == true ? "show" : "hide");
            await Tick();
        };
        var open = new ToolStripMenuItem("打开面板");
        open.Click += (_, _) => OpenPanel();
        var exit = new ToolStripMenuItem("退出控制台");
        exit.Click += (_, _) => Quit();

        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = new DarkRenderer(), Font = new Font("Microsoft YaHei UI", 9.5f), Padding = new Padding(4) };
        menu.Items.AddRange([toggleItem, open, new ToolStripSeparator(), exit]);
        menu.Opening += (_, _) =>
        {
            bool? hidden = panel?.Hidden;
            toggleItem.Enabled = hidden is not null;
            toggleItem.Text = hidden == false ? "收起" : "出场";
        };

        tray = new NotifyIcon { Icon = Whale.Icon(32), Text = "大肥鱼", Visible = false, ContextMenuStrip = menu };
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenPanel(); };
    }

    private void OpenPanel()
    {
        if (panel is null) return;
        if (panel.IsVisible) { panel.Activate(); return; }
        panel.Open();
    }

    private async System.Threading.Tasks.Task Tick()
    {
        if (panel is null) return;
        bool running = Pet.Running;
        if (running) { petSeen = true; petGoneSince = null; }
        else
        {
            petGoneSince ??= DateTime.UtcNow;
            // 桌宠退场了（或者一直没起来）：控制台也退，不留个空图标
            if ((petSeen && DateTime.UtcNow - petGoneSince > TimeSpan.FromSeconds(2)) || (!petSeen && DateTime.UtcNow - startedAt > TimeSpan.FromSeconds(40)))
            { Quit(); return; }
        }
        await panel.Refresh();
        if (tray is not null)
        {
            bool? hidden = panel.Hidden;
            tray.Text = hidden is null ? "大肥鱼（连接中）" : hidden == true ? "大肥鱼 · 收起着" : "大肥鱼 · 在桌面上";
            // 她收着的时候托盘图标也不露面；她出场（或面板还开着）才出现，默认落在“显示隐藏的图标”里
            tray.Visible = hidden == false || panel.IsVisible;
        }
    }

    private void Quit()
    {
        poll.Stop();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); tray = null; }
        panel?.Close();
        Shutdown();
    }

    // 暗色右键菜单
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Color.FromArgb(232, 240, 255) : Color.FromArgb(110, 125, 165);
            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
    {
        private static readonly Color Back = Color.FromArgb(10, 20, 56), Hover = Color.FromArgb(38, 102, 182), Line = Color.FromArgb(42, 63, 122);
        public override Color ToolStripDropDownBackground => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
    }
}
