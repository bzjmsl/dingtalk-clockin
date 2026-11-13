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

        public string Run(string args, int timeoutMs)
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
