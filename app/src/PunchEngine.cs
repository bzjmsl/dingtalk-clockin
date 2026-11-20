using System;
using System.Drawing;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;

namespace DingTalkClockIn
{
    public class RunResult
    {
        public bool Ok;
        public string Message;
        public string Shot;
    }

    public class PunchEngine
    {
        private readonly Config _cfg;
        private readonly Adb _adb;
        private readonly Logger _log;
        private readonly AppPaths _paths;
        private string _stayOnBefore;
        private readonly List<string> _tempShots = new List<string>();

        private bool ScreenshotsEnabled
        {
            get { return _cfg.B("captureScreenshots", true); }
        }

        public PunchEngine(Config cfg, Adb adb, Logger log, AppPaths paths)
        {
            _cfg = cfg;
            _adb = adb;
            _log = log;
            _paths = paths;
        }

        public RunResult Run(string mode, bool dryRun)
        {
            try
            {
                _log.Info("开始执行（模式=" + mode + (dryRun ? "，干跑" : "") + "）");

                if (!EnsureDevice())
                {
                    string serial;
                    var state = _adb.DeviceState(out serial);
                    if (state == "unauthorized")
                        return Fail("手机未授权 USB 调试，请在手机上点「允许」");
                    if (state == "offline")
                        return Fail("手机处于 offline 状态，请拔插数据线后重试");
                    return Fail("手机未连接，请检查数据线和 USB 调试开关");
                }
                if (!EnsureAwake()) return Fail("无法唤醒屏幕");
                if (!EnsureUnlocked()) return Fail("解锁失败，请检查锁屏密码设置");
                _log.Info("屏幕已解锁");

                var preflight = Preflight();
                if (preflight != null) return preflight;

                var wakeMode = mode == "wake";
                var openApp = !wakeMode || _cfg.B("openDingTalkInWakeMode", true);
                if (openApp && !OpenDingTalk()) return Fail("钉钉没有打开");

                if (wakeMode)
                {
                    var hold = _cfg.I("wakeHoldSec", 60);
                    string shot = null;
                    if (ScreenshotsEnabled) shot = Capture("woke");
                    else _log.Info("截图已关闭，本次不保存截图");
                    _log.Info("保持亮屏 " + hold + " 秒，等待极速打卡生效");
                    Thread.Sleep(hold * 1000);
                    return new RunResult
                    {
                        Ok = true,
                        Message = "已唤醒并打开钉钉，保持亮屏 " + hold + " 秒",
                        Shot = shot
                    };
                }

                return RunFull(dryRun);
            }
            catch (Exception ex)
            {
                _log.Error("执行异常：" + ex.Message);
                return Fail(ex.Message);
            }
            finally
            {
                FinishSession(dryRun);
            }
        }

        /// <summary>
        /// 收尾：恢复"充电常亮"设置；只要屏幕还亮着就回到桌面并锁屏，
        /// 否则钉钉会一直留在前台把屏幕占住（极速打卡也会在下一个时段自己触发）。
        /// 干跑不锁屏，避免打断正在操作手机的人。
        /// </summary>
        private void FinishSession(bool dryRun)
        {
            CleanupTempShots();
            try
            {
                RestoreStayOn();

                if (dryRun)
                {
                    _log.Info("干跑结束，不锁屏");
                    return;
                }

                if (!_cfg.B("lockAfterRun", true))
                {
                    _log.Info("自动锁屏已关闭，屏幕保持原样");
                    return;
                }

                // 无论手机是不是程序唤醒的，只要现在亮着就回桌面并熄屏，
                // 否则钉钉会一直留在前台把屏幕占着。
                if (!IsAwake())
                {
                    _log.Info("屏幕本来就已熄灭");
                    return;
                }

                _adb.Shell("input keyevent 3", 10000);
                Thread.Sleep(600);
                _adb.Shell("input keyevent 26", 10000);
                _log.Info("已回桌面并熄屏");
            }
            catch { }
        }

        private string PendingFile
        {
            get { return Path.Combine(_paths.DataDir, "stay_on.pending"); }
        }

        /// <summary>只接受 "0".."7"；adb 掉线时返回的是错误文本，不能直接写回设置里。</summary>
        private static bool IsStayOnValue(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length == 1 && value[0] >= '0' && value[0] <= '7';
        }

        private void SavePending(string value)
        {
            try
            {
                File.WriteAllText(PendingFile, IsStayOnValue(value) ? value : "null");
            }
            catch { }
        }

        private void ClearPending()
        {
            try { if (File.Exists(PendingFile)) File.Delete(PendingFile); } catch { }
        }

        public bool HasPendingStayOn()
        {
            try { return File.Exists(PendingFile); } catch { return false; }
        }

        /// <summary>读 pending：null = 没有标记；"null" = 原值本来就是未设置；"0".."7" = 原值。</summary>
        private string ReadPending()
        {
            try
            {
                if (!File.Exists(PendingFile)) return null;
                var value = File.ReadAllText(PendingFile).Trim();
                if (value == "null") return "null";
                return IsStayOnValue(value) ? value : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 写回"充电常亮"设置并读回校验。adb 丢设备时命令只是打印错误、不会抛异常，
        /// 所以必须读回来确认，否则会出现"以为恢复了、其实还常亮"。
        /// </summary>
        private bool ApplyStayOn(string value)
        {
            try
            {
                var explicitValue = IsStayOnValue(value);
                if (explicitValue)
                    _adb.Shell("settings put global stay_on_while_plugged_in " + value, 10000);
                else
                    _adb.Shell("svc power stayon false", 10000);

                var want = explicitValue ? value : "0";
                var now = _adb.Shell("settings get global stay_on_while_plugged_in", 10000).Trim();
                return now == want;
            }
            catch { return false; }
        }

        private void RestoreStayOn()
        {
            // 本次根本没走到"打开常亮"（比如手机没连上），没什么可恢复的。
            if (!IsStayOnValue(_stayOnBefore) && !HasPendingStayOn()) return;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (ApplyStayOn(_stayOnBefore))
                {
                    ClearPending();
                    _log.Info("充电常亮设置已恢复为 " + (IsStayOnValue(_stayOnBefore) ? _stayOnBefore : "0"));
                    return;
                }
                Thread.Sleep(500);
            }
            _log.Warn("恢复充电常亮设置没生效（手机可能刚好断开），下次启动会补一次");
        }

        /// <summary>
        /// 上次执行没走完（关程序、崩溃、拔线）会留下 pending 文件，下次启动时补恢复。
        /// 返回 true 表示已经没事可做（没有 pending 或已恢复成功）。
        /// </summary>
        public bool RecoverStayOn()
        {
            try
            {
                if (!File.Exists(PendingFile)) return true;
                var value = File.ReadAllText(PendingFile).Trim();
                if (!ApplyStayOn(value))
                {
                    _log.Warn("补恢复充电常亮设置失败，手机连上后会再试");
                    return false;
                }
                ClearPending();
                _log.Info("已补回上次没恢复的充电常亮设置：" + (IsStayOnValue(value) ? value : "0"));
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 打卡结束一段时间后清理后台：先强停钉钉（打卡页常驻会占用几百 MB），
        /// 再用 am kill-all 收掉其余后台应用。钉钉下次执行时会被重新打开，
        /// 不影响极速打卡。
        /// </summary>
        public void CleanBackground()
        {
            try
            {
                _adb.Shell("am force-stop com.alibaba.android.rimet", 20000);
                _adb.Shell("am kill-all", 20000);
                _log.Info("已清理手机后台（钉钉及其余后台应用已关闭）");
            }
            catch (Exception ex)
            {
                _log.Warn("清理后台失败：" + ex.Message);
            }
        }

        private RunResult RunFull(bool dryRun)
        {
            var keep = ScreenshotsEnabled;
            var entry = FindPunchEntry();
            _log.Info("点击打卡入口 " + entry.X + "," + entry.Y);
            Tap(entry.X, entry.Y);

            if (!WaitFocus("TheOneActivityMainTask", 30))
                return Fail("没有进入打卡页");

            _log.Info("打卡页已打开，等待小程序渲染");
            Thread.Sleep(10000);

            string color = null;
            string shot = null;
            var deadline = DateTime.Now.AddSeconds(40);
            while (DateTime.Now < deadline)
            {
                shot = Capture("punchpage");
                color = ButtonColor(shot);
                if (color != null) break;
                Discard(shot);
                Thread.Sleep(2000);
            }

            if (color == null)
            {
                Discard(shot);
                if (keep && shot != null) _log.Error("没有识别到打卡按钮，截图：" + shot);
                else _log.Error("没有识别到打卡按钮");
                return Fail(keep ? "没有识别到打卡按钮，请查看截图" : "没有识别到打卡按钮");
            }

            var label = color == "blue" ? "内勤（蓝色按钮）" : "外勤（绿色按钮，会记录定位）";
            _log.Info("识别到打卡按钮：" + label);

            if (color == "green" && _cfg.B("abortOnFieldPunch", true))
            {
                Discard(shot);
                return Fail("当前不在考勤范围，会打成外勤，已中止");
            }

            if (dryRun)
            {
                _log.Info("干跑：不点击打卡按钮");
                var savedShot = keep ? shot : null;
                Discard(shot);
                return new RunResult { Ok = true, Message = "干跑成功：识别到" + label + "，未点击", Shot = savedShot };
            }

            var btn = ParsePoint(_cfg.S("advanced.punchButton", "540,1168"), new Point(540, 1168));
            Tap(btn.X, btn.Y);
            _log.Info("已点击打卡按钮，等待结果");

            var done = DateTime.Now.AddSeconds(25);
            string after = null;
            while (DateTime.Now < done)
            {
                Thread.Sleep(2000);
                after = Capture("result");
                if (after != null && ButtonColor(after) == null)
                {
                    var savedAfter = keep ? after : null;
                    if (keep) _log.Info("打卡后界面已变化，结果截图：" + after);
                    else _log.Info("打卡后界面已变化");
                    Discard(after);
                    return new RunResult
                    {
                        Ok = true,
                        Message = keep ? "打卡动作已完成，请核对截图" : "打卡动作已完成",
                        Shot = savedAfter
                    };
                }
                Discard(after);
            }

            _log.Warn("打卡后界面未确认变化");
            return new RunResult { Ok = false, Message = "打卡后界面未确认变化，请人工核对", Shot = keep ? after : null };
        }

        private bool EnsureDevice()
        {
            _adb.StartServer();
            for (var i = 1; i <= 4; i++)
            {
                string serial;
                var state = _adb.DeviceState(out serial);
                if (state == "device")
                {
                    _log.Info("设备已连接：" + serial);
                    return true;
                }
                if (state == "unauthorized")
                {
                    _log.Warn("手机未授权 ADB（第 " + i + " 次）：请在手机上点「允许 USB 调试」");
                    Thread.Sleep(10000);
                    continue;
                }
                if (state == "offline")
                {
                    _log.Warn("设备 offline（第 " + i + " 次），等待恢复");
                    Thread.Sleep(5000);
                    continue;
                }
                _log.Warn("未检测到设备（第 " + i + " 次）");
                Thread.Sleep(10000);
            }
            return false;
        }

        /// <summary>
        /// 打卡前检查：没有网络时钉钉无法上报，必须中止；定位未开只做提示
        /// （实测关闭定位仍可极速打卡，真正致命的是断网）。
        /// </summary>
        private RunResult Preflight()
        {
            if (!_cfg.B("preflightCheck", true)) return null;

            var location = _adb.LocationEnabled();
            bool? validated;
            var network = _adb.NetworkState(out validated);

            _log.Info("打卡前检查：定位=" + (location == null ? "未知" : (location.Value ? "已开启" : "已关闭"))
                + "，网络=" + NetworkText(network)
                + (validated == null ? "" : (validated.Value ? "（已联网）" : "（未验证外网）")));

            if (network == "none")
                return Fail("手机没有可用网络，请连接 WiFi 或打开移动数据后重试");
            if (validated == false)
                return Fail("网络已连接但无法访问外网（比如连的是没有外网的热点），请检查网络后重试");
            if (location == false)
                _log.Warn("定位服务未开启（实测不影响极速打卡，仅作提示）");

            return null;
        }

        private static string NetworkText(string state)
        {
            if (state == "wifi") return "WiFi";
            if (state == "mobile") return "移动数据";
            if (state == "none") return "无";
            if (state == "other") return "其他网络";
            return "未知";
        }

        private bool IsAwake()
        {
            try { return _adb.Shell("dumpsys window policy", 20000).Contains("mAwake=true"); }
            catch { return false; }
        }

        private bool IsLocked()
        {
            try { return _adb.Shell("dumpsys window policy", 20000).Contains("mShowingLockscreen=true"); }
            catch { return true; }
        }

        private bool EnsureAwake()
        {
            if (IsAwake()) return true;
            for (var i = 0; i < 2; i++)
            {
                _adb.Shell("input keyevent 26", 15000);
                Thread.Sleep(1500);
                if (IsAwake()) return true;
            }
            return false;
        }

        private bool EnsureUnlocked()
        {
            try { _stayOnBefore = _adb.Shell("settings get global stay_on_while_plugged_in", 10000).Trim(); }
            catch { _stayOnBefore = null; }
            if (!IsStayOnValue(_stayOnBefore)) _stayOnBefore = null;

            // 先落盘再改设置：万一程序中途被关掉/崩溃，下次启动能从这里补恢复。
            // 上一次没恢复成功时手机还停在常亮，那个 7 不是"原值"，以 pending 里的最早值为准。
            var pending = ReadPending();
            if (pending != null) _stayOnBefore = pending;
            else SavePending(_stayOnBefore);

            _adb.Shell("svc power stayon true", 15000);
            if (!IsLocked()) return true;

            var pin = _cfg.Pin;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                _log.Info("解锁尝试 " + attempt);
                if (!EnsureAwake()) continue;
                if (!IsLocked()) return true;

                var swipe = ParseSwipe(_cfg.S("advanced.swipeUp", "540,1500,540,400"));
                _adb.Shell("input swipe " + swipe[0] + " " + swipe[1] + " " + swipe[2] + " " + swipe[3] + " 250", 15000);
                Thread.Sleep(1200);

                foreach (var ch in pin)
                {
                    var pt = ParsePoint(_cfg.S("advanced.keypad." + ch, ""), new Point(-1, -1));
                    if (pt.X < 0) continue;
                    _adb.Shell("input tap " + pt.X + " " + pt.Y, 15000);
                    Thread.Sleep(1100);
                }

                Thread.Sleep(2000);
                if (!IsLocked()) return true;
            }
            return false;
        }

        private bool OpenDingTalk()
        {
            try
            {
                _adb.Shell("am start -n com.alibaba.android.rimet/com.alibaba.android.rimet.biz.LaunchHomeActivity", 25000);
                if (WaitFocus("rimet", 30)) return true;
                _adb.Shell("monkey -p com.alibaba.android.rimet -c android.intent.category.LAUNCHER 1", 30000);
                return WaitFocus("LaunchHomeActivity", 30);
            }
            catch (Exception ex)
            {
                _log.Error("启动钉钉失败：" + ex.Message);
                return false;
            }
        }

        private string GetFocus()
        {
            try
            {
                var text = _adb.Shell("dumpsys window", 25000);
                var m = Regex.Match(text, @"mCurrentFocus=Window\{[^}]+\s+u0\s+([^\}\s]+)");
                return m.Success ? m.Groups[1].Value : "";
            }
            catch
            {
                return "";
            }
        }

        private bool WaitFocus(string pattern, int seconds)
        {
            var deadline = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < deadline)
            {
                var focus = GetFocus();
                if (focus.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                Thread.Sleep(1500);
            }
            return false;
        }

        private Point FindPunchEntry()
        {
            var fallback = ParsePoint(_cfg.S("advanced.entry", "701,290"), new Point(701, 290));
            try
            {
                _adb.Shell("uiautomator dump /sdcard/_dtc_ui.xml", 30000);
                var local = Path.Combine(Path.GetTempPath(), "dtc_ui.xml");
                if (File.Exists(local)) File.Delete(local);
                _adb.Run("pull /sdcard/_dtc_ui.xml \"" + local + "\"", 20000);
                if (!File.Exists(local)) return fallback;

                var xml = File.ReadAllText(local);
                foreach (Match m in Regex.Matches(xml, "<node[^>]*>"))
                {
                    var tag = m.Value;
                    if (tag.IndexOf("text=\"打卡\"", StringComparison.Ordinal) < 0) continue;
                    var b = Regex.Match(tag, "bounds=\"\\[(\\d+),(\\d+)\\]\\[(\\d+),(\\d+)\\]\"");
                    if (!b.Success) continue;
                    var top = int.Parse(b.Groups[2].Value);
                    if (top > 400) continue;
                    var x = (int.Parse(b.Groups[1].Value) + int.Parse(b.Groups[3].Value)) / 2;
                    var y = (int.Parse(b.Groups[2].Value) + int.Parse(b.Groups[4].Value)) / 2;
                    return new Point(x, y);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("读取界面结构失败，使用兜底坐标：" + ex.Message);
            }
            return fallback;
        }

        private string ButtonColor(string pngPath)
        {
            if (pngPath == null || !File.Exists(pngPath)) return null;
            try
            {
                var center = ParsePoint(_cfg.S("advanced.punchButton", "540,1168"), new Point(540, 1168));
                var points = new[]
                {
                    new Point(center.X, center.Y),
                    new Point(center.X - 80, center.Y),
                    new Point(center.X + 80, center.Y)
                };
                int blue = 0, green = 0;
                using (var bmp = new Bitmap(pngPath))
                {
                    foreach (var p in points)
                    {
                        if (p.X < 0 || p.Y < 0 || p.X >= bmp.Width || p.Y >= bmp.Height) continue;
                        var c = bmp.GetPixel(p.X, p.Y);
                        if (c.R < 80 && c.B > 230 && c.G > 90 && c.G < 190) blue++;
                        if (c.R < 80 && c.G > 170 && c.B > 100 && c.B < 190) green++;
                    }
                }
                if (blue >= 2) return "blue";
                if (green >= 2) return "green";
                return null;
            }
            catch
            {
                return null;
            }
        }

        private string Capture(string tag)
        {
            try
            {
                var keep = ScreenshotsEnabled;
                var name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + tag + ".png";
                var local = keep
                    ? Path.Combine(_paths.ShotDir, name)
                    : Path.Combine(Path.GetTempPath(), "dtc-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                if (!_adb.Capture(local)) return null;
                if (!keep) _tempShots.Add(local);
                return local;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>截图功能关闭时，判定完成后立刻删除临时截图。</summary>
        private void Discard(string path)
        {
            if (string.IsNullOrEmpty(path) || ScreenshotsEnabled) return;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
            _tempShots.Remove(path);
        }

        private void CleanupTempShots()
        {
            foreach (var p in _tempShots.ToArray())
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                }
                catch { }
            }
            _tempShots.Clear();
        }

        private void Tap(int x, int y)
        {
            _adb.Shell("input tap " + x + " " + y, 15000);
        }

        private static Point ParsePoint(string text, Point fallback)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            var parts = text.Split(',');
            if (parts.Length < 2) return fallback;
            int x, y;
            if (!int.TryParse(parts[0].Trim(), out x)) return fallback;
            if (!int.TryParse(parts[1].Trim(), out y)) return fallback;
            return new Point(x, y);
        }

        private static int[] ParseSwipe(string text)
        {
            var parts = (text ?? "").Split(',');
            var result = new int[4];
            var def = new[] { 540, 1500, 540, 400 };
            for (var i = 0; i < 4; i++)
            {
                if (i < parts.Length && int.TryParse(parts[i].Trim(), out result[i])) continue;
                result[i] = def[i];
            }
            return result;
        }

        private RunResult Fail(string message)
        {
            _log.Error(message);
            return new RunResult { Ok = false, Message = message };
        }
    }
}
