// AttackOnShadow setup — installs / updates / removes the mod for Attack Shark Driver.
//
// Built by GitHub Actions with the C# compiler that ships with Windows
// (.NET Framework 4.x, C# 5), so it needs no runtime install. The mod files are
// embedded as payload.zip; installing runs patch/patch.js inside the driver's
// own Electron (ELECTRON_RUN_AS_NODE=1), exactly like install.bat does.
//
//   AttackOnShadow.exe                         window
//   AttackOnShadow.exe --install [--silent] [--relaunch|--relaunch-hidden] [--driver "<exe or folder>"]
//   AttackOnShadow.exe --uninstall [--silent] [--driver ...]
//   AttackOnShadow.exe --extract "<folder>"    unpack the payload (used by CI)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("AttackOnShadow")]
[assembly: AssemblyProduct("AttackOnShadow")]
[assembly: AssemblyDescription("Persona 3 Reload mod for Attack Shark Driver")]

namespace AttackOnShadow
{
    static class Program
    {
        const string ExeName = "Attack Shark Driver.exe";
        const string ProcName = "Attack Shark Driver";
        public static string LogPath = Path.Combine(Path.GetTempPath(), "aos-setup.log");

        [STAThread]
        static int Main(string[] argv)
        {
            var args = new List<string>(argv);
            try
            {
                string extract = Arg(args, "--extract");
                if (extract != null) { ExtractPayload(extract); return 0; }

                bool install = args.Contains("--install"), uninstall = args.Contains("--uninstall");
                bool silent = args.Contains("--silent");
                string relaunch = args.Contains("--relaunch-hidden") ? "hidden" : args.Contains("--relaunch") ? "normal" : null;
                string driver = Arg(args, "--driver");

                if ((install || uninstall) && silent)
                {
                    string exe = ResolveDriver(driver) ?? FindDriver();
                    if (exe == null) { File.WriteAllText(LogPath, "Attack Shark Driver not found\r\n"); return 2; }
                    if (!CanWrite(exe))
                    {
                        if (IsAdmin()) { File.WriteAllText(LogPath, "No write access to " + Path.GetDirectoryName(exe) + "\r\n"); return 5; }
                        return Elevate(argv, exe);
                    }
                    var log = new StringBuilder();
                    int rc = Run(install ? "install" : "uninstall", exe, delegate (string line) { log.AppendLine(line); });
                    File.WriteAllText(LogPath, log.ToString(), Encoding.UTF8);
                    if (rc == 0 && relaunch != null) StartDriver(exe, relaunch == "hidden");
                    return rc;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var form = new SetupForm(ResolveDriver(driver) ?? FindDriver());
                if (install) form.Shown += delegate { form.DoAction("install"); };
                if (uninstall) form.Shown += delegate { form.DoAction("uninstall"); };
                Application.Run(form);
                return 0;
            }
            catch (Exception e)
            {
                try { File.WriteAllText(LogPath, e.ToString()); } catch { }
                if (!args.Contains("--silent")) MessageBox.Show(e.Message, "AttackOnShadow", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static string Arg(List<string> args, string name)
        {
            int i = args.IndexOf(name);
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }

        // ---------- payload ----------
        public static string ModVersion()
        {
            using (var zip = OpenPayload())
            {
                var e = zip.GetEntry("mod/version.json");
                if (e == null) return "?";
                using (var r = new StreamReader(e.Open(), Encoding.UTF8))
                {
                    var m = Regex.Match(r.ReadToEnd(), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                    return m.Success ? m.Groups[1].Value : "?";
                }
            }
        }
        static ZipArchive OpenPayload()
        {
            var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip");
            if (s == null) throw new Exception("payload.zip is missing from the build");
            return new ZipArchive(s, ZipArchiveMode.Read);
        }
        public static void ExtractPayload(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            using (var zip = OpenPayload())
            {
                foreach (var e in zip.Entries)
                {
                    if (string.IsNullOrEmpty(e.Name)) continue;
                    string dst = Path.Combine(dir, e.FullName.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    e.ExtractToFile(dst, true);
                }
            }
        }

        // ---------- locating the driver ----------
        public static string ResolveDriver(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            p = p.Trim().Trim('"');
            if (File.Exists(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(p);
            string inDir = Path.Combine(p, ExeName);
            return File.Exists(inDir) ? Path.GetFullPath(inDir) : null;
        }
        public static string FindDriver()
        {
            // 1. a running driver knows exactly where it is
            foreach (var pr in Process.GetProcessesByName(ProcName))
            {
                try { var f = pr.MainModule.FileName; if (File.Exists(f)) return f; } catch { }
            }
            // 2. usual folders
            var dirs = new List<string> {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Attack Shark Driver"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Attack Shark Driver"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Attack Shark Driver"),
            };
            foreach (var d in dirs) { var r = ResolveDriver(d); if (r != null) return r; }
            // 3. "installed programs" in the registry
            var roots = new[] { Registry.CurrentUser, Registry.LocalMachine };
            var keys = new[] { @"Software\Microsoft\Windows\CurrentVersion\Uninstall", @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (var root in roots) foreach (var key in keys)
            {
                try
                {
                    using (var k = root.OpenSubKey(key))
                    {
                        if (k == null) continue;
                        foreach (var sub in k.GetSubKeyNames())
                        {
                            using (var s = k.OpenSubKey(sub))
                            {
                                var name = s == null ? null : s.GetValue("DisplayName") as string;
                                if (name == null || !name.StartsWith("Attack Shark", StringComparison.OrdinalIgnoreCase)) continue;
                                var loc = s.GetValue("InstallLocation") as string;
                                var r = ResolveDriver(loc);
                                if (r != null) return r;
                                var icon = s.GetValue("DisplayIcon") as string;
                                if (icon != null) { r = ResolveDriver(Regex.Replace(icon, ",\\d+$", "").Trim('"')); if (r != null) return r; }
                            }
                        }
                    }
                }
                catch { }
            }
            return null;
        }
        public static string AppDir(string exe) { return Path.Combine(Path.GetDirectoryName(exe), "resources", "app"); }
        public static string InstalledInfo(string exe)
        {
            try
            {
                string f = Path.Combine(AppDir(exe), "aos", "INSTALLED");
                if (!File.Exists(f)) return null;
                var m = Regex.Match(File.ReadAllText(f), "\"version\"\\s*:\\s*\"([^\"]+)\"");
                return m.Success ? m.Groups[1].Value : "старая версия";
            }
            catch { return null; }
        }

        // ---------- rights ----------
        public static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        public static bool CanWrite(string exe)
        {
            try
            {
                string probe = Path.Combine(AppDir(exe), ".aos-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }
        public static int Elevate(string[] argv, string exe)
        {
            var a = new List<string>(argv);
            int i = a.IndexOf("--driver");
            if (i >= 0) a.RemoveRange(i, Math.Min(2, a.Count - i));
            a.Add("--driver"); a.Add(exe);
            var psi = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, string.Join(" ", a.Select(Quote)))
            { Verb = "runas", UseShellExecute = true };
            try { var p = Process.Start(psi); p.WaitForExit(); return p.ExitCode; }
            catch (System.ComponentModel.Win32Exception) { return 6; } // UAC cancelled
        }
        static string Quote(string s) { return s.IndexOfAny(new[] { ' ', '"' }) >= 0 ? "\"" + s.Replace("\"", "\\\"") + "\"" : s; }

        // ---------- doing the work ----------
        public static void KillDriver()
        {
            foreach (var p in Process.GetProcessesByName(ProcName))
            {
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
            Thread.Sleep(500);
        }
        public static int Run(string action, string exe, Action<string> log)
        {
            string work = Path.Combine(Path.GetTempPath(), "AttackOnShadow-setup");
            ExtractPayload(work);
            KillDriver();
            var psi = new ProcessStartInfo(exe, Quote(Path.Combine(work, "patch", "patch.js")) + " " + action + " " + Quote(Path.GetDirectoryName(exe)))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["ELECTRON_RUN_AS_NODE"] = "1";
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
                p.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e) { if (e.Data != null) log(e.Data); };
                p.Start();
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } log("Патчер не ответил за 2 минуты"); return 3; }
                p.WaitForExit();
                return p.ExitCode;
            }
        }
        public static void StartDriver(string exe, bool hidden)
        {
            var psi = new ProcessStartInfo(exe, hidden ? "--aos-hidden" : "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            psi.EnvironmentVariables.Remove("ELECTRON_RUN_AS_NODE");
            Process.Start(psi);
        }
    }

    // ---------- the window ----------
    class SetupForm : Form
    {
        static readonly Color Navy0 = Color.FromArgb(2, 10, 38), Navy1 = Color.FromArgb(6, 26, 77), Navy2 = Color.FromArgb(11, 45, 125);
        static readonly Color Cyan = Color.FromArgb(41, 211, 255), Ice = Color.FromArgb(216, 236, 255), Muted = Color.FromArgb(143, 178, 230);

        string exe;
        readonly TextBox pathBox;
        readonly Label status;
        readonly RichTextBox logBox;
        readonly PlateButton installBtn, removeBtn, launchBtn, browseBtn;
        bool busy;

        static Font F(float size, FontStyle st)
        {
            foreach (var name in new[] { "Bahnschrift SemiBold SemiConden", "Bahnschrift", "Segoe UI" })
            {
                var f = new Font(name, size, st);
                if (f.Name == name) return f;
            }
            return new Font(FontFamily.GenericSansSerif, size, st);
        }

        public SetupForm(string found)
        {
            exe = found;
            Text = "AttackOnShadow " + Program.ModVersion();
            try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
            ClientSize = new Size(620, 430);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Navy1;
            ForeColor = Ice;
            Font = F(10f, FontStyle.Regular);
            DoubleBuffered = true;

            Controls.Add(new Label { Text = "Папка драйвера", Location = new Point(28, 92), AutoSize = true, ForeColor = Muted, BackColor = Color.Transparent });
            pathBox = new TextBox { Location = new Point(28, 114), Width = 440, BackColor = Navy0, ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, ReadOnly = true };
            Controls.Add(pathBox);
            browseBtn = new PlateButton("Обзор…", false) { Location = new Point(482, 110), Size = new Size(110, 30) };
            browseBtn.Click += delegate { Browse(); };
            Controls.Add(browseBtn);

            status = new Label { Location = new Point(28, 152), Size = new Size(564, 44), ForeColor = Ice, BackColor = Color.Transparent };
            Controls.Add(status);

            installBtn = new PlateButton("Установить", true) { Location = new Point(28, 200), Size = new Size(170, 36) };
            installBtn.Click += delegate { DoAction("install"); };
            removeBtn = new PlateButton("Удалить", false) { Location = new Point(212, 200), Size = new Size(130, 36) };
            removeBtn.Click += delegate { DoAction("uninstall"); };
            launchBtn = new PlateButton("Запустить драйвер", false) { Location = new Point(356, 200), Size = new Size(190, 36) };
            launchBtn.Click += delegate { if (exe != null) Program.StartDriver(exe, false); };
            Controls.AddRange(new Control[] { installBtn, removeBtn, launchBtn });

            logBox = new RichTextBox
            {
                Location = new Point(28, 252), Size = new Size(564, 152), ReadOnly = true, BorderStyle = BorderStyle.None,
                BackColor = Navy0, ForeColor = Muted, Font = new Font("Consolas", 9f), DetectUrls = false,
            };
            Controls.Add(logBox);
            Refresh2();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new LinearGradientBrush(ClientRectangle, Navy2, Navy0, 70f)) g.FillRectangle(bg, ClientRectangle);
            using (var streak = new SolidBrush(Color.FromArgb(18, 120, 200, 255)))
                g.FillPolygon(streak, new[] { new Point(380, 0), new Point(470, 0), new Point(300, ClientSize.Height), new Point(210, ClientSize.Height) });
            // title plate
            var plate = new[] { new Point(36, 22), new Point(300, 22), new Point(288, 64), new Point(24, 64) };
            using (var sh = new SolidBrush(Cyan)) g.FillPolygon(sh, plate.Select(p => new Point(p.X + 6, p.Y + 5)).ToArray());
            g.FillPolygon(Brushes.White, plate);
            using (var tf = F(19f, FontStyle.Bold | FontStyle.Italic))
            using (var tb = new SolidBrush(Navy1)) g.DrawString("AttackOnShadow", tf, tb, 44, 27);
            using (var vf = F(10f, FontStyle.Italic))
            using (var vb = new SolidBrush(Muted)) g.DrawString("v" + Program.ModVersion() + " · мод для Attack Shark Driver", vf, vb, 318, 38);
            using (var line = new Pen(Cyan, 3)) g.DrawLine(line, 0, 0, 0, ClientSize.Height);
        }

        void Refresh2()
        {
            pathBox.Text = exe != null ? Path.GetDirectoryName(exe) : "не найдена — нажми «Обзор…» и выбери Attack Shark Driver.exe";
            if (exe == null) { status.Text = "Драйвер не найден."; }
            else
            {
                var inst = Program.InstalledInfo(exe);
                string mine = Program.ModVersion();
                status.Text = inst == null ? "Мод не установлен."
                    : inst == mine ? "Установлен AttackOnShadow v" + inst + " — последняя версия из этого файла."
                    : "Установлен AttackOnShadow v" + inst + ". Этот файл поставит v" + mine + ".";
                installBtn.Text = inst == null ? "Установить" : inst == mine ? "Переустановить" : "Обновить";
            }
            installBtn.Enabled = removeBtn.Enabled = launchBtn.Enabled = exe != null && !busy;
            browseBtn.Enabled = !busy;
            installBtn.Invalidate();
        }

        void Browse()
        {
            using (var d = new OpenFileDialog { Filter = "Attack Shark Driver|Attack Shark Driver.exe|Программы|*.exe", Title = "Где лежит Attack Shark Driver.exe?" })
            {
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    var r = Program.ResolveDriver(d.FileName);
                    if (r == null || !Directory.Exists(Program.AppDir(r))) { MessageBox.Show(this, "Это не похоже на Attack Shark Driver (нет resources\\app).", "AttackOnShadow"); return; }
                    exe = r; Refresh2();
                }
            }
        }

        void Log(string s)
        {
            if (InvokeRequired) { BeginInvoke(new Action<string>(Log), s); return; }
            logBox.AppendText(s + "\n");
            logBox.ScrollToCaret();
        }

        public void DoAction(string action)
        {
            if (exe == null || busy) return;
            if (!Program.CanWrite(exe))
            {
                if (Program.IsAdmin()) { Log("Нет прав на запись в " + Path.GetDirectoryName(exe)); return; }
                Log("Нужны права администратора, спрашиваю Windows…");
                busy = true; Refresh2();
                var t0 = new Thread(delegate ()
                {
                    int rc = Program.Elevate(new[] { "--" + action, "--silent" }, exe);
                    try { foreach (var l in File.ReadAllLines(Program.LogPath)) Log(l); } catch { }
                    Log(rc == 0 ? "Готово." : rc == 6 ? "Отменено." : "Ошибка, код " + rc);
                    BeginInvoke(new Action(delegate { busy = false; Refresh2(); }));
                }) { IsBackground = true };
                t0.Start();
                return;
            }
            busy = true; Refresh2();
            logBox.Clear();
            var t = new Thread(delegate ()
            {
                int rc;
                try { rc = Program.Run(action, exe, Log); }
                catch (Exception e) { Log(e.Message); rc = 1; }
                Log(rc == 0 ? (action == "install" ? "Готово. Можно запускать драйвер." : "Готово. Оригинальный драйвер возвращён.") : "Ошибка, код " + rc);
                BeginInvoke(new Action(delegate { busy = false; Refresh2(); }));
            }) { IsBackground = true };
            t.Start();
        }
    }

    // A skewed P3R-style plate button.
    class PlateButton : Control
    {
        readonly bool main;
        bool hover;
        public PlateButton(string text, bool main)
        {
            this.main = main; Text = text; Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width - 8, h = Height - 5, k = 8;
            var poly = new[] { new Point(k, 0), new Point(w, 0), new Point(w - k, h), new Point(0, h) };
            Color fill = !Enabled ? Color.FromArgb(40, 60, 110) : hover ? Color.White : main ? Color.FromArgb(31, 107, 255) : Color.FromArgb(18, 50, 130);
            Color fg = !Enabled ? Color.FromArgb(110, 130, 170) : hover ? Color.FromArgb(6, 26, 77) : Color.FromArgb(216, 236, 255);
            if (Enabled && (hover || main))
                using (var sh = new SolidBrush(Color.FromArgb(41, 211, 255))) g.FillPolygon(sh, poly.Select(p => new Point(p.X + 5, p.Y + 4)).ToArray());
            using (var b = new SolidBrush(fill)) g.FillPolygon(b, poly);
            using (var tb = new SolidBrush(fg))
                TextRenderer.DrawText(g, Text, new Font(Parent != null ? Parent.Font : Font, FontStyle.Italic), new Rectangle(0, 0, w, h), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }
}
