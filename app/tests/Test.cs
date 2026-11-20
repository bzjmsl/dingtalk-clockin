using System;
using System.IO;
using DingTalkClockIn;

internal class Test
{
    private static int _pass;
    private static int _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine("PASS  " + name);
        }
        else
        {
            _fail++;
            Console.WriteLine("FAIL  " + name + "  " + (detail ?? ""));
        }
    }

    private static void Main()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dtc_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var paths = new AppPaths(dir);
        var log = new Logger(paths.LogDir, 30);
        var cfg = Config.Load(paths.ConfigFile);

        Check("默认模式是极速打卡", cfg.S("mode", "") == "wake", cfg.S("mode", ""));
        Check("默认上班窗口 07:50", cfg.S("windows.morning.start", "") == "07:50", cfg.S("windows.morning.start", ""));
        Check("默认下班窗口 17:40", cfg.S("windows.evening.start", "") == "17:40", "");
        Check("PIN 为空时不报错", cfg.Pin == "", cfg.Pin);

        cfg.Pin = "2468";
        Check("PIN 加密后可还原", cfg.Pin == "2468", cfg.Pin);
        Check("PIN 不落明文", cfg.S("pin", "").StartsWith("dpapi:"), cfg.S("pin", ""));
        Check("默认保存截图", cfg.B("captureScreenshots", false), "");
        Check("默认开启打卡前检查", cfg.B("preflightCheck", false), "");
        Check("默认显示托盘通知", cfg.B("notifications", false), "");
        Check("默认开启电源监控", cfg.B("powerMonitor", false), "");
        Check("默认执行后锁屏", cfg.B("lockAfterRun", false), "");
        Check("默认打卡后清理后台", cfg.B("cleanAfterRun", false), "");
        Check("默认清理延迟 60 秒", cfg.I("cleanDelaySec", 0) == 60, cfg.I("cleanDelaySec", 0).ToString());
        Check("默认采样间隔 60 秒", cfg.I("powerMonitorSec", 0) == 60, cfg.I("powerMonitorSec", 0).ToString());

        cfg.Set("captureScreenshots", false);
        Check("截图开关可关闭", !cfg.B("captureScreenshots", true), "");
        cfg.Set("captureScreenshots", true);

        var hol = new HolidayService(paths, log);
        hol.Load();
        string reason;

        Check("周四算工作日", hol.IsWorkday(new DateTime(2026, 11, 12), out reason), reason);
        Check("周六算休息日", !hol.IsWorkday(new DateTime(2026, 11, 14), out reason), reason);

        string msg;
        hol.AddManual("2026-11-12", "holiday", "测试假日", out msg);
        Check("手动节假日覆盖工作日", !hol.IsWorkday(new DateTime(2026, 11, 12), out reason), reason);

        hol.RemoveManual("2026-11-12");
        Check("删除后恢复工作日", hol.IsWorkday(new DateTime(2026, 11, 12), out reason), reason);

        hol.AddManual("2026-11-14", "workday", "调休补班", out msg);
        Check("调休补班把周六变成工作日", hol.IsWorkday(new DateTime(2026, 11, 14), out reason), reason);

        var off1 = Scheduler.Offset(new DateTime(2026, 11, 12), "morning", 600);
        var off2 = Scheduler.Offset(new DateTime(2026, 11, 12), "morning", 600);
        var off3 = Scheduler.Offset(new DateTime(2026, 11, 12), "evening", 600);
        var off4 = Scheduler.Offset(new DateTime(2026, 11, 13), "morning", 600);
        Check("随机偏移可复现", off1 == off2, off1 + " vs " + off2);
        Check("随机偏移在窗口内", off1 >= 0 && off1 < 600, off1.ToString());
        Check("早晚时段偏移不同", off1 != off3, off1 + " vs " + off3);
        Check("不同日期偏移不同", off1 != off4, off1 + " vs " + off4);

        var state = AppState.Load(paths.StateFile);
        var sch = new Scheduler(cfg, hol, state);

        sch.Recompute(new DateTime(2026, 11, 12, 7, 45, 0), false);
        Check("窗口前能算出上班任务", sch.NextSession == "morning", sch.NextSession ?? "null");
        var first = sch.NextRun;
        sch.Recompute(new DateTime(2026, 11, 12, 7, 45, 5), false);
        Check("重复计算目标时刻不变", sch.NextRun == first,
            (first.HasValue ? first.Value.ToString("HH:mm:ss") : "null") + " vs " +
            (sch.NextRun.HasValue ? sch.NextRun.Value.ToString("HH:mm:ss") : "null"));

        state.MarkRun(new DateTime(2026, 11, 12), "morning", new DateTime(2026, 11, 12, 7, 56, 0));
        sch.Recompute(new DateTime(2026, 11, 12, 7, 58, 0), false);
        Check("已执行后跳过上班", sch.NextSession == "evening", sch.NextSession ?? "null");

        sch.Recompute(new DateTime(2026, 11, 12, 18, 0, 0), false);
        Check("窗口全过后不再安排", sch.NextRun == null, sch.NextRun.HasValue ? sch.NextRun.ToString() : "null");

        sch.Recompute(new DateTime(2026, 11, 14, 7, 45, 0), false);
        Check("补班日仍会安排", sch.NextRun.HasValue, "null");

        sch.Recompute(new DateTime(2026, 11, 15, 7, 45, 0), false);
        Check("普通周日不安排", sch.NextRun == null, sch.NextRun.HasValue ? sch.NextRun.ToString() : "null");

        sch.Recompute(new DateTime(2026, 11, 12, 7, 45, 0), true);
        Check("暂停时不安排", sch.NextRun == null, "null");

        var csv = Path.Combine(dir, "t.csv");
        File.WriteAllText(csv, "2026-12-25,holiday,测试\r\n2026-12-26,workday,补班\r\n");
        string im;
        hol.ImportCsv(csv, out im);
        Check("CSV 导入节假日", !hol.IsWorkday(new DateTime(2026, 12, 25), out reason), reason);
        Check("CSV 导入补班", hol.IsWorkday(new DateTime(2026, 12, 26), out reason), reason);

        var json = Path.Combine(paths.CacheDir, "holiday-2030.json");
        File.WriteAllText(json, "[{\"date\":\"2030-01-02\",\"type\":\"holiday\",\"name\":\"测试\"}]");
        hol.Load();
        Check("接口缓存参与判定", !hol.IsWorkday(new DateTime(2030, 1, 2), out reason), reason);

        state.SetLastResult(DateTime.Now, "测试结果", @"C:\shots\a.png", true);
        state.ClearShotIf(@"C:\shots\a.png");
        var lr2 = state.GetLastResult();
        Check("删除截图后清空 lastResult.shot",
            lr2 != null && Convert.ToString(lr2["shot"]) == "",
            lr2 == null ? "null" : Convert.ToString(lr2["shot"]));

        Console.WriteLine();
        Console.WriteLine("PASS=" + _pass + " FAIL=" + _fail);
        Environment.Exit(_fail == 0 ? 0 : 1);
    }
}
