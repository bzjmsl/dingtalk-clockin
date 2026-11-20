using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows;

namespace DingTalkClockIn
{
    public class AppPaths
    {
        public string Root { get; private set; }
        public string DataDir { get; private set; }
        public string ConfigFile { get; private set; }
        public string HolidayFile { get; private set; }
        public string StateFile { get; private set; }
        public string LogDir { get; private set; }
        public string ShotDir { get; private set; }
        public string CacheDir { get; private set; }
        public string PowerDir { get; private set; }

        public AppPaths(string baseDir)
        {
            Root = baseDir;
            DataDir = Path.Combine(baseDir, "data");
            ConfigFile = Path.Combine(DataDir, "config.json");
            HolidayFile = Path.Combine(DataDir, "holidays.json");
            StateFile = Path.Combine(DataDir, "state.json");
            LogDir = Path.Combine(DataDir, "logs");
            ShotDir = Path.Combine(DataDir, "shots");
            CacheDir = Path.Combine(DataDir, "cache");
            PowerDir = Path.Combine(DataDir, "power");

            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(ShotDir);
            Directory.CreateDirectory(CacheDir);
            Directory.CreateDirectory(PowerDir);
        }
    }

    public class Config
    {
        private readonly string _path;
        public Dictionary<string, object> Data { get; private set; }

        private Config(string path, Dictionary<string, object> data)
        {
            _path = path;
            Data = data;
        }

        public static Config Load(string path)
        {
            Dictionary<string, object> data = null;
            if (File.Exists(path))
            {
                try
                {
                    var text = File.ReadAllText(path, Encoding.UTF8);
                    data = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                }
                catch
                {
                    data = null;
                }
            }
            if (data == null) data = new Dictionary<string, object>();

            var cfg = new Config(path, data);
            cfg.EnsureDefaults();
            return cfg;
        }

        private void EnsureDefaults()
        {
            if (Get("version") == null) Set("version", 1);
            if (Get("mode") == null) Set("mode", "wake");
            if (Get("windows.morning.start") == null) Set("windows.morning.start", "07:50");
            if (Get("windows.morning.end") == null) Set("windows.morning.end", "08:00");
            if (Get("windows.evening.start") == null) Set("windows.evening.start", "17:40");
            if (Get("windows.evening.end") == null) Set("windows.evening.end", "17:50");
            if (Get("pin") == null) Set("pin", "");
            if (Get("adbPath") == null) Set("adbPath", Adb.ResolveDefault(null));
            if (Get("autostart") == null) Set("autostart", false);
            if (Get("paused") == null) Set("paused", false);
            if (Get("theme") == null) Set("theme", "dark");
            if (Get("wakeHoldSec") == null) Set("wakeHoldSec", 60);
            if (Get("openDingTalkInWakeMode") == null) Set("openDingTalkInWakeMode", true);
            if (Get("abortOnFieldPunch") == null) Set("abortOnFieldPunch", true);
            if (Get("captureScreenshots") == null) Set("captureScreenshots", true);
            if (Get("preflightCheck") == null) Set("preflightCheck", true);
            if (Get("notifications") == null) Set("notifications", true);
            if (Get("powerMonitor") == null) Set("powerMonitor", true);
            if (Get("lockAfterRun") == null) Set("lockAfterRun", true);
            if (Get("cleanAfterRun") == null) Set("cleanAfterRun", true);
            if (Get("cleanDelaySec") == null) Set("cleanDelaySec", 60);
            if (Get("powerMonitorSec") == null) Set("powerMonitorSec", 60);
            if (Get("powerWarnMv") == null) Set("powerWarnMv", 3650);
            if (Get("logRetentionDays") == null) Set("logRetentionDays", 30);
            if (Get("skipWhenMissed") == null) Set("skipWhenMissed", true);
            if (Get("advanced.punchButton") == null) Set("advanced.punchButton", "540,1168");
            if (Get("advanced.entry") == null) Set("advanced.entry", "701,290");
            if (Get("advanced.keypad.0") == null) Set("advanced.keypad.0", "540,1600");
            if (Get("advanced.keypad.1") == null) Set("advanced.keypad.1", "260,1013");
            if (Get("advanced.keypad.2") == null) Set("advanced.keypad.2", "540,1013");
            if (Get("advanced.keypad.3") == null) Set("advanced.keypad.3", "820,1013");
            if (Get("advanced.keypad.4") == null) Set("advanced.keypad.4", "260,1210");
            if (Get("advanced.keypad.5") == null) Set("advanced.keypad.5", "540,1210");
            if (Get("advanced.keypad.6") == null) Set("advanced.keypad.6", "820,1210");
            if (Get("advanced.keypad.7") == null) Set("advanced.keypad.7", "260,1405");
            if (Get("advanced.keypad.8") == null) Set("advanced.keypad.8", "540,1405");
            if (Get("advanced.keypad.9") == null) Set("advanced.keypad.9", "820,1405");
            if (Get("advanced.swipeUp") == null) Set("advanced.swipeUp", "540,1500,540,400");
        }

        public object Get(string path)
        {
            var parts = path.Split('.');
            object cur = Data;
            for (var i = 0; i < parts.Length; i++)
            {
                var d = cur as Dictionary<string, object>;
                if (d == null) return null;
                if (!d.ContainsKey(parts[i])) return null;
                cur = d[parts[i]];
            }
            return cur;
        }

        public string S(string path, string def)
        {
            var v = Get(path);
            if (v == null) return def;
            var s = Convert.ToString(v);
            return string.IsNullOrEmpty(s) ? def : s;
        }

        public int I(string path, int def)
        {
            int n;
            return int.TryParse(S(path, ""), out n) ? n : def;
        }

        public bool B(string path, bool def)
        {
            var v = Get(path);
            if (v == null) return def;
            if (v is bool) return (bool)v;
            bool b;
            return bool.TryParse(Convert.ToString(v), out b) ? b : def;
        }

        public void Set(string path, object value)
        {
            var parts = path.Split('.');
            var d = Data;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!d.ContainsKey(parts[i]) || !(d[parts[i]] is Dictionary<string, object>))
                    d[parts[i]] = new Dictionary<string, object>();
                d = (Dictionary<string, object>)d[parts[i]];
            }
            d[parts[parts.Length - 1]] = value;
        }

        public string Pin
        {
            get { return Secret.Unprotect(S("pin", "")); }
            set { Set("pin", Secret.Protect(value)); }
        }

        public void Save()
        {
            var json = new JavaScriptSerializer().Serialize(Data);
            File.WriteAllText(_path, JsonPretty.Format(json), new UTF8Encoding(false));
        }
    }

    public static class JsonPretty
    {
        public static string Format(string json)
        {
            var sb = new StringBuilder();
            var depth = 0;
            var inString = false;
            var escape = false;
            foreach (var ch in json)
            {
                if (inString)
                {
                    sb.Append(ch);
                    if (escape) escape = false;
                    else if (ch == '\\') escape = true;
                    else if (ch == '"') inString = false;
                    continue;
                }
                switch (ch)
                {
                    case '"':
                        inString = true;
                        sb.Append(ch);
                        break;
                    case '{':
                    case '[':
                        depth++;
                        sb.Append(ch);
                        sb.Append(Environment.NewLine);
                        sb.Append(new string(' ', depth * 2));
                        break;
                    case '}':
                    case ']':
                        depth--;
                        sb.Append(Environment.NewLine);
                        sb.Append(new string(' ', depth * 2));
                        sb.Append(ch);
                        break;
                    case ',':
                        sb.Append(ch);
                        sb.Append(Environment.NewLine);
                        sb.Append(new string(' ', depth * 2));
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n') sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    public static class Secret
    {
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }

        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return "";
            if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored;
            try
            {
                var bytes = Convert.FromBase64String(stored.Substring(6));
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            catch
            {
                return "";
            }
        }
    }

    public class LogEntry
    {
        public string Time { get; set; }
        public string Level { get; set; }
        public string Text { get; set; }
    }

    public class Logger
    {
        private readonly string _dir;
        private readonly int _keepDays;
        public ObservableCollection<LogEntry> Recent { get; private set; }

        public Logger(string dir, int keepDays)
        {
            _dir = dir;
            _keepDays = keepDays <= 0 ? 30 : keepDays;
            Recent = new ObservableCollection<LogEntry>();
        }

        public void Info(string message) { Write("INFO", message); }
        public void Warn(string message) { Write("WARN", message); }
        public void Error(string message) { Write("ERROR", message); }

        private void Write(string level, string message)
        {
            var now = DateTime.Now;
            var line = string.Format("{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}", now, level, message);
            try
            {
                File.AppendAllText(Path.Combine(_dir, now.ToString("yyyy-MM-dd") + ".log"),
                    line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }

            var entry = new LogEntry { Time = now.ToString("HH:mm:ss"), Level = level, Text = message };
            var app = Application.Current;
            if (app != null && app.Dispatcher != null && !app.Dispatcher.CheckAccess())
                app.Dispatcher.BeginInvoke(new Action<LogEntry>(Append), entry);
            else
                Append(entry);
        }

        private void Append(LogEntry entry)
        {
            Recent.Insert(0, entry);
            while (Recent.Count > 300) Recent.RemoveAt(Recent.Count - 1);
        }

        public void TrimOldFiles()
        {
            try
            {
                var limit = DateTime.Now.AddDays(-_keepDays);
                foreach (var f in Directory.GetFiles(_dir, "*.log"))
                {
                    if (File.GetLastWriteTime(f) < limit) File.Delete(f);
                }
            }
            catch { }
        }

        public void LoadToday()
        {
            try
            {
                var file = Path.Combine(_dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                if (!File.Exists(file)) return;
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                var added = new List<LogEntry>();
                for (var i = lines.Length - 1; i >= 0 && added.Count < 300; i--)
                {
                    var m = Regex.Match(lines[i], @"^\d{4}-\d{2}-\d{2} (\d{2}:\d{2}:\d{2}) \[(\w+)\] (.*)$");
                    if (!m.Success) continue;
                    added.Add(new LogEntry { Time = m.Groups[1].Value, Level = m.Groups[2].Value, Text = m.Groups[3].Value });
                }
                foreach (var e in added) Recent.Add(e);
            }
            catch { }
        }
    }

    public class AppState
    {
        private readonly string _path;
        public Dictionary<string, object> Data { get; private set; }

        private AppState(string path, Dictionary<string, object> data)
        {
            _path = path;
            Data = data;
        }

        public static AppState Load(string path)
        {
            Dictionary<string, object> data = null;
            if (File.Exists(path))
            {
                try
                {
                    data = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8))
                        as Dictionary<string, object>;
                }
                catch { data = null; }
            }
            if (data == null) data = new Dictionary<string, object>();
            return new AppState(path, data);
        }

        public string LastRun(DateTime date, string session)
        {
            var last = Data.ContainsKey("lastRun") ? Data["lastRun"] as Dictionary<string, object> : null;
            if (last == null) return null;
            var dayKey = date.ToString("yyyy-MM-dd");
            if (!last.ContainsKey(dayKey)) return null;
            var day = last[dayKey] as Dictionary<string, object>;
            if (day == null || !day.ContainsKey(session)) return null;
            return Convert.ToString(day[session]);
        }

        public void MarkRun(DateTime date, string session, DateTime at)
        {
            if (!Data.ContainsKey("lastRun") || !(Data["lastRun"] is Dictionary<string, object>))
                Data["lastRun"] = new Dictionary<string, object>();
            var last = (Dictionary<string, object>)Data["lastRun"];
            var dayKey = date.ToString("yyyy-MM-dd");
            if (!last.ContainsKey(dayKey) || !(last[dayKey] is Dictionary<string, object>))
                last[dayKey] = new Dictionary<string, object>();
            ((Dictionary<string, object>)last[dayKey])[session] = at.ToString("HH:mm:ss");
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(_path, JsonPretty.Format(new JavaScriptSerializer().Serialize(Data)),
                    new UTF8Encoding(false));
            }
            catch { }
        }

        public void SetLastResult(DateTime at, string message, string shot, bool ok)
        {
            Data["lastResult"] = new Dictionary<string, object>
            {
                { "time", at.ToString("yyyy-MM-dd HH:mm:ss") },
                { "message", message ?? "" },
                { "shot", shot ?? "" },
                { "ok", ok }
            };
            Save();
        }

        public Dictionary<string, object> GetLastResult()
        {
            return Data.ContainsKey("lastResult") ? Data["lastResult"] as Dictionary<string, object> : null;
        }

        /// <summary>删除截图后，把仍然指向该文件的 lastResult.shot 清空。</summary>
        public void ClearShotIf(string path)
        {
            var lr = GetLastResult();
            if (lr == null) return;
            var shot = lr.ContainsKey("shot") ? Convert.ToString(lr["shot"]) : "";
            if (string.IsNullOrEmpty(shot)) return;
            if (!string.Equals(shot, path, StringComparison.OrdinalIgnoreCase)) return;
            lr["shot"] = "";
            Save();
        }
    }
}
