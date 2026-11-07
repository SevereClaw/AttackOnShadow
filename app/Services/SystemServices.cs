using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace AttackOnShadow.Services
{
    public static class AppInfo
    {
        public const string Repo = "SevereClaw/AttackOnShadow";
        public static string Version
        {
            get
            {
                var a = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                var v = a?.InformationalVersion ?? "0.0.0";
                int plus = v.IndexOf('+');
                return plus > 0 ? v.Substring(0, plus) : v;
            }
        }
    }

    /// <summary>Start with Windows (into the tray) via HKCU\...\Run.</summary>
    public static class Autostart
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "AttackOnShadow";
        public static bool Enabled
        {
            get { using (var k = Registry.CurrentUser.OpenSubKey(Key)) return k?.GetValue(Name) is string; }
        }
        public static void Set(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (on) k.SetValue(Name, "\"" + Paths.Exe + "\" --tray");
                else k.DeleteValue(Name, false);
            }
        }
        /// <summary>Keeps the Run entry pointing at the current exe (after moving or updating).</summary>
        public static void Refresh() { if (Enabled) Set(true); }
    }

    /// <summary>One running copy; a second launch just shows the first window.</summary>
    public sealed class SingleInstance : IDisposable
    {
        readonly Mutex mutex;
        readonly EventWaitHandle showSignal;
        public bool IsFirst { get; }

        public SingleInstance()
        {
            mutex = new Mutex(true, @"Local\AttackOnShadow.App", out bool created);
            IsFirst = created;
            showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AttackOnShadow.Show");
        }
        public void SignalFirst() => showSignal.Set();
        public void Listen(Action onShow)
        {
            var t = new Thread(() => { while (showSignal.WaitOne()) onShow(); }) { IsBackground = true, Name = "show-signal" };
            t.Start();
        }
        public void Dispose() { try { if (IsFirst) mutex.ReleaseMutex(); } catch { } mutex.Dispose(); }
    }

    /// <summary>Copies the exe to %LOCALAPPDATA%\Programs\AttackOnShadow and adds a Start menu shortcut.</summary>
    public static class Installer
    {
        public static string InstalledExe => Path.Combine(Paths.InstallDir, "AttackOnShadow.exe");
        public static bool RunningInstalled => string.Equals(Path.GetFullPath(Paths.Exe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

        public static void Install()
        {
            Directory.CreateDirectory(Paths.InstallDir);
            File.Copy(Paths.Exe, InstalledExe, true);
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "AttackOnShadow.lnk"), InstalledExe);
            Log.Info("installed to " + InstalledExe);
        }

        static void CreateShortcut(string lnk, string target)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(shellType);
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = target;
                sc.WorkingDirectory = Path.GetDirectoryName(target);
                sc.Description = "AttackOnShadow — Attack Shark K85";
                sc.IconLocation = target + ",0";
                sc.Save();
            }
            catch (Exception e) { Log.Info("shortcut failed: " + e.Message); }
        }

        public static void CleanupOld()
        {
            try { foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Paths.Exe), "*.old")) File.Delete(f); } catch { }
        }
    }

    public sealed class ReleaseInfo
    {
        public string Version, Url, ExeUrl, Notes;
    }

    /// <summary>Checks GitHub releases and replaces the running exe with the new one.</summary>
    public static class Updater
    {
        static HttpClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("AttackOnShadow/" + AppInfo.Version);
            return c;
        }

        public static int Compare(string a, string b)
        {
            int[] P(string s) => Regex.Replace(s ?? "", "^[vV]", "").Split('.', '-').Select(x => int.TryParse(x, out int n) ? n : 0).ToArray();
            var pa = P(a); var pb = P(b);
            for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int x = i < pa.Length ? pa[i] : 0, y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        public static async Task<ReleaseInfo> LatestAsync()
        {
            using (var c = Client())
            {
                c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                string json = await c.GetStringAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest");
                var info = new ReleaseInfo
                {
                    Version = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"").Groups[1].Value,
                    Url = Regex.Match(json, "\"html_url\"\\s*:\\s*\"(https://github.com/[^\"]+/releases/tag/[^\"]+)\"").Groups[1].Value,
                };
                var exe = Regex.Match(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+/AttackOnShadow\\.exe)\"");
                if (exe.Success) info.ExeUrl = exe.Groups[1].Value;
                return info;
            }
        }

        /// <summary>Downloads the new exe next to the current one, swaps them and starts the new copy.</summary>
        public static async Task InstallAsync(ReleaseInfo r, bool startHidden)
        {
            if (string.IsNullOrEmpty(r.ExeUrl)) throw new Exception("В релизе нет AttackOnShadow.exe");
            string exe = Paths.Exe, dir = Path.GetDirectoryName(exe);
            string fresh = Path.Combine(dir, "AttackOnShadow.exe.new");
            using (var c = Client())
            using (var s = await c.GetStreamAsync(r.ExeUrl))
            using (var f = File.Create(fresh))
                await s.CopyToAsync(f);
            if (new FileInfo(fresh).Length < 50 * 1024) throw new Exception("Скачанный файл повреждён");
            string old = exe + "." + DateTime.Now.Ticks + ".old";
            File.Move(exe, old);        // a running exe can be renamed, not overwritten
            File.Move(fresh, exe);
            Log.Info($"updated to {r.Version}");
            Process.Start(new ProcessStartInfo(exe, (startHidden ? "--tray " : "") + "--updated") { UseShellExecute = false, WorkingDirectory = dir });
        }
    }

    /// <summary>The previous product: a mod patched into Attack Shark Driver. Can be removed from the app.</summary>
    public static class LegacyMod
    {
        public static string FindDriverApp()
        {
            var dirs = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Attack Shark Driver"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Attack Shark Driver"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Attack Shark Driver"),
            };
            foreach (var d in dirs)
            {
                string app = Path.Combine(d, "resources", "app");
                if (Directory.Exists(Path.Combine(app, ".aos-backup"))) return app;
            }
            return null;
        }

        /// <summary>Puts the driver's original files back (same as the mod's uninstall).</summary>
        public static void Remove(string app)
        {
            foreach (var p in Process.GetProcessesByName("Attack Shark Driver")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            string backup = Path.Combine(app, ".aos-backup");
            foreach (var f in Directory.GetFiles(backup, "*", SearchOption.AllDirectories))
            {
                string dst = Path.Combine(app, f.Substring(backup.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(f, dst, true);
            }
            foreach (var d in new[] { Path.Combine(app, "dist", "aos"), Path.Combine(app, "aos"), backup })
                if (Directory.Exists(d)) Directory.Delete(d, true);
            Log.Info("legacy mod removed from " + app);
        }
    }
}
