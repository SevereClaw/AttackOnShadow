using System;
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
    }

    /// <summary>The real keyboard over USB HID feature reports.</summary>
    public sealed class K85Keyboard : IKeyboard
    {
        readonly HidDevice dev;
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        public string Name => "Attack Shark K85";

        K85Keyboard(HidDevice d) { dev = d; }

        public static HidInfo Find() =>
            HidDevice.Enumerate(K85Protocol.Vid, K85Protocol.Pid)
                .FirstOrDefault(i => i.UsagePage == K85Protocol.UsagePage && i.Usage == K85Protocol.Usage);

        public static K85Keyboard Open(HidInfo info)
        {
            Log.Usb($"open {info}");
            return new K85Keyboard(HidDevice.Open(info));
        }

        async Task<T> Locked<T>(Func<T> io)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try { return await Task.Run(io).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        void Write(byte[] p)
        {
            Thread.Sleep(10);
            Log.Usb("→ " + K85Protocol.Hex(p, 12));
            dev.SetFeature(p);
        }

        byte[] Query(byte[] p)
        {
            Write(p);
            Thread.Sleep(15);
            var r = dev.GetFeature();
            Log.Usb("← " + K85Protocol.Hex(r, 12));
            return r;
        }

        public Task<int> GetLayerAsync() => Locked(() =>
        {
            int slot = K85Protocol.ParseProfile(Query(K85Protocol.GetProfile()));
            return slot < 0 ? -1 : K85Protocol.LayerFromSlot(slot);
        });

        public Task<bool> SetLayerAsync(int layer) => Locked(() => { Write(K85Protocol.SetLayer(layer)); Thread.Sleep(50); return true; });

        public Task<Lighting> GetLightingAsync() => Locked(() => K85Protocol.ParseLedParam(Query(K85Protocol.GetLedParam())));

        public Task<bool> SetLightingAsync(Lighting l) => Locked(() =>
        {
            Write(K85Protocol.SetLedParam(l));
            Thread.Sleep(500); // the stock driver waits COMMONDELAY after lighting changes
            return true;
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
        public void Dispose() { }
    }
}
