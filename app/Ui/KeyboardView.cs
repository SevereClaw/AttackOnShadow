using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AttackOnShadow.Device;

namespace AttackOnShadow.Ui
{
    /// <summary>A small K85 drawing whose keys show colours (pictures, GIF frames). Click selects a key.</summary>
    public sealed class KeyboardView : Viewbox
    {
        const double U = 40;
        readonly Canvas canvas = new Canvas { Background = Brushes.Transparent };
        readonly Dictionary<int, (Border box, TextBlock label, Border mark)> keys = new Dictionary<int, (Border, TextBlock, Border)>();
        int selected = -1;

        public event Action<int> KeyClicked;
        public HashSet<int> Marked { get; } = new HashSet<int>();   // keys with their own GIF

        public KeyboardView()
        {
            Stretch = Stretch.Uniform;
            Child = canvas;
            canvas.Width = K85Travel.LayoutWidth * U;
            canvas.Height = K85Travel.LayoutRows * U;
            foreach (var k in K85Travel.Layout)
            {
                var g = new Grid { Width = k.W * U - 3, Height = U - 3, Cursor = Cursors.Hand };
                var box = new Border { CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.5), Background = Brushes.Black };
                var label = new TextBlock
                {
                    Text = k.Label, FontSize = k.Label.Length > 3 ? 10 : 12.5, FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(5, 3, 3, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                };
                var mark = new Border
                {
                    Width = 7, Height = 7, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 5, 5), Background = Brushes.White,
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x06, 0x1A, 0x4D)), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed,
                };
                g.Children.Add(box); g.Children.Add(label); g.Children.Add(mark);
                int m = k.Matrix;
                g.MouseLeftButtonDown += (s, e) => KeyClicked?.Invoke(m);
                Canvas.SetLeft(g, k.X * U + 1.5);
                Canvas.SetTop(g, k.Row * U + 1.5);
                canvas.Children.Add(g);
                keys[m] = (box, label, mark);
            }
            SetColors(null);
        }

        public int Selected
        {
            get => selected;
            set { selected = value; Refresh(); }
        }

        byte[] colors;

        /// <summary>3 bytes per matrix index; null = dark.</summary>
        public void SetColors(byte[] rgb) { colors = rgb; Refresh(); }

        public void Refresh()
        {
            foreach (var kv in keys)
            {
                int m = kv.Key;
                byte r = 0, g = 0, b = 0;
                if (colors != null && 3 * m + 2 < colors.Length) { r = colors[3 * m]; g = colors[3 * m + 1]; b = colors[3 * m + 2]; }
                var (box, label, mark) = kv.Value;
                // keys that are off still read as keys
                var c = Color.FromRgb((byte)Math.Max((int)r, 0x0A), (byte)Math.Max((int)g, 0x12), (byte)Math.Max((int)b, 0x2A));
                box.Background = new SolidColorBrush(c);
                bool bright = 0.299 * r + 0.587 * g + 0.114 * b > 150;
                label.Foreground = bright ? new SolidColorBrush(Color.FromRgb(0x06, 0x1A, 0x4D)) : Brushes.White;
                box.BorderBrush = m == selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(0x59, 0x29, 0xD3, 0xFF));
                box.BorderThickness = new Thickness(m == selected ? 3 : 1.5);
                mark.Visibility = Marked.Contains(m) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
