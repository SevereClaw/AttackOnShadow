using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AttackOnShadow.Device;
using AttackOnShadow.Services;

namespace AttackOnShadow
{
    // The "КАРТИНКИ" page: a picture in one of the 5 slots, and the experimental GIF-on-press mode.
    public partial class MainWindow
    {
        List<KeyFrame> picFrames;
        string picFile;
        int picSlot;
        int gifKey = -1;                    // key being edited on the GIF scheme, -1 = default animation
        DispatcherTimer previewTimer;
        List<KeyFrame> previewFrames;
        int previewIndex;
        bool picsBuilt;

        const string Filter = "Картинки и GIF|*.gif;*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*";

        void BuildPictures()
        {
            if (picsBuilt) return;
            picsBuilt = true;
            for (int i = 0; i < K85Protocol.PictureSlots; i++)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = (i + 1).ToString(), Tag = i, GroupName = "picslot", IsChecked = i == 0 };
                rb.Checked += (s, e) => { picSlot = (int)((RadioButton)s).Tag; UpdatePicStatus(); };
                PicSlots.Children.Add(rb);
            }
            PicPreview.SetColors(KeyImage.Sample());
            PicFileText.Text = "Пока пример: ночной градиент";
            GifPreview.KeyClicked += m => { gifKey = gifKey == m ? -1 : m; ShowGifKey(); };
            previewTimer = new DispatcherTimer();
            previewTimer.Tick += PreviewTick;
            app.Reactive.Changed += () => Dispatcher.BeginInvoke(new Action(ShowGifState));
            app.Reactive.Frame += rgb => { if (PagePics.Visibility == Visibility.Visible && !previewTimer.IsEnabled) GifPreview.SetColors(rgb); };
            ShowGifState();
            ShowGifKey();
            UpdatePicStatus();
        }

        // ---------- picture slots ----------
        async void PicOpen_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Filter, Title = "Картинка для клавиатуры" };
            if (dlg.ShowDialog(this) != true) return;
            PicStatus.Text = "Открываю…";
            try
            {
                string path = dlg.FileName;
                picFrames = await Task.Run(() => KeyImage.Load(path));
                picFile = path;
            }
            catch (Exception x) { PicStatus.Text = "Не получилось открыть: " + x.Message; return; }
            PicFileText.Text = Path.GetFileName(picFile);
            loading = true;
            PicFrameSlider.Maximum = Math.Max(0, picFrames.Count - 1);
            PicFrameSlider.Value = 0;
            loading = false;
            PicFramePanel.Visibility = picFrames.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            ShowPicFrame();
            UpdatePicStatus();
        }

        void PicFrame_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (IsInitialized && picsBuilt) ShowPicFrame(); }

        void ShowPicFrame()
        {
            if (picFrames == null) return;
            int i = (int)PicFrameSlider.Value;
            PicPreview.SetColors(picFrames[Math.Min(i, picFrames.Count - 1)].Rgb);
            PicFrameText.Text = $"кадр {i + 1}/{picFrames.Count}";
        }

        byte[] CurrentPicture() => picFrames == null ? KeyImage.Sample() : picFrames[Math.Min((int)PicFrameSlider.Value, picFrames.Count - 1)].Rgb;

        void UpdatePicStatus()
        {
            if (!picsBuilt) return;
            PicWrite.IsEnabled = app.Device.Connected;
            PicStatus.Text = !app.Device.Connected ? "Клавиатура не подключена"
                : picSlot == ReactiveLights.Slot && app.Settings.ReactiveEnabled ? "Слот 5 сейчас занят режимом «GIF при нажатии» — запись его выключит."
                : $"Запишется в слот {picSlot + 1}, и подсветка переключится на «Своя картинка».";
        }

        async void PicWrite_Click(object sender, RoutedEventArgs e)
        {
            if (!app.Device.Connected) { ShowToast("Клавиатура не подключена"); return; }
            PicWrite.IsEnabled = false;
            PicStatus.Text = "Записываю…";
            if (app.Reactive.Active || app.Settings.ReactiveEnabled) await TurnGifOff(false);
            var rgb = CurrentPicture();
            int slot = picSlot;
            // the stock driver's order: show the slot first, then store the picture into it
            bool ok = await app.Device.Run("picture mode", kb => kb.SetLightingAsync(new Lighting { Mode = K85Protocol.UserPictureMode, Variant = slot, Brightness = 4 }), false)
                   && await app.Device.Run("picture", kb => kb.SetUserPictureAsync(rgb, false), false);
            lightingLoaded = false;
            ShowToast(ok ? $"Картинка в слоте {slot + 1}" : "Не удалось записать картинку");
            UpdatePicStatus();
            if (ok) PicStatus.Text = $"Готово. Слоты переключаются на странице «Подсветка» → «Своя картинка».";
        }

        // ---------- GIF on press ----------
        void ShowGifState()
        {
            if (!picsBuilt) return;
            loading = true;
            GifEnabledBox.IsChecked = app.Settings.ReactiveEnabled;
            loading = false;
            GifStatus.Text = app.Reactive.Status;
            GifPreview.Marked.Clear();
            foreach (var r in app.Settings.KeyGifs) GifPreview.Marked.Add(r.Matrix);
            GifPreview.Refresh();
            UpdatePicStatus();
        }

        async void GifEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || !picsBuilt) return;
            bool on = GifEnabledBox.IsChecked == true;
            app.Settings.ReactiveEnabled = on;
            app.Settings.Save();
            if (on) await app.Reactive.StartAsync();
            else await app.Reactive.StopAsync(true);
            lightingLoaded = false;
            ShowGifState();
        }

        async Task TurnGifOff(bool restore)
        {
            app.Settings.ReactiveEnabled = false;
            app.Settings.Save();
            await app.Reactive.StopAsync(restore);
            ShowGifState();
        }

        string KeyName(int m)
        {
            var k = K85Travel.Layout.FirstOrDefault(x => x.Matrix == m);
            return k == null ? "?" : k.Label.Length == 0 ? "Пробел" : k.Label;
        }

        void ShowGifKey()
        {
            GifPreview.Selected = gifKey;
            var rule = gifKey < 0 ? null : app.Settings.KeyGifs.FirstOrDefault(r => r.Matrix == gifKey);
            string def = app.Settings.DefaultGif != null ? Path.GetFileName(app.Settings.DefaultGif) : "волна от клавиши (встроенная)";
            if (gifKey < 0)
            {
                GifKeyTitle.Text = "По умолчанию";
                GifKeyFile.Text = def;
                GifClear.IsEnabled = app.Settings.DefaultGif != null;
            }
            else
            {
                GifKeyTitle.Text = "Клавиша " + KeyName(gifKey);
                GifKeyFile.Text = rule != null ? Path.GetFileName(rule.File) : "как по умолчанию: " + def;
                GifClear.IsEnabled = rule != null;
            }
        }

        void GifAssign_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = Filter, Title = gifKey < 0 ? "GIF по умолчанию" : "GIF для клавиши " + KeyName(gifKey) };
            if (dlg.ShowDialog(this) != true) return;
            string file;
            try
            {
                file = ReactiveLights.Import(dlg.FileName);
                if (app.Reactive.Load(file) == null) { ShowToast("Не получилось прочитать картинку"); return; }
            }
            catch (Exception x) { ShowToast("Не получилось: " + x.Message); return; }
            var s = app.Settings;
            if (gifKey < 0) { app.Reactive.Forget(s.DefaultGif); s.DefaultGif = file; }
            else
            {
                s.KeyGifs.RemoveAll(r => r.Matrix == gifKey);
                s.KeyGifs.Add(new KeyGif { Matrix = gifKey, File = file });
            }
            s.Save();
            ShowGifKey();
            ShowGifState();
            GifShow_Click(null, null);
        }

        void GifClear_Click(object sender, RoutedEventArgs e)
        {
            var s = app.Settings;
            if (gifKey < 0) { app.Reactive.Forget(s.DefaultGif); s.DefaultGif = null; }
            else s.KeyGifs.RemoveAll(r => r.Matrix == gifKey);
            s.Save();
            ShowGifKey();
            ShowGifState();
        }

        void GifShow_Click(object sender, RoutedEventArgs e)
        {
            int m = gifKey >= 0 ? gifKey : K85Travel.Layout.First(k => k.Label == "G").Matrix;
            if (app.Reactive.Active) app.Reactive.Play(m);   // the keyboard plays it too; the preview follows the frames sent
            else StartPreview(app.Reactive.FramesFor(m));
        }

        void StartPreview(List<KeyFrame> frames)
        {
            previewFrames = frames;
            previewIndex = 0;
            previewTimer.Stop();
            PreviewTick(null, null);
        }

        void PreviewTick(object sender, EventArgs e)
        {
            if (previewFrames == null || previewIndex >= previewFrames.Count)
            {
                previewTimer.Stop();
                GifPreview.SetColors(null);
                return;
            }
            var f = previewFrames[previewIndex++];
            GifPreview.SetColors(f.Rgb);
            previewTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(40, f.DelayMs));
            previewTimer.Start();
        }

        /// <summary>For CI screenshots: a frame of the ripple and the sample picture.</summary>
        public void DemoPictures()
        {
            BuildPictures();
            var g = K85Travel.Layout.First(k => k.Label == "H").Matrix;
            GifPreview.Marked.Add(K85Travel.Layout.First(k => k.Label == "Q").Matrix);
            GifPreview.SetColors(KeyImage.Ripple(g)[3].Rgb);
        }
    }
}
