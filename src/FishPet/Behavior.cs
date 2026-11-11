using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FishPet;

// A face made of the model's switches and face parameters, held for a few seconds, and the body moving with it.
// Motion is what her body does meanwhile: a parameter or a View* track of her whole figure, then seconds:value
// keys that start and end at 0; EyeOpen closes her eyes as far as it goes below 0. Pushed marks a movement that
// turns with a side: away from where she was touched, or either way at random when she moves by herself.
internal sealed record Mood(string[] Expressions, (string Id, double Value)[] Parameters, double Seconds = 3,
    (string Id, string Keys)[]? Motion = null, (string On, string Off)? Gesture = null, bool Pushed = false);

// Her reflexes on the Live2D display: what she does by herself when clicked, dragged, left alone or busy.
// Built only for this display, nothing here reads the clip rules. It gives the renderer commands and
// knows nothing of windows, so all of it is checked in FishPet.Tests.
internal sealed class Behavior
{
    private const string Glasses = "黑框眼镜", Monocle = "单片眼镜";
    private static readonly TimeSpan ClickSpan = TimeSpan.FromSeconds(2.5);
    private const int ClicksForBasin = 5;

    internal static readonly IReadOnlyDictionary<string, Mood> Moods = new Dictionary<string, Mood>
    {
        ["问候"] = new([], [("EyeSmile", 1), ("ParamMouthForm", 1)], 4.4, [("FaceAngleZ", "0:0 .5:7 1:5 1.5:0 3.4:0 3.8:7 4.4:0"), ("LeanInput", "0:0 1.1:0 1.7:-11 2.6:-11 3.3:0"), ("FaceAngleY", "0:0 1.1:0 1.7:-19 2.2:-17 2.6:-19 3.3:0"), ("ViewSquash", "0:0 1.1:0 1.7:.04 2.6:.04 3.3:0"), ("EyeOpen", "0:0 1.2:0 1.6:-1 2.7:-1 3.1:0 4.4:0")]), // 抚胸交给 Body（PetWindow 出场时 Apply("抚胸")）：手留到后面的话说完，不在 4.4 秒时自己放下（本机改，2026-11-06）
        ["告别"] = new([], [("EyeSmile", 1), ("ParamMouthForm", 1)], 3, [("FaceAngleZ", "0:0 .4:6 .8:0"), ("LeanInput", "0:0 .7:0 1.3:-12 2.6:-12 3:0"), ("FaceAngleY", "0:0 .7:0 1.3:-19 2.6:-19 3:0"), ("ViewSquash", "0:0 .7:0 1.3:.04 2.6:.04 3:0"), ("EyeOpen", "0:0 .8:0 1.2:-1 2.6:-1 3:0")], ("默认手切举手", "举手切默认手")),
        ["落地"] = new(["圈圈眼"], [("BrowInnerUp", .6), ("ParamMouthOpenY", .2)], 1.25, [("ViewSquash", "0:0 .1:.06 .3:-.02 .5:.015 .75:0"), ("FaceAngleY", "0:0 .1:-10 .75:0"), ("FaceAngleX", "0:0 .5:0 .65:-8 .8:8 .95:-6 1.1:4 1.3:0"), ("EyeOpen", "0:0 1.15:0 1.25:-1 1.4:0")]),
        ["害羞"] = new(["脸红"], [("FaceAngleY", -12), ("FaceAngleX", 10), ("FaceAngleZ", 9), ("ParamEyeBallX", -.6), ("ParamEyeBallY", -.5), ("MouthPressLipOpen", .6)], 3, [("FaceAngleZ", "0:0 .5:2 1:-1 1.6:2 2.2:-1 3:0"), ("ViewSquash", "0:0 .4:.02 2.4:.02 3:0")]),
        ["生气"] = new(["黑脸"], [("CheeckPuff", 1), ("ParamBrowLY", -.8), ("ParamBrowRY", -.8), ("ParamBrowLAngle", -1), ("ParamBrowRAngle", -1), ("FaceAngleX", -14), ("FaceAngleY", -6)], 3, [("FaceAngleX", "0:0 .15:-8 .3:6 .45:-5 .6:3 .8:0"), ("ViewSquash", "0:0 .08:.04 .25:0 1:0"), ("LeanInput", "0:0 .2:8 1.6:6 2.2:0")]),
        ["得意"] = new([], [("REyeSquint", .8), ("LEyeSquint", .8), ("ParamMouthForm", 1), ("MouthX", .5), ("FaceAngleY", 10), ("FaceAngleZ", 12)], 3, [("FaceAngleX", "0:0 .5:3 1.2:-2 1.9:3 3:0"), ("LeanInput", "0:0 .35:6 2.4:5 3:0")]),
        ["委屈"] = new(["流泪"], [("BrowInnerUp", 1), ("Mouthpucker", 1), ("FaceAngleY", -10), ("FaceAngleZ", -6)], 3, [("ViewSquash", "0:0 .4:.03 2.4:.025 3:0"), ("FaceAngleX", "0:0 .4:-3 .9:2 1.4:-3 1.9:2 3:0")]),
        ["开心"] = new([], [("EyeSmile", 1), ("EyeOpen", 0), ("ParamMouthForm", 1), ("ParamMouthOpenY", .5), ("FaceAngleY", 6)], 3, [("FaceAngleZ", "0:0 .3:-10 .7:10 1.1:-8 1.5:6 2:0"), ("FaceAngleY", "0:0 .3:6 .7:2 1.1:6 1.5:2 2:0")], ("默认手切举手", "举手切默认手")),
        ["心虚"] = new(["流冷汗"], [("ParamEyeBallX", .8), ("ParamEyeBallY", .3), ("MouthX", .6), ("ParamMouthForm", -.5), ("FaceAngleX", 14), ("FaceAngleY", -8)], 3, [("FaceAngleZ", "0:0 .3:-4 .6:2 .9:-3 1.2:2 1.5:-2 2:0"), ("ViewSquash", "0:0 .3:.03 2:.02 2.6:0"), ("LeanInput", "0:0 .3:6 2:5 2.6:0")], Pushed: true),
        ["震惊"] = new(["圈圈眼"], [("ParamMouthOpenY", .8), ("JawOpen", .5), ("FaceAngleX", -16), ("FaceAngleY", -8), ("ParamEyeBallX", -.6), ("ParamEyeBallY", -.5)], 3, [("ViewSquash", "0:0 .06:.06 .18:-.05 .4:.05 .6:0"), ("ViewY", "0:0 .06:0 .2:.06 .4:0"), ("FaceAngleY", "0:0 .2:16 .5:4 1.4:0")], Pushed: true),
        ["思考"] = new([], [("ParamEyeBallY", .7), ("ParamEyeBallX", .4), ("FaceAngleZ", 12), ("FaceAngleY", 6), ("BrowInnerUp", .6)], 3.4, [("FaceAngleX", "0:0 .6:4 1.4:2 2.2:5 3:0")], ("竖手指切出来动画", "竖手指切回默认")),
        ["摸头"] = new(["脸红"], [("EyeSmile", .9), ("ParamMouthForm", 1)], 3, [("FaceAngleY", "0:0 .25:-18 .7:-12 1.1:-19 1.5:-12 1.9:-16 2.4:0"), ("FaceAngleZ", "0:0 .5:14 1.1:-14 1.7:14 2.4:0"), ("ViewRoll", "0:0 .5:2 1.1:-2 1.7:2 2.4:0"), ("ViewSquash", "0:0 .25:.05 1.9:.04 2.4:0")]),
        ["戳脸"] = new(["＞＜"], [("CheeckPuff", .8), ("FaceAngleX", 14), ("FaceAngleZ", 8)], 3, [("FaceAngleX", "0:0 .08:12 .3:6 1.2:0"), ("FaceAngleY", "0:0 .08:-6 .4:-2 1.2:0"), ("ViewSquash", "0:0 .06:.04 .25:0 1.2:0")], Pushed: true),
        ["挨打"] = new(["钢盆", "流冷汗"], [("ParamMouthForm", -.6), ("FaceAngleY", -12)], 4, [("ViewSquash", "0:0 .09:.11 .3:.02 .5:.05 .75:.01 1.4:0"), ("FaceAngleZ", "0:0 .15:10 .4:-8 .65:6 .9:-4 1.2:2 1.6:0"), ("FaceAngleY", "0:0 .1:-12 .5:-4 1.6:0")]),
        ["护裙"] = new(["黑脸", "脸红"], [("CheeckPuff", .8), ("ParamBrowLY", -.6), ("ParamBrowRY", -.6), ("FaceAngleX", -16), ("FaceAngleY", -8), ("ParamEyeBallX", .5)], 3, [("FaceAngleZ", "0:0 .2:-12 .5:-7 1.6:-8 2.8:0"), ("ViewSquash", "0:0 .12:.05 .35:.01 2.8:0"), ("LeanInput", "0:0 .3:5 2.2:4 2.8:0")], ("默认手切举手", "举手切默认手")),
        // 亲密度满档（第 5 级）时戳裙子：不凶了，脸红、眼冒爱心，低着头扭扭捏捏地左右晃，手按在胸口（本机加，2026-11-08）
        ["娇羞"] = new(["脸红", "爱心眼"], [("EyeSmile", .7), ("MouthPressLipOpen", .5), ("ParamMouthForm", .6), ("FaceAngleY", -10), ("ParamEyeBallX", -.5), ("ParamEyeBallY", -.4), ("BrowInnerUp", .7)], 4.2,
            [("FaceAngleZ", "0:0 .4:-9 .9:6 1.4:-8 1.9:5 2.4:-7 2.9:4 3.5:-3 4.2:0"), ("FaceAngleX", "0:0 .4:-6 .9:4 1.4:-6 1.9:4 2.4:-5 2.9:3 4.2:0"), ("LeanInput", "0:0 .3:-4 .9:3 1.4:-4 1.9:3 2.4:-3 3:2 4.2:0"), ("ViewSquash", "0:0 .3:.03 3.6:.025 4.2:0")]),
        // 满档戳裙子的另外两种（和“娇羞”三选一，概率平分）：红着脸往前凑；大大地往前倾、手按在胸口、笑眼（和 Body 的「笑眼」同一组参数）（用户定的，2026-11-08）
        ["娇羞前倾"] = new(["脸红"], [("EyeSmile", .6), ("MouthPressLipOpen", .4), ("ParamMouthForm", .6), ("BrowInnerUp", .5), ("FaceAngleZ", 7)], 4,
            [("LeanInput", "0:0 .45:-14 3.4:-13 4:0"), ("FaceAngleY", "0:0 .45:-7 3.4:-6 4:0"), ("FaceAngleZ", "0:0 .8:3 1.6:-2 2.4:3 3.2:-1 4:0"), ("ViewSquash", "0:0 .45:.02 3.4:.02 4:0")]),
        ["娇羞抚胸"] = new(["脸红"], [("EyeOpen", .45), ("EyeSmile", 1), ("ParamMouthForm", .8), ("BrowInnerUp", .6), ("FaceAngleZ", -6)], 4.2,
            [("LeanInput", "0:0 .5:-21 3.5:-20 4.2:0"), ("FaceAngleY", "0:0 .5:-10 3.5:-9 4.2:0"), ("ViewSquash", "0:0 .5:.03 3.5:.03 4.2:0"), ("FaceAngleZ", "0:0 1:-2 2:2 3:-2 4.2:0")], ("默认手切举手", "举手切默认手")),
        ["张望"] = new([], [], 3.4, [("FaceAngleX", "0:0 .5:24 .8:22 1.1:25 1.3:24 1.9:-22 2.2:-20 2.5:-23 2.7:-22 3.3:0"), ("FaceAngleY", "0:0 .5:4 1.3:3 1.9:5 2.7:3 3.3:0")]),
        ["哼歌"] = new([], [("EyeSmile", 1), ("EyeOpen", 0), ("ParamMouthForm", 1)], 3.4, [("FaceAngleZ", "0:0 .6:14 1.4:-14 2.2:14 3.2:0"), ("FaceAngleX", "0:0 .6:4 1.4:-4 2.2:4 3.2:0"), ("FaceAngleY", "0:0 .3:3 .6:0 1:3 1.4:0 1.8:3 2.2:0 3.2:0")]),
        ["打哈欠"] = new([], [], 3.6, [("EyeOpen", "0:0 .5:-.5 .9:-1 2.3:-1 2.7:-.45 3.1:-.4 3.6:0"), ("ParamMouthOpenY", "0:0 .5:.4 1:1 2.1:1 2.6:0"), ("JawOpen", "0:0 .5:.3 1:1 2.1:1 2.6:0"), ("BrowInnerUp", "0:0 .8:.9 2.1:.9 2.7:0"), ("FaceAngleY", "0:0 .9:12 2.1:14 2.8:-3 3.6:0"), ("FaceAngleZ", "0:0 .9:7 2.1:9 3.2:0"), ("LeanInput", "0:0 .9:7 2.1:8 3:0"), ("ViewSquash", "0:0 .9:-.025 2.1:-.03 2.6:.015 3.1:0")]),
        ["打瞌睡"] = new([], [], 5.2, [("EyeOpen", "0:0 .8:-.45 1.6:-.7 2.4:-.9 3:-1 3.15:0 3.9:0 4:-1 4.1:0 4.25:-1 4.35:0 5.2:0"), ("FaceAngleY", "0:0 1:-4 2:-9 2.8:-15 3:-17 3.2:5 3.6:0 5.2:0"), ("FaceAngleZ", "0:0 1.5:5 3:9 3.25:0 5.2:0"), ("ViewSquash", "0:0 1.5:.01 3:.025 3.15:-.02 3.4:.01 3.7:0"), ("ViewY", "0:0 3.05:0 3.2:.02 3.45:0"), ("BrowInnerUp", "0:0 3.05:0 3.25:.9 4.3:.6 5.2:0"), ("FaceAngleX", "0:0 3.4:0 3.8:12 4.4:-10 5:0")], Pushed: true),
        ["歪头"] = new([], [("FaceAngleZ", 18), ("FaceAngleX", 5), ("BrowInnerUp", .6), ("ParamBrowLY", .4), ("ParamBrowRY", .4), ("ParamEyeBallY", .2)], 3.2, [("FaceAngleZ", "0:0 .25:4 .5:0"), ("EyeOpen", "0:0 1.1:0 1.2:-1 1.32:0 1.48:-1 1.6:0 3.2:0")], Pushed: true),
        ["发呆"] = new([], [], 4.6, [("EyeOpen", "0:0 .9:-.45 3.1:-.5 3.25:-1 3.4:0 4.6:0"), ("ParamEyeBallY", "0:0 .9:.45 3.1:.45 3.4:0"), ("ParamMouthOpenY", "0:0 1:.3 3.1:.3 3.3:0"), ("JawOpen", "0:0 1:.18 3.1:.18 3.3:0"), ("FaceAngleY", "0:0 1:-3 3.1:-4 3.35:3 3.8:0"), ("FaceAngleX", "0:0 3.2:0 3.35:-7 3.55:6 3.75:-3 4:0"), ("ViewSquash", "0:0 1:.015 3.1:.015 3.35:0")]),
        ["叹气"] = new([], [], 3.6, [("LeanInput", "0:0 .8:5 1.8:-7 2.8:-6 3.5:0"), ("FaceAngleY", "0:0 .8:7 1.8:-9 2.8:-8 3.5:0"), ("ViewSquash", "0:0 .8:-.02 1.2:-.02 1.9:.03 2.8:.025 3.5:0"), ("EyeOpen", "0:0 .8:-.2 1.8:-.6 2.8:-.55 3.5:0"), ("ParamMouthOpenY", "0:0 1.1:0 1.4:.3 2.2:.12 2.6:0"), ("MouthFunnel", "0:0 1.1:0 1.4:.9 2.2:.6 2.6:0"), ("BrowInnerUp", "0:0 1:.6 2.8:.6 3.5:0")]),
        // 鼠标在她身上停了约 2 秒时的小反应（本机加，2026-11-10）：幅度都很轻，对应 hover.txt 的台词。
        // 摸头结束（右键松开）后按摸了多久分三档：脸都会红一下；越摸得久越舍不得、越腻歪（本机加，2026-11-10，对应 stroke.txt 的「抚摸头1/2/3」）。
        ["摸后失落"] = new(["脸红"], [("BrowInnerUp", .7), ("MouthPressLipOpen", .3), ("ParamMouthForm", -.1), ("ParamEyeBallY", -.4), ("FaceAngleY", -5), ("FaceAngleZ", 4)], 3.4,
            [("FaceAngleZ", "0:0 .5:3 1.3:-2 2.1:3 3.4:0"), ("ViewSquash", "0:0 .3:.01 2.8:.01 3.4:0")]),
        ["摸后不舍"] = new(["脸红"], [("EyeSmile", .45), ("BrowInnerUp", .9), ("MouthPressLipOpen", .5), ("ParamMouthForm", .2), ("ParamEyeBallY", -.45), ("FaceAngleY", -8), ("FaceAngleZ", 7)], 4,
            [("LeanInput", "0:0 .5:-9 3.3:-8 4:0"), ("FaceAngleZ", "0:0 .7:3 1.5:-3 2.3:3 3.1:-2 4:0"), ("ViewSquash", "0:0 .4:.02 3.4:.02 4:0")]),
        ["摸后腻歪"] = new(["脸红", "爱心眼"], [("EyeSmile", .6), ("ParamMouthForm", 1), ("MouthPressLipOpen", .4), ("BrowInnerUp", .6), ("FaceAngleY", -6), ("FaceAngleZ", 9)], 4.8,
            [("LeanInput", "0:0 .5:-15 3.9:-14 4.8:0"), ("FaceAngleZ", "0:0 .8:3 1.6:-3 2.4:3 3.2:-3 4:2 4.8:0"), ("FaceAngleX", "0:0 .8:-4 1.8:4 2.8:-4 3.8:3 4.8:0"), ("ViewSquash", "0:0 .5:.03 4.1:.03 4.8:0")], ("默认手切举手", "举手切默认手")),
        // 摸头彩蛋（本机加，2026-11-11）：有效抚摸满 20 秒、融化后松手。脸红 + 手抬到胸口 + 快眨两下，6 秒。
        // 前倾：松手时按住 -17，表情里用动画加 0→+1 慢慢回到 -16，结束再交回给 clear 淡出，不会和旧值叠起来弹一下。
        ["摸后融化"] = new(["脸红"], [("EyeSmile", .55), ("ParamMouthForm", .9), ("MouthPressLipOpen", .35), ("BrowInnerUp", .5), ("FaceAngleY", -4), ("FaceAngleZ", 4), ("LeanInput", -17)], 6,
            [("LeanInput", "0:0 .8:1 4.8:1 6:0"), ("FaceAngleZ", "0:0 .8:5 2:-2 3.6:3 6:0"), ("FaceAngleX", "0:0 .7:-3 2.2:2 3.8:-2 6:0"),
             ("EyeOpen", "0:0 .2:-1 .28:-.2 .45:0 .95:-1 1.03:-.2 1.2:0 6:0"), ("ViewSquash", "0:0 .6:.02 5.2:.02 6:0")], ("默认手切举手", "举手切默认手")),
        ["悬停头"] = new([], [("FaceAngleZ", 11), ("FaceAngleX", -3), ("ParamEyeBallY", .3), ("BrowInnerUp", .4), ("EyeSmile", .35)], 2.8, [("FaceAngleZ", "0:0 .3:3 .8:-2 1.4:2 2.8:0")], Pushed: true),
        ["悬停脸"] = new(["脸红"], [("ParamMouthForm", .5), ("ParamEyeBallX", .4), ("ParamEyeBallY", -.2), ("FaceAngleY", -4)], 2.6, [("EyeOpen", "0:0 .15:-1 .3:0 1.1:0 1.25:-1 1.4:0 2.6:0"), ("ViewSquash", "0:0 .3:.012 2.2:.01 2.6:0")], Pushed: true),
        ["悬停手"] = new([], [("EyeSmile", .8), ("ParamMouthForm", 1), ("FaceAngleY", 4), ("FaceAngleZ", -4)], 2.8, [("FaceAngleZ", "0:0 .4:-5 1:-2 1.6:-5 2.8:0")], ("默认手切举手", "举手切默认手")),
        ["悬停裙"] = new([], [("ParamEyeBallX", .7), ("ParamEyeBallY", -.3), ("ParamMouthForm", -.3), ("ParamBrowLY", -.3), ("ParamBrowRY", -.3), ("FaceAngleX", -8)], 2.6, [("ViewSquash", "0:0 .12:.025 .5:.012 2.2:.01 2.6:0")], Pushed: true),
        ["悬停尾"] = new(["流冷汗"], [("ParamEyeBallX", .7), ("ParamEyeBallY", -.3), ("ParamMouthForm", -.4), ("FaceAngleX", -10), ("BrowInnerUp", .4)], 2.6, [("ViewSquash", "0:0 .08:.03 .4:.01 2.2:.01 2.6:0"), ("FaceAngleY", "0:0 .1:-5 .5:0 2.6:0")], Pushed: true),
        ["悬停身"] = new([], [("FaceAngleZ", 9), ("ParamEyeBallY", .3), ("BrowInnerUp", .3)], 2.6, [("FaceAngleX", "0:0 .5:3 1.1:-2 1.8:2 2.6:0")], Pushed: true),
        ["玩狗尾草"] = new([], [("ParamEyeBallX", -.7), ("ParamEyeBallY", -.3), ("EyeOpen", .75), ("EyeSmile", 1), ("ParamMouthForm", 1)], 5.4, [("FaceAngleX", "0:0 .5:0 1.2:-10 4.7:-10 5.4:0"), ("FaceAngleZ", "0:0 .5:0 1.2:8 1.9:3 2.6:9 3.3:3 4:9 4.7:6 5.4:0"), ("FaceAngleY", "0:0 .5:0 1.2:-5 4.7:-5 5.4:0")], ("狗尾草切换出来", "狗尾草切回默认手")),
    };
    // What she does by herself when left alone. A hand only comes up as part of one of these: a gesture on
    // its own for no reason looked odd (the owner, 2026-11-02). Late at night she gets sleepy.
    private static readonly string[] IdleMoods = ["张望", "哼歌", "害羞", "得意", "思考", "打哈欠", "打瞌睡", "歪头", "发呆", "叹气", "玩狗尾草"];
    private static readonly HashSet<string> Sleepy = ["打哈欠", "打瞌睡"];
    internal static int IdleWeight(string name, int hour) => Sleepy.Contains(name) && hour is >= 23 or < 6 ? 4 : 1;
    // 拎着时：嘴张成“啊”、眉毛抬起、圈圈眼；晃动由 PetWindow.Touch 实时驱动。落地见 DragEnd。
    private static readonly (string Id, double Value)[] Hanging = [("ParamMouthOpenY", .35), ("JawOpen", .2), ("MouthFunnel", .3), ("BrowInnerUp", .8)];

    private readonly Action<string> send;
    private readonly Func<IReadOnlyCollection<string>> worn;
    private readonly Random random;
    private readonly HashSet<string> activities = [];
    private readonly List<DateTime> clicks = [];
    private Mood? mood;
    private DateTime moodUntil, nextIdle;
    private string? lastIdle;
    private bool dragging, wearingGlasses;
    private DateTime motionUntil;
    private (string On, string Off)? moodGesture;
    internal string? Showing { get; private set; }
    // Her own body language (Body) under the reflexes: a hand she has up stays, and what a
    // reflex held and let go of is hers again once it ends.
    // 用户正按着右键抚摸、或她头晕着：待机的抽签和小动作都别插进来（本机加，2026-11-10）。
    internal bool Touching { get; set; }
    // 把正演着的小反应收掉（抚摸开始、头晕开始时用）。
    internal void Interrupt() => EndMood();
    internal static string HoverMood(string zone) => zone switch
    {
        "head" => "悬停头", "face" => "悬停脸", "hand" => "悬停手", "skirt" => "悬停裙", "tail" => "悬停尾", _ => "悬停身",
    };
    // 头部范围（占窗口的比例）：中心 (X, Y)、半宽 Rx、半高 Ry。右键摸头时那只手按它来倾斜，鼠标在这个椭圆里就算摸头。
    internal static (double X, double Y, double Rx, double Ry) HeadBox(string shape) => shape switch
    {
        "chibi" => (.5, .31, .26, .22),
        "full" => (.5, .19, .12, .085),
        _ => (.5, .225, .19, .095),
    };
    internal Func<bool>? HandBusy { get; init; }
    internal Action? Ended { get; init; }
    // 每次待机小动作开始时，把动作名交出去（PetWindow 用来触发自言自语）。
    internal Action<string>? IdleStarted { get; set; }
    // 用户写好的固定嘀咕文本（与待机小动作、小剧场三类平分概率，类内每条等权）。
    internal string[] TextIdleMoods { get; set; } = [];
    internal Action<string>? TextIdle { get; set; }
    // 待机小剧场（skits.txt）：几句话加动作、临时道具组成的一小段，和待机小动作、固定嘀咕三类平分概率（见 Tick）。
    internal string[] SkitIdleMoods { get; set; } = [];
    internal Action<string>? SkitIdle { get; set; }
    // 她现在是不是因为在读、看、写而戴着眼镜（和主人用 dress 戴的不同）。
    internal bool GlassesOnForWork => wearingGlasses;

    internal Behavior(Action<string> send, Func<IReadOnlyCollection<string>> worn, Random random)
    {
        this.send = send; this.worn = worn; this.random = random;
    }

    // A new renderer knows nothing of what the one before showed.
    internal void Resume(DateTime now, int intervalSeconds)
    {
        mood = null; Showing = null; moodGesture = null; dragging = false; wearingGlasses = false; motionUntil = default;
        nextIdle = now.AddSeconds(intervalSeconds);
        UpdateGlasses();
    }

    // side: which way she was pushed, 1 to the viewer's right. Clicked again in the middle of a movement the
    // face changes at once, but a second movement on top of the first would throw her about.
    internal void Show(string name, DateTime now, int side = 1, bool force = false)
    {
        EndMood();
        var next = Moods[name];
        foreach (var expression in next.Expressions) send($"exp {expression} 1");
        foreach (var (id, value) in next.Parameters)
            send(FormattableString.Invariant($"set {id} {(next.Pushed && side < 0 && Sided.Contains(id) ? 0 - value : value)}"));
        mood = next; Showing = name; moodUntil = now.AddSeconds(next.Seconds);
        if (next.Motion == null || (now < motionUntil && !force)) return;
        // Only what turns with the side flips: squashing, leaning, nodding and her eyelids stay as they are.
        foreach (var (id, keys) in next.Motion) send($"anim {id} {(next.Pushed && side < 0 && Sided.Contains(id) ? Mirrored(keys) : keys)}");
        motionUntil = now.AddSeconds(next.Motion.Max(track => Seconds(track.Keys)) * .6);
        if (next.Gesture is { } hand && HandBusy?.Invoke() != true) { moodGesture = hand; send($"motion {hand.On}"); }
    }

    // What turns with the side she was touched on, held as well as moved.
    private static readonly HashSet<string> Sided = ["FaceAngleX", "FaceAngleZ", "ParamEyeBallX", "MouthX"];

    private static double Seconds(string keys) => double.Parse(keys[(keys.LastIndexOf(' ') + 1)..].Split(':')[0], CultureInfo.InvariantCulture);
    private static string Mirrored(string keys) => string.Join(' ', keys.Split(' ').Select(key =>
    {
        var parts = key.Split(':');
        return FormattableString.Invariant($"{parts[0]}:{0 - double.Parse(parts[1], CultureInfo.InvariantCulture)}");
    }));

    private void EndMood()
    {
        if (mood == null) return;
        var keep = worn();
        // What she wears by the owner's choice stays on when a mood that also used it ends.
        foreach (var expression in mood.Expressions) if (!keep.Contains(expression)) send($"exp {expression} 0");
        foreach (var (id, _) in mood.Parameters) send($"clear {id}");
        if (moodGesture is { } hand) { send($"motion {hand.Off}"); moodGesture = null; }
        mood = null; Showing = null;
        Ended?.Invoke();
    }

    // What she wears changes what is under the cursor: with her tail put away the same spot holds only
    // the ends of her sash, and a long skirt covers her legs.
    internal static string Zone(string shape, double x, double y, IReadOnlyCollection<string> worn)
    {
        string zone = BareZone(shape, x, y);
        if (zone == "tail" && worn.Contains("尾巴开关")) return "skirt";
        if (zone == "body" && y > .5 && worn.Contains("裙子切换")) return "skirt";
        return zone;
    }

    private static string BareZone(string shape, double x, double y) => shape switch
    {
        "chibi" => y < .33 ? "head" : y < .53 ? (x is > .27 and < .73 ? "face" : "head")
            : y < .66 && x is < .4 or > .6 ? "hand" : y < .76 ? "skirt" : "body",
        "full" => y < .17 ? "head" : y < .26 ? (x is > .33 and < .67 ? "face" : "head")
            : y is > .46 and < .56 && x is < .26 or > .74 ? "hand" : y is > .42 and < .57 ? "skirt"
            : y >= .57 && x is < .36 or > .64 ? "tail" : "body",
        _ => SmallZone(x, (y * 620 - 60) / 560),
    };

    // Measured on her without the headroom the window keeps above her for jumping.
    private static string SmallZone(double x, double y) => y < .125 ? "head" : y < .215 ? (x is > .3 and < .7 ? "face" : "head")
        : y is > .5 and < .6 && x is < .28 or > .74 ? "hand" : y is > .43 and < .62 ? "skirt"
        : y >= .62 && x is < .36 or > .62 ? "tail" : "body";

    private string ClickMood(string zone) => zone switch
    {
        "head" => "摸头", "face" => "戳脸", "hand" => "开心", "skirt" => Smitten?.Invoke() == true ? SmittenMoods[random.Next(SmittenMoods.Length)] : "护裙", "tail" => "震惊", _ => "心虚",
    };

    // 亲密度到了最高一级：戳裙子时换成这三种之一，概率平分（本机加，2026-11-08）。
    internal static readonly string[] SmittenMoods = ["娇羞", "娇羞前倾", "娇羞抚胸"];
    internal Func<bool>? Smitten { get; set; }
    // 专注模式（番茄钟）：待机只做安静的小动作，不嘀咕、不演小剧场、不报时（本机加，2026-11-08）。
    internal bool Quiet { get; set; }
    private static readonly string[] QuietMoods = ["思考", "发呆", "歪头"];

    internal void Click(string zone, DateTime now, int side = 1)
    {
        clicks.RemoveAll(time => now - time > ClickSpan);
        clicks.Add(now);
        if (clicks.Count >= ClicksForBasin) { clicks.Clear(); Show("挨打", now, force: true); return; }
        Show(ClickMood(zone), now, side);
    }

    internal void DragStart()
    {
        EndMood();
        dragging = true;
        send("exp 圈圈眼 1");
        foreach (var (id, value) in Hanging) send(FormattableString.Invariant($"set {id} {value}"));
        // 拎着时的晃动不再放固定循环，交给 PetWindow.Touch 按拖动速度实时驱动（惯性、头晕）。
    }

    // The dizzy eyes stay on through the landing and go with a blink.
    // dizzy：转晕了，不播“落地”，头晕那一段由 PetWindow.Touch 接手（圈圈眼也由它收）。
    internal void DragEnd(DateTime now, bool dizzy = false)
    {
        if (!dragging) return;
        dragging = false;
        send("animstop");
        foreach (var (id, _) in Hanging) send($"clear {id}");
        if (dizzy) { send("exp 圈圈眼 1"); return; }
        Show("落地", now, force: true);
    }

    // While she is reading, watching or writing something (any kind the agent names) she wears her glasses,
    // however long it takes, until it says she is done.
    internal void Activity(string kind, bool active)
    {
        if (active) activities.Add(kind); else activities.Remove(kind);
        UpdateGlasses();
    }

    internal void ClearActivities()
    {
        if (activities.Count == 0) return;
        activities.Clear();
        UpdateGlasses();
    }

    // Glasses worn by choice (dress) count: she does not stack a second pair, and those stay on after her work ends.
    internal void UpdateGlasses()
    {
        var keep = worn();
        bool wanted = activities.Count > 0 && !keep.Contains(Glasses) && !keep.Contains(Monocle);
        if (wanted == wearingGlasses) return;
        wearingGlasses = wanted;
        if (wanted || !keep.Contains(Glasses)) send($"exp {Glasses} {(wanted ? 1 : 0)}");
    }

    // 台词和小剧场的“条件”（本机加，2026-11-06，见 Conditions.cs）：(名字, 本地时间) → 权重，0 就是这次不抽。不设就一律 1。
    internal Func<string, DateTime, int>? TextWeight { get; set; }
    internal Func<string, DateTime, int>? SkitWeightOf { get; set; }
    // 整点报时：这个钟点该报的几条台词（本地时间 → 台词，没有就空）；ChimeBlocked 为真时先不报、下一帧再看。
    internal Func<DateTime, string[]>? ChimeTexts { get; set; }
    internal Func<bool>? ChimeBlocked { get; set; }
    private (DateTime Day, int Hour)? lastChime;
    // 钟点刚过几分钟内才算“整点”（她那会儿在说话、被摸，就等到闲下来；过了这几分钟就算错过，不补报）。
    internal const int ChimeMinutes = 3;
    // 小剧场：上次演完的时间；太久没演就下一次直接给它（2026-11-10 用户反馈「有时不会触发」）
    private DateTime lastSkitAt = DateTime.MinValue;
    internal void SkitPlayed(DateTime now) => lastSkitAt = now;
    internal void SkitMissed(DateTime now) { if (now > lastSkitAt) nextIdle = now.AddSeconds(45); }

    internal void Tick(DateTime now, bool speaking, int intervalSeconds)
    {
        if (mood != null && now >= moodUntil) EndMood();
        if (speaking || dragging || Touching || mood != null) return;
        var local = now.ToLocalTime();
        // 整点报时：每个钟点最多一次，不受“隔多久做一个小动作”的限制。
        if (!Quiet && local.Minute < ChimeMinutes && lastChime != (local.Date, local.Hour) && ChimeTexts is { } chimes && chimes(local) is { Length: > 0 } hourly && ChimeBlocked?.Invoke() != true)
        {
            lastChime = (local.Date, local.Hour);
            nextIdle = now.AddSeconds(intervalSeconds);
            TextIdle?.Invoke(hourly[random.Next(hourly.Length)]);
            return;
        }
        if (now < nextIdle) return;
        nextIdle = now.AddSeconds(intervalSeconds);
        // Never the same one twice in a row.
        int hour = local.Hour;
        var actions = (Quiet ? QuietMoods : IdleMoods).Where(name => name != lastIdle).ToArray();
        // 三类（待机小动作 / 固定嘀咕 / 小剧场）先平分：从有内容的几类里等概率挑一类，再在这一类里挑一条。
        // （本机改，2026-11-06；原来是每条权重 1 混在一起抽，条数多的那类占大头。）
        // 类内：小动作夜里犯困的权重高；嘀咕、小剧场按条件加权（不符合条件的这次不抽）；类里一条都抽不了就不算这一类。
        var texts = Quiet ? [] : Weighted(TextIdleMoods, TextWeight, local);
        var plays = Quiet ? [] : Weighted(SkitIdleMoods, SkitWeightOf, local);
        // 超过 12 分钟没演小剧场：这一轮直接演，不再和别的待机动作抢（2026-11-10）
        if (!Quiet && plays.Length > 0 && now - lastSkitAt > TimeSpan.FromMinutes(12))
        {
            SkitIdle?.Invoke(PickWeighted(plays));
            return;
        }
        var kinds = new List<int>();
        if (actions.Length > 0) kinds.Add(0);
        if (texts.Length > 0) kinds.Add(1);
        if (plays.Length > 0) kinds.Add(2);
        if (kinds.Count == 0) return;
        switch (kinds[random.Next(kinds.Count)])
        {
            case 0:
                int pick = random.Next(actions.Sum(name => IdleWeight(name, hour)));
                foreach (var name in actions)
                {
                    pick -= IdleWeight(name, hour);
                    if (pick >= 0) continue;
                    lastIdle = name;
                    Show(name, now, random.Next(2) == 0 ? 1 : -1);
                    IdleStarted?.Invoke(name);
                    return;
                }
                return;
            case 1:
                TextIdle?.Invoke(PickWeighted(texts));
                return;
            default:
                SkitIdle?.Invoke(PickWeighted(plays));
                return;
        }
    }

    private static (string Name, int Weight)[] Weighted(string[] names, Func<string, DateTime, int>? weight, DateTime local) =>
        names.Select(name => (Name: name, Weight: weight?.Invoke(name, local) ?? 1)).Where(item => item.Weight > 0).ToArray();

    private string PickWeighted((string Name, int Weight)[] items)
    {
        int pick = random.Next(items.Sum(item => item.Weight));
        foreach (var (name, weight) in items)
        {
            pick -= weight;
            if (pick < 0) return name;
        }
        return items[^1].Name;
    }

    // How open her mouth is, one value per step of a 16-bit PCM wav; null for anything else.
    internal static float[]? VoiceLevels(byte[] wav, int stepMilliseconds)
    {
        if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return null;
        int channels = 0, rate = 0, bits = 0;
        for (int at = 12; at + 8 <= wav.Length;)
        {
            string chunk = Encoding.ASCII.GetString(wav, at, 4);
            int size = BitConverter.ToInt32(wav, at + 4), start = at + 8;
            if (size < 0) return null;
            if (chunk == "fmt " && start + 16 <= wav.Length)
            {
                channels = BitConverter.ToInt16(wav, start + 2); rate = BitConverter.ToInt32(wav, start + 4); bits = BitConverter.ToInt16(wav, start + 14);
            }
            else if (chunk == "data")
            {
                if (bits != 16 || channels < 1 || rate < 1) return null;
                return Levels(wav, start, Math.Min(size, wav.Length - start), channels, rate, stepMilliseconds);
            }
            at = start + size + (size & 1);
        }
        return null;
    }

    private static float[] Levels(byte[] wav, int start, int size, int channels, int rate, int stepMilliseconds)
    {
        int window = Math.Max(1, rate * stepMilliseconds / 1000) * channels * 2;
        var loudness = new float[size / window];
        for (int step = 0; step < loudness.Length; step++)
        {
            double sum = 0;
            for (int at = start + step * window; at + 1 < start + (step + 1) * window; at += 2)
            {
                double sample = BitConverter.ToInt16(wav, at) / 32768.0;
                sum += sample * sample;
            }
            loudness[step] = (float)Math.Sqrt(sum / (window / 2));
        }
        // Against the loud parts of this recording, so a quiet voice opens her mouth as far as a loud one.
        float peak = loudness.Length == 0 ? 0 : loudness.Order().ElementAt(loudness.Length * 95 / 100);
        if (peak <= 0) return new float[loudness.Length];
        return loudness.Select(level => level / peak < .12f ? 0f : (float)Math.Min(1, Math.Sqrt(level / peak))).ToArray();
    }
}

