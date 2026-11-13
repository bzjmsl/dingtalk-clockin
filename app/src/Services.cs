using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
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
}
