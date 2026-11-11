using System.Collections.Generic;
using System.Linq;

namespace FishTray;

// 面板里“表情 / 动作 / 小剧场”三大块的内容清单：这套 Live2D 里有的、桌宠做得出来的都在这里。
// 点一下做什么由 Kind 决定（MainWindow.Run）：
//   Cue        一个或几个动作词（和对话里〔〕里写的一样），做出来保持到点“恢复平常”
//   Face       脸上的开关式表情（脸红、流泪……）：再点一下关掉，可叠加；State 是桌宠 get_state 的 overlays 里的名字
//   RawExp     模型自带、词表里没有的原版表情开关（加载、前倾满、竖食指开）；State 在 raw 里
//   Idle       待机小动作 / 被碰时的反应（桌宠 Behavior.Moods 里的名字）
//   RawMotion  模型自带的动作文件（Animation\*.motion3.json）
//   Skit       小剧场（skits.txt 里的名字）；SkitRandom 随机来一个；Demo 演全部待机小动作
internal enum Kind { Cue, Pose, Face, RawExp, Idle, RawMotion, Skit, SkitRandom, Demo }

internal sealed record Item(string Label, Kind Kind, string Arg = "", string Tip = "", string State = "")
{
    // 收藏用的唯一标识
    internal string Key => Kind + ":" + Label;
}

internal sealed record Group(string Title, Item[] Items, string Hint = "");

internal sealed record Section(string Key, string Title, string Blurb, Group[] Groups)
{
    internal int Count => Groups.Sum(group => group.Items.Length);
}

internal static class Catalog
{
    private static Item W(string word, string tip = "") => new(word, Kind.Cue, word, tip);
    private static Item Combo(string label, string cue, string tip = "") => new(label, Kind.Cue, cue, tip.Length > 0 ? tip : cue);
    private static Item Face(string label, string cue, string overlay, string tip) => new(label, Kind.Face, cue, tip, overlay);
    private static Item Idle(string name, string tip = "") => new(name, Kind.Idle, name, tip);
    private static Item Pose(string word, string tip = "") => new(word, Kind.Pose, word, tip);
    private static Item PoseCombo(string label, string cue, string tip = "") => new(label, Kind.Pose, cue, tip.Length > 0 ? tip : cue);
    private static Item[] Words(params string[] words) => words.Select(word => W(word)).ToArray();

    // 桌宠 Behavior.Moods 里的 23 个：待机时自己会做的 11 个，和被碰、出场、收起时的反应 12 个。
    internal static readonly string[] IdleNames = ["张望", "哼歌", "害羞", "得意", "思考", "打哈欠", "打瞌睡", "歪头", "发呆", "叹气", "玩狗尾草"];
    internal static readonly string[] ReactionNames = ["开心", "生气", "委屈", "心虚", "震惊", "摸头", "戳脸", "挨打", "护裙", "落地", "问候", "告别"];

    internal static Section[] Build(IEnumerable<string> skitNames)
    {
        var skits = skitNames.ToArray();
        var expressions = new Section("expr", "表情",
            "模型自带 23 个表情文件：8 个脸上表情在这里，4 件衣服、4 个配饰、3 个体型在上面的“衣服和配饰”“体型”里，其余在下面的“原版”。",
        [
            new("脸上（点开、再点关，可叠加）",
            [
                Face("＞＜", "＞＜", "＞＜", "使劲闭眼"), Face("爱心眼", "爱心眼", "爱心眼", "想表达爱意时用"), Face("黑脸", "黑脸", "黑脸", "脸上一片黑线"),
                Face("脸红", "脸红", "脸红", "害羞脸红"), Face("流泪", "眼泪", "流泪", "眼里含泪"), Face("流冷汗", "冷汗", "流冷汗", "冷汗直流"),
                Face("圈圈眼", "圈圈眼", "圈圈眼", "晕乎乎"), Face("屑屑眼", "屑屑眼", "屑屑眼", "得意的坏笑眼，自带一张小嘴"),
            ]),
            new("眼睛", Words("睁大", "半睁", "闭眼", "眯眼笑", "笑眼", "斜眼", "眨单眼")),
            new("眉毛", Words("挑眉", "皱眉", "八字眉")),
            new("嘴", Words("微笑", "大笑", "坏笑", "瘪嘴", "张嘴", "嘟嘴", "鼓腮", "抿嘴", "吐舌")),
            new("组合神情（几个词一起做，约 3.5 秒自己恢复）",
            [
                Pose("高兴"), Pose("偷笑"), Pose("傻眼"), Pose("无语"), Pose("得瑟"), Pose("抓狂"), Pose("犯困"), Pose("感动"), Pose("害怕"), Pose("鬼点子"), Pose("撒娇"), Pose("得救"), Pose("警觉"),
                PoseCombo("得意", "屑屑眼 晃头"), PoseCombo("害羞", "低头 脸红 瞟一眼"), PoseCombo("心虚", "瞟一眼 冷汗 抿嘴"), PoseCombo("委屈", "瘪嘴 八字眉 低头"), PoseCombo("生气", "皱眉 黑脸 鼓腮"),
            ]),
            new("原版（词表里没有对应的）",
            [
                new("加载", Kind.RawExp, "加载", "脸上转圈的“加载中”小图标", "加载"),
                new("前倾·满", Kind.RawExp, "前倾", "原版“前倾”表情：身子大幅前倾（比〔前倾〕更夸张）", "前倾"),
                new("竖食指开", Kind.RawExp, "竖食指开", "原版“竖食指开”表情", "竖食指开"),
            ], "竖食指关、默认体型 只是“关闭键”，单独点没有效果，所以不放这里。"),
        ]);

        var motions = new Section("move", "动作",
            "头、身子、手、小动作、舞步，以及待机时自己会做的 11 个小动作、被碰时的 12 个反应、1 个原版动作文件。",
        [
            new("头和身体（保持住，点“正视”“站直”收回）", Words("歪头", "转头", "低头", "抬头", "正视", "前倾", "后仰", "站直", "看过来", "看左边", "看右边", "看上面", "看下面")),
            new("小动作（一下就过去）", Words("点头", "摇头", "眨眼", "瞟一眼", "挑一下眉", "歪一下头", "晃头", "缩一下", "叹气", "吓一跳", "单眨眼")),
            new("手", Words("抚胸", "竖手指", "狗尾草", "放下手")),
            new("舞步（小剧场“跳舞”里用的）", [Combo("摇摆一", "摇摆一", "左右左右左右（121212），约 7.7 秒"), Combo("摇摆二", "摇摆二", "左左右右左左右右（11221122），约 3.7 秒"),
                Combo("慢摇", "慢摇", "越摇越慢越小，渐渐停下"), Combo("左右张望", "左右张望", "眼睛和头左右看一下")]),
            new("待机小动作（她没人理时自己会做的）", IdleNames.Select(name => Idle(name)).Append(new Item("全部演一遍", Kind.Demo, "", "依次演完上面 11 个，每个约 4 秒")).ToArray()),
            new("被碰、出场、收起时的反应", ReactionNames.Select(name => Idle(name)).ToArray()),
            new("模型原版动作文件", [new("卡机", Kind.RawMotion, "脱捕", "原版“脱捕”：圈圈眼、头顶转圈加载、拿着狗尾草晃，约 13 秒")],
                "其余 7 个原版动作：IDLE 是一直在放的呼吸待机，不用点；举手、竖手指、狗尾草各一对“出来/收回”，就是上面“手”里的 4 个词。"),
        ]);

        var theatre = new Section("skit", "小剧场",
            "skits.txt 里写好的整段表演（台词、动作、临时道具），她没人理时也会自己演。",
        [
            new("点一个演出来", skits.Select(name => new Item(name, Kind.Skit, name, "演小剧场：" + name)).ToArray()),
        ]);

        return [expressions, motions, theatre];
    }
}
