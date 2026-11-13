using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FishPet;

// 第一次真正出场时她自己说的欢迎（本机加，2026-11-13，照公版 welcome 的做法接上了录音）：
// 不经过模型、不花 token，每个用户第一次看到的都一样。台词有两份，按语音包选：
//   青春、活力（second）→ welcome.txt，对用户叫「ご主人様」；
//   冷静、沉稳（calm）  → welcome_calm.txt，叫「ボス」。
// 格式：「# 欢迎」下面写一条，「／」隔开的是一个个气泡，〔〕写动作，// 开头是注释。
// 只说一次：说完在 settings.json 里记 Welcomed；已经有 settings.json 的老用户不补说（见 Settings.Load）。
internal static class Welcome
{
    internal const string Name = "欢迎";

    internal static string FileFor(string pack) => pack == "calm" ? "welcome_calm.txt" : "welcome.txt";

    // 台词文件丢了、或者里面没写，也有话说（跟 welcome.txt 的第一版一样）
    internal const string Fallback =
        "〔看过来 眯眼笑〕嗨，老大！我是大肥鱼，以后住在你桌面上啦／" +
        "〔歪一下头〕我会一直待在最上面，挡住窗口就直接用鼠标拖我走；想让我先躲起来，说一声“收起来”／" +
        "〔竖手指〕任务栏右下角有只虎鲸，找不到就在“^”里，拖出来点开能调我／" +
        "〔挑一下眉〕右键按住我的头再慢慢移，是摸头，试试／" +
        "〔抚胸 微点头〕我记的事都只存你电脑里；你的 AI 一关，我几秒后自己退场，不是坏了";

    // 取文件里「# 欢迎」下面的第一条；读不出来就用内置的
    internal static string Text(string root, string pack)
    {
        try
        {
            var file = Path.Combine(root, FileFor(pack));
            if (File.Exists(file) && Alarms.Parse(File.ReadAllLines(file)).TryGetValue(Name, out var lines))
                foreach (var line in lines)
                    if (Bubbles(line).Length > 0) return line;
        }
        catch (Exception error) { Places.Log("欢迎台词读不出来：" + error.Message); }
        return Fallback;
    }

    internal static string[] Bubbles(string text) =>
        text.Split('／', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // 新装才欢迎：启动时没有 settings.json 就是新装；有（哪怕里面没有 Welcomed 这一项）就是老用户，不补说。
    internal static bool Pending(bool settingsFileExisted) => !settingsFileExisted;
}
