using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using AttackOnShadow.Device;

namespace AttackOnShadow.Services
{
    /// <summary>
    /// Experimental: plays a GIF on the keys when a key is pressed. The K85 has no
    /// live-frame command, so every frame is written as the custom picture of slot 5
    /// (7 packets), at most 8 frames per second and only while an animation runs.
    /// </summary>
    public sealed class ReactiveLights
    {
        public const int Slot = 4; // slot 5 is used for the animation

        readonly AppSettings settings;
        readonly DeviceManager device;
        readonly Dispatcher ui;
        readonly Dictionary<string, List<KeyFrame>> cache = new Dictionary<string, List<KeyFrame>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<int> down = new HashSet<int>();
        IntPtr hook = IntPtr.Zero;
        HookProc proc;
        Lighting saved;
        int setupLayer = -1;
        bool starting;
        CancellationTokenSource loopCts, wakeCts;
        Task loop;
        List<KeyFrame> anim;
        DateTime animStart;
        int frames;
        DateTime lastFailure = DateTime.MinValue;

        public bool Active { get; private set; }
        public string Status { get; private set; } = "Выключено.";
        public event Action Changed;
        public event Action<byte[]> Frame;   // what was just sent to the keyboard (for the on-screen preview)

        public ReactiveLights(AppSettings s, DeviceManager d, Dispatcher dispatcher)
        {
            settings = s; device = d; ui = dispatcher;
            device.Changed += OnDevice;
        }

        void OnDevice()
        {
            if (!device.Connected)
            {
                if (Active) { StopLoop(); Active = false; SetStatus("Клавиатура отключена — включу снова, когда она вернётся."); }
                return;
            }
            if (settings.ReactiveEnabled && !Active && !starting) _ = StartAsync();
            else if (Active && device.Layer >= 0 && setupLayer >= 0 && device.Layer != setupLayer && !starting) _ = Resetup();
        }

        // ---------- start / stop ----------
        public async Task<bool> StartAsync()
        {
            if (Active || starting) return Active;
            if (!device.Connected) { SetStatus("Включится, когда клавиатура подключится."); return false; }
            starting = true;
            try
            {
                SetStatus("Включаю…");
                var cur = await device.Run("reactive: read lighting", kb => kb.GetLightingAsync());
                if (saved == null || cur != null && cur.Mode != K85Protocol.UserPictureMode) saved = cur;
                if (!await SetPictureMode()) { SetStatus("Не удалось включить режим картинки."); return false; }
                InstallHook();
                Active = true;
                frames = 0;
                loopCts = new CancellationTokenSource();
                anim = null;
                loop = Loop(loopCts.Token);
                Log.Info("reactive GIF on");
                SetStatus("Включено. Нажимай клавиши.");
                return true;
            }
            finally { starting = false; }
        }

        async Task<bool> SetPictureMode()
        {
            var l = new Lighting { Mode = K85Protocol.UserPictureMode, Variant = Slot, Brightness = saved != null && saved.Mode != 0 ? Math.Max(1, saved.Brightness) : 4 };
            bool ok = await device.Run("reactive: picture mode", kb => kb.SetLightingAsync(l), false);
            setupLayer = device.Layer;
            return ok;
        }

        async Task Resetup()
        {
            // a layer has its own lighting: switch that one to the picture too
            starting = true;
            try { await SetPictureMode(); Wake(); }
            finally { starting = false; }
        }

        /// <summary>Turns the mode off; restore = put back the lighting that was on before.</summary>
        public async Task StopAsync(bool restore)
        {
            UninstallHook();
            bool was = Active;
            Active = false;
            await StopLoopAsync();
            if (was && restore && device.Connected)
            {
                // the picture mode was already on when we started (e.g. after a restart): fall back to a rainbow wave
                var back = saved == null || saved.Mode == K85Protocol.UserPictureMode
                    ? new Lighting { Mode = 4, Brightness = 4, Speed = 2, Rainbow = true }
                    : saved;
                await device.Run("reactive: restore lighting", kb => kb.SetLightingAsync(back), false);
            }
            if (was) Log.Info($"reactive GIF off after {frames} frames");
            SetStatus("Выключено.");
        }

        void StopLoop() { UninstallHook(); loopCts?.Cancel(); wakeCts?.Cancel(); }

        async Task StopLoopAsync()
        {
            StopLoop();
            if (loop != null) { try { await loop; } catch { } loop = null; }
        }

        // ---------- playing ----------
        /// <summary>Starts the animation for this key (the key's own GIF, else the default one).</summary>
        public void Play(int matrix)
        {
            var frames = FramesFor(matrix);
            if (frames == null || frames.Count == 0) return;
            anim = frames;
            animStart = DateTime.Now;
            Wake();
        }

        public List<KeyFrame> FramesFor(int matrix)
        {
            var rule = settings.KeyGifs.FirstOrDefault(r => r.Matrix == matrix);
            string file = rule?.File ?? settings.DefaultGif;
            if (file != null)
            {
                var f = Load(file);
                if (f != null) return f;
            }
            return KeyImage.Ripple(matrix);
        }

        public List<KeyFrame> Load(string file)
        {
            if (cache.TryGetValue(file, out var f)) return f;
            try { f = KeyImage.Load(file); }
            catch (Exception e) { Log.Info($"gif {file}: {e.Message}"); f = null; }
            cache[file] = f;
            return f;
        }

        public void Forget(string file) { if (file != null) cache.Remove(file); }

        void Wake() { var w = wakeCts; w?.Cancel(); }

        (byte[] rgb, int wait) Current()
        {
            var a = anim;
            if (a == null) return (null, -1);
            int t = (int)(DateTime.Now - animStart).TotalMilliseconds, acc = 0;
            foreach (var f in a)
            {
                if (t < acc + f.DelayMs) return (f.Rgb, acc + f.DelayMs - t);
                acc += f.DelayMs;
            }
            anim = null;
            return (KeyImage.Black(), -1); // animation over: keys go dark until the next press
        }

        async Task Loop(CancellationToken ct)
        {
            byte[] last = null;
            var pending = KeyImage.Black();
            while (!ct.IsCancellationRequested)
            {
                wakeCts = new CancellationTokenSource();
                var (rgb, wait) = Current();
                if (rgb == null) rgb = pending;
                pending = null;
                if (rgb != null && (last == null || !rgb.SequenceEqual(last)))
                {
                    var sent = rgb;
                    bool ok = await device.Run("reactive frame", kb => kb.SetUserPictureAsync(sent, true), false);
                    if (ct.IsCancellationRequested) break;
                    if (!ok)
                    {
                        // one lost connection may be an unplugged cable: the mode comes back on reconnect.
                        // Two within ten minutes means the keyboard does not cope: switch the mode off.
                        bool repeated = (DateTime.Now - lastFailure).TotalMinutes < 10;
                        lastFailure = DateTime.Now;
                        Log.Info($"reactive frame failed after {frames} frames" + (repeated ? " (again)" : ""));
                        if (!repeated && !device.Connected) return;
                        Active = false;
                        UninstallHook();
                        settings.ReactiveEnabled = false; settings.Save();
                        SetStatus("Клавиатура не приняла кадр — режим выключен. Пришли usb.txt и log.txt.");
                        return;
                    }
                    last = sent;
                    frames++;
                    Frame?.Invoke(sent);
                    continue; // the write took time: look at the clock again
                }
                try { await Task.Delay(wait < 0 ? Timeout.Infinite : Math.Max(10, wait), CancellationTokenSource.CreateLinkedTokenSource(ct, wakeCts.Token).Token); }
                catch (TaskCanceledException) { }
            }
        }

        void SetStatus(string s) { Status = s; Changed?.Invoke(); }

        // ---------- global keyboard hook ----------
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc fn, IntPtr mod, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;

        void InstallHook()
        {
            if (hook != IntPtr.Zero) return;
            proc = OnHook;
            using (var p = Process.GetCurrentProcess())
                hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(p.MainModule.ModuleName), 0);
            if (hook == IntPtr.Zero) Log.Info("keyboard hook failed: " + Marshal.GetLastWin32Error());
        }

        void UninstallHook()
        {
            if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
            down.Clear();
        }

        IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                int msg = (int)wParam, vk = Marshal.ReadInt32(lParam);
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (down.Add(vk) && VkToMatrix.TryGetValue(vk, out int m)) ui.BeginInvoke(new Action(() => { if (Active) Play(m); }));
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP) down.Remove(vk);
            }
            return CallNextHookEx(hook, code, wParam, lParam);
        }

        /// <summary>Windows virtual-key code → K85 matrix index.</summary>
        public static readonly Dictionary<int, int> VkToMatrix = BuildVkMap();

        static Dictionary<int, int> BuildVkMap()
        {
            var d = new Dictionary<int, int>();
            int M(string label) => K85Travel.Layout.First(k => k.Label == label).Matrix;
            for (char c = 'A'; c <= 'Z'; c++) d[c] = M(c.ToString());
            for (char c = '0'; c <= '9'; c++) d[c] = M(c.ToString());
            for (int i = 1; i <= 12; i++) d[0x6F + i] = M("F" + i);
            var named = new (int vk, string label)[]
            {
                (0x1B, "Esc"), (0x09, "Tab"), (0x14, "Caps"), (0x20, ""), (0x0D, "Enter"), (0x08, "Bksp"),
                (0x2E, "Del"), (0x24, "Home"), (0x23, "End"), (0x21, "PgUp"), (0x22, "PgDn"),
                (0x25, "←"), (0x26, "↑"), (0x27, "→"), (0x28, "↓"), (0x5B, "Win"),
                (0xC0, "`"), (0xBD, "-"), (0xBB, "="), (0xDB, "["), (0xDD, "]"), (0xDC, "\\"),
                (0xBA, ";"), (0xDE, "'"), (0xBC, ","), (0xBE, "."), (0xBF, "/"),
            };
            foreach (var (vk, label) in named) d[vk] = M(label);
            // keys that exist twice: left one first in the layout, right one second
            int Nth(string label, int n) => K85Travel.Layout.Where(k => k.Label == label).ElementAt(n).Matrix;
            d[0xA0] = d[0x10] = Nth("Shift", 0); d[0xA1] = Nth("Shift", 1);
            d[0xA2] = d[0x11] = Nth("Ctrl", 0); d[0xA3] = Nth("Ctrl", 1);
            d[0xA4] = d[0x12] = Nth("Alt", 0); d[0xA5] = Nth("Alt", 1);
            return d;
        }

        /// <summary>Copies a chosen GIF into the app's folder so it keeps working if the original moves.</summary>
        public static string Import(string path)
        {
            Directory.CreateDirectory(Paths.Gifs);
            string name = Path.GetFileName(path);
            string dest = Path.Combine(Paths.Gifs, name);
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
            {
                for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(Paths.Gifs, Path.GetFileNameWithoutExtension(name) + "-" + i + Path.GetExtension(name));
                File.Copy(path, dest);
            }
            return dest;
        }
    }
}
