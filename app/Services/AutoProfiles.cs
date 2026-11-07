using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using AttackOnShadow.Device;

namespace AttackOnShadow.Services
{
    /// <summary>
    /// Switches the keyboard layer while a game from the rules is running and
    /// returns to the normal layer after it closes. Only acts when the wanted
    /// layer changes, so a layer picked by hand is not overridden.
    /// </summary>
    public sealed class AutoProfiles
    {
        readonly AppSettings settings;
        readonly DeviceManager device;
        readonly DispatcherTimer timer;
        string lastTarget;
        bool first = true;

        public GameRule Active { get; private set; }
        public string LastSwitch { get; private set; } = "";
        public HashSet<string> Running { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public event Action Changed;
        public event Action<string> Switched;

        public AutoProfiles(AppSettings s, DeviceManager d, Dispatcher ui)
        {
            settings = s; device = d;
            timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (a, b) => await Tick(), ui);
            device.Changed += () => { if (!device.Connected) { lastTarget = null; first = true; } };
        }

        public void Start() { timer.Start(); _ = Tick(); }
        public void Reset() { lastTarget = null; first = true; _ = Tick(); }

        static HashSet<string> Processes()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { set.Add(p.ProcessName + ".exe"); } catch { }
                finally { p.Dispose(); }
            }
            return set;
        }

        async Task Tick()
        {
            Running = await Task.Run(() => Processes());
            var rule = settings.Rules.FirstOrDefault(r => Running.Contains(r.Exe));
            bool changed = rule != Active;
            Active = rule;
            if (settings.AutoEnabled && device.Connected)
            {
                int target = rule != null ? rule.Layer : settings.FallbackLayer;
                string id = rule != null ? "rule:" + rule.Exe + ":" + rule.Layer : "normal:" + settings.FallbackLayer;
                if (id != lastTarget)
                {
                    bool wasFirst = first;
                    first = false;
                    lastTarget = id;
                    // at start-up only a running game forces a switch
                    if (target >= 0 && !(wasFirst && rule == null) && target != device.Layer)
                    {
                        if (await device.SetLayerAsync(target))
                        {
                            LastSwitch = $"Слой {target + 1}" + (rule != null ? " — " + rule.Exe : " — обычный");
                            Log.Info("auto: " + LastSwitch);
                            Switched?.Invoke(rule != null ? $"{rule.Exe}: слой {target + 1}" : $"Обычный слой {target + 1}");
                        }
                    }
                    changed = true;
                }
            }
            if (changed) Changed?.Invoke();
        }

        /// <summary>Running programs worth offering in the picker (skips system processes).</summary>
        public IEnumerable<string> Candidates()
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "svchost.exe","system.exe","idle.exe","registry.exe","smss.exe","csrss.exe","wininit.exe","winlogon.exe","services.exe","lsass.exe",
                "fontdrvhost.exe","dwm.exe","conhost.exe","runtimebroker.exe","sihost.exe","taskhostw.exe","ctfmon.exe","explorer.exe","searchhost.exe",
                "startmenuexperiencehost.exe","textinputhost.exe","dllhost.exe","wmiprvse.exe","audiodg.exe","spoolsv.exe","msmpeng.exe","nissrv.exe",
                "smartscreen.exe","cmd.exe","powershell.exe","attackonshadow.exe","memory compression.exe","securityhealthservice.exe",
                "securityhealthsystray.exe","applicationframehost.exe","shellexperiencehost.exe","searchindexer.exe","lockapp.exe","useroobebroker.exe",
            };
            return Running.Where(n => !skip.Contains(n)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase);
        }
    }
}
