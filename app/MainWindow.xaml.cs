using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AttackOnShadow.Device;
using AttackOnShadow.Services;

namespace AttackOnShadow
{
    public partial class MainWindow : Window
    {
        readonly App app;
        readonly DispatcherTimer lightDebounce, updateTimer;
        bool loading;                 // true while the UI is filled from the keyboard
        bool lightingLoaded;
        Lighting light = new Lighting();
        ReleaseInfo pendingRelease;

        static readonly string[] Swatches =
        {
            "#FFFFFF", "#29D3FF", "#1F6BFF", "#6B4BFF", "#C04BFF", "#FF4BD8",
            "#FF2E4D", "#FF8A1F", "#FFE01F", "#5CFF4B", "#1FFFB0", "#7FB2FF",
        };

        public MainWindow(App a)
        {
            app = a;
            InitializeComponent();
            VersionText.Text = "v" + AppInfo.Version;

            foreach (var eff in K85Protocol.Effects)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = eff.Name, Tag = eff.Code, GroupName = "effect" };
                rb.Checked += Effect_Checked;
                EffectsPanel.Children.Add(rb);
            }
            foreach (var hex in Swatches)
            {
                var b = new Border
                {
                    Width = 30, Height = 26, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, Tag = hex,
                    Background = (Brush)new BrushConverter().ConvertFromString(hex),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
                    RenderTransform = new SkewTransform(-18, 0),
                };
                b.MouseLeftButtonUp += (s, e) => { HexBox.Text = (string)((Border)s).Tag; RainbowBox.IsChecked = false; ApplyHex(); };
                SwatchPanel.Children.Add(b);
            }

            lightDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            lightDebounce.Tick += async (s, e) => { lightDebounce.Stop(); await PushLighting(); };
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            updateTimer.Tick += async (s, e) => await MaybeCheckUpdates(false);

            LoadSettingsUi();
            app.Device.Changed += OnDevice;
            app.Auto.Changed += OnAuto;
            IsVisibleChanged += (s, e) => { if (IsVisible) { RefreshConflict(); RefreshProcesses(); } };
            Closing += OnClosing;
            OnDevice();
            OnAuto();
            ShowLighting(light);
        }

        // ---------- navigation ----------
        void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            if (sender == NavLayers) ShowPage("layers");
            else if (sender == NavKeys) ShowPage("keys");
            else if (sender == NavLight) ShowPage("light");
            else if (sender == NavPics) ShowPage("pics");
            else if (sender == NavAuto) ShowPage("auto");
            else ShowPage("settings");
        }

        public void ShowPage(string page)
        {
            PageLayers.Visibility = page == "layers" ? Visibility.Visible : Visibility.Collapsed;
            PageKeys.Visibility = page == "keys" ? Visibility.Visible : Visibility.Collapsed;
            PageLight.Visibility = page == "light" ? Visibility.Visible : Visibility.Collapsed;
            PageAuto.Visibility = page == "auto" ? Visibility.Visible : Visibility.Collapsed;
            PagePics.Visibility = page == "pics" ? Visibility.Visible : Visibility.Collapsed;
            PageSettings.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            PageTitle.Text = page == "layers" ? "СЛОИ" : page == "keys" ? "КЛАВИШИ" : page == "pics" ? "КАРТИНКИ" : page == "light" ? "ПОДСВЕТКА" : page == "auto" ? "АВТОПРОФИЛИ" : "НАСТРОЙКИ";
            var nav = page == "layers" ? NavLayers : page == "keys" ? NavKeys : page == "pics" ? NavPics : page == "light" ? NavLight : page == "auto" ? NavAuto : NavSettings;
            if (nav.IsChecked != true) nav.IsChecked = true;
            if (page == "light" && !lightingLoaded) _ = LoadLighting();
            if (page == "keys") { BuildKeys(); travelFailed = false; _ = LoadKeys(); }
            if (page == "pics") BuildPictures();
            if (page == "auto") RefreshProcesses();
            if (page == "settings") RefreshConflict();

            // P3R-style slide-in
            var target = (FrameworkElement)(page == "layers" ? PageLayers : page == "keys" ? (FrameworkElement)PageKeys : page == "pics" ? (FrameworkElement)PagePics : page == "light" ? PageLight : page == "auto" ? (FrameworkElement)PageAuto : PageSettings);
            var tt = new TranslateTransform(-30, 0);
            target.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-30, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() });
            target.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        // ---------- device / layers ----------
        void OnDevice()
        {
            var d = app.Device;
            DeviceDot.Fill = (Brush)FindResource(d.Connected ? "Cyan" : "Danger");
            DeviceName.Text = d.Keyboard?.Name ?? "Attack Shark K85";
            DeviceStatus.Text = d.Connected
                ? (d.Layer >= 0 ? $"Подключена · слой {d.Layer + 1}" : "Подключена")
                : d.Status;
            Layer1.IsEnabled = Layer2.IsEnabled = d.Connected;
            Layer1.IsChecked = d.Layer == 0;
            Layer2.IsChecked = d.Layer == 1;
            if (!d.Connected) lightingLoaded = false;
            else if (!lightingLoaded && PageLight.Visibility == Visibility.Visible) _ = LoadLighting();
            LightStatus.Text = d.Connected ? LightStatus.Text : "Клавиатура не подключена";
            KeysOnDevice();
            UpdatePicStatus();
        }

        async void Layer_Click(object sender, RoutedEventArgs e)
        {
            int layer = int.Parse((string)((FrameworkElement)sender).Tag);
            if (await app.Device.SetLayerAsync(layer)) ShowToast($"Слой {layer + 1}");
            else { ShowToast("Не удалось переключить слой"); OnDevice(); }
            lightingLoaded = false; // lighting is per layer
        }

        // ---------- lighting ----------
        async Task LoadLighting()
        {
            if (!app.Device.Connected) return;
            LightStatus.Text = "Читаю настройки…";
            var l = await app.Device.GetLightingAsync();
            if (l == null) { LightStatus.Text = "Не удалось прочитать подсветку"; return; }
            light = l;
            lightingLoaded = true;
            ShowLighting(l);
            LightStatus.Text = "Изменения сразу сохраняются в клавиатуре.";
        }

        void ShowLighting(Lighting l)
        {
            loading = true;
            try
            {
                foreach (RadioButton rb in EffectsPanel.Children) rb.IsChecked = (int)rb.Tag == l.Mode;
                BrightnessSlider.Value = l.Brightness;
                SpeedSlider.Value = l.Speed;
                RainbowBox.IsChecked = l.Rainbow;
                string hex = $"#{l.R:X2}{l.G:X2}{l.B:X2}";
                HexBox.Text = hex;
                ColorPreview.Background = (Brush)new BrushConverter().ConvertFromString(hex);
                BuildVariants(l.Mode, l.Variant);
                UpdateLightEnabled();
            }
            finally { loading = false; }
        }

        void BuildVariants(int mode, int selected)
        {
            VariantsPanel.Children.Clear();
            var eff = K85Protocol.EffectFor(mode);
            bool has = eff != null && eff.Variants.Length > 0;
            VariantTitle.Visibility = VariantsPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            if (!has) return;
            foreach (var (code, name) in eff.Variants)
            {
                var rb = new RadioButton { Style = (Style)FindResource("Chip"), Content = name, Tag = code, GroupName = "variant", IsChecked = code == selected };
                rb.Checked += (s, e) => { if (!loading) { light.Variant = (int)((RadioButton)s).Tag; QueueLight(); } };
                VariantsPanel.Children.Add(rb);
            }
        }

        void UpdateLightEnabled()
        {
            var eff = K85Protocol.EffectFor(light.Mode);
            bool on = light.Mode != 0;
            BrightnessSlider.IsEnabled = on;
            SpeedSlider.IsEnabled = on && eff != null && eff.HasSpeed;
            bool color = on && eff != null && eff.HasColor;
            RainbowBox.IsEnabled = color;
            SwatchPanel.IsEnabled = HexBox.IsEnabled = color && RainbowBox.IsChecked != true;
            SwatchPanel.Opacity = HexBox.Opacity = ColorPreview.Opacity = SwatchPanel.IsEnabled ? 1 : 0.35;
        }

        void Effect_Checked(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            light.Mode = (int)((RadioButton)sender).Tag;
            light.Variant = 0;
            loading = true; BuildVariants(light.Mode, 0); loading = false;
            UpdateLightEnabled();
            QueueLight();
        }

        void Light_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || !IsInitialized) return;
            light.Brightness = (int)BrightnessSlider.Value;
            light.Speed = (int)SpeedSlider.Value;
            light.Rainbow = RainbowBox.IsChecked == true;
            UpdateLightEnabled();
            QueueLight();
        }
        void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => Light_Changed(sender, e);

        void Hex_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) ApplyHex(); }
        void Hex_Commit(object sender, KeyboardFocusChangedEventArgs e) => ApplyHex();

        void ApplyHex()
        {
            string t = HexBox.Text.Trim().TrimStart('#');
            if (t.Length != 6 || !int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out int v))
            {
                HexBox.Text = $"#{light.R:X2}{light.G:X2}{light.B:X2}";
                return;
            }
            light.R = (byte)(v >> 16); light.G = (byte)(v >> 8); light.B = (byte)v;
            light.Rainbow = RainbowBox.IsChecked == true;
            HexBox.Text = "#" + t.ToUpperInvariant();
            ColorPreview.Background = new SolidColorBrush(Color.FromRgb(light.R, light.G, light.B));
            UpdateLightEnabled();
            if (!loading) QueueLight();
        }

        void QueueLight() { lightDebounce.Stop(); lightDebounce.Start(); LightStatus.Text = "…"; }

        async Task PushLighting()
        {
            if (!app.Device.Connected) { LightStatus.Text = "Клавиатура не подключена"; return; }
            if (app.Settings.ReactiveEnabled || app.Reactive.Active)
            {
                await TurnGifOff(false);
                ShowToast("«GIF при нажатии» выключен");
            }
            var sent = light.Clone();
            bool ok = await app.Device.SetLightingAsync(sent);
            LightStatus.Text = ok ? "Сохранено в клавиатуре." : "Не удалось сохранить.";
        }

        // ---------- auto profiles ----------
        void OnAuto()
        {
            var s = app.Settings;
            loading = true;
            AutoEnabledBox.IsChecked = s.AutoEnabled;
            FallbackNone.IsChecked = s.FallbackLayer < 0;
            Fallback1.IsChecked = s.FallbackLayer == 0;
            Fallback2.IsChecked = s.FallbackLayer == 1;
            loading = false;

            var active = app.Auto.Active;
            AutoStatus.Text = !s.AutoEnabled ? "Выключено."
                : active != null ? $"Сейчас запущена {active.Exe} → слой {active.Layer + 1}."
                : "Ни одна игра из списка не запущена." + (app.Auto.LastSwitch != "" ? " Последняя смена: " + app.Auto.LastSwitch + "." : "");
            LayersAutoNote.Text = s.AutoEnabled && s.Rules.Count > 0
                ? (active != null ? $"Автопрофили: запущена {active.Exe}, держу слой {active.Layer + 1}." : $"Автопрофили включены: игр в списке — {s.Rules.Count}.")
                : "";
            BuildRules();
            if (PageAuto.Visibility == Visibility.Visible && !ProcessList.IsKeyboardFocusWithin) RefreshProcesses();
        }

        void BuildRules()
        {
            RulesPanel.Children.Clear();
            var s = app.Settings;
            if (s.Rules.Count == 0)
            {
                RulesPanel.Children.Add(new TextBlock { Style = (Style)FindResource("Hint"), Text = "Пока пусто. Добавь игру справа." });
                return;
            }
            foreach (var r in s.Rules.ToList())
            {
                bool live = app.Auto.Active == r;
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6), Background = new SolidColorBrush(live ? Color.FromArgb(0x40, 0x29, 0xD3, 0xFF) : Color.FromArgb(0x55, 0x0B, 0x2D, 0x7D)) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = (Brush)FindResource(live ? "Cyan" : "Blue") });
                var name = new TextBlock { Text = r.Exe, FontSize = 16, Foreground = Brushes.White, Margin = new Thickness(14, 8, 10, 8), TextTrimming = TextTrimming.CharacterEllipsis };
                row.Children.Add(name);
                var layerBtn = new Button { Style = (Style)FindResource("Plate"), Content = $"Слой {r.Layer + 1}", ToolTip = "Нажми, чтобы сменить слой", Margin = new Thickness(0, 4, 10, 4) };
                layerBtn.Click += (o, e) => { r.Layer = r.Layer == 0 ? 1 : 0; s.Save(); app.Auto.Reset(); OnAuto(); };
                Grid.SetColumn(layerBtn, 1);
                row.Children.Add(layerBtn);
                var del = new Button { Style = (Style)FindResource("Caption"), Content = "✕", ToolTip = "Удалить" };
                del.Click += (o, e) => { s.Rules.Remove(r); s.Save(); app.Auto.Reset(); OnAuto(); };
                Grid.SetColumn(del, 2);
                row.Children.Add(del);
                RulesPanel.Children.Add(row);
            }
        }

        void RefreshProcesses()
        {
            var sel = ProcessList.SelectedItem as string;
            ProcessList.ItemsSource = app.Auto.Candidates().ToList();
            if (sel != null) ProcessList.SelectedItem = sel;
        }

        void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProcessList.SelectedItem is string s) ExeBox.Text = s;
        }

        void AddRule_Click(object sender, RoutedEventArgs e)
        {
            string exe = ExeBox.Text.Trim();
            if (exe.Length == 0) { ShowToast("Впиши .exe игры"); return; }
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            var s = app.Settings;
            s.Rules.RemoveAll(r => string.Equals(r.Exe, exe, StringComparison.OrdinalIgnoreCase));
            s.Rules.Add(new GameRule { Exe = exe, Layer = NewRule2.IsChecked == true ? 1 : 0 });
            s.Save();
            ExeBox.Text = "";
            ProcessList.SelectedItem = null;
            app.Auto.Reset();
            OnAuto();
            ShowToast($"{exe} → слой {(NewRule2.IsChecked == true ? 2 : 1)}");
        }

        void AutoEnabled_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || !IsInitialized) return;
            app.Settings.AutoEnabled = AutoEnabledBox.IsChecked == true;
            app.Settings.Save();
            app.Auto.Reset();
            OnAuto();
        }

        void Fallback_Checked(object sender, RoutedEventArgs e)
        {
            if (loading || !IsInitialized) return;
            app.Settings.FallbackLayer = int.Parse((string)((FrameworkElement)sender).Tag);
            app.Settings.Save();
            app.Auto.Reset();
        }

        // ---------- settings ----------
        void LoadSettingsUi()
        {
            loading = true;
            var s = app.Settings;
            AutostartBox.IsChecked = Autostart.Enabled;
            TrayBox.IsChecked = s.CloseToTray;
            UsbLogBox.IsChecked = s.UsbLog;
            UpdatesBox.IsChecked = s.CheckUpdates;
            AutoUpdateBox.IsChecked = s.AutoInstallUpdates;
            InstallButton.Visibility = Installer.RunningInstalled ? Visibility.Collapsed : Visibility.Visible;
            UpdateStatus.Text = "Версия " + AppInfo.Version + ".";
            loading = false;
        }

        void Settings_Changed(object sender, RoutedEventArgs e)
        {
            if (loading || !IsInitialized) return;
            var s = app.Settings;
            s.CloseToTray = TrayBox.IsChecked == true;
            s.UsbLog = UsbLogBox.IsChecked == true;
            Log.UsbEnabled = s.UsbLog;
            s.CheckUpdates = UpdatesBox.IsChecked == true;
            s.AutoInstallUpdates = AutoUpdateBox.IsChecked == true;
            s.Save();
            if (sender == AutostartBox)
            {
                try { Autostart.Set(AutostartBox.IsChecked == true); }
                catch (Exception x) { ShowToast("Автозапуск: " + x.Message); }
            }
        }

        void OpenLogs_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(Paths.Data);
            Process.Start("explorer.exe", "\"" + Paths.Data + "\"");
        }

        void Install_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Installer.Install();
                Process.Start(Installer.InstalledExe, "--updated");
                app.Quit();
            }
            catch (Exception x) { ShowToast("Не получилось: " + x.Message); }
        }

        void RefreshConflict()
        {
            bool vendor = Process.GetProcessesByName("Attack Shark Driver").Length > 0
                || Process.GetProcesses().Any(p => { try { return p.ProcessName.StartsWith("iot_driver", StringComparison.OrdinalIgnoreCase); } catch { return false; } });
            string mod = LegacyMod.FindDriverApp();
            ConflictCard.Visibility = vendor || mod != null ? Visibility.Visible : Visibility.Collapsed;
            ConflictText.Text =
                (vendor ? "Сейчас запущен Attack Shark Driver (или его служба iot_driver). Он тоже общается с клавиатурой — лучше закрыть его, чтобы команды не перемешивались. " : "") +
                (mod != null ? "В Attack Shark Driver стоит старый мод AttackOnShadow. Он больше не нужен: кнопка ниже вернёт драйвер в исходный вид." : "");
            RemoveModButton.Visibility = mod != null ? Visibility.Visible : Visibility.Collapsed;
            RemoveModButton.Tag = mod;
        }

        void RemoveMod_Click(object sender, RoutedEventArgs e)
        {
            try { LegacyMod.Remove((string)RemoveModButton.Tag); ShowToast("Мод удалён, драйвер как был"); }
            catch (Exception x) { ShowToast("Не получилось: " + x.Message + " (может, нужны права администратора)"); }
            RefreshConflict();
        }

        // ---------- updates ----------
        public void StartUpdateChecks()
        {
            updateTimer.Start();
            _ = Task.Delay(TimeSpan.FromSeconds(15)).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(async () => await MaybeCheckUpdates(false))));
        }

        async Task MaybeCheckUpdates(bool manual)
        {
            var s = app.Settings;
            if (!manual)
            {
                // a found update is installed when the window is hidden and no game runs
                if (pendingRelease != null && s.AutoInstallUpdates && !IsVisible && app.Auto.Active == null) { await InstallUpdate(true); return; }
                if (!s.CheckUpdates || DateTime.Now - s.LastUpdateCheck < TimeSpan.FromHours(24)) return;
            }
            UpdateStatus.Text = "Проверяю…";
            try
            {
                var r = await Updater.LatestAsync();
                s.LastUpdateCheck = DateTime.Now; s.Save();
                if (Updater.Compare(r.Version, AppInfo.Version) > 0 && r.ExeUrl != null)
                {
                    pendingRelease = r;
                    UpdateStatus.Text = $"Доступна версия {r.Version}." + (s.AutoInstallUpdates ? " Встанет сама, когда программа будет в трее." : "");
                    UpdateButton.Content = "Обновить до " + r.Version;
                    UpdateButton.Visibility = Visibility.Visible;
                    if (!manual) ShowToast("Доступна новая версия " + r.Version);
                    if (!manual && s.AutoInstallUpdates && !IsVisible && app.Auto.Active == null) await InstallUpdate(true);
                }
                else UpdateStatus.Text = $"Стоит последняя версия ({AppInfo.Version}).";
            }
            catch (Exception x) { UpdateStatus.Text = "Не удалось проверить: " + x.Message; }
        }

        async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await MaybeCheckUpdates(true);
        async void Update_Click(object sender, RoutedEventArgs e) => await InstallUpdate(false);

        async Task InstallUpdate(bool hidden)
        {
            if (pendingRelease == null) return;
            UpdateStatus.Text = "Скачиваю…";
            try
            {
                await Updater.InstallAsync(pendingRelease, hidden);
                app.Quit();
            }
            catch (Exception x) { UpdateStatus.Text = "Не вышло: " + x.Message; Log.Info("update failed: " + x); }
        }

        // ---------- window ----------
        void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        void Close_Click(object sender, RoutedEventArgs e) => Close();

        void OnClosing(object sender, CancelEventArgs e)
        {
            if (app.Settings.CloseToTray) { e.Cancel = true; Hide(); }
            else app.Quit();
        }

        public void ShowToast(string text)
        {
            ToastText.Text = text;
            var a = new DoubleAnimationUsingKeyFrames();
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2300))));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
            Toast.BeginAnimation(OpacityProperty, a);
        }
    }
}
