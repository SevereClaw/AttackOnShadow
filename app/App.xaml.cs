using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AttackOnShadow.Device;
using AttackOnShadow.Services;

namespace AttackOnShadow
{
    public partial class App : Application
    {
        SingleInstance instance;
        Tray tray;
        MainWindow window;

        public AppSettings Settings { get; private set; }
        public DeviceManager Device { get; private set; }
        public AutoProfiles Auto { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args.ToList();
            DispatcherUnhandledException += (s, x) => { Log.Info("UI error: " + x.Exception); x.Handled = true; };

            // CI: protocol self-test
            int st = args.IndexOf("--selftest");
            if (st >= 0) { RunSelfTest(st + 1 < args.Count ? args[st + 1] : null); return; }

            // CI: render every page with a simulated keyboard
            int shot = args.IndexOf("--screenshot");
            if (shot >= 0) { _ = RunScreenshots(shot + 1 < args.Count ? args[shot + 1] : "shots"); return; }

            // Called by the old mod's updater (it runs the release exe with these switches): nothing to do.
            if (args.Contains("--silent") && (args.Contains("--install") || args.Contains("--uninstall"))) { Shutdown(0); return; }

            instance = new SingleInstance();
            if (!instance.IsFirst && ReplaceOlderCopies())
            {
                instance.Dispose();
                instance = new SingleInstance();
            }
            if (!instance.IsFirst) { instance.SignalFirst(); Shutdown(0); return; }

            Installer.CleanupOld();
            Settings = AppSettings.Load();
            Log.UsbEnabled = Settings.UsbLog;
            Log.Info($"start v{AppInfo.Version} {string.Join(" ", args)}");

            // first run from Downloads etc.: offer to install into %LOCALAPPDATA%\Programs
            if (!Installer.RunningInstalled && !args.Contains("--portable") && !args.Contains("--demo") && !args.Contains("--updated")
                && !File.Exists(Path.Combine(Paths.Data, "portable.flag")))
            {
                var r = MessageBox.Show(
                    "Установить AttackOnShadow?\n\nПрограмма скопируется в «Программы» вашей учётной записи и появится в меню «Пуск». " +
                    "Так работают автозапуск и автообновление.\n\n«Нет» — просто запустить отсюда.",
                    "AttackOnShadow", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (r == MessageBoxResult.Cancel) { Shutdown(0); return; }
                if (r == MessageBoxResult.Yes)
                {
                    try
                    {
                        Installer.Install();
                        instance.Dispose();
                        System.Diagnostics.Process.Start(Installer.InstalledExe, "--updated");
                        Shutdown(0);
                        return;
                    }
                    catch (Exception x) { MessageBox.Show("Не получилось установить: " + x.Message, "AttackOnShadow"); }
                }
                else { Directory.CreateDirectory(Paths.Data); File.WriteAllText(Path.Combine(Paths.Data, "portable.flag"), Paths.Exe); }
            }
            Autostart.Refresh();

            Device = new DeviceManager(Dispatcher, args.Contains("--demo"));
            Auto = new AutoProfiles(Settings, Device, Dispatcher);
            window = new MainWindow(this);
            tray = new Tray(this);
            Auto.Switched += msg => { window.ShowToast(msg); tray.Balloon(msg); };

            Device.Start();
            Auto.Start();
            instance.Listen(() => Dispatcher.BeginInvoke(new Action(ShowWindow)));

            if (args.Contains("--tray")) Log.Info("started in tray");
            else ShowWindow();
            window.StartUpdateChecks();
        }

        /// <summary>Closes running copies that are older than this one (e.g. 2.0.2 sitting in the tray).</summary>
        static bool ReplaceOlderCopies()
        {
            bool killed = false;
            var me = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("AttackOnShadow"))
            {
                try
                {
                    if (p.Id == me.Id) continue;
                    string theirs = p.MainModule.FileVersionInfo.ProductVersion ?? "0";
                    if (theirs.Contains("+")) theirs = theirs.Substring(0, theirs.IndexOf('+'));
                    if (Updater.Compare(AppInfo.Version, theirs) > 0)
                    {
                        Log.Info($"closing older copy {theirs} (pid {p.Id})");
                        p.Kill(); p.WaitForExit(3000); killed = true;
                    }
                }
                catch (Exception e) { Log.Info("replace older copy: " + e.Message); }
            }
            return killed;
        }

        public void ShowWindow()
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
            window.Topmost = true; window.Topmost = false;
        }

        public bool WindowVisible => window != null && window.IsVisible;

        public void Quit()
        {
            tray?.Dispose();
            instance?.Dispose();
            Shutdown(0);
        }

        // ---------- CI helpers ----------
        void RunSelfTest(string outFile)
        {
            var fails = K85Protocol.SelfTest();
            string report = fails.Count == 0 ? "selftest OK" : "selftest FAILED\n" + string.Join("\n", fails);
            if (outFile != null) File.WriteAllText(outFile, report);
            Shutdown(fails.Count == 0 ? 0 : 1);
        }

        async Task RunScreenshots(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                Settings = new AppSettings();
                Settings.Rules.Add(new GameRule { Exe = "cs2.exe", Layer = 1 });
                Settings.Rules.Add(new GameRule { Exe = "VALORANT-Win64-Shipping.exe", Layer = 1 });
                Settings.Rules.Add(new GameRule { Exe = "osu!.exe", Layer = 0 });
                Settings.FallbackLayer = 0;
                Device = new DeviceManager(Dispatcher, true);
                Auto = new AutoProfiles(Settings, Device, Dispatcher);
                window = new MainWindow(this) { Left = 0, Top = 0, ShowActivated = false };
                window.Show();
                Device.Start();
                await Task.Delay(1500);
                foreach (var page in new[] { "layers", "light", "auto", "settings" })
                {
                    window.ShowPage(page);
                    await Task.Delay(700);
                    window.UpdateLayout();
                    var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bmp.Render((Visual)window.Content);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(bmp));
                    using (var f = File.Create(Path.Combine(dir, page + ".png"))) enc.Save(f);
                }
                Shutdown(0);
            }
            catch (Exception x)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), x.ToString());
                Shutdown(1);
            }
        }
    }
}
