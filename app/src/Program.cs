using System;
using System.IO;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using WinForms = System.Windows.Forms;

namespace DingTalkClockIn
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            var selftest = args.Length > 0 &&
                           string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase);

            bool created;
            using (var mutex = new Mutex(true, "DingTalkClockIn.SingleInstance", out created))
            {
                if (!created && !selftest) return;
                var host = new AppHost(selftest);
                host.Run();
            }
        }
    }

    public class AppHost : Application
    {
        public AppPaths Paths { get; private set; }
        public Config Config { get; private set; }
        public Logger Log { get; private set; }
        public HolidayService Holidays { get; private set; }
        public Adb Adb { get; private set; }
        public PunchEngine Engine { get; private set; }
        public AppState State { get; private set; }
        public Scheduler Scheduler { get; private set; }

        private readonly bool _selftest;
        private MainWindow _window;
        private WinForms.NotifyIcon _tray;

        public AppHost(bool selftest)
        {
            _selftest = selftest;
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Paths = new AppPaths(AppDomain.CurrentDomain.BaseDirectory);
            Config = Config.Load(Paths.ConfigFile);
            Log = new Logger(Paths.LogDir, Config.I("logRetentionDays", 30));
            Log.TrimOldFiles();
            Log.LoadToday();
            BackupAdbKey();

            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(Paths.DataDir, "crash.txt"),
                        DateTime.Now + Environment.NewLine + ex.ExceptionObject);
                }
                catch { }
            };
            DispatcherUnhandledException += delegate(object s, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs ex)
            {
                try { Log.Error("未处理异常：" + ex.Exception.Message); } catch { }
                try
                {
                    File.AppendAllText(Path.Combine(Paths.DataDir, "crash.txt"),
                        DateTime.Now + Environment.NewLine + ex.Exception + Environment.NewLine);
                }
                catch { }
                ex.Handled = true;
            };

            Holidays = new HolidayService(Paths, Log);
            Holidays.Load();
            Adb = new Adb(Config.S("adbPath", Adb.ResolveDefault(Paths.Root)));
            Engine = new PunchEngine(Config, Adb, Log, Paths);
            State = AppState.Load(Paths.StateFile);
            Scheduler = new Scheduler(Config, Holidays, State);

            ApplyTheme(Config.S("theme", "dark"));

            _window = new MainWindow(this);

            var cmd = Environment.GetCommandLineArgs();
            if (cmd.Length > 2 && string.Equals(cmd[1], "--render", StringComparison.OrdinalIgnoreCase))
            {
                if (cmd.Length > 3) _window.ShowPageByName(cmd[3]);
                System.Threading.Thread.Sleep(3500);
                _window.RenderTo(cmd[2]);
                Shutdown(0);
                return;
            }

            if (_selftest)
            {
                var report = _window.SelfTest();
                File.WriteAllText(Path.Combine(Paths.DataDir, "selftest.txt"), report);
                Shutdown(0);
                return;
            }

            _window.Window.Show();
            CreateTray();
            _window.StartTimer();
            Log.Info("程序已启动，模式=" + Config.S("mode", "wake"));
        }

        public void ApplyTheme(string theme)
        {
            var res = Resources;
            Action<string, string> set = delegate(string key, string hex)
            {
                res[key] = new SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
            };

            if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
            {
                set("Bg", "#F5F7FB");
                set("BgAlt", "#FFFFFF");
                set("CardBg", "#FFFFFF");
                set("CardHover", "#EEF2F9");
                set("Line", "#E1E7F0");
                set("TextPrimary", "#1A1F27");
                set("TextSecondary", "#5C6675");
                set("Accent", "#2F6BFF");
                set("AccentSoft", "#E6EEFF");
                set("Success", "#12965A");
                set("Warning", "#C77F06");
                set("Danger", "#D64545");
            }
            else
            {
                set("Bg", "#0F1115");
                set("BgAlt", "#161A21");
                set("CardBg", "#1A1F28");
                set("CardHover", "#222A35");
                set("Line", "#29313D");
                set("TextPrimary", "#E7ECF5");
                set("TextSecondary", "#8B96A8");
                set("Accent", "#5B93FF");
                set("AccentSoft", "#1E3050");
                set("Success", "#37C483");
                set("Warning", "#F0A63A");
                set("Danger", "#FF6B6B");
            }
        }

        private void CreateTray()
        {
            _tray = new WinForms.NotifyIcon();
            _tray.Icon = IconFactory.Create();
            _tray.Text = "钉钉打卡助手";
            _tray.Visible = true;

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("打开主界面", null, delegate { ShowMain(); });
            menu.Items.Add("立即试跑（干跑）", null, delegate { if (_window != null) _window.RunNowFromTray(); });
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApp(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowMain(); };
        }

        /// <summary>
        /// adb 的授权密钥在 %USERPROFILE%\.android 下，一旦丢失就会重新生成，
        /// 手机侧看到的是一把新钥匙，需要重新授权。这里做一份备份并自动还原。
        /// </summary>
        public void BackupAdbKey()
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var dir = Path.Combine(home, ".android");
                var key = Path.Combine(dir, "adbkey");
                var pub = Path.Combine(dir, "adbkey.pub");
                var bakKey = Path.Combine(Paths.DataDir, "adbkey.backup");
                var bakPub = Path.Combine(Paths.DataDir, "adbkey.pub.backup");

                if (File.Exists(key) && File.Exists(pub))
                {
                    if (!File.Exists(bakKey) || !SameFile(key, bakKey))
                    {
                        if (File.Exists(bakKey))
                        {
                            File.Copy(bakKey, bakKey + ".prev", true);
                            if (File.Exists(bakPub)) File.Copy(bakPub, bakPub + ".prev", true);
                            Log.Warn("检测到 ADB 密钥发生变化，旧密钥已留档；若手机提示未授权，请在手机上点「允许 USB 调试」");
                        }
                        File.Copy(key, bakKey, true);
                        File.Copy(pub, bakPub, true);
                        Log.Info("已备份 ADB 密钥");
                    }
                    return;
                }

                if (File.Exists(bakKey) && File.Exists(bakPub))
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(bakKey, key, true);
                    File.Copy(bakPub, pub, true);
                    Log.Warn("检测到 ADB 密钥缺失，已从备份恢复（若手机仍提示未授权，请点允许）");
                }
            }
            catch (Exception ex)
            {
                try { Log.Warn("ADB 密钥备份失败：" + ex.Message); } catch { }
            }
        }

        private static bool SameFile(string a, string b)
        {
            try
            {
                var x = File.ReadAllBytes(a);
                var y = File.ReadAllBytes(b);
                if (x.Length != y.Length) return false;
                for (var i = 0; i < x.Length; i++)
                {
                    if (x[i] != y[i]) return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void ShowMain()
        {
            if (_window == null || _window.Window == null) return;
            _window.Window.Show();
            _window.Window.WindowState = WindowState.Normal;
            _window.Window.Activate();
        }

        public void ExitApp()
        {
            try
            {
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            }
            catch { }
            Shutdown(0);
        }

        public void Notify(string title, string text, bool error)
        {
            if (!Config.B("notifications", true)) return;
            if (_tray == null) return;
            try
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = error ? WinForms.ToolTipIcon.Warning : WinForms.ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
            }
            catch { }
        }
    }

    public static class IconFactory
    {
        public static System.Drawing.Icon Create()
        {
            using (var bmp = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    var rect = new Rectangle(1, 1, 30, 30);
                    using (var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                        rect, System.Drawing.Color.FromArgb(91, 147, 255), System.Drawing.Color.FromArgb(55, 196, 131), 45f))
                    {
                        g.FillEllipse(brush, rect);
                    }
                    using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, 3f))
                    {
                        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        g.DrawLines(pen, new[]
                        {
                            new System.Drawing.Point(9, 17),
                            new System.Drawing.Point(14, 22),
                            new System.Drawing.Point(23, 11)
                        });
                    }
                }
                return System.Drawing.Icon.FromHandle(bmp.GetHicon());
            }
        }
    }
}
