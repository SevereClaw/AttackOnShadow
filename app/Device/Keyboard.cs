using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AttackOnShadow.Hid;
using AttackOnShadow.Services;

namespace AttackOnShadow.Device
{
    public interface IKeyboard : IDisposable
    {
        string Name { get; }
        Task<int> GetLayerAsync();               // 0-based, -1 if unknown
        Task<bool> SetLayerAsync(int layer);
        Task<Lighting> GetLightingAsync();
        Task<bool> SetLightingAsync(Lighting l);
        Task<Performance> GetPerformanceAsync();
        Task<bool> SetReportRateAsync(int hz);
        Task<bool> SetDebounceAsync(int ms);
        Task<TravelMap> GetTravelAsync();
        Task<bool> SetTravelAsync(TravelMap before, TravelMap after);
        Task CalibrationStartAsync();             // zero point (2 s, hands off), then max-travel mode on
        Task CalibrationFinishAsync();            // max-travel mode off
        Task<double[]> GetCalibrationAsync();     // per matrix index 0..1, null if unavailable
    }

    public sealed class Performance
    {
        public int Slot = -1;       // onboard profile slot the values belong to
        public int RateHz = -1;     // 125/250/500/1000, -1 unknown
        public int DebounceMs = -1;
    }

    /// <summary>The real keyboard over USB HID feature reports.</summary>
    public sealed class K85Keyboard : IKeyboard
    {
        readonly HidDevice dev;
        readonly byte reportId;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public string Name => "Attack Shark K85";

        K85Keyboard(HidDevice d, byte id) { dev = d; reportId = id; }

        /// <summary>All HID collections of the K85, logged for diagnostics.</summary>
        public static List<HidInfo> Candidates()
        {
            var all = HidDevice.Enumerate(K85Protocol.Vid, K85Protocol.Pid);
            Log.Usb($"found {all.Count} HID collection(s) of {K85Protocol.Vid:X4}:{K85Protocol.Pid:X4}:");
            foreach (var i in all) Log.Usb("   " + i + "  " + i.Path);
            // the vendor interface (FFFF/0002) first, then anything else with 64-byte feature reports
            return all.Where(i => i.FeatureReportLength >= 65 || (i.UsagePage == K85Protocol.UsagePage && i.Usage == K85Protocol.Usage))
                      .OrderBy(i => i.UsagePage == K85Protocol.UsagePage && i.Usage == K85Protocol.Usage ? 0 : i.UsagePage == K85Protocol.UsagePage ? 1 : 2)
                      .ToList();
        }

        /// <summary>
        /// Tries every candidate collection and both access modes; the first one
        /// that answers GET_PROFILE wins. Throws with all the reasons if none does.
        /// </summary>
        public static K85Keyboard Connect(List<HidInfo> candidates)
        {
            var errors = new List<string>();
            foreach (var info in candidates)
            {
                // the report id the collection declares (hidapi puts it in byte 0), then 0 as a fallback
                var ids = info.FeatureReportIds.Where(b => b != 0).Concat(new byte[] { 0 }).Distinct().ToArray();
                foreach (byte id in ids)
                foreach (var access in new[] { HidDevice.AccessNone, HidDevice.AccessReadWrite })
                {
                    HidDevice dev = null;
                    try
                    {
                        dev = HidDevice.Open(info, access);
                        try { Log.Usb($"read-only probe id={id:X2} access={access:X}: ← {K85Protocol.Hex(dev.GetFeature(id), 12)}"); }
                        catch (Exception re) { Log.Usb($"read-only probe id={id:X2} access={access:X}: {re.Message}"); }
                        var kb = new K85Keyboard(dev, id);
                        var r = kb.Query(K85Protocol.GetProfile());
                        int slot = K85Protocol.ParseProfile(r);
                        Log.Usb($"probe {info} id={id:X2} access={access:X}: answer {K85Protocol.Hex(r, 8)} → slot {slot}");
                        if (slot < 0 && r.All(b => b == 0)) throw new IOException("пустой ответ");
                        Log.Info($"connected via {info} id={id:X2} access={access:X}");
                        return kb;
                    }
                    catch (Exception e)
                    {
                        dev?.Dispose();
                        string msg = $"{info.UsagePage:X4}/{info.Usage:X4} id {id:X2} доступ {access:X}: {e.Message}";
                        errors.Add(msg);
                        Log.Usb("probe failed " + msg);
                    }
                }
            }
            throw new IOException(errors.Count == 0 ? "нет подходящего интерфейса" : string.Join("; ", errors));
        }

        // The K85 firmware hangs (every request then fails with error 31 until the
        // cable is replugged) if it is queried while it rebuilds a lighting effect.
        // After each write nothing else is sent until the keyboard has settled.
        DateTime quietUntil = DateTime.MinValue;
        public DateTime LastWrite { get; private set; } = DateTime.MinValue;

        async Task<T> Locked<T>(Func<T> io)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var wait = quietUntil - DateTime.Now;
                if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
                return await Task.Run(io).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }

        void Settle(int ms) { LastWrite = DateTime.Now; quietUntil = DateTime.Now.AddMilliseconds(ms); }

        void Write(byte[] p)
        {
            Thread.Sleep(10);
            Log.Usb("→ " + K85Protocol.Hex(p, 12));
            dev.SetFeature(p, reportId);
        }

        byte[] Query(byte[] p)
        {
            Write(p);
            Thread.Sleep(15);
            var r = dev.GetFeature(reportId);
            Log.Usb("← " + K85Protocol.Hex(r, 12));
            return r;
        }

        public Task<int> GetLayerAsync() => Locked(() =>
        {
            int slot = K85Protocol.ParseProfile(Query(K85Protocol.GetProfile()));
            return slot < 0 ? -1 : K85Protocol.LayerFromSlot(slot);
        });

        public Task<bool> SetLayerAsync(int layer) => Locked(() => { Write(K85Protocol.SetLayer(layer)); Settle(400); return true; });

        public Task<Lighting> GetLightingAsync() => Locked(() => K85Protocol.ParseLedParam(Query(K85Protocol.GetLedParam())));

        public Task<bool> SetLightingAsync(Lighting l) => Locked(() =>
        {
            Write(K85Protocol.SetLedParam(l));
            Settle(2000); // stock driver: COMMONDELAY 500 ms; the K85 needs more before the next request
            return true;
        });

        int CurrentSlot()
        {
            int slot = K85Protocol.ParseProfile(Query(K85Protocol.GetProfile()));
            return slot < 0 ? 0 : slot;
        }

        public Task<Performance> GetPerformanceAsync() => Locked(() =>
        {
            var p = new Performance { Slot = CurrentSlot() };
            p.RateHz = K85Travel.ParseReportRate(Query(K85Travel.GetReportRate(p.Slot)));
            p.DebounceMs = K85Travel.ParseDebounce(Query(K85Travel.GetDebounce()));
            return p;
        });

        public Task<bool> SetReportRateAsync(int hz) => Locked(() =>
        {
            // per onboard profile, like the stock driver: the layer that is active now
            Write(K85Travel.SetReportRate(CurrentSlot(), hz));
            Settle(1000);
            return true;
        });

        public Task<bool> SetDebounceAsync(int ms) => Locked(() => { Write(K85Travel.SetDebounce(ms)); Settle(1000); return true; });

        public Task<TravelMap> GetTravelAsync() => Locked(() =>
        {
            var m = new TravelMap();
            var km = Query(K85Travel.GetKeyboardMode());
            if (km.Length > 3 && km[0] == K85Travel.GET_KEY_MAG_MODE && km[1] <= 3)
            {
                m.KeyboardMode = km[1]; m.ModeFlag1 = km[2]; m.ModeFlag2 = km[3];
            }
            foreach (int param in K85Travel.Params)
            {
                var arr = m.Array(param);
                for (int page = 0; page < K85Travel.ReadPages; page++)
                {
                    var r = Query(K85Travel.GetArray(param, page));
                    Log.Usb($"   param {param} page {page}: {K85Protocol.Hex(r, 64)}");
                    string bad = K85Travel.CheckPage(param, r);
                    if (bad != null) throw new InvalidDataException("непонятный ответ клавиатуры (" + bad + ")");
                    Array.Copy(r, 0, arr, page * K85Travel.ReadPageSize, K85Travel.ReadPageSize);
                }
            }
            string why = K85Travel.Validate(m);
            if (why != null) throw new InvalidDataException("непонятные значения хода: " + why);
            return m;
        });

        public Task<bool> SetTravelAsync(TravelMap before, TravelMap after) => Locked(() =>
        {
            var packets = K85Travel.Diff(before, after);
            if (packets.Count == 0) return true;
            if (after.KeyboardMode != 3)
            {
                // preset modes (comfort / gaming…) ignore per-key values
                Write(K85Travel.SetKeyboardMode(3, after.ModeFlag1, after.ModeFlag2));
                Thread.Sleep(300);
                after.KeyboardMode = 3;
            }
            foreach (var p in packets) { Write(p); Thread.Sleep(15); }
            Settle(1500);
            return true;
        });

        public Task CalibrationStartAsync() => Locked(() =>
        {
            Write(K85Travel.Calibrate(true));
            Thread.Sleep(2000);           // the keyboard measures the released position
            Write(K85Travel.Calibrate(false));
            Thread.Sleep(500);
            Write(K85Travel.CalibrateMax(true));
            Settle(300);
            return true;
        });

        public Task CalibrationFinishAsync() => Locked(() => { Write(K85Travel.CalibrateMax(false)); Settle(1500); return true; });

        public Task<double[]> GetCalibrationAsync() => Locked(() =>
        {
            var max = new List<short>(); var cal = new List<short>();
            for (int page = 0; page < 4; page++)
            {
                var r = Query(K85Travel.GetCalMax(page));
                if (r.Length < 64) return null;
                max.AddRange(K85Travel.ParseInt16Page(r));
            }
            for (int page = 0; page < 4; page++)
            {
                var r = Query(K85Travel.GetCal(page));
                if (r.Length < 64) return null;
                cal.AddRange(K85Travel.ParseInt16Page(r));
            }
            return K85Travel.CalProgress(max.ToArray(), cal.ToArray());
        });

        public void Dispose() { dev.Dispose(); gate.Dispose(); }
    }

    /// <summary>Simulated keyboard for screenshots (--screenshot) and demo mode (--demo).</summary>
    public sealed class FakeKeyboard : IKeyboard
    {
        int layer;
        Lighting light = new Lighting { Mode = 4, Brightness = 3, Speed = 2, Variant = 0, R = 0x29, G = 0xD3, B = 0xFF };
        public string Name => "Attack Shark K85 (демо)";
        public Task<int> GetLayerAsync() => Task.FromResult(layer);
        public Task<bool> SetLayerAsync(int l) { layer = l; return Task.FromResult(true); }
        public Task<Lighting> GetLightingAsync() => Task.FromResult(light.Clone());
        public Task<bool> SetLightingAsync(Lighting l) { light = l.Clone(); return Task.FromResult(true); }
        Performance perf = new Performance { Slot = 0, RateHz = 1000, DebounceMs = 1 };
        TravelMap travel = K85Travel.Demo();
        DateTime calStart;
        public Task<Performance> GetPerformanceAsync() => Task.FromResult(new Performance { Slot = layer * 4, RateHz = perf.RateHz, DebounceMs = perf.DebounceMs });
        public Task<bool> SetReportRateAsync(int hz) { perf.RateHz = hz; return Task.FromResult(true); }
        public Task<bool> SetDebounceAsync(int ms) { perf.DebounceMs = ms; return Task.FromResult(true); }
        public Task<TravelMap> GetTravelAsync() => Task.FromResult(travel.Clone());
        public Task<bool> SetTravelAsync(TravelMap before, TravelMap after) { travel = after.Clone(); travel.KeyboardMode = 3; return Task.FromResult(true); }
        public async Task CalibrationStartAsync() { await Task.Delay(1500); calStart = DateTime.Now; }
        public Task CalibrationFinishAsync() => Task.CompletedTask;
        public Task<double[]> GetCalibrationAsync()
        {
            // keys "get pressed" one after another, left to right
            double t = (DateTime.Now - calStart).TotalSeconds;
            var r = new double[TravelMap.Size];
            foreach (var k in K85Travel.Layout) r[k.Matrix] = Math.Max(0, Math.Min(1, t * 2 - k.X));
            return Task.FromResult(r);
        }
        public void Dispose() { }
    }
}
