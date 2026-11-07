using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using AttackOnShadow.Services;

namespace AttackOnShadow.Device
{
    /// <summary>
    /// Keeps a connection to the keyboard: finds it, reconnects after unplugging,
    /// and polls the current layer (it can also be changed with Fn on the keyboard).
    /// All events are raised on the UI thread.
    /// </summary>
    public sealed class DeviceManager
    {
        readonly Dispatcher ui;
        readonly DispatcherTimer timer;
        readonly bool demo;
        IKeyboard kb;
        bool busy;

        public IKeyboard Keyboard => kb;
        public bool Connected => kb != null;
        public int Layer { get; private set; } = -1;
        public string Status { get; private set; } = "Ищу клавиатуру…";

        public event Action Changed;

        public DeviceManager(Dispatcher dispatcher, bool demoMode)
        {
            ui = dispatcher;
            demo = demoMode;
            timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (s, e) => await Tick(), ui);
        }

        public void Start() { timer.Start(); _ = Tick(); }

        async Task Tick()
        {
            if (busy) return;
            busy = true;
            try
            {
                if (kb == null) await Connect();
                if (kb != null)
                {
                    int l = await kb.GetLayerAsync();
                    if (l != Layer) { Layer = l; Raise(); }
                }
            }
            catch (Exception e)
            {
                Log.Info("device lost: " + e.Message);
                Drop("Клавиатура отключена");
            }
            finally { busy = false; }
        }

        async Task Connect()
        {
            if (demo) { kb = new FakeKeyboard(); Status = "Демо-режим"; Raise(); return; }
            var info = await Task.Run(() => K85Keyboard.Find());
            if (info == null)
            {
                if (Status != "Клавиатура не найдена") { Status = "Клавиатура не найдена"; Raise(); }
                return;
            }
            try
            {
                kb = K85Keyboard.Open(info);
                Status = "Подключена";
                Log.Info("connected " + info);
                Raise();
            }
            catch (Exception e)
            {
                Status = "Нет доступа: " + e.Message;
                Log.Info("open failed: " + e);
                Raise();
            }
        }

        void Drop(string why)
        {
            try { kb?.Dispose(); } catch { }
            kb = null;
            Layer = -1;
            Status = why;
            Raise();
        }

        public async Task<bool> SetLayerAsync(int layer)
        {
            if (kb == null) return false;
            try
            {
                await kb.SetLayerAsync(layer);
                int l = await kb.GetLayerAsync();
                Layer = l >= 0 ? l : layer;
                Raise();
                return Layer == layer;
            }
            catch (Exception e) { Log.Info("set layer failed: " + e.Message); Drop("Клавиатура отключена"); return false; }
        }

        public async Task<Lighting> GetLightingAsync()
        {
            if (kb == null) return null;
            try { return await kb.GetLightingAsync(); }
            catch (Exception e) { Log.Info("get lighting failed: " + e.Message); Drop("Клавиатура отключена"); return null; }
        }

        public async Task<bool> SetLightingAsync(Lighting l)
        {
            if (kb == null) return false;
            try { return await kb.SetLightingAsync(l); }
            catch (Exception e) { Log.Info("set lighting failed: " + e.Message); Drop("Клавиатура отключена"); return false; }
        }

        void Raise() { if (ui.CheckAccess()) Changed?.Invoke(); else ui.BeginInvoke(new Action(() => Changed?.Invoke())); }
    }
}
