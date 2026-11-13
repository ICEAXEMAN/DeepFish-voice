using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Animation;

namespace FishPet;

// 到点叫人接到她身上：时间表和台词规则在 Alarms.cs。
internal sealed partial class PetWindow
{
    private DateTime alarmLast = DateTime.Now, alarmNextLook;

    // 每帧看一眼（实际一秒一次）：到点就演。
    private void AlarmTick()
    {
        if (test || exiting) return;
        var utc = DateTime.UtcNow;
        if (utc < alarmNextLook) return;
        alarmNextLook = utc.AddSeconds(1);
        var now = DateTime.Now;
        var due = Alarms.Due(alarmLast, now, settings.CoaxSleep, settings.CoaxLunch);
        alarmLast = now;
        foreach (var slot in due) _ = Ring(slot);
    }

    // 到点（用户 2026-11-13 改）：
    //   · 她收着待命（含正在收起）就不提醒，不会再自己跳出来；
    //   · 她在桌面上：先快速鞠一下躬淡出退场，再在屏幕正中快速淡入出场，然后才开口提醒；
    //     不再是突然消失、突然在中间冒出来。
    private bool alarmBusy;

    private async Task Ring(Alarms.Slot slot)
    {
        if (alarmBusy) { Places.Log($"到点：{slot.Name}（上一次提醒还没演完，这次跳过）"); return; }
        if (standby || hiding)
        {
            Places.Log($"到点：{slot.Name}（她收着待命，不提醒）"); // 留个底，方便查“昨晚怎么没哄”
            return;
        }
        alarmBusy = true;
        try
        {
            var text = AlarmLine(slot.Name);
            Places.Log($"到点：{slot.Name}");
            YieldMutter();
            bool hadPlace = coaxScale != 0; // 之前已经有人记过位置（别的一层还在演）就别替人家清掉
            RememberPlace();
            // 她正说着话（比如在回你）先让她说完，最多等 8 秒；等的时候被收起/退出就不演了
            for (int wait = 0; wait < 40 && speech.IsSaying && !standby && !hiding && !exiting; wait++) await Task.Delay(200);
            if (!await QuickLeave())
            {
                // 被打断了，没演成，也不会有人来归位：把刚记下的位置忘掉，不然下次闹钟会跳过记录、
                // 归位时把你中间改过的体型、大小改回旧的
                if (!hadPlace) ForgetPlace();
                return;
            }
            if (slot.Stage == 4)
            {
                coaxStage4Text = text;
                RunStage(4, EnterFadeMs); // 第四层自己会逐字说
                return;
            }
            coaxStage4Text = null;
            RunStage(slot.Stage, EnterFadeMs);
            await Task.Delay(EnterFadeMs + 180); // 等她在正中站稳了再开口
            var bubbles = text.Split('／', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var bubble in bubbles)
                _ = InTurn(() => { if (!exiting) SayLine(bubble, null); return Task.CompletedTask; }, CancellationToken.None);
        }
        finally { alarmBusy = false; }
    }

    private const int EnterFadeMs = 300;

    // 快速退场：低头鞠一躬（约 0.9 秒），中途淡出。动作是自己的几条短轨道，不借「告别」那套心情
    // （它要演 3 秒，会拖到她在正中重新出场之后）。返回 false 是中途被收起/退出了，这次不演。
    private async Task<bool> QuickLeave()
    {
        if (standby || hiding || exiting || test) return false;
        Send("anim LeanInput 0:0 .25:-12 .6:-12 .9:0");
        Send("anim FaceAngleY 0:0 .25:-19 .6:-19 .9:0");
        Send("anim ViewSquash 0:0 .25:.04 .6:.04 .9:0");
        Send("anim EyeOpen 0:0 .2:-1 .65:-1 .9:0");
        await Task.Delay(450);
        if (standby || hiding || exiting) return false;
        image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        await Task.Delay(500); // 退场的轨道在这时候都走完了，换体型、大小、位置不会被半途的鞠躬带着走
        return !(standby || hiding || exiting); // 这时被收起/退出：它们自己管淡出，这里不再动她
    }

    private string AlarmLine(string name)
    {
        try
        {
            var file = Path.Combine(Places.Root, "alarms.txt");
            if (File.Exists(file) && Alarms.Parse(File.ReadAllLines(file)).TryGetValue(name, out var lines) && lines.Count > 0)
                return lines[random.Next(lines.Count)];
        }
        catch (Exception error) { Places.Log("alarms.txt 读不出来：" + error.Message); }
        return Alarms.Fallback[name];
    }
}
