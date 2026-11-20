using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace DingTalkClockIn
{
    public class HolidayItem
    {
        public string Date { get; set; }
        public string Type { get; set; }
        public string Name { get; set; }
        public string Source { get; set; }

        [ScriptIgnore]
        public string TypeText
        {
            get { return Type == "workday" ? "调休上班" : "节假日"; }
        }

        [ScriptIgnore]
        public string SourceText
        {
            get { return Source == "手动" ? "手动添加" : "接口同步"; }
        }
    }

    public class HolidayService
    {
        private readonly AppPaths _paths;
        private readonly Logger _log;
        private List<HolidayItem> _manual = new List<HolidayItem>();
        private List<HolidayItem> _cache = new List<HolidayItem>();

        public HolidayService(AppPaths paths, Logger log)
        {
            _paths = paths;
            _log = log;
        }

        public List<HolidayItem> Manual { get { return _manual; } }

        public List<HolidayItem> All
        {
            get
            {
                var list = new List<HolidayItem>();
                list.AddRange(_manual);
                list.AddRange(_cache);
                list.Sort(delegate(HolidayItem a, HolidayItem b) { return string.Compare(a.Date, b.Date, StringComparison.Ordinal); });
                return list;
            }
        }

        public void Load()
        {
            _manual = ReadFile(_paths.HolidayFile, "手动");
            _cache = new List<HolidayItem>();
            foreach (var f in Directory.GetFiles(_paths.CacheDir, "holiday-*.json"))
            {
                _cache.AddRange(ReadFile(f, "接口"));
            }
        }

        private List<HolidayItem> ReadFile(string path, string defaultSource)
        {
            var list = new List<HolidayItem>();
            if (!File.Exists(path)) return list;
            try
            {
                var arr = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as object[];
                if (arr == null) return list;
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    var item = new HolidayItem();
                    item.Date = d.ContainsKey("Date") ? Convert.ToString(d["Date"]) : (d.ContainsKey("date") ? Convert.ToString(d["date"]) : "");
                    item.Type = d.ContainsKey("Type") ? Convert.ToString(d["Type"]) : (d.ContainsKey("type") ? Convert.ToString(d["type"]) : "holiday");
                    item.Name = d.ContainsKey("Name") ? Convert.ToString(d["Name"]) : (d.ContainsKey("name") ? Convert.ToString(d["name"]) : "");
                    item.Source = d.ContainsKey("Source") ? Convert.ToString(d["Source"]) : defaultSource;
                    if (item.Date.Length >= 10) list.Add(item);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("读取节假日文件失败：" + path + " " + ex.Message);
            }
            return list;
        }

        public void SaveManual()
        {
            var json = new JavaScriptSerializer().Serialize(_manual);
            File.WriteAllText(_paths.HolidayFile, JsonPretty.Format(json), new UTF8Encoding(false));
        }

        public bool IsWorkday(DateTime date, out string reason)
        {
            var key = date.ToString("yyyy-MM-dd");

            foreach (var item in _manual)
            {
                if (item.Date == key)
                {
                    if (item.Type == "workday")
                    {
                        reason = "手动设为调休上班" + (string.IsNullOrEmpty(item.Name) ? "" : "：" + item.Name);
                        return true;
                    }
                    reason = "手动设为节假日" + (string.IsNullOrEmpty(item.Name) ? "" : "：" + item.Name);
                    return false;
                }
            }

            foreach (var item in _cache)
            {
                if (item.Date == key)
                {
                    if (item.Type == "workday")
                    {
                        reason = "接口：调休补班" + (string.IsNullOrEmpty(item.Name) ? "" : "：" + item.Name);
                        return true;
                    }
                    reason = "接口：法定节假日" + (string.IsNullOrEmpty(item.Name) ? "" : "：" + item.Name);
                    return false;
                }
            }

            if (date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday)
            {
                reason = "周末";
                return false;
            }

            reason = "工作日（未命中节假日数据）";
            return true;
        }

        public bool AddManual(string date, string type, string name, out string message)
        {
            DateTime parsed;
            if (!DateTime.TryParse(date, out parsed))
            {
                message = "日期格式不对，请用 2026-10-01";
                return false;
            }
            var key = parsed.ToString("yyyy-MM-dd");
            _manual.RemoveAll(delegate(HolidayItem i) { return i.Date == key; });
            _manual.Add(new HolidayItem { Date = key, Type = type, Name = name, Source = "手动" });
            SaveManual();
            message = "已保存 " + key;
            return true;
        }

        public bool RemoveManual(string date)
        {
            var removed = _manual.RemoveAll(delegate(HolidayItem i) { return i.Date == date; });
            if (removed > 0) SaveManual();
            return removed > 0;
        }

        public bool SyncYear(int year, out string message)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                var url = "https://timor.tech/api/holiday/year/" + year;
                string json;
                using (var wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    wc.Headers.Add("User-Agent", "DingTalkClockIn/1.0");
                    json = wc.DownloadString(url);
                }

                var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("holiday"))
                {
                    message = "接口返回格式不符合预期，请改用导入功能";
                    return false;
                }

                var map = root["holiday"] as Dictionary<string, object>;
                if (map == null)
                {
                    message = "接口未返回节假日数据";
                    return false;
                }

                var items = new List<HolidayItem>();
                foreach (var kv in map)
                {
                    var v = kv.Value as Dictionary<string, object>;
                    if (v == null) continue;
                    var isHoliday = v.ContainsKey("holiday") && Convert.ToBoolean(v["holiday"]);
                    var date = v.ContainsKey("date") ? Convert.ToString(v["date"]) : "";
                    var name = v.ContainsKey("name") ? Convert.ToString(v["name"]) : "";
                    if (date.Length < 10) continue;
                    items.Add(new HolidayItem
                    {
                        Date = date,
                        Type = isHoliday ? "holiday" : "workday",
                        Name = name,
                        Source = "接口"
                    });
                }

                var file = Path.Combine(_paths.CacheDir, "holiday-" + year + ".json");
                File.WriteAllText(file, JsonPretty.Format(new JavaScriptSerializer().Serialize(items)),
                    new UTF8Encoding(false));

                Load();
                message = "已同步 " + items.Count + " 条";
                return true;
            }
            catch (Exception ex)
            {
                message = "联网同步失败：" + ex.Message + "（可改用导入）";
                return false;
            }
        }

        public int ImportCsv(string path, out string message)
        {
            var count = 0;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(',');
                if (parts.Length < 2) continue;
                DateTime d;
                if (!DateTime.TryParse(parts[0].Trim(), out d)) continue;
                var type = parts[1].Trim().ToLowerInvariant();
                if (type != "holiday" && type != "workday")
                {
                    if (parts[1].Contains("调休")) type = "workday";
                    else type = "holiday";
                }
                var name = parts.Length > 2 ? parts[2].Trim() : "";
                AddManual(d.ToString("yyyy-MM-dd"), type, name, out message);
                count++;
            }
            message = "已导入 " + count + " 条";
            return count;
        }

        public int ImportJson(string path, out string message)
        {
            var items = ReadFile(path, "手动");
            var count = 0;
            foreach (var it in items)
            {
                AddManual(it.Date, it.Type, it.Name, out message);
                count++;
            }
            message = "已导入 " + count + " 条";
            return count;
        }

        public void ExportCsv(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# date,type,name  (type: holiday=节假日, workday=调休上班)");
            foreach (var it in All)
            {
                sb.AppendLine(it.Date + "," + it.Type + "," + it.Name);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }

    public class Scheduler
    {
        public const string Morning = "morning";
        public const string Evening = "evening";

        private readonly Config _cfg;
        private readonly HolidayService _holiday;
        private readonly AppState _state;

        public DateTime? NextRun { get; private set; }
        public string NextSession { get; private set; }

        public Scheduler(Config cfg, HolidayService holiday, AppState state)
        {
            _cfg = cfg;
            _holiday = holiday;
            _state = state;
        }

        public static TimeSpan ParseTime(string text, TimeSpan fallback)
        {
            TimeSpan ts;
            return TimeSpan.TryParse(string.IsNullOrEmpty(text) ? "" : text.Trim(), out ts) ? ts : fallback;
        }

        public static int Offset(DateTime date, string session, int span)
        {
            var key = date.ToString("yyyy-MM-dd") + "|" + session;
            uint hash = 2166136261;
            foreach (var ch in key)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return (int)(hash % (uint)span);
        }

        public void Recompute(DateTime now, bool paused)
        {
            NextRun = null;
            NextSession = null;
            if (paused) return;

            var today = now.Date;
            string reason;
            if (!_holiday.IsWorkday(today, out reason)) return;

            foreach (var session in new[] { Morning, Evening })
            {
                if (_state.LastRun(today, session) != null) continue;

                var start = ParseTime(_cfg.S("windows." + session + ".start", "07:50"), new TimeSpan(7, 50, 0));
                var end = ParseTime(_cfg.S("windows." + session + ".end", "08:00"), new TimeSpan(8, 0, 0));
                var winStart = today + start;
                var winEnd = today + end;
                if (winEnd <= winStart) winEnd = winStart.AddMinutes(1);
                if (now > winEnd) continue;

                var span = (int)(winEnd - winStart).TotalSeconds;
                if (span < 1) span = 1;

                var target = winStart.AddSeconds(Offset(today, session, span));
                if (now >= target) target = now;

                if (!NextRun.HasValue || target < NextRun.Value)
                {
                    NextRun = target;
                    NextSession = session;
                }
            }
        }
    }

    /// <summary>
    /// 后台电源监控：定时读手机电量/电压/电流写入 CSV，
    /// 并记录断开、重启事件，重启后自动抓取上一轮内核日志，
    /// 用来判断"手机为什么自己关机"。
    /// </summary>
    public class PowerMonitor
    {
        private readonly AppPaths _paths;
        private readonly Config _cfg;
        private readonly Adb _adb;
        private readonly Logger _log;

        private bool _wasOnline;
        private int _lastUptime = -1;
        private DateTime _lastWarn = DateTime.MinValue;
        private int _samples;

        public bool Online { get; private set; }
        public int Capacity { get; private set; }
        public int Voltage { get; private set; }
        public int Current { get; private set; }
        public string Status { get; private set; }
        public string LastEvent { get; private set; }
        public DateTime LastPoll { get; private set; }
        public int Samples { get { return _samples; } }

        public PowerMonitor(AppPaths paths, Config cfg, Adb adb, Logger log)
        {
            _paths = paths;
            _cfg = cfg;
            _adb = adb;
            _log = log;
            Capacity = -1;
            Voltage = -1;
            Current = -1;
            Status = "";
            LastEvent = "";
            LastPoll = DateTime.MinValue;
        }

        /// <summary>采样一次。可能耗时几百毫秒，请放在后台线程调用。</summary>
        public void Poll()
        {
            try
            {
                LastPoll = DateTime.Now;

                string serial;
                var state = _adb.DeviceState(out serial);
                Online = state == "device";

                if (!Online)
                {
                    if (_wasOnline)
                    {
                        Record("设备断开（状态：" + StateText(state) + "）");
                        _log.Warn("电源监控：设备断开 " + StateText(state));
                    }
                    _wasOnline = false;
                    return;
                }

                var uptime = _adb.UptimeSeconds();

                if (_wasOnline && _lastUptime >= 0 && uptime >= 0 && uptime + 30 < _lastUptime)
                {
                    Record("手机重启过（开机时长 " + _lastUptime + "s → " + uptime + "s）");
                    DumpKernelLog("reboot");
                }
                else if (!_wasOnline && _lastUptime >= 0 && uptime >= 0 && uptime + 30 < _lastUptime)
                {
                    Record("断开重连且开机时长变短，判定为重启（" + _lastUptime + "s → " + uptime + "s）");
                    DumpKernelLog("reconnect");
                }

                _wasOnline = true;
                if (uptime >= 0) _lastUptime = uptime;

                Capacity = _adb.BatteryLevel();
                Voltage = _adb.BatteryVoltage();
                Current = _adb.BatteryCurrent();
                Status = _adb.BatteryStatusText();
                var usb = _adb.UsbPowered();

                AppendCsv(usb);
                _samples++;

                var warnMv = _cfg.I("powerWarnMv", 3650);
                if (Voltage > 0 && warnMv > 0 && Voltage < warnMv &&
                    (DateTime.Now - _lastWarn).TotalMinutes >= 30)
                {
                    _lastWarn = DateTime.Now;
                    Record("电压偏低：" + Voltage + " mV（阈值 " + warnMv + " mV）");
                    _log.Warn("电源监控：电压偏低 " + Voltage + " mV，可能出现关机");
                }
            }
            catch (Exception ex)
            {
                try { _log.Warn("电源监控异常：" + ex.Message); } catch { }
            }
        }

        private static string StateText(string state)
        {
            if (state == "unauthorized") return "未授权";
            if (state == "offline") return "离线";
            if (state == "none" || string.IsNullOrEmpty(state)) return "未连接";
            return state;
        }

        private void AppendCsv(bool usb)
        {
            try
            {
                var file = Path.Combine(_paths.PowerDir,
                    "battery-" + DateTime.Now.ToString("yyyyMMdd") + ".csv");
                var sb = new StringBuilder();
                if (!File.Exists(file))
                    sb.AppendLine("time,capacity,voltage_mv,current_ma,status,usb");
                sb.AppendFormat("{0:yyyy-MM-dd HH:mm:ss},{1},{2},{3},{4},{5}{6}",
                    DateTime.Now, Capacity, Voltage, Current, Status, usb ? 1 : 0, Environment.NewLine);
                File.AppendAllText(file, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static readonly object EventsGate = new object();

        private void Record(string text)
        {
            LastEvent = DateTime.Now.ToString("MM-dd HH:mm") + " " + text;
            try
            {
                var file = Path.Combine(_paths.PowerDir, "events.log");
                var line = string.Format("{0:yyyy-MM-dd HH:mm:ss}  {1}{2}", DateTime.Now, text, Environment.NewLine);
                lock (EventsGate) File.AppendAllText(file, line, new UTF8Encoding(false));
            }
            catch { }
        }

        /// <summary>记一条外部事件（例如系统日志抓取中断），会写进 events.log 并显示在界面上。</summary>
        public void Note(string text)
        {
            Record(text);
            try { _log.Warn("电源监控：" + text); } catch { }
        }

        private void DumpKernelLog(string tag)
        {
            try
            {
                var kmsg = _adb.Shell("cat /proc/last_kmsg", 30000);
                if (string.IsNullOrEmpty(kmsg)) return;

                var name = "last_kmsg-" + tag + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
                File.WriteAllText(Path.Combine(_paths.PowerDir, name), kmsg, new UTF8Encoding(false));

                var reason = _adb.Shell("getprop ro.boot.bootreason", 8000).Trim();
                Record("已保存上一轮内核日志：" + name + "（开机原因 " + reason + "）");
            }
            catch (Exception ex)
            {
                try { _log.Warn("保存内核日志失败：" + ex.Message); } catch { }
            }
        }
    }

    /// <summary>
    /// 常驻把手机的 logcat（system + events 缓冲）落到 data\power\logcat-yyyyMMdd.log。
    /// 手机是断电式关机时，它自己什么都记不下来；这条流在 PC 上，能留下关机前最后一刻。
    /// 系统主动关机会出现 PowerManagerService / ShutdownThread 的记录；硬件掉电则戛然而止。
    /// </summary>
    public class LogStreamer
    {
        private const long MaxBytesPerDay = 64L * 1024 * 1024;
        private const int KeepDays = 7;

        private readonly Adb _adb;
        private readonly AppPaths _paths;
        private readonly Logger _log;
        private readonly Action<string> _note;

        private readonly object _gate = new object();
        private Process _proc;
        private StreamWriter _writer;
        private DateTime _day = DateTime.MinValue;
        private long _bytes;
        private bool _fullWarned;
        private volatile bool _stopping;
        private Timer _watchdog;
        private DateTime _nextTry = DateTime.MinValue;

        public bool Running { get; private set; }
        public long Bytes { get { return Interlocked.Read(ref _bytes); } }

        public LogStreamer(Adb adb, AppPaths paths, Logger log, Action<string> note)
        {
            _adb = adb;
            _paths = paths;
            _log = log;
            _note = note;
        }

        public void Start()
        {
            if (_stopping) return;
            lock (_gate)
            {
                if (_proc != null) return;
                try
                {
                    PruneOld();
                    _proc = _adb.StartStream("logcat -v time -b system -b events");
                    _proc.EnableRaisingEvents = true;
                    _proc.OutputDataReceived += OnLine;
                    _proc.ErrorDataReceived += delegate { };
                    _proc.Exited += OnExited;
                    _proc.BeginOutputReadLine();
                    _proc.BeginErrorReadLine();
                    Running = true;
                    if (_watchdog == null) _watchdog = new Timer(Check, null, 30000, 30000);
                    _log.Info("已开始抓取手机系统日志");
                }
                catch (Exception ex)
                {
                    try { if (_proc != null) _proc.Dispose(); } catch { }
                    _proc = null;
                    Running = false;
                    _nextTry = DateTime.Now.AddMinutes(1);
                    _log.Warn("启动系统日志抓取失败：" + ex.Message);
                }
            }
        }

        public void Stop()
        {
            _stopping = true;
            try { if (_watchdog != null) { _watchdog.Dispose(); _watchdog = null; } } catch { }

            Process proc;
            lock (_gate)
            {
                proc = _proc;
                _proc = null;
                Running = false;
            }
            if (proc != null)
            {
                try { if (!proc.HasExited) { proc.Kill(); proc.WaitForExit(5000); } } catch { }
                try { proc.Dispose(); } catch { }
            }
            lock (_gate)
            {
                try { if (_writer != null) { _writer.Flush(); _writer.Dispose(); } } catch { }
                _writer = null;
            }
            try { _log.Info("已停止抓取手机系统日志"); } catch { }
        }

        private void Check(object state)
        {
            if (_stopping) return;
            try
            {
                lock (_gate)
                {
                    if (_proc != null && !_proc.HasExited) return;
                    if (_proc != null)
                    {
                        try { _proc.Dispose(); } catch { }
                        _proc = null;
                    }
                }
                if (DateTime.Now < _nextTry) return;
                Start();
            }
            catch { }
        }

        private void OnExited(object sender, EventArgs e)
        {
            var code = -1;
            try { code = ((Process)sender).ExitCode; } catch { }
            lock (_gate) { Running = false; }
            if (_stopping) return;
            _nextTry = DateTime.Now.AddSeconds(20);
            if (_note != null)
            {
                try { _note("手机系统日志中断（退出码 " + code + "），若手机此刻关机即为关机时刻"); } catch { }
            }
        }

        private void OnLine(object sender, DataReceivedEventArgs e)
        {
            if (e.Data == null) return;
            try
            {
                var now = DateTime.Now;
                lock (_gate)
                {
                    if (now.Date != _day) Roll(now);
                    if (_writer == null) return;
                    if (Interlocked.Read(ref _bytes) >= MaxBytesPerDay)
                    {
                        if (!_fullWarned)
                        {
                            _fullWarned = true;
                            _writer.WriteLine("# 当天日志已超过 64 MB，今天不再记录");
                            _writer.Flush();
                            _writer.Dispose();
                            _writer = null;
                        }
                        return;
                    }
                    _writer.WriteLine(e.Data);
                    _writer.Flush();
                    Interlocked.Add(ref _bytes, e.Data.Length + 2);
                }
            }
            catch { }
        }

        private void Roll(DateTime now)
        {
            try { if (_writer != null) _writer.Dispose(); } catch { }
            _writer = null;
            _day = now.Date;
            Interlocked.Exchange(ref _bytes, 0);
            _fullWarned = false;

            var file = Path.Combine(_paths.PowerDir, "logcat-" + now.ToString("yyyyMMdd") + ".log");
            var fresh = !File.Exists(file) || new FileInfo(file).Length == 0;
            var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, new UTF8Encoding(false));
            if (fresh)
            {
                _writer.WriteLine("# " + now.ToString("yyyy-MM-dd HH:mm:ss") + " 开始记录手机系统日志");
                _writer.Flush();
            }
        }

        private void PruneOld()
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-KeepDays);
                foreach (var f in Directory.GetFiles(_paths.PowerDir, "logcat-*.log"))
                {
                    try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { }
                }
            }
            catch { }
        }
    }
}
