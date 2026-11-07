using System;
using System.Drawing;
using System.Windows.Forms;
using AttackOnShadow.Services;

namespace AttackOnShadow
{
    /// <summary>Tray icon: open, quick layer switch, quit.</summary>
    public sealed class Tray : IDisposable
    {
        readonly NotifyIcon icon;
        readonly App app;
        readonly ToolStripMenuItem layer1, layer2, auto;

        public Tray(App a)
        {
            app = a;
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            menu.Items.Add("Открыть", null, (s, e) => app.ShowWindow());
            menu.Items.Add(new ToolStripSeparator());
            layer1 = new ToolStripMenuItem("Слой 1", null, async (s, e) => await app.Device.SetLayerAsync(0));
            layer2 = new ToolStripMenuItem("Слой 2", null, async (s, e) => await app.Device.SetLayerAsync(1));
            auto = new ToolStripMenuItem("Автопрофили", null, (s, e) =>
            {
                app.Settings.AutoEnabled = !app.Settings.AutoEnabled;
                app.Settings.Save();
                app.Auto.Reset();
            });
            menu.Items.AddRange(new ToolStripItem[] { layer1, layer2, auto, new ToolStripSeparator() });
            menu.Items.Add("Выход", null, (s, e) => app.Quit());
            menu.Opening += (s, e) =>
            {
                layer1.Checked = app.Device.Layer == 0;
                layer2.Checked = app.Device.Layer == 1;
                layer1.Enabled = layer2.Enabled = app.Device.Connected;
                auto.Checked = app.Settings.AutoEnabled;
            };

            Icon ico;
            try
            {
                var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/aos.ico")).Stream;
                ico = new Icon(s, SystemInformation.SmallIconSize);
            }
            catch { ico = SystemIcons.Application; }

            icon = new NotifyIcon { Icon = ico, Text = "AttackOnShadow", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) app.ShowWindow(); };
            app.Device.Changed += Update;
            Update();
        }

        void Update()
        {
            string t = app.Device.Connected
                ? "AttackOnShadow — K85, слой " + (app.Device.Layer >= 0 ? (app.Device.Layer + 1).ToString() : "?")
                : "AttackOnShadow — " + app.Device.Status;
            icon.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        public void Balloon(string text)
        {
            if (app.WindowVisible) return;
            try { icon.ShowBalloonTip(2500, "AttackOnShadow", text, ToolTipIcon.None); } catch { }
        }

        public void Dispose() { icon.Visible = false; icon.Dispose(); }
    }
}
