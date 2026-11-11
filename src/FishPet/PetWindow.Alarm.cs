using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        foreach (var slot in due) Ring(slot);
    }

    private void Ring(Alarms.Slot slot)
    {
        var text = AlarmLine(slot.Name);
        Places.Log($"到点：{slot.Name}"); // 留个底，方便查“昨晚哄了没有”
        if (Wake()) coaxWoke = true; // 收着时被叫出来：演完再自己收回去
        YieldMutter();
        RememberPlace();
        if (slot.Stage == 4)
        {
            coaxStage4Text = text;
            RunStage(4); // 第四层自己会逐字说
            return;
        }
        coaxStage4Text = null;
        RunStage(slot.Stage);
        var bubbles = text.Split('／', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var bubble in bubbles)
            _ = InTurn(() => { if (!exiting) SayLine(bubble, null); return Task.CompletedTask; }, CancellationToken.None);
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
