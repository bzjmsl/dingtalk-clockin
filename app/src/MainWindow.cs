using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DingTalkClockIn
{
    public class ShotItem
    {
        public string Path { get; set; }
        public string Display { get; set; }
        public string Kind { get; set; }
    }

    public class MainWindow
    {
        private readonly AppHost _host;
        private DispatcherTimer _timer;
        private bool _running;
        private string _lastShot;
        private DateTime _retryAfter = DateTime.MinValue;

        public Window Window { get; private set; }

        public MainWindow(AppHost host)
        {
            _host = host;

            var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("ui.xaml");
            if (stream == null) throw new InvalidOperationException("找不到内嵌界面资源 ui.xaml");
            Window = (Window)System.Windows.Markup.XamlReader.Load(stream);

            Wire();
            F<TextBlock>("TxtVersion").Text = "v" + AssemblyVersionText() + " · ADB";
            LoadSettingsToUi();
            RefreshHolidayList();
            BindLogs();
            RefreshShots();
            RestoreLastResult();
            UpdateOverview();
            RefreshDevice(null, null);
        }

        private T F<T>(string name) where T : class
        {
            return Window.FindName(name) as T;
        }

        private static string AssemblyVersionText()
        {
            try
            {
                var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v == null ? "1.0" : v.ToString(3);
            }
            catch
            {
                return "1.0";
            }
        }

        private void Wire()
        {
            F<Grid>("TitleBar").MouseLeftButtonDown += delegate
            {
                try { Window.DragMove(); } catch { }
            };
            F<Button>("BtnMin").Click += delegate { Window.WindowState = WindowState.Minimized; };
            F<Button>("BtnClose").Click += delegate { Window.Hide(); };

            F<RadioButton>("NavOverview").Click += delegate { ShowPage("overview"); };
            F<RadioButton>("NavSettings").Click += delegate { ShowPage("settings"); };
            F<RadioButton>("NavHoliday").Click += delegate { ShowPage("holiday"); };
            F<RadioButton>("NavLogs").Click += delegate { ShowPage("logs"); };

            F<Button>("BtnRefreshDevice").Click += RefreshDevice;
            F<Button>("BtnTogglePause").Click += delegate { TogglePause(); };
            F<Button>("BtnRunNow").Click += delegate { RunNow(false); };

            F<Button>("BtnSaveSettings").Click += delegate { SaveSettings(); };
            F<Button>("BtnReset").Click += delegate { ResetDefaults(); };
            F<Button>("BtnTestAdb").Click += delegate { TestAdb(); };

            F<Button>("BtnAddHoliday").Click += delegate { AddHoliday(); };
            F<Button>("BtnDeleteHoliday").Click += delegate { DeleteHoliday(); };
            F<Button>("BtnSyncYear").Click += delegate { SyncYear(); };
            F<Button>("BtnImportCsv").Click += delegate { ImportHoliday(true); };
            F<Button>("BtnImportJson").Click += delegate { ImportHoliday(false); };
            F<Button>("BtnExport").Click += delegate { ExportHoliday(); };

            F<Button>("BtnOpenFolder").Click += delegate
            {
                try { Process.Start("explorer.exe", _host.Paths.DataDir); } catch { }
            };
            F<Button>("BtnDeleteShot").Click += delegate { DeleteSelectedShot(); };
            F<Button>("BtnClearShots").Click += delegate { ClearShots(); };

            F<ListBox>("LstLogs").SelectionChanged += delegate
            {
                var item = F<ListBox>("LstLogs").SelectedItem as LogEntry;
                if (item == null) return;
                var m = Regex.Match(item.Text, @"[A-Za-z]:\\[^\s""]+\.png");
                if (m.Success) ShowImage(F<Image>("ImgShotPreview"), m.Value);
            };

            F<ListBox>("LstShots").SelectionChanged += delegate
            {
                var item = F<ListBox>("LstShots").SelectedItem as ShotItem;
                if (item != null) ShowImage(F<Image>("ImgShotPreview"), item.Path);
            };
            F<Image>("ImgLastShot").MouseLeftButtonUp += delegate { ShowPreview(_lastShot); };
            F<Image>("ImgShotPreview").MouseLeftButtonUp += delegate
            {
                var item = F<ListBox>("LstShots").SelectedItem as ShotItem;
                if (item != null) ShowPreview(item.Path);
            };
        }

        public void StartTimer()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += OnTick;
            _timer.Start();
        }

        private async void OnTick(object sender, EventArgs e)
        {
            UpdateOverview();
            if (_running) return;
            if (DateTime.Now < _retryAfter) return;
            var next = _host.Scheduler.NextRun;
            if (next.HasValue && DateTime.Now >= next.Value && _host.Scheduler.NextSession != null)
            {
                await ExecuteAsync(_host.Scheduler.NextSession, false);
            }
        }

        private void ShowPage(string page)
        {
            F<Grid>("PageOverview").Visibility = page == "overview" ? Visibility.Visible : Visibility.Collapsed;
            F<Grid>("PageSettings").Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            F<Grid>("PageHoliday").Visibility = page == "holiday" ? Visibility.Visible : Visibility.Collapsed;
            F<Grid>("PageLogs").Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
            if (page == "logs") RefreshShots();
        }

        public void ShowPageByName(string page)
        {
            ShowPage((page ?? "overview").ToLowerInvariant());
            var nav = page == "settings" ? "NavSettings"
                : page == "holiday" ? "NavHoliday"
                : page == "logs" ? "NavLogs" : "NavOverview";
            F<RadioButton>(nav).IsChecked = true;
            UpdateLayoutSafe();
        }

        private void UpdateLayoutSafe()
        {
            try
            {
                var root = Window.Content as FrameworkElement;
                if (root == null) return;
                root.Measure(new Size(Window.Width, Window.Height));
                root.Arrange(new Rect(0, 0, Window.Width, Window.Height));
                root.UpdateLayout();
            }
            catch { }
        }

        private Brush Res(string key)
        {
            var b = Application.Current.Resources[key] as Brush;
            return b ?? Brushes.Gray;
        }

        private void UpdateOverview()
        {
            var cfg = _host.Config;
            var paused = cfg.B("paused", false);
            var mode = cfg.S("mode", "wake");

            F<TextBlock>("TxtModeBadge").Text = mode == "wake" ? "极速打卡" : "手动点击";
            F<Button>("BtnTogglePause").Content = paused ? "恢复" : "暂停";

            string reason;
            var workday = _host.Holidays.IsWorkday(DateTime.Today, out reason);
            F<TextBlock>("TxtTodayState").Text = workday ? "工作日" : "休息日";
            F<TextBlock>("TxtTodayState").Foreground = workday ? Res("TextPrimary") : Res("Warning");
            F<TextBlock>("TxtTodayReason").Text = reason;

            _host.Scheduler.Recompute(DateTime.Now, paused);

            if (paused)
            {
                F<TextBlock>("TxtNextRun").Text = "已暂停";
                F<TextBlock>("TxtCountdown").Text = "恢复后按计划继续";
                return;
            }
            if (!workday)
            {
                F<TextBlock>("TxtNextRun").Text = "今天不用打卡";
                F<TextBlock>("TxtCountdown").Text = reason;
                return;
            }
            if (!_host.Scheduler.NextRun.HasValue)
            {
                F<TextBlock>("TxtNextRun").Text = "今日已完成";
                F<TextBlock>("TxtCountdown").Text = "如需重跑可点「立即试跑」";
                return;
            }

            var next = _host.Scheduler.NextRun.Value;
            var session = _host.Scheduler.NextSession == Scheduler.Morning ? "上班" : "下班";
            F<TextBlock>("TxtNextRun").Text = next.ToString("HH:mm:ss") + " · " + session;

            var left = next - DateTime.Now;
            if (left.TotalSeconds < 0) left = TimeSpan.Zero;
            F<TextBlock>("TxtCountdown").Text = string.Format("还有 {0:hh\\:mm\\:ss}（窗口内随机时刻）", left);
        }

        private class DeviceInfo
        {
            public bool Online;
            public string State;
            public string Model;
            public string Serial;
            public int Battery;
            public bool? Location;
            public string Network;
        }

        private async void RefreshDevice(object sender, RoutedEventArgs e)
        {
            F<TextBlock>("TxtDeviceModel").Text = "读取中…";
            var info = await Task.Run(delegate
            {
                var d = new DeviceInfo();
                try
                {
                    string serial;
                    d.State = _host.Adb.DeviceState(out serial);
                    d.Serial = serial;
                    d.Online = d.State == "device";
                    if (d.Online)
                    {
                        d.Model = _host.Adb.GetProp("ro.product.model");
                        d.Battery = _host.Adb.BatteryLevel();
                        d.Location = _host.Adb.LocationEnabled();
                        d.Network = _host.Adb.NetworkState();
                    }
                }
                catch { d.Online = false; d.State = "none"; }
                return d;
            });

            F<TextBlock>("TxtDeviceModel").Text = string.IsNullOrEmpty(info.Model) ? "—" : info.Model;
            F<TextBlock>("TxtDeviceSerial").Text = string.IsNullOrEmpty(info.Serial) ? "—" : info.Serial;
            F<TextBlock>("TxtBattery").Text = info.Battery >= 0 ? info.Battery + "%" : "—";

            var locText = info.Location == null ? "—" : (info.Location.Value ? "已开启" : "已关闭");
            F<TextBlock>("TxtLocation").Text = info.Online ? locText : "—";
            F<TextBlock>("TxtLocation").Foreground = info.Online
                ? (info.Location == false ? Res("Warning") : Res("TextPrimary"))
                : Res("TextSecondary");

            var netText = info.Online ? NetworkLabel(info.Network) : "—";
            F<TextBlock>("TxtNetwork").Text = netText;
            F<TextBlock>("TxtNetwork").Foreground = info.Online
                ? (info.Network == "none" ? Res("Danger") : Res("TextPrimary"))
                : Res("TextSecondary");

            var state = info.State ?? "none";
            _host.Log.Info("设备状态：" + state + (string.IsNullOrEmpty(info.Serial) ? "" : " (" + info.Serial + ")"));
            var label = state == "device" ? "已连接"
                : state == "unauthorized" ? "未授权"
                : state == "offline" ? "离线"
                : "未连接";
            F<TextBlock>("TxtConnState").Text = label;
            F<TextBlock>("TxtConnState").Foreground = state == "device" ? Res("Success")
                : state == "unauthorized" ? Res("Warning")
                : Res("Danger");
            F<TextBlock>("TxtSideStatus").Text = state == "device"
                ? "已连接 · " + (string.IsNullOrEmpty(info.Model) ? "手机" : info.Model)
                : state == "unauthorized" ? "设备未授权，请在手机上点「允许 USB 调试」"
                : "未连接设备";
            F<TextBlock>("TxtSideStatus").Foreground = state == "device" ? Res("Success") : Res("TextSecondary");
            if (info.Online) _host.BackupAdbKey();
        }

        private static string NetworkLabel(string state)
        {
            if (state == "wifi") return "WiFi";
            if (state == "mobile") return "移动数据";
            if (state == "none") return "无";
            if (state == "other") return "其他";
            return "未知";
        }

        private void TogglePause()
        {
            var paused = !_host.Config.B("paused", false);
            _host.Config.Set("paused", paused);
            _host.Config.Save();
            _host.Log.Info(paused ? "已暂停定时" : "已恢复定时");
            UpdateOverview();
        }

        private void RunNow(bool live)
        {
            var session = DateTime.Now.Hour < 12 ? Scheduler.Morning : Scheduler.Evening;
            var task = ExecuteAsync(session, !live);
            GC.KeepAlive(task);
        }

        public void RunNowFromTray()
        {
            RunNow(false);
        }

        private async Task ExecuteAsync(string session, bool dryRun)
        {
            if (_running) return;
            _running = true;
            var btn = F<Button>("BtnRunNow");
            btn.IsEnabled = false;
            try
            {
                var mode = _host.Config.S("mode", "wake");
                _host.Log.Info("触发 " + session + " 打卡（" + mode + (dryRun ? "，干跑" : "") + "）");
                var result = await Task.Run(delegate { return _host.Engine.Run(mode, dryRun); });

                if (!dryRun && result.Ok)
                {
                    _host.State.MarkRun(DateTime.Today, session, DateTime.Now);
                    _host.State.Save();
                }
                else if (!dryRun)
                {
                    _retryAfter = DateTime.Now.AddMinutes(2);
                    _host.Log.Warn("本次未成功，2 分钟后在窗口内会再试一次");
                }

                ApplyResult(result, dryRun);
                _host.Notify(result.Ok ? "打卡完成" : "打卡未完成", result.Message, !result.Ok);
            }
            catch (Exception ex)
            {
                _host.Log.Error("执行异常：" + ex.Message);
                _host.Notify("执行出错", ex.Message, true);
            }
            finally
            {
                _running = false;
                btn.IsEnabled = true;
                UpdateOverview();
            }
        }

        private void ApplyResult(RunResult result, bool dryRun)
        {
            var prefix = dryRun ? "[干跑] " : "";
            F<TextBlock>("TxtLastResult").Text = prefix + DateTime.Now.ToString("MM-dd HH:mm:ss") + " · " + result.Message;
            F<TextBlock>("TxtLastResult").Foreground = result.Ok ? Res("TextPrimary") : Res("Danger");
            if (!string.IsNullOrEmpty(result.Shot) && File.Exists(result.Shot))
            {
                _lastShot = result.Shot;
                ShowImage(F<Image>("ImgLastShot"), result.Shot);
                _host.Log.Info("截图：" + result.Shot);
            }
            _host.State.SetLastResult(DateTime.Now, prefix + result.Message, result.Shot, result.Ok);
            RefreshShots();
        }

        private void RestoreLastResult()
        {
            var lr = _host.State.GetLastResult();
            if (lr == null) return;
            var message = lr.ContainsKey("message") ? Convert.ToString(lr["message"]) : "";
            var shot = lr.ContainsKey("shot") ? Convert.ToString(lr["shot"]) : "";
            var time = lr.ContainsKey("time") ? Convert.ToString(lr["time"]) : "";
            if (string.IsNullOrEmpty(message)) return;
            F<TextBlock>("TxtLastResult").Text = (time ?? "") + " · " + message;
            if (!string.IsNullOrEmpty(shot) && File.Exists(shot))
            {
                _lastShot = shot;
                ShowImage(F<Image>("ImgLastShot"), shot);
            }
        }

        private void RefreshShots()
        {
            var list = new List<ShotItem>();
            try
            {
                var files = new DirectoryInfo(_host.Paths.ShotDir).GetFiles("*.png");
                Array.Sort(files, delegate(FileInfo a, FileInfo b)
                {
                    return b.LastWriteTime.CompareTo(a.LastWriteTime);
                });
                foreach (var f in files)
                {
                    list.Add(new ShotItem
                    {
                        Path = f.FullName,
                        Display = f.LastWriteTime.ToString("MM-dd HH:mm:ss"),
                        Kind = KindOfShot(f.Name)
                    });
                    if (list.Count >= 60) break;
                }
            }
            catch { }

            var box = F<ListBox>("LstShots");
            box.ItemsSource = list;
            if (list.Count > 0) box.SelectedIndex = 0;
            if (list.Count == 0) F<Image>("ImgShotPreview").Source = null;
            F<Button>("BtnDeleteShot").IsEnabled = list.Count > 0;
            F<Button>("BtnClearShots").IsEnabled = list.Count > 0;
        }

        private static string KindOfShot(string fileName)
        {
            if (fileName.IndexOf("-woke", StringComparison.OrdinalIgnoreCase) >= 0) return "唤醒留证";
            if (fileName.IndexOf("-punchpage", StringComparison.OrdinalIgnoreCase) >= 0) return "打卡页";
            if (fileName.IndexOf("-result", StringComparison.OrdinalIgnoreCase) >= 0) return "打卡结果";
            if (fileName.IndexOf("-unlock-failed", StringComparison.OrdinalIgnoreCase) >= 0) return "解锁失败";
            return "";
        }

        private void DeleteSelectedShot()
        {
            var item = F<ListBox>("LstShots").SelectedItem as ShotItem;
            if (item == null)
            {
                F<TextBlock>("TxtLogHint").Text = "请先在列表里选中一张截图";
                return;
            }

            var name = Path.GetFileName(item.Path);
            var answer = MessageBox.Show("确定删除这张截图吗？\n\n" + name + "\n\n删除后无法恢复。",
                "删除截图", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            DeleteShotFile(item.Path);
            RefreshShots();
            F<TextBlock>("TxtLogHint").Text = "已删除 " + name;
            _host.Log.Info("已删除截图：" + item.Path);
        }

        private void ClearShots()
        {
            var dir = _host.Paths.ShotDir;
            string[] files;
            try { files = Directory.GetFiles(dir, "*.png"); }
            catch { files = new string[0]; }
            if (files.Length == 0)
            {
                F<TextBlock>("TxtLogHint").Text = "没有可删除的截图";
                return;
            }

            var answer = MessageBox.Show("确定清空全部 " + files.Length + " 张截图吗？\n\n删除后无法恢复。",
                "清空截图", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            var failed = 0;
            foreach (var f in files)
            {
                try { File.Delete(f); }
                catch { failed++; }
            }

            _lastShot = null;
            F<Image>("ImgLastShot").Source = null;
            F<Image>("ImgShotPreview").Source = null;
            var lr = _host.State.GetLastResult();
            if (lr != null && lr.ContainsKey("shot")) lr["shot"] = "";
            _host.State.Save();

            RefreshShots();
            F<TextBlock>("TxtLogHint").Text = failed == 0
                ? "已清空 " + files.Length + " 张截图"
                : "有 " + failed + " 张删除失败，可能被占用";
            _host.Log.Info("清空截图：" + files.Length + " 张，失败 " + failed);
        }

        private void DeleteShotFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                _host.Log.Warn("删除截图失败：" + ex.Message);
            }

            _host.State.ClearShotIf(path);
            if (string.Equals(_lastShot, path, StringComparison.OrdinalIgnoreCase))
            {
                _lastShot = null;
                F<Image>("ImgLastShot").Source = null;
            }
        }

        private void ShowPreview(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try
            {
                var win = new System.Windows.Window();
                win.Title = Path.GetFileName(path);
                win.Width = 760;
                win.Height = 1200;
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                win.Background = new SolidColorBrush(Color.FromRgb(10, 12, 16));
                win.WindowStyle = WindowStyle.ToolWindow;
                win.Content = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
                var img = (Image)win.Content;
                img.Source = LoadBitmap(path);
                win.MouseLeftButtonUp += delegate { win.Close(); };
                win.KeyDown += delegate(object s, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape) win.Close();
                };
                win.ShowDialog();
            }
            catch (Exception ex)
            {
                _host.Log.Warn("打开截图失败：" + ex.Message);
            }
        }

        private static BitmapImage LoadBitmap(string path)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        private void ShowImage(Image target, string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                target.Source = LoadBitmap(path);
            }
            catch { }
        }

        private void LoadSettingsToUi()
        {
            var cfg = _host.Config;
            F<RadioButton>("RbModeWake").IsChecked = cfg.S("mode", "wake") == "wake";
            F<RadioButton>("RbModeFull").IsChecked = cfg.S("mode", "wake") != "wake";
            F<TextBox>("TxtMorningStart").Text = cfg.S("windows.morning.start", "07:50");
            F<TextBox>("TxtMorningEnd").Text = cfg.S("windows.morning.end", "08:00");
            F<TextBox>("TxtEveningStart").Text = cfg.S("windows.evening.start", "17:40");
            F<TextBox>("TxtEveningEnd").Text = cfg.S("windows.evening.end", "17:50");
            F<CheckBox>("ChkAutostart").IsChecked = cfg.B("autostart", false);
            F<CheckBox>("ChkOpenApp").IsChecked = cfg.B("openDingTalkInWakeMode", true);
            F<CheckBox>("ChkAbortField").IsChecked = cfg.B("abortOnFieldPunch", true);
            F<CheckBox>("ChkScreenshots").IsChecked = cfg.B("captureScreenshots", true);
            F<CheckBox>("ChkPreflight").IsChecked = cfg.B("preflightCheck", true);
            F<CheckBox>("ChkSkipMissed").IsChecked = cfg.B("skipWhenMissed", true);
            F<PasswordBox>("PwdPin").Password = cfg.Pin;
            F<TextBox>("TxtAdbPath").Text = cfg.S("adbPath", Adb.ResolveDefault(_host.Paths.Root));
            F<CheckBox>("ChkLight").IsChecked = cfg.S("theme", "dark") == "light";
        }

        private static bool ValidTime(string text)
        {
            TimeSpan ts;
            return TimeSpan.TryParse((text ?? "").Trim(), out ts);
        }

        private void SaveSettings()
        {
            var cfg = _host.Config;
            var fields = new[]
            {
                F<TextBox>("TxtMorningStart"), F<TextBox>("TxtMorningEnd"),
                F<TextBox>("TxtEveningStart"), F<TextBox>("TxtEveningEnd")
            };
            foreach (var f in fields)
            {
                if (!ValidTime(f.Text))
                {
                    F<TextBlock>("TxtSettingsHint").Foreground = Res("Danger");
                    F<TextBlock>("TxtSettingsHint").Text = "时间格式不对，请用 HH:mm";
                    return;
                }
            }

            cfg.Set("mode", F<RadioButton>("RbModeWake").IsChecked == true ? "wake" : "full");
            cfg.Set("windows.morning.start", F<TextBox>("TxtMorningStart").Text.Trim());
            cfg.Set("windows.morning.end", F<TextBox>("TxtMorningEnd").Text.Trim());
            cfg.Set("windows.evening.start", F<TextBox>("TxtEveningStart").Text.Trim());
            cfg.Set("windows.evening.end", F<TextBox>("TxtEveningEnd").Text.Trim());
            cfg.Set("autostart", F<CheckBox>("ChkAutostart").IsChecked == true);
            cfg.Set("openDingTalkInWakeMode", F<CheckBox>("ChkOpenApp").IsChecked == true);
            cfg.Set("abortOnFieldPunch", F<CheckBox>("ChkAbortField").IsChecked == true);
            cfg.Set("captureScreenshots", F<CheckBox>("ChkScreenshots").IsChecked == true);
            cfg.Set("preflightCheck", F<CheckBox>("ChkPreflight").IsChecked == true);
            cfg.Set("theme", F<CheckBox>("ChkLight").IsChecked == true ? "light" : "dark");
            cfg.Set("adbPath", F<TextBox>("TxtAdbPath").Text.Trim());
            cfg.Pin = F<PasswordBox>("PwdPin").Password;
            cfg.Save();

            _host.Adb.Path = cfg.S("adbPath", Adb.ResolveDefault(_host.Paths.Root));
            ApplyAutostart();
            _host.ApplyTheme(cfg.S("theme", "dark"));
            UpdateOverview();

            F<TextBlock>("TxtSettingsHint").Foreground = Res("Success");
            F<TextBlock>("TxtSettingsHint").Text = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
            _host.Log.Info("设置已保存");
        }

        private void ResetDefaults()
        {
            F<RadioButton>("RbModeWake").IsChecked = true;
            F<TextBox>("TxtMorningStart").Text = "07:50";
            F<TextBox>("TxtMorningEnd").Text = "08:00";
            F<TextBox>("TxtEveningStart").Text = "17:40";
            F<TextBox>("TxtEveningEnd").Text = "17:50";
            F<CheckBox>("ChkOpenApp").IsChecked = true;
            F<CheckBox>("ChkAbortField").IsChecked = true;
            F<TextBlock>("TxtSettingsHint").Foreground = Res("TextSecondary");
            F<TextBlock>("TxtSettingsHint").Text = "已填入默认值，点保存生效";
        }

        private void ApplyAutostart()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    var exe = Process.GetCurrentProcess().MainModule.FileName;
                    if (_host.Config.B("autostart", false))
                        key.SetValue("DingTalkClockIn", "\"" + exe + "\"");
                    else
                        key.DeleteValue("DingTalkClockIn", false);
                }
            }
            catch (Exception ex)
            {
                _host.Log.Warn("设置开机自启失败：" + ex.Message);
            }
        }

        private async void TestAdb()
        {
            var path = F<TextBox>("TxtAdbPath").Text.Trim();
            _host.Adb.Path = path;
            F<TextBlock>("TxtSettingsHint").Foreground = Res("TextSecondary");
            F<TextBlock>("TxtSettingsHint").Text = "正在测试…";

            var msg = await Task.Run(delegate
            {
                try
                {
                    string serial;
                    var state = _host.Adb.DeviceState(out serial);
                    if (state == "unauthorized") return "设备未授权：请在手机上点「允许 USB 调试」";
                    if (state == "offline") return "设备 offline：请拔插数据线";
                    if (state != "device") return "未检测到设备：请检查数据线和 USB 调试";
                    var model = _host.Adb.GetProp("ro.product.model");
                    return "连接正常：" + model + "，电量 " + _host.Adb.BatteryLevel() + "%";
                }
                catch (Exception ex)
                {
                    return "失败：" + ex.Message;
                }
            });

            var ok = msg.StartsWith("连接正常");
            F<TextBlock>("TxtSettingsHint").Foreground = ok ? Res("Success") : Res("Danger");
            F<TextBlock>("TxtSettingsHint").Text = msg;
            _host.Log.Info("ADB 测试：" + msg);
        }

        // ---------------- 节假日 ----------------

        private void RefreshHolidayList()
        {
            F<ListBox>("LstHolidays").ItemsSource = null;
            F<ListBox>("LstHolidays").ItemsSource = _host.Holidays.All;
        }

        private void AddHoliday()
        {
            var date = F<TextBox>("TxtHolidayDate").Text.Trim();
            var type = F<RadioButton>("RbTypeWorkday").IsChecked == true ? "workday" : "holiday";
            var name = F<TextBox>("TxtHolidayName").Text.Trim();
            string message;
            var ok = _host.Holidays.AddManual(date, type, name, out message);
            F<TextBlock>("TxtHolidayHint").Foreground = ok ? Res("Success") : Res("Danger");
            F<TextBlock>("TxtHolidayHint").Text = message;
            RefreshHolidayList();
            UpdateOverview();
        }

        private void DeleteHoliday()
        {
            var item = F<ListBox>("LstHolidays").SelectedItem as HolidayItem;
            if (item == null)
            {
                F<TextBlock>("TxtHolidayHint").Foreground = Res("Warning");
                F<TextBlock>("TxtHolidayHint").Text = "请先在列表里选中一条";
                return;
            }
            var ok = _host.Holidays.RemoveManual(item.Date);
            F<TextBlock>("TxtHolidayHint").Foreground = ok ? Res("Success") : Res("Warning");
            F<TextBlock>("TxtHolidayHint").Text = ok ? "已删除 " + item.Date : "这条来自接口同步，只能手动覆盖";
            RefreshHolidayList();
            UpdateOverview();
        }

        private async void SyncYear()
        {
            F<TextBlock>("TxtHolidayHint").Foreground = Res("TextSecondary");
            F<TextBlock>("TxtHolidayHint").Text = "正在联网同步…";
            var year = DateTime.Today.Year;
            var message = "";
            var ok = await Task.Run(delegate
            {
                string msg;
                var r = _host.Holidays.SyncYear(year, out msg);
                message = msg;
                return r;
            });
            F<TextBlock>("TxtHolidayHint").Foreground = ok ? Res("Success") : Res("Danger");
            F<TextBlock>("TxtHolidayHint").Text = message;
            RefreshHolidayList();
            UpdateOverview();
        }

        private void ImportHoliday(bool csv)
        {
            var dlg = new OpenFileDialog();
            dlg.Filter = csv ? "CSV 文件|*.csv|所有文件|*.*" : "JSON 文件|*.json|所有文件|*.*";
            if (dlg.ShowDialog() != true) return;
            string message;
            try
            {
                if (csv) _host.Holidays.ImportCsv(dlg.FileName, out message);
                else _host.Holidays.ImportJson(dlg.FileName, out message);
                F<TextBlock>("TxtHolidayHint").Foreground = Res("Success");
            }
            catch (Exception ex)
            {
                message = "导入失败：" + ex.Message;
                F<TextBlock>("TxtHolidayHint").Foreground = Res("Danger");
            }
            F<TextBlock>("TxtHolidayHint").Text = message;
            RefreshHolidayList();
            UpdateOverview();
        }

        private void ExportHoliday()
        {
            var dlg = new SaveFileDialog();
            dlg.Filter = "CSV 文件|*.csv";
            dlg.FileName = "holidays-" + DateTime.Today.Year + ".csv";
            if (dlg.ShowDialog() != true) return;
            _host.Holidays.ExportCsv(dlg.FileName);
            F<TextBlock>("TxtHolidayHint").Foreground = Res("Success");
            F<TextBlock>("TxtHolidayHint").Text = "已导出 " + dlg.FileName;
        }

        // ---------------- 日志 ----------------

        private void BindLogs()
        {
            F<ListBox>("LstLogs").ItemsSource = _host.Log.Recent;
            F<TextBlock>("TxtLogHint").Text = "数据目录：" + _host.Paths.DataDir;
            F<TextBlock>("TxtLogHint").ToolTip = _host.Paths.DataDir;
            if (_host.Log.Recent.Count > 0)
                F<ListBox>("LstLogs").SelectedIndex = 0;
        }

        // ---------------- 自检 ----------------

        public void RenderTo(string path)
        {
            try
            {
                var root = Window.Content as FrameworkElement;
                if (root == null) return;
                root.Width = Window.Width;
                root.Height = Window.Height;
                root.Measure(new Size(root.Width, root.Height));
                root.Arrange(new Rect(0, 0, root.Width, root.Height));
                root.UpdateLayout();

                var rtb = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (var fs = File.Create(path)) enc.Save(fs);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(_host.Paths.DataDir, "render-error.txt"), ex.ToString());
                }
                catch { }
            }
        }

        public string SelfTest()
        {
            var sb = new StringBuilder();
            var names = new[]
            {
                "RootBorder", "TitleBar", "BtnMin", "BtnClose",
                "NavOverview", "NavSettings", "NavHoliday", "NavLogs",
                "PageOverview", "PageSettings", "PageHoliday", "PageLogs",
                "TxtDeviceModel", "TxtBattery", "TxtConnState", "TxtNextRun", "TxtCountdown",
                "TxtTodayState", "TxtTodayReason", "TxtLastResult", "ImgLastShot",
                "RbModeWake", "RbModeFull", "TxtMorningStart", "TxtEveningEnd",
                "ChkAutostart", "ChkOpenApp", "PwdPin", "TxtAdbPath",
                "LstHolidays", "LstLogs", "LstShots", "ImgShotPreview",
                "ChkScreenshots", "BtnDeleteShot", "BtnClearShots"
                , "ChkPreflight", "TxtLocation", "TxtNetwork", "TxtVersion"
            };
            var missing = 0;
            foreach (var n in names)
            {
                if (Window.FindName(n) == null)
                {
                    sb.AppendLine("MISSING " + n);
                    missing++;
                }
            }
            sb.AppendLine("elements=" + (names.Length - missing) + "/" + names.Length);

            string reason;
            var work = _host.Holidays.IsWorkday(DateTime.Today, out reason);
            sb.AppendLine("today=" + DateTime.Today.ToString("yyyy-MM-dd") + " workday=" + work + " reason=" + reason);

            _host.Scheduler.Recompute(DateTime.Now, false);
            sb.AppendLine("next=" + (_host.Scheduler.NextRun.HasValue
                ? _host.Scheduler.NextRun.Value.ToString("HH:mm:ss")
                : "-") + " session=" + (_host.Scheduler.NextSession ?? "-"));

            sb.AppendLine("adb=" + _host.Config.S("adbPath", ""));
            sb.AppendLine("mode=" + _host.Config.S("mode", "wake"));
            sb.AppendLine("RESULT=" + (missing == 0 ? "OK" : "FAIL"));
            return sb.ToString();
        }
    }
}
