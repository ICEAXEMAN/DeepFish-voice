using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FishPet;

// Her own body language on the Live2D display: the words she puts in 〔〕 in what she says, each made of a
// hand-tuned piece, stacked so that whatever she writes keeps to what looked right on this model (the owner
// reviewed it, 2026-11-02). Held words set a channel, and a later word on the same channel takes its place;
// moves play once on top. Directions are as the owner sees the screen: positive FaceAngleX/Z and eye X turn
// to the screen's left. Knows nothing of windows, so it is checked in FishPet.Tests.
internal sealed partial class Body
{
    private sealed record Word(string? Channel = null, (string Id, double Value)[]? Hold = null, string? Overlay = null,
        (string On, string Off)? Hand = null, bool HandDown = false, (string Id, string Keys)[]? Move = null, string[]? Clear = null, bool Reset = false);

    private const double LongestWait = 2.4;
    private static readonly (string On, string Off) HandToChest = ("默认手切举手", "举手切默认手"), FingerUp = ("竖手指切出来动画", "竖手指切回默认"),
        Grass = ("狗尾草切换出来", "狗尾草切回默认手");

    private static int Side(string? where) => where == "右" ? -1 : 1;
    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Scale(string keys, double by) => Rework(keys, (time, value) => (time, value * by));
    private static string Pace(string keys, double by) => Rework(keys, (time, value) => (time * by, value));
    private static string Rework(string keys, Func<double, double, (double, double)> change) => string.Join(' ', keys.Split(' ').Select(key =>
    {
        var parts = key.Split(':');
        var (time, value) = change(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
        return Number(time) + ":" + Number(value);
    }));
    private static double End(string keys) => double.Parse(keys[(keys.LastIndexOf(' ') + 1)..].Split(':')[0], CultureInfo.InvariantCulture);
    // A run of the same small move: down and back each time, then still.
    private static string Repeat(int times, double each, double down, double back, double downValue, double backValue, double rest)
    {
        var keys = new List<string> { "0:0" };
        double at = 0;
        for (int i = 0; i < times; i++) { keys.Add(Number(at + down) + ":" + Number(downValue)); keys.Add(Number(at + back) + ":" + Number(backValue)); at += each; }
        keys.Add(Number(at + rest) + ":0");
        return string.Join(' ', keys);
    }

    private static Word Look(string where, double s) => where switch
    {
        "上" => new("gaze", [("ParamEyeBallY", .6 * s), ("FaceAngleY", 4 * s)]),
        "下" => new("gaze", [("ParamEyeBallY", -.6 * s), ("FaceAngleY", -5 * s)]),
        _ => new("gaze", [("ParamEyeBallX", .75 * s * Side(where)), ("FaceAngleX", 7 * s * Side(where))]),
    };

    // A nod or shake is two unless she says otherwise: one alone was over before it showed (the owner, 2026-11-02).
    private static readonly Dictionary<string, Func<string?, double, int?, Word>> Words = new()
    {
        ["歪头"] = (a, s, _) => new("tilt", [("FaceAngleZ", 16 * s * Side(a))]),
        ["转头"] = (a, s, _) => new("turn", [("FaceAngleX", 20 * s * Side(a))]),
        ["低头"] = (_, s, _) => new("pitch", [("FaceAngleY", -14 * s)]),
        ["抬头"] = (_, s, _) => new("pitch", [("FaceAngleY", 12 * s)]),
        ["正视"] = (_, _, _) => new(Clear: ["tilt", "turn", "pitch", "gaze"]),
        ["前倾"] = (_, s, _) => new("lean", [("LeanInput", -14 * s)]),
        ["后仰"] = (_, s, _) => new("lean", [("LeanInput", 9 * s)]),
        ["站直"] = (_, _, _) => new(Clear: ["lean"]),
        // Her eyes lead and her head follows a little, the way people look aside.
        ["看过来"] = (_, _, _) => new(Clear: ["gaze"]), ["看你"] = (_, _, _) => new(Clear: ["gaze"]),
        ["看左边"] = (_, s, _) => Look("左", s), ["看右边"] = (_, s, _) => Look("右", s),
        ["看上面"] = (_, s, _) => Look("上", s), ["看下面"] = (_, s, _) => Look("下", s),
        ["睁大"] = (_, s, _) => new("eyes", [("EyeOpen", 1 + .35 * s)]),
        ["半睁"] = (_, s, _) => new("eyes", [("EyeOpen", 1 - .55 * s)]),
        ["闭眼"] = (_, _, _) => new("eyes", [("EyeOpen", 0)]),
        // EyeSmile shows only on closing lids.
        ["眯眼笑"] = (_, _, _) => new("eyes", [("EyeOpen", 0), ("EyeSmile", 1)]),
        ["笑眼"] = (_, _, _) => new("eyes", [("EyeOpen", .45), ("EyeSmile", 1)]),
        ["斜眼"] = (_, s, _) => new("eyes", [("REyeSquint", .9 * s), ("LEyeSquint", .9 * s), ("EyeOpen", .7)]),
        ["眨单眼"] = (_, _, _) => new("eyes", [("ParamEyeLOpen", 0), ("EyeSmile", 1)]),
        ["单眨眼"] = (_, _, _) => new(Move: [("ParamEyeLOpen", "0:0 .2:-1 2.2:-1 2.35:0")]),
        ["挑眉"] = (_, s, _) => new("brows", [("ParamBrowLY", .6 * s), ("ParamBrowRY", .6 * s), ("BrowInnerUp", .4 * s)]),
        ["皱眉"] = (_, s, _) => new("brows", [("ParamBrowLY", -.8 * s), ("ParamBrowRY", -.8 * s), ("ParamBrowLAngle", -s), ("ParamBrowRAngle", -s)]),
        ["八字眉"] = (_, s, _) => new("brows", [("BrowInnerUp", s), ("ParamBrowLAngle", .6 * s), ("ParamBrowRAngle", .6 * s)]),
        ["微笑"] = (_, s, _) => new("mouth", [("ParamMouthForm", s)]),
        ["大笑"] = (_, s, _) => new("mouth", [("ParamMouthForm", 1), ("ParamMouthOpenY", .6 * s), ("JawOpen", .3 * s)]),
        ["坏笑"] = (a, s, _) => new("mouth", [("ParamMouthForm", 1), ("MouthX", .6 * s * Side(a))]),
        ["瘪嘴"] = (_, s, _) => new("mouth", [("ParamMouthForm", -s)]),
        ["张嘴"] = (_, s, _) => new("mouth", [("ParamMouthOpenY", .7 * s), ("JawOpen", .4 * s), ("MouthFunnel", .3)]),
        ["嘟嘴"] = (_, _, _) => new("mouth", [("MouthFunnel", .7), ("Mouthpucker", 1), ("ParamMouthForm", -.4)]),
        ["鼓腮"] = (_, _, _) => new("mouth", [("CheeckPuff", 1)]),
        ["抿嘴"] = (_, _, _) => new("mouth", [("MouthPressLipOpen", -1), ("ParamMouthForm", -.5)]),
        ["吐舌"] = (_, _, _) => new("mouth", [("TongueOut", 1), ("ParamMouthOpenY", .35)]),
        ["脸红"] = (_, _, _) => new(Overlay: "脸红"), ["黑脸"] = (_, _, _) => new(Overlay: "黑脸"),
        ["冷汗"] = (_, _, _) => new(Overlay: "流冷汗"), ["眼泪"] = (_, _, _) => new(Overlay: "流泪"),
        ["爱心眼"] = (_, _, _) => new(Overlay: "爱心眼"), ["圈圈眼"] = (_, _, _) => new(Overlay: "圈圈眼"),
        ["＞＜"] = (_, _, _) => new(Overlay: "＞＜"), ["><"] = (_, _, _) => new(Overlay: "＞＜"), ["屑屑眼"] = (_, _, _) => new(Overlay: "屑屑眼"),
        ["抚胸"] = (_, _, _) => new(Hand: HandToChest), ["竖手指"] = (_, _, _) => new(Hand: FingerUp),
        ["狗尾草"] = (_, _, _) => new(Hand: Grass), ["放下手"] = (_, _, _) => new(HandDown: true),
        // Small things that come and go: how many and how fast is most of what they say (the owner, 2026-11-02).
        ["点头"] = (_, s, n) => new(Move: [("FaceAngleY", Scale(Repeat(n ?? 2, .46, .17, .4, -10, 1, .14), s))]),
        ["摇头"] = (_, s, n) => new(Move: [("FaceAngleX", Scale(Repeat(n ?? 2, .56, .17, .45, -11, 11, .22), s))]),
        ["眨眼"] = (_, _, n) => new(Move: [("EyeOpen", Repeat(n ?? 1, .22, .07, .16, -1, 0, 0))]),
        ["瞟一眼"] = (a, s, _) => new(Move: a is "上" or "下" ? [("ParamEyeBallY", Scale("0:0 .12:.7 .55:.7 .8:0", s * (a == "上" ? 1 : -1)))]
            : [("ParamEyeBallX", Scale("0:0 .12:.85 .55:.85 .8:0", s * Side(a)))]),
        ["挑一下眉"] = (_, s, _) => new(Move: [("ParamBrowLY", Scale("0:0 .1:.7 .45:.7 .7:0", s)), ("ParamBrowRY", Scale("0:0 .1:.7 .45:.7 .7:0", s)),
            ("BrowInnerUp", Scale("0:0 .1:.5 .45:.5 .7:0", s))]),
        ["歪一下头"] = (a, s, _) => new(Move: [("FaceAngleZ", Scale("0:0 .2:12 .6:12 .9:0", s * Side(a)))]),
        ["晃头"] = (_, s, _) => new(Move: [("FaceAngleZ", Scale("0:0 .3:-10 .7:10 1.1:-8 1.5:6 2:0", s))]),
        ["缩一下"] = (_, s, _) => new(Move: [("ViewSquash", Scale("0:0 .08:.05 .3:.01 .6:0", s)), ("FaceAngleY", Scale("0:0 .08:-8 .6:0", s))]),
        ["叹气"] = (_, _, _) => new(Move: [("LeanInput", "0:0 .8:5 1.8:-7 2.8:-6 3.5:0"), ("FaceAngleY", "0:0 .8:7 1.8:-9 2.8:-8 3.5:0"),
            ("ViewSquash", "0:0 .8:-.02 1.2:-.02 1.9:.03 2.8:.025 3.5:0"), ("EyeOpen", "0:0 .8:-.2 1.8:-.6 2.8:-.55 3.5:0"),
            ("MouthFunnel", "0:0 1.1:0 1.4:.9 2.2:.6 2.6:0"), ("ParamMouthOpenY", "0:0 1.1:0 1.4:.3 2.2:.12 2.6:0")]),
        // A jump is for being startled only (the owner, 2026-11-02): the same one as when her tail is touched.
        ["吓一跳"] = (_, s, _) => new(Move: [("ViewY", Scale("0:0 .06:0 .2:.06 .4:0", s)), ("ViewSquash", Scale("0:0 .06:.06 .18:-.05 .4:.05 .6:0", s)),
            ("EyeOpen", Scale("0:0 .08:.35 .8:.3 1.2:0", s)), ("BrowInnerUp", Scale("0:0 .08:.9 1:.6 1.4:0", s))]),
        ["恢复"] = (_, _, _) => new(Reset: true),
        // 跳舞（用户亲自指导，2026-11-06）：身体左右转，头往相反方向转，头走小拱形。只在 skits.txt 的「跳舞」里用，不进身体说明。
        // 摇摆一：左右左右左右（121212）；摇摆二：左右左右左右，慢一点（见下面 Groove）；慢摇：越摇越慢越小，渐渐停下。
        ["摇摆一"] = (_, s, _) => Dance(s, Enumerable.Repeat(.45, 12).ToArray(), [1, -1, 1, -1, 1, -1, 1, -1, 1, -1, 1, -1], false),
        // 摇摆二（用户，2026-11-08 改）：不再左右甩头，改成可爱的歪头、扭头、耸肩，跟着拍子一下一下颠，节奏是左右左右左右（用户改，比第一段慢：6 拍走完原来 8 拍的时长）。
        ["摇摆二"] = (_, s, _) => Groove(s, Enumerable.Repeat(.4 * 8 / 6, 6).ToArray(), [1, -1, 1, -1, 1, -1]),
        // 慢摇（用户，2026-11-09 改）：不再用第一段的转身甩头，改成摇摆二的动作（歪头、扭头、耸肩、颠一下）接着往下做，
        // 从第二段停下的那一边（右）的下一拍（左）接着来，越摇越慢越小，渐渐停下。拍子和总长都没变（3.86 秒）。
        ["慢摇"] = (_, s, _) => Groove(s, [.7, .9, 1.1], [1, -.7, .4]),
        ["左右张望"] = (_, s, _) => new(Move: [("ParamEyeBallX", Scale("0:0 .15:.8 .75:.8 1:-.8 1.6:-.8 1.9:0", s)), ("FaceAngleX", Scale("0:0 .15:8 .75:8 1:-8 1.6:-8 1.9:0", s))]),
    };

    // 一段舞：每一拍（hits）身体转到一边（正数转向屏幕左边，负数右边，绝对值是幅度），头往反方向转、反方向歪，
    // 头比身子晚 0.14 秒到，两头不是同一刻一起掉头，看着连贯；头还走一条小拱形（拍点低头、两拍中间抬头），就是点头。
    // gaps 是每一拍的长度（秒）；dip：同一边连着两拍时，两拍之间身体回弹一半（“顿”一下）。每条轨迹最后都回到 0。
    private static Word Dance(double size, double[] beats, double[] hits, bool dip)
    {
        // 整体速度降低 30%（用户，2026-11-06）：每一拍都拉长到 1/0.7 倍。
        var gaps = beats.Select(beat => beat / .7).ToArray();
        const double HeadLag = .14;
        var times = new double[hits.Length];
        times[0] = gaps[0] / 2;
        for (int i = 1; i < hits.Length; i++) times[i] = times[i - 1] + (gaps[i - 1] + gaps[i]) / 2;
        double end = gaps.Sum();
        string Track(double amount, double extra, Func<int, double> at, double lag = 0)
        {
            var keys = new List<(double Time, double Value)> { (0, 0) };
            for (int i = 0; i < hits.Length; i++)
            {
                keys.Add((times[i] + lag, at(i) * amount));
                if (i + 1 < hits.Length && dip && Math.Sign(hits[i]) == Math.Sign(hits[i + 1])) keys.Add(((times[i] + times[i + 1]) / 2 + lag, at(i) * amount * extra));
            }
            keys.Add((end + lag, 0));
            return string.Join(' ', keys.Select(key => Number(key.Time) + ":" + Number(key.Value * size)));
        }
        // 点头的拱形（用户 2026-11-12 让加回来）：每一拍落到 low（低头），两拍中间抬到 high。
        string Arch(double high, double low, double lag = 0)
        {
            var keys = new List<(double Time, double Value)> { (0, 0) };
            for (int i = 0; i < hits.Length; i++)
            {
                keys.Add((times[i] + lag, low));
                if (i + 1 < hits.Length) keys.Add(((times[i] + times[i + 1]) / 2 + lag, Math.Sign(hits[i]) == Math.Sign(hits[i + 1]) ? high * .25 : high));
            }
            keys.Add((end + lag, 0));
            return string.Join(' ', keys.Select(key => Number(key.Time) + ":" + Number(key.Value * size)));
        }
        return new(Move: [("ParamBodyAngleX", Track(13, .55, i => hits[i])), ("ParamBodyAngleZ", Track(6.5, .55, i => hits[i])), ("ViewRoll", Track(2.5, .55, i => hits[i])), ("ViewX", Track(.02, .55, i => hits[i])),
            ("FaceAngleX", Track(-19, .55, i => hits[i], HeadLag)), ("FaceAngleZ", Track(-8, .55, i => hits[i], HeadLag)), ("FaceAngleY", Arch(9.5, -4.5, HeadLag))]);
    }

    // 第二段舞：每一拍头歪向一边、脸跟着转过去一点，同一边的肩膀耸起来靠向头，身子轻轻往那边扭；每拍落下时整个人往下颠一下、
    // 头点一下，拍子就出来了。同一边连着两拍时，第二拍是“再歪一下”：中间先回弹一半再歪回去，比第一拍更俏皮一点。
    // 所有轨迹都是一拍一个关键帧、中间平滑过渡（渲染器用 smoothstep），只有拍点，不停顿，所以是丝滑的。
    private static Word Groove(double size, double[] beats, double[] hits)
    {
        var gaps = beats.Select(beat => beat / .7).ToArray();
        var times = new double[hits.Length];
        times[0] = gaps[0] / 2;
        for (int i = 1; i < hits.Length; i++) times[i] = times[i - 1] + (gaps[i - 1] + gaps[i]) / 2;
        double end = gaps.Sum();
        bool Again(int i) => i > 0 && Math.Sign(hits[i]) == Math.Sign(hits[i - 1]);
        // 跟着拍子往两边：同一边第二拍更深一点；两拍之间回弹一半。lag：比身子晚一点点。
        string Side(double amount, double lag = 0, double back = .45)
        {
            var keys = new List<(double, double)> { (0, 0) };
            for (int i = 0; i < hits.Length; i++)
            {
                keys.Add((times[i] + lag, hits[i] * amount * (Again(i) ? 1.15 : 1)));
                if (i + 1 < hits.Length && Math.Sign(hits[i]) == Math.Sign(hits[i + 1])) keys.Add(((times[i] + times[i + 1]) / 2 + lag, hits[i] * amount * back));
            }
            keys.Add((end + lag, 0));
            return string.Join(' ', keys.Select(key => Number(key.Item1) + ":" + Number(key.Item2 * size)));
        }
        // 每拍一颠：拍点上落到 down，两拍中间回到 up。幅度跟着这一拍的 |hits| 走（摇摆二每拍都是 ±1，所以和以前完全一样；
        // 慢摇越摇越小时，颠的幅度也跟着变小）。
        string Bounce(double down, double up, double lag = 0)
        {
            var keys = new List<(double, double)> { (0, 0) };
            for (int i = 0; i < hits.Length; i++)
            {
                keys.Add((times[i] + lag, down * Math.Abs(hits[i])));
                if (i + 1 < hits.Length) keys.Add(((times[i] + times[i + 1]) / 2 + lag, up * (Math.Abs(hits[i]) + Math.Abs(hits[i + 1])) / 2));
            }
            keys.Add((end + lag, 0));
            return string.Join(' ', keys.Select(key => Number(key.Item1) + ":" + Number(key.Item2 * size)));
        }
        return new(Move: [
            ("FaceAngleZ", Side(21, .06)), ("FaceAngleX", Side(9, .1)), ("FaceAngleY", Bounce(-4, 3, .05)),
            ("ShoulderYy", Side(14)), ("ShoulderX", Side(10)),
            ("ParamBodyAngleZ", Side(5)), ("ParamBodyAngleX", Side(4)), ("ViewRoll", Side(1.2)),
            ("ViewY", Bounce(-.008, .004))]);
    }

    // 组合词：一个词展开成几个基础词一起做（比如「高兴」＝张嘴 + 眯眼笑 + 左右晃头）。
    private static readonly Dictionary<string, string> Combos = new()
    {
        ["高兴"] = "张嘴 眯眼笑 晃头",
        // 以下是待机自言自语 / 小剧场用的小情绪（身体说明里没有，只在 mutter.txt、skits.txt 和模型现编的嘀咕里出现）。
        ["偷笑"] = "坏笑 瞟一眼:左 眯眼笑",
        ["傻眼"] = "睁大 张嘴 冷汗 快眨眼×2",
        ["无语"] = "斜眼 抿嘴 微摇头",
        ["得瑟"] = "屑屑眼 晃头 后仰",
        ["抓狂"] = "皱眉 ＞＜ 张嘴 快摇头×3",
        ["犯困"] = "半睁 低头 张嘴 慢眨眼×2",
        ["感动"] = "八字眉 眼泪 抚胸 微笑",
        ["害怕"] = "缩一下 冷汗 八字眉 后仰",
        ["鬼点子"] = "挑眉 坏笑 竖手指",
        ["撒娇"] = "嘟嘴 歪头 八字眉 慢眨眼",
        ["得救"] = "睁大 抬头 微笑 叹气",
        ["警觉"] = "正视 站直 皱眉 看左边",
    };

    // 让模型现编嘀咕时可以选的动作词（挑过的：不含吓一跳、爱心眼这类有特定用法的）。
    internal const string MutterVocabulary =
        "歪头 低头 抬头 前倾 后仰 看左边 看右边 看上面 看下面 睁大 半睁 眯眼笑 笑眼 斜眼 眨单眼 挑眉 皱眉 八字眉 微笑 大笑 坏笑 瘪嘴 张嘴 嘟嘴 鼓腮 抿嘴 吐舌 " +
        "脸红 黑脸 冷汗 屑屑眼 竖手指 抚胸 点头 摇头 眨眼 瞟一眼 挑一下眉 晃头 缩一下 叹气 " +
        "偷笑 傻眼 无语 得瑟 抓狂 犯困 感动 害怕 鬼点子 撒娇 得救 警觉 高兴";

    // 这段动作词（空格分隔）是不是都读得懂，数量合不合理。
    internal static bool CueReadable(string? cue, int most = 5)
    {
        if (string.IsNullOrWhiteSpace(cue)) return false;
        if (cue.Split([' ', '　', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries).Length > most) return false;
        return Unread("〔" + cue + "〕").Count == 0;
    }

    private readonly Action<string> send;
    private readonly Dictionary<string, (string Id, double Value)[]> channels = [];
    // 跳舞时的表情单独放：不算「摆着姿势」，否则跳舞一换脸就判定成被摆姿势、舞蹈自己把自己停下（本机修，2026-11-12）
    // 哪些脸上的表情会把两只眼睛整个换掉。这几个同时只能开一个：换一个就把前一个关掉
    // （用户 2026-11-13 报的：两个眼睛表情叠着开，脸上只看得到先开的那个，再点后面那个就像没反应）。
    private static readonly string[] EyeOverlays = ["＞＜", "爱心眼", "圈圈眼", "屑屑眼"];

    private (string Id, double Value)[]? danceFace;
    private readonly HashSet<string> overlays = [];
    private Dictionary<string, double> shown = [];
    private (string On, string Off)? hand;

    internal Body(Action<string> send) => this.send = send;

    // What she wears this way stays on when one of her reflexes, which also used it, ends.
    internal IReadOnlyCollection<string> Overlays => overlays;
    internal bool HandUp => hand != null;
    internal bool Posing => channels.Count > 0 || overlays.Count > 0 || hand != null;
    // 她这会儿用 set 按住不放的参数（音乐律动收工时不去 clear 这些，否则会把她的神情/姿势一起收掉）。
    internal IReadOnlyDictionary<string, double> Shown => shown;
    // Words her body could not do, for telling her so in her own terms.
    internal List<string> Unknown { get; } = [];

    [GeneratedRegex("〔[^〔〕]*〕")]
    private static partial Regex CuePattern();
    internal static string Strip(string text) => CuePattern().Replace(text, "");
    internal static bool HasCues(string text) => CuePattern().IsMatch(text);
    // The words in her 〔〕 her body cannot read, each with the one word it is a character away from when there is
    // just one (瞒一眼 → 瞟一眼): to tell her, never to do in its place, as 大哭 is not 大笑 (2026-11-05).
    internal static List<(string Word, string? Like)> Unread(string text)
    {
        var unread = new List<(string, string?)>();
        foreach (Match cue in CuePattern().Matches(text))
            foreach (var token in cue.Value[1..^1].Split([' ', '　', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                string name = Regex.Match(token.TrimStart('-', '－'), @"^(.+?)(?:[×xX*](\d))?(?:[:：](.+))?$").Groups[1].Value;
                while (!Words.ContainsKey(name) && !Combos.ContainsKey(name) && name.Length > 1 && name[0] is '微' or '大' or '快' or '慢') name = name[1..];
                if (Words.ContainsKey(name) || Combos.ContainsKey(name)) continue;
                var near = Words.Keys.Where(word => word.Length == name.Length && word.Zip(name).Count(pair => pair.First != pair.Second) == 1).ToList();
                unread.Add((token, near.Count == 1 ? near[0] : null));
            }
        return unread;
    }
    // Whether a line moves her body while it is said, not only before it: a 〔〕 after some of its words.
    internal static bool ActsWithin(string line) => Regex.IsMatch(Regex.Replace(line, @"^(\s*〔[^〔〕]*〕)+", ""), @"[^〕\s]\s*〔");
    // Reminded to think of her body as she speaks while fewer than two of her last ten lines moved within, or fewer
    // than five were said to tell by (replayed on the promotion video, a good performer was reminded 5 times in 53).
    internal static bool NeedsActing(IReadOnlyCollection<bool> lines) => lines.Count < 5 || lines.Count(within => within) < 2;
    // The body guide's version, from its end line 【你的身体·完】（身体说明 … 版）, or null.
    internal static string? GuideVersion(string guide) => Regex.Match(guide, @"【你的身体·完】（身体说明 (\S+) 版）") is { Success: true } found ? found.Groups[1].Value : null;
    // What she says in order, each piece either words or a cue (without its brackets).
    internal static List<(bool Cue, string Text)> Parts(string text)
    {
        var parts = new List<(bool, string)>();
        int at = 0;
        foreach (Match match in CuePattern().Matches(text))
        {
            if (match.Index > at) parts.Add((false, text[at..match.Index]));
            parts.Add((true, match.Value[1..^1]));
            at = match.Index + match.Length;
        }
        if (at < text.Length) parts.Add((false, text[at..]));
        return parts;
    }

    // Moves that are reflexes keep their own speed whatever she is saying.
    private static readonly HashSet<string> Reflexes = ["吓一跳", "眨眼", "缩一下"];
    private const double Beat = .35, HandBeat = .7, StartleBeat = .45;

    // Plays one cue and says how long her words wait before going on. Her moves go with her words (tuned by eye,
    // 2026-11-02: "动作要配合语气和句子长短"): stretched or quickened to the words they come with (spoken: how long
    // those take, 0 for none), bigger and quicker before a "！", smaller and slower before "……" (tone), unless she
    // says 微/大/快/慢 herself. Her words wait only for it to begin, a hand to come up, a jump to land; with
    // nothing to say after it, for all of it.
    internal double Apply(string cue, double spoken = 0, char tone = '\0')
    {
        // 展开组合词（比如「高兴」→「张嘴 眯眼笑 晃头」）
        cue = string.Join(' ', cue.Split([' ', '　', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(token => Combos.TryGetValue(token, out var parts) ? parts.Split(' ') : new[] { token }));
        double wait = 0;
        double toneSize = tone == '！' ? 1.2 : tone == '…' ? .85 : 1, toneSpeed = tone == '！' ? .85 : tone == '…' ? 1.25 : 1;
        foreach (var token in cue.Split([' ', '　', '、', '，', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token[0] is '-' or '－')
            {
                if (Find(token[1..]) is not { } gone) { Unknown.Add(token); continue; }
                if (gone.Overlay is { } face && overlays.Remove(face)) send($"exp {face} 0");
                if (gone.Channel is { } place) channels.Remove(place);
                if (gone.Hand is { } off && hand == off) PutHandDown();
                continue;
            }
            var match = Regex.Match(token, @"^(.+?)(?:[×xX*](\d))?(?:[:：](.+))?$");
            string name = match.Groups[1].Value;
            double? size = null, speed = null;
            // 微/大 in front make a word smaller or bigger, 快/慢 a move quicker or slower, unless they belong to it (大笑).
            while (!Words.ContainsKey(name) && name.Length > 1 && name[0] is '微' or '大' or '快' or '慢')
            {
                switch (name[0]) { case '微': size = .5; break; case '大': size = 1.4; break; case '快': speed = .65; break; default: speed = 1.7; break; }
                name = name[1..];
            }
            if (!Words.TryGetValue(name, out var make)) { Unknown.Add(token); continue; }
            int? count = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : null;
            string? where = match.Groups[3].Success ? match.Groups[3].Value : null;
            bool reflex = Reflexes.Contains(name);
            var word = make(where, size ?? 1, count);
            if (word.Move != null && size == null && !reflex) word = make(where, toneSize, count);
            var move = word.Move?.Select(track => (track.Id, track.Keys)).ToArray();
            if (move != null && !reflex)
            {
                double length = move.Max(track => End(track.Keys));
                double pace = speed ?? toneSpeed * (spoken > 0 ? Math.Clamp(spoken / length, .7, 1.6) : 1);
                pace = Math.Clamp(pace, .6, 1.8);
                if (pace != 1) move = move.Select(track => (track.Id, Pace(track.Keys, pace))).ToArray();
            }
            else if (move != null && speed is { } quick) move = move.Select(track => (track.Id, Pace(track.Keys, quick))).ToArray();
            double moving = move?.Max(track => End(track.Keys)) ?? 0;
            wait = Math.Max(wait, spoken <= 0 ? Math.Max(.8, Math.Max(moving, word.Hand != null ? 1.1 : 0))
                : name == "吓一跳" ? StartleBeat : word.Hand != null ? HandBeat : Beat);
            if (word.Reset) Relax();
            foreach (var place in word.Clear ?? []) channels.Remove(place);
            if (word.Channel is { } channel) channels[channel] = word.Hold!;
            if (word.Overlay is { } overlay)
            {
                // 眼睛类表情互斥：先关掉另一个，再开这个（用户 2026-11-13）
                if (EyeOverlays.Contains(overlay))
                    foreach (var other in EyeOverlays)
                        if (other != overlay && overlays.Remove(other)) send($"exp {other} 0");
                if (overlays.Add(overlay)) send($"exp {overlay} 1");
            }
            if (word.HandDown) PutHandDown();
            if (word.Hand is { } up && hand != up) { PutHandDown(); hand = up; send($"motion {up.On}"); }
            foreach (var (id, keys) in move ?? []) send($"anim {id} {keys}");
        }
        // The smug eyes bring their own little mouth: nothing else goes on it (a tongue would float outside).
        // 但鼓腮（CheeckPuff）是腮帮子，跟自带的小嘴不冲突，留着。
        if (overlays.Contains("屑屑眼") && channels.TryGetValue("mouth", out var held))
        {
            var cheeks = held.Where(kv => kv.Id == "CheeckPuff").ToArray();
            if (cheeks.Length == 0) channels.Remove("mouth"); else channels["mouth"] = cheeks;
        }
        Show();
        return Math.Min(wait, LongestWait);
    }

    private static Word? Find(string name) => Words.TryGetValue(name, out var make) ? make(null, 1, null) : null;

    // 跳舞时的脸（本机加，2026-11-12 用户要的；三个表情也是用户定的）：
    // 0 眯眼笑＋微笑、1 眯眼笑＋大笑、2 闭眼＋挑眉；-1 收掉，回到默认脸。
    internal void DanceFace(int mood)
    {
        danceFace = mood < 0 ? null : mood switch
        {
            1 => [("EyeOpen", 0), ("EyeSmile", 1), ("ParamMouthForm", 1), ("ParamMouthOpenY", .5), ("JawOpen", .25)],
            2 => [("EyeOpen", 0), ("ParamBrowLY", .6), ("ParamBrowRY", .6), ("BrowInnerUp", .4)],
            _ => [("EyeOpen", 0), ("EyeSmile", 1), ("ParamMouthForm", .9)],
        };
        Show();
    }

    // 音乐律动用（本机加，2026-11-10）：把现成的舞步按拍子变速演一遍——BPM 高就放快、BPM 低就放慢。
    // seconds 是这一句该演多久（Dance.Seconds 算的：拍数 × 一拍）；返回实际演了多久（变速有 .45~2.4 的限制）。
    // 只发 anim 轨道，不碰通道和表情：这些舞步本来就是纯粹的一串轨迹，演完自己回原样。
    internal double DanceToBeat(string word, double seconds, double size = 1)
    {
        if (!Words.TryGetValue(word, out var make)) return 0;
        if (make(null, size, null).Move is not { } move) return 0;
        double natural = move.Max(track => End(track.Keys));
        if (natural <= 0 || seconds <= 0) return 0;
        double pace = Math.Clamp(seconds / natural, .45, 2.4);
        foreach (var (id, keys) in move) send($"anim {id} {Pace(keys, pace)}");
        return natural * pace;
    }

    private void PutHandDown()
    {
        if (hand is { } up) send($"motion {up.Off}");
        hand = null;
    }

    // Channels add up where they touch the same input: looking aside while turned turns a bit further.
    private void Show()
    {
        var total = new Dictionary<string, double>();
        foreach (var hold in channels.Values)
            foreach (var (id, value) in hold) total[id] = total.GetValueOrDefault(id) + value;
        foreach (var (id, value) in danceFace ?? []) total[id] = total.GetValueOrDefault(id) + value;
        foreach (var id in shown.Keys.Where(id => !total.ContainsKey(id))) send($"clear {id}");
        foreach (var (id, value) in total) if (!shown.TryGetValue(id, out var before) || before != value) send($"set {id} {Number(value)}");
        shown = total;
    }

    // Back to how she is when she writes nothing. Only her own: her body type and what the owner dressed her in stay.
    internal void Relax()
    {
        channels.Clear();
        foreach (var overlay in overlays) send($"exp {overlay} 0");
        overlays.Clear();
        var wasHand = hand;
        PutHandDown();
        if (wasHand is { } h) send($"motion {h.Off}"); // 再补发一次放下：渲染器的手部动作是队列式的，偶尔没及时让位，手会一直举着
        Show();
    }

    // 只收会保持的眼睛/嘴/手/身子，脸上表情（overlays）不动：控制台点「组合神情」临时摆一下时用（本机加 2026-11-06）。
    internal void RelaxChannels()
    {
        channels.Clear();
        PutHandDown();
        Show();
    }

    // After one of her reflexes, which held some of the same parameters and let them go.
    internal void Restore()
    {
        foreach (var (id, value) in shown) send($"set {id} {Number(value)}");
    }

    // A new renderer knows nothing of what the one before showed.
    internal void Forget()
    {
        channels.Clear(); overlays.Clear(); shown = []; hand = null; danceFace = null;
    }
}

// One thing she says, acted out: the words come at a speaking pace, and each 〔〕 holds them until her body has
// shown it, so what she says and does stay together. With her recorded voice the words spread over its length
// instead and nothing waits: a recording cannot pause.
internal sealed class Line
{
    internal const double CharactersPerSecond = 9;
    private const double Dots = .25, Pause = .12;
    private const string Stops = "，。！？、,!?;；：:~～—";
    private readonly List<(string? Cue, int End, double Seconds, bool Talks)> steps = [];
    // act(cue, spoken, tone): spoken is how long the words after it take, tone their "！" or "…" (Body.Apply).
    private readonly Func<string, double, char, double> act;
    private int index;
    private double at, clock;

    // What she says in one go, a bubble for each line, said one after the other: she thinks the whole of it through and
    // says it at once, not a line, a pause to think, then the next (the owner, 2026-11-03). A line of nothing but 〔〕
    // goes with the words after it (at the end, before it): her body has no bubble of its own.
    internal static List<string> Split(string text)
    {
        var lines = new List<string>();
        string pending = "";
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (Body.Strip(line).Trim().Length == 0) { pending += line; continue; }
            lines.Add(pending + line);
            pending = "";
        }
        if (pending.Length > 0) { if (lines.Count > 0) lines[^1] += pending; else lines.Add(pending); }
        return lines;
    }

    private readonly bool recorded;
    internal Line(string text, Func<string, double, char, double> act, double? voiceSeconds = null, double charactersPerSecond = CharactersPerSecond)
    {
        this.act = act;
        recorded = voiceSeconds is > 0;
        var plain = new System.Text.StringBuilder();
        foreach (var (cue, words) in Body.Parts(text))
        {
            if (cue) { steps.Add((words, plain.Length, 0, false)); continue; }
            var letters = StringInfo.GetTextElementEnumerator(words);
            while (letters.MoveNext())
            {
                var letter = letters.GetTextElement();
                plain.Append(letter);
                bool dots = letter is "…" or "⋯" or ".", stop = Stops.Contains(letter, StringComparison.Ordinal), blank = string.IsNullOrWhiteSpace(letter);
                steps.Add((null, plain.Length, blank ? 0 : dots ? Dots : stop ? Pause : 1 / charactersPerSecond, !blank && !dots && !stop));
            }
        }
        Plain = plain.ToString();
        double total = steps.Sum(step => step.Seconds);
        if (recorded && total > 0)
            for (int i = 0; i < steps.Count; i++) steps[i] = steps[i] with { Seconds = steps[i].Seconds * voiceSeconds!.Value / total };
        Spoken = recorded ? voiceSeconds!.Value : total;
    }

    // The words between a cue and the next: how long they take to say, and whether they are said with a "！" or trail off.
    private (double Spoken, char Tone) After(int cue)
    {
        double spoken = 0;
        int from = steps[cue].End, to = from;
        for (int i = cue + 1; i < steps.Count && steps[i].Cue == null; i++) { spoken += steps[i].Seconds; to = steps[i].End; }
        var words = Plain[from..to];
        char tone = words.Contains('！') || words.Contains('!') ? '！' : words.TrimEnd().EndsWith('…') || words.TrimEnd().EndsWith('⋯') ? '…' : '\0';
        return (spoken, tone);
    }

    internal string Plain { get; }
    // How much of Plain is out, in UTF-16 units.
    internal int Shown { get; private set; }
    internal bool Talking { get; private set; }
    // Once a move at the end has played too, not as it begins: the next line waits for it.
    internal bool Done => index >= steps.Count && clock >= at;
    // When all of it, the last wait included, will have played (seconds since the line began).
    internal double Until => at;
    // How long its words take to say (with a recording, the recording), without the waits for her body.
    internal double Spoken { get; }

    // seconds: since the line began.
    internal void Advance(double seconds)
    {
        clock = seconds;
        Talking = false;
        while (index < steps.Count && at <= seconds)
        {
            var step = steps[index];
            if (step.Cue is { } cue)
            {
                var (spoken, tone) = After(index);
                double wait = act(cue, spoken, tone);
                if (!recorded) at += wait;
                index++;
                continue;
            }
            Shown = step.End;
            if (seconds < at + step.Seconds) { Talking = step.Talks; return; }
            at += step.Seconds;
            index++;
        }
    }
}

