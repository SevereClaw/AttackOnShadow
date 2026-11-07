using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AttackOnShadow.Device;

namespace AttackOnShadow
{
    // The "КЛАВИШИ" page: per-key travel and Rapid Trigger, polling rate, debounce, calibration.
    public partial class MainWindow
    {
        const double U = 46;          // one key unit in canvas pixels
        const double RowGap = 10;     // extra gap under the F-row

        sealed class KeyView { public KeyDef Def; public Border Box; public TextBlock Label, Value; public Border Rt; }

        readonly Dictionary<int, KeyView> keyViews = new Dictionary<int, KeyView>();
        readonly HashSet<int> selected = new HashSet<int>();
        TravelMap deviceMap, workMap;  // as stored in the keyboard / with unsaved edits
        Performance perf;
        bool travelLoaded, travelLoading, perfLoaded, keysBuilt, travelFailed;
        int keysLayer = -2;
        bool dragging, dragAdd;
        DispatcherTimer calTimer;
        bool calibrating, calPolling;
        double[] calProgress;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string Mm(double v) => v.ToString("0.0", Inv) + " мм";

        Brush B(string key) => (Brush)FindResource(key);
        static SolidColorBrush Rgb(byte a, byte r, byte g, byte b) => new SolidColorBrush(Color.FromArgb(a, r, g, b));

        // ---------- building ----------
        void BuildKeys()
        {
            if (keysBuilt) return;
            keysBuilt = true;
            KeyCanvas.Width = K85Travel.LayoutWidth * U;
            KeyCanvas.Height = K85Travel.LayoutRows * U + RowGap;
            foreach (var k in K85Travel.Layout)
            {
                var v = new KeyView { Def = k };
                var g = new Grid();
                v.Box = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
                g.Children.Add(v.Box);
                v.Rt = new Border { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 4, 3), Background = B("Cyan"), Visibility = Visibility.Collapsed };
                g.Children.Add(v.Rt);
                v.Label = new TextBlock { Text = k.Label, FontSize = k.Label.Length > 3 ? 11 : 13.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 4, 4, 0), VerticalAlignment = VerticalAlignment.Top };
                g.Children.Add(v.Label);
                v.Value = new TextBlock { FontSize = 11, Margin = new Thickness(4, 0, 6, 6), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
                g.Children.Add(v.Value);
                g.Width = k.W * U - 4;
                g.Height = U - 4;
                Canvas.SetLeft(g, k.X * U + 2);
                Canvas.SetTop(g, k.Row * U + (k.Row > 0 ? RowGap : 0) + 2);
                g.IsHitTestVisible = false;
                KeyCanvas.Children.Add(g);
                keyViews[k.Matrix] = v;
            }
            foreach (int hz in K85Travel.Rates)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = hz + " Гц", Tag = hz, GroupName = "rate" };
                rb.Checked += Rate_Checked;
                RatePanel.Children.Add(rb);
            }
            calTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            calTimer.Tick += async (s, e) => await PollCalibration();
            PaintKeys();
            ShowEditor();
        }

        void PaintKeys()
        {
            foreach (var v in keyViews.Values)
            {
                int m = v.Def.Matrix;
                bool sel = selected.Contains(m);
                double cal = calibrating && calProgress != null && m < calProgress.Length ? calProgress[m] : -1;
                if (cal >= 0)
                {
                    v.Box.Background = cal >= 1 ? B("Cyan") : Rgb((byte)(0x50 + 0x80 * cal), 0x1F, 0x6B, 0xFF);
                    v.Box.BorderBrush = Rgb(0x59, 0x29, 0xD3, 0xFF);
                }
                else
                {
                    v.Box.Background = sel ? Brushes.White : Rgb(0x8C, 0x0B, 0x2D, 0x7D);
                    v.Box.BorderBrush = sel ? B("Cyan") : Rgb(0x59, 0x29, 0xD3, 0xFF);
                }
                var fg = sel && cal < 0 || cal >= 1 ? B("Navy1") : Brushes.White;
                v.Label.Foreground = fg;
                v.Value.Foreground = sel && cal < 0 ? B("Navy2") : B("Muted");
                if (workMap != null)
                {
                    bool dirty = deviceMap != null && Differs(m);
                    v.Value.Text = (workMap.Plain(m) ? K85Travel.TravelMm(workMap.Act[m]).ToString("0.0", Inv) : ModeShort(workMap.Mode[m])) + (dirty ? "•" : "");
                    v.Rt.Visibility = workMap.Rapid(m) ? Visibility.Visible : Visibility.Collapsed;
                }
                else { v.Value.Text = ""; v.Rt.Visibility = Visibility.Collapsed; }
            }
        }

        static string ModeShort(byte mode)
        {
            switch (mode & 0x7F) { case 2: return "DKS"; case 3: return "MT"; case 4: case 5: return "TGL"; case 6: return "PAD"; default: return "?"; }
        }

        bool Differs(int m) =>
            deviceMap.Mode[m] != workMap.Mode[m] || deviceMap.Act[m] != workMap.Act[m] || deviceMap.Rel[m] != workMap.Rel[m]
            || deviceMap.Dead[m] != workMap.Dead[m] || deviceMap.RtPress[m] != workMap.RtPress[m] || deviceMap.RtRelease[m] != workMap.RtRelease[m];

        int DirtyCount() => deviceMap == null || workMap == null ? 0 : K85Travel.Layout.Count(k => Differs(k.Matrix));

        // ---------- loading ----------
        void KeysOnDevice()
        {
            var d = app.Device;
            if (!d.Connected)
            {
                travelLoaded = perfLoaded = travelFailed = false;
                if (calibrating) EndCalibrationUi();
            }
            else if (d.Layer != keysLayer && d.Layer >= 0)
            {
                // settings belong to the active layer; reload unless there are unsaved edits
                if (keysLayer != -2 && DirtyCount() == 0) travelLoaded = false;
                perfLoaded = false;
                keysLayer = d.Layer;
            }
            if (PageKeys.Visibility == Visibility.Visible && d.Connected && !travelFailed) _ = LoadKeys();
            if (keysBuilt) ShowEditor();
        }

        async Task LoadKeys()
        {
            BuildKeys();
            if (!app.Device.Connected || travelLoading || calibrating) return;
            travelLoading = true;
            try
            {
                if (!travelLoaded)
                {
                    TravelStatus.Text = "Читаю настройки клавиш…";
                    var m = await app.Device.Run("get travel", kb => kb.GetTravelAsync());
                    if (m == null)
                    {
                        deviceMap = workMap = null;
                        travelFailed = true; // no automatic retries; opening the page again retries
                        TravelStatus.Text = "Не удалось прочитать ход клавиш" + (app.Device.LastError != null ? ": " + app.Device.LastError : ".")
                            + " Включи журнал USB в настройках и пришли usb.txt.";
                    }
                    else
                    {
                        deviceMap = m; workMap = m.Clone();
                        travelLoaded = true; travelFailed = false;
                    }
                    PaintKeys();
                    ShowEditor();
                }
                if (!perfLoaded && app.Device.Connected)
                {
                    var p = await app.Device.Run("get performance", kb => kb.GetPerformanceAsync());
                    if (p != null) { perf = p; perfLoaded = true; }
                    ShowPerformance();
                }
            }
            finally { travelLoading = false; }
        }

        // ---------- selection ----------
        KeyDef KeyAt(Point p)
        {
            foreach (var k in K85Travel.Layout)
            {
                double x = k.X * U, y = k.Row * U + (k.Row > 0 ? RowGap : 0);
                if (p.X >= x && p.X < x + k.W * U && p.Y >= y && p.Y < y + U) return k;
            }
            return null;
        }

        void KeyCanvas_Down(object sender, MouseButtonEventArgs e)
        {
            if (calibrating) return;
            var k = KeyAt(e.GetPosition(KeyCanvas));
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            if (!ctrl) selected.Clear();
            dragAdd = k == null || !selected.Contains(k.Matrix);
            if (k != null) { if (dragAdd) selected.Add(k.Matrix); else selected.Remove(k.Matrix); }
            dragging = true;
            KeyCanvas.CaptureMouse();
            PaintKeys();
            ShowEditor();
        }

        void KeyCanvas_Move(object sender, MouseEventArgs e)
        {
            if (!dragging) return;
            var k = KeyAt(e.GetPosition(KeyCanvas));
            if (k == null) return;
            bool changed = dragAdd ? selected.Add(k.Matrix) : selected.Remove(k.Matrix);
            if (changed) { PaintKeys(); ShowEditor(); }
        }

        void KeyCanvas_Up(object sender, MouseButtonEventArgs e) { dragging = false; KeyCanvas.ReleaseMouseCapture(); }

        void SelectKeys(IEnumerable<int> keys)
        {
            if (calibrating) return;
            selected.Clear();
            foreach (int k in keys) selected.Add(k);
            PaintKeys();
            ShowEditor();
        }

        void KeysAll_Click(object sender, RoutedEventArgs e) => SelectKeys(K85Travel.Layout.Select(k => k.Matrix));
        void KeysNone_Click(object sender, RoutedEventArgs e) => SelectKeys(new int[0]);
        void KeysWasd_Click(object sender, RoutedEventArgs e) => SelectWasd();
        public void SelectWasd() => SelectKeys(K85Travel.Layout.Where(k => "WASD".Contains(k.Label) && k.Label.Length == 1).Select(k => k.Matrix));

        // ---------- editor ----------
        void ShowEditor()
        {
            bool have = workMap != null && selected.Count > 0 && !calibrating;
            loading = true;
            try
            {
                bool anyPlain = have && selected.Any(m => workMap.Plain(m));
                if (have)
                {
                    int first = K85Travel.Layout.First(k => selected.Contains(k.Matrix)).Matrix;
                    var t = K85Travel.Read(workMap, first);
                    ActSlider.Value = t.Actuation; RelSlider.Value = t.Release;
                    RtPressSlider.Value = t.RtPress; RtReleaseSlider.Value = t.RtRelease;
                    DeadSlider.Value = t.DeadZone; RapidBox.IsChecked = t.Rapid;
                    TravelTitle.Text = selected.Count == 1
                        ? "Ход клавиши " + (keyViews[first].Def.Label.Length > 0 ? keyViews[first].Def.Label : "Пробел")
                        : $"Ход клавиш · выбрано {selected.Count}";
                }
                else TravelTitle.Text = "Ход клавиш";
                ActSlider.IsEnabled = RelSlider.IsEnabled = DeadSlider.IsEnabled = anyPlain;
                RapidBox.IsEnabled = have;
                RtPressSlider.IsEnabled = RtReleaseSlider.IsEnabled = have && RapidBox.IsChecked == true;
                RapidPanel.Opacity = have && RapidBox.IsChecked == true ? 1 : 0.4;
                KeysHint.Text = have && selected.Any(m => !workMap.Plain(m))
                    ? "Среди выбранных есть DKS / Mod-Tap / Toggle — у них меняется только Rapid Trigger."
                    : "Щелчок — одна клавиша, Ctrl или протяжка — несколько. На клавишах — точка срабатывания в мм, полоска — Rapid Trigger.";
                UpdateTravelLabels();
                UpdateTravelStatus();
            }
            finally { loading = false; }
        }

        void UpdateTravelLabels()
        {
            ActValue.Text = Mm(ActSlider.Value);
            RelValue.Text = Mm(RelSlider.Value);
            RtPressValue.Text = Mm(RtPressSlider.Value);
            RtReleaseValue.Text = Mm(RtReleaseSlider.Value);
            DeadValue.Text = DeadSlider.Value < 0.05 ? "выкл" : Mm(DeadSlider.Value);
        }

        void UpdateTravelStatus()
        {
            int dirty = DirtyCount();
            TravelApply.IsEnabled = TravelRevert.IsEnabled = dirty > 0 && app.Device.Connected && !calibrating;
            if (!app.Device.Connected) { TravelStatus.Text = "Клавиатура не подключена"; return; }
            if (workMap == null) return;
            bool preset = deviceMap.KeyboardMode >= 0 && deviceMap.KeyboardMode < 3;
            string mode = preset ? $"Сейчас в клавиатуре режим «{K85Travel.KeyboardModes[deviceMap.KeyboardMode]}»: при сохранении он сменится на «Свой», и заработают значения со схемы." : "";
            TravelStatus.Text = dirty > 0 ? $"Не сохранено: изменено клавиш — {dirty}. " + mode
                : (selected.Count == 0 ? "Выбери клавиши на схеме. " : "Всё сохранено в клавиатуре. ") + mode;
        }

        void Travel_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || !keysBuilt) return;
            UpdateTravelLabels();
            if (loading || workMap == null || selected.Count == 0) return;
            RapidPanel.Opacity = RapidBox.IsChecked == true ? 1 : 0.4;
            RtPressSlider.IsEnabled = RtReleaseSlider.IsEnabled = RapidBox.IsChecked == true;
            K85Travel.Apply(workMap, selected, new KeyTravel
            {
                Actuation = ActSlider.Value, Release = RelSlider.Value, DeadZone = DeadSlider.Value,
                RtPress = RtPressSlider.Value, RtRelease = RtReleaseSlider.Value, Rapid = RapidBox.IsChecked == true,
            });
            PaintKeys();
            UpdateTravelStatus();
        }
        void TravelSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Travel_Changed(sender, e);

        async void TravelApply_Click(object sender, RoutedEventArgs e)
        {
            if (deviceMap == null || workMap == null) return;
            TravelApply.IsEnabled = false;
            TravelStatus.Text = "Сохраняю…";
            var target = workMap.Clone();
            bool ok = await app.Device.Run("set travel", kb => kb.SetTravelAsync(deviceMap, target), false);
            if (ok)
            {
                deviceMap = target.Clone();
                deviceMap.KeyboardMode = 3;
                workMap = deviceMap.Clone();
                ShowToast("Ход клавиш сохранён");
            }
            else ShowToast("Не удалось сохранить ход клавиш");
            PaintKeys();
            ShowEditor();
        }

        void TravelRevert_Click(object sender, RoutedEventArgs e)
        {
            if (deviceMap == null) return;
            workMap = deviceMap.Clone();
            PaintKeys();
            ShowEditor();
        }

        // ---------- polling rate & debounce ----------
        void ShowPerformance()
        {
            loading = true;
            try
            {
                bool on = app.Device.Connected && perf != null;
                foreach (RadioButton rb in RatePanel.Children) { rb.IsChecked = on && (int)rb.Tag == perf.RateHz; rb.IsEnabled = on; }
                DebounceSlider.IsEnabled = on && perf.DebounceMs >= 0;
                if (on && perf.DebounceMs >= 0) DebounceSlider.Value = Math.Min(K85Travel.DebounceMax, perf.DebounceMs);
                DebounceValue.Text = on && perf.DebounceMs >= 0 ? perf.DebounceMs + " мс" : "—";
                RateTitle.Text = on && perf.Slot >= 0 ? $"Частота опроса · слой {K85Protocol.LayerFromSlot(perf.Slot) + 1}" : "Частота опроса";
                PerfStatus.Text = !app.Device.Connected ? "Клавиатура не подключена"
                    : perf == null ? "Не удалось прочитать"
                    : "Антидребезг — пауза, в течение которой повторное срабатывание клавиши игнорируется. Меньше — быстрее отклик.";
            }
            finally { loading = false; }
        }

        async void Rate_Checked(object sender, RoutedEventArgs e)
        {
            if (loading || perf == null) return;
            int hz = (int)((RadioButton)sender).Tag;
            if (hz == perf.RateHz) return;
            bool ok = await app.Device.Run("set report rate", kb => kb.SetReportRateAsync(hz), false);
            if (ok) { perf.RateHz = hz; ShowToast($"Частота опроса {hz} Гц"); }
            else ShowToast("Не удалось сменить частоту");
            ShowPerformance();
        }

        void Debounce_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsInitialized || DebounceValue == null) return;
            DebounceValue.Text = (int)DebounceSlider.Value + " мс";
        }

        void Debounce_KeyUp(object sender, KeyEventArgs e) => Debounce_Commit(sender, null);

        async void Debounce_Commit(object sender, MouseButtonEventArgs e)
        {
            if (loading || perf == null) return;
            await Task.Delay(50); // let the slider finish moving
            int ms = (int)DebounceSlider.Value;
            if (ms == perf.DebounceMs) return;
            bool ok = await app.Device.Run("set debounce", kb => kb.SetDebounceAsync(ms), false);
            if (ok) { perf.DebounceMs = ms; ShowToast($"Антидребезг {ms} мс"); }
            else ShowToast("Не удалось сменить антидребезг");
            ShowPerformance();
        }

        // ---------- calibration ----------
        async void Calibrate_Click(object sender, RoutedEventArgs e)
        {
            if (!app.Device.Connected) { ShowToast("Клавиатура не подключена"); return; }
            if (!calibrating)
            {
                calibrating = true;
                app.Device.PausePolling = true;
                calProgress = null;
                CalButton.IsEnabled = false;
                CalCancel.Visibility = Visibility.Collapsed;
                CalText.Text = "Убери руки с клавиатуры — измеряю отпущенное положение…";
                ShowEditor();
                await app.Device.Run("calibration start", async kb => { await kb.CalibrationStartAsync(); return true; }, false);
                if (!app.Device.Connected) { EndCalibrationUi(); return; }
                CalText.Text = "Теперь нажми до упора каждую клавишу по одному разу. Измеренные подсвечиваются. Закончишь — жми «Готово».";
                CalButton.Content = "Готово";
                CalButton.IsEnabled = true;
                CalCancel.Visibility = Visibility.Visible;
                calPolling = true;
                calTimer.Start();
            }
            else await FinishCalibration(true);
        }

        async void CalibrateCancel_Click(object sender, RoutedEventArgs e) => await FinishCalibration(false);

        async Task FinishCalibration(bool done)
        {
            calTimer.Stop();
            calPolling = false;
            CalButton.IsEnabled = false;
            await app.Device.Run("calibration finish", async kb => { await kb.CalibrationFinishAsync(); return true; }, false);
            EndCalibrationUi();
            ShowToast(done ? "Калибровка сохранена" : "Калибровка прервана");
            travelLoaded = false;
            await Task.Delay(1600);
            if (PageKeys.Visibility == Visibility.Visible) await LoadKeys();
        }

        void EndCalibrationUi()
        {
            calTimer?.Stop();
            calibrating = calPolling = false;
            app.Device.PausePolling = false;
            calProgress = null;
            CalButton.Content = "Начать";
            CalButton.IsEnabled = true;
            CalCancel.Visibility = Visibility.Collapsed;
            CalText.Text = "Нужна после замены свичей, обновления прошивки или если клавиши срабатывают странно.";
            PaintKeys();
            ShowEditor();
        }

        async Task PollCalibration()
        {
            if (!calPolling) return;
            calPolling = false; // one request at a time
            var p = await app.Device.Run<double[]>("calibration progress", kb => kb.GetCalibrationAsync());
            if (!calibrating) return;
            if (p != null)
            {
                calProgress = p;
                int done = K85Travel.Layout.Count(k => k.Matrix < p.Length && p[k.Matrix] >= 1);
                CalText.Text = $"Нажми до упора каждую клавишу по одному разу. Измерено: {done} из {K85Travel.Layout.Length}. Закончишь — жми «Готово».";
                PaintKeys();
                calPolling = true;
            }
            // no answer: keep the step going without the live view
        }
    }
}
