using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DingTalkClockIn
{
    public class Adb
    {
        public string Path { get; set; }

        public Adb(string path)
        {
            Path = path;
        }

        public static string ResolveDefault(string baseDir)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(baseDir))
                candidates.Add(System.IO.Path.Combine(baseDir, "platform-tools", "adb.exe"));

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                candidates.Add(System.IO.Path.Combine(local, @"Android\Sdk\platform-tools\adb.exe"));

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
                candidates.Add(System.IO.Path.Combine(programFiles, @"Android\platform-tools\adb.exe"));

            candidates.Add(@"C:\platform-tools\adb.exe");

            foreach (var c in candidates)
            {
                if (File.Exists(c)) return c;
            }

            // 最后交给 PATH 里的 adb
            return "adb.exe";
        }

        private bool IsScript
        {
            get
            {
                return Path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                    || Path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            }
        }

        private ProcessStartInfo BuildStartInfo(string args)
        {
            var psi = new ProcessStartInfo();
            if (IsScript)
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c \"\"" + Path + "\" " + args + "\"";
            }
            else
            {
                psi.FileName = Path;
                psi.Arguments = args;
            }
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            return psi;
        }

        public string Run(string args, int timeoutMs)
        {
            var psi = BuildStartInfo(args);

            using (var p = new Process())
            {
                p.StartInfo = psi;
                p.Start();
                var outTask = p.StandardOutput.ReadToEndAsync();
                var errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(); } catch { }
                    throw new TimeoutException("adb 命令超时：" + args);
                }
                var stdout = outTask.Result ?? "";
                var stderr = errTask.Result ?? "";
                if (stdout.Trim().Length == 0) return stderr.Trim();
                return stdout.Trim();
            }
        }

        public string Shell(string command, int timeoutMs)
        {
            return Run("shell \"" + command + "\"", timeoutMs);
        }

        /// <summary>开一条常驻命令（如 logcat），进程的读取和结束由调用方负责。</summary>
        public Process StartStream(string args)
        {
            var p = new Process();
            p.StartInfo = BuildStartInfo(args);
            p.Start();
            return p;
        }

        public bool StartServer()
        {
            try
            {
                Run("start-server", 20000);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public List<string> DeviceList()
        {
            var result = new List<string>();
            var text = Run("devices", 15000);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of")) continue;
                var parts = line.Split('\t');
                if (parts.Length >= 2 && parts[1].Trim() == "device") result.Add(parts[0].Trim());
            }
            return result;
        }

        /// <summary>返回 none / unauthorized / offline / device</summary>
        public string DeviceState(out string serial)
        {
            serial = null;
            string text;
            try { text = Run("devices", 20000); }
            catch { return "none"; }
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("List of")) continue;
                var parts = line.Split('\t');
                if (parts.Length < 2) parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                serial = parts[0].Trim();
                var state = parts[1].Trim();
                if (state == "device" || state == "unauthorized" || state == "offline") return state;
            }
            return "none";
        }

        public bool IsOnline()
        {
            try
            {
                return DeviceList().Count > 0;
            }
            catch
            {
                return false;
            }
        }

        public string GetProp(string name)
        {
            try { return Shell("getprop " + name, 10000).Trim(); }
            catch { return ""; }
        }

        public int BatteryLevel()
        {
            try
            {
                var text = Shell("dumpsys battery", 10000);
                var m = Regex.Match(text, @"level:\s*(\d+)");
                if (m.Success)
                {
                    int level;
                    if (int.TryParse(m.Groups[1].Value, out level)) return level;
                }
            }
            catch { }
            return -1;
        }

        /// <summary>读一个 power_supply 节点的整数值，读不到返回 -1</summary>
        private int ReadBatteryNode(string node, int timeoutMs)
        {
            try
            {
                var text = Shell("cat /sys/class/power_supply/battery/" + node, timeoutMs);
                int value;
                if (int.TryParse(text.Trim(), out value)) return value;
            }
            catch { }
            return -1;
        }

        /// <summary>电池电压，单位 mV，读不到返回 -1</summary>
        public int BatteryVoltage()
        {
            return ReadBatteryNode("batt_vol", 8000);
        }

        /// <summary>平均电流，单位 mA，读不到返回 -1</summary>
        public int BatteryCurrent()
        {
            return ReadBatteryNode("BatteryAverageCurrent", 8000);
        }

        /// <summary>充电状态文本：Charging / Discharging / Not charging …</summary>
        public string BatteryStatusText()
        {
            try { return Shell("cat /sys/class/power_supply/battery/status", 8000).Trim(); }
            catch { return ""; }
        }

        /// <summary>是否接着 USB 电源</summary>
        public bool UsbPowered()
        {
            try
            {
                var text = Shell("cat /sys/class/power_supply/usb/online", 8000);
                return text.Trim() == "1";
            }
            catch { return false; }
        }

        /// <summary>本次开机已运行的秒数，读不到返回 -1</summary>
        public int UptimeSeconds()
        {
            try
            {
                var text = Shell("cat /proc/uptime", 8000);
                var parts = text.Trim().Split(' ');
                double seconds;
                if (parts.Length > 0 && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out seconds))
                    return (int)seconds;
            }
            catch { }
            return -1;
        }

        /// <summary>定位总开关：true 开 / false 关 / null 读不到</summary>
        public bool? LocationEnabled()
        {
            try
            {
                var text = Shell("dumpsys location | grep mEnabled=", 20000);
                if (text.IndexOf("mEnabled=true", StringComparison.Ordinal) >= 0) return true;
                if (text.IndexOf("mEnabled=false", StringComparison.Ordinal) >= 0) return false;
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>当前默认网络：wifi / mobile / other / none / unknown</summary>
        public string NetworkState()
        {
            bool? validated;
            return NetworkState(out validated);
        }

        /// <summary>同时返回系统对该网络是否已通过联网校验（lastValidated）</summary>
        public string NetworkState(out bool? validated)
        {
            validated = null;
            try
            {
                var text = Shell("dumpsys connectivity", 30000);
                if (text.IndexOf("lastValidated{true}", StringComparison.Ordinal) >= 0) validated = true;
                else if (text.IndexOf("lastValidated{false}", StringComparison.Ordinal) >= 0) validated = false;

                var m = Regex.Match(text, @"Active default network:\s*(\S+)");
                if (!m.Success) return "unknown";
                if (string.Equals(m.Groups[1].Value, "none", StringComparison.OrdinalIgnoreCase)) return "none";
                if (text.IndexOf("type: WIFI", StringComparison.Ordinal) >= 0) return "wifi";
                if (text.IndexOf("type: MOBILE", StringComparison.Ordinal) >= 0) return "mobile";
                return "other";
            }
            catch
            {
                return "unknown";
            }
        }

        public bool Capture(string localPath)
        {
            try
            {
                Shell("screencap -p /sdcard/_dtc.png", 30000);
                Run("pull /sdcard/_dtc.png \"" + localPath + "\"", 30000);
                Shell("rm -f /sdcard/_dtc.png", 10000);
                return File.Exists(localPath) && new FileInfo(localPath).Length > 1024;
            }
            catch
            {
                return false;
            }
        }
    }
}
