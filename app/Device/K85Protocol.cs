using System;
using System.Collections.Generic;
using System.Linq;

namespace AttackOnShadow.Device
{
    /// <summary>Lighting settings as stored in the keyboard (see docs/protocol-k85.md).</summary>
    public sealed class Lighting
    {
        public int Mode;          // effect code
        public int Brightness;    // 0..4
        public int Speed;         // 0..4 (UI value; the keyboard stores 4 - speed)
        public int Variant;       // effect variant (direction etc.), high nibble of the option byte
        public bool Rainbow;      // "dazzle": multicolour instead of one colour
        public byte R = 0x29, G = 0xD3, B = 0xFF;

        public Lighting Clone() => (Lighting)MemberwiseClone();
        public override bool Equals(object o) => o is Lighting l && l.Mode == Mode && l.Brightness == Brightness && l.Speed == Speed
            && l.Variant == Variant && l.Rainbow == Rainbow && l.R == R && l.G == G && l.B == B;
        public override int GetHashCode() => Mode ^ (Brightness << 4) ^ (Speed << 8) ^ (R << 12) ^ (G << 16) ^ (B << 20);
    }

    public sealed class Effect
    {
        public int Code;
        public string Name;
        public bool HasSpeed = true, HasColor = true;
        public (int code, string name)[] Variants = new (int, string)[0];
    }

    public static class K85Protocol
    {
        public const ushort Vid = 0x3151, Pid = 0x4015;
        public const ushort UsagePage = 0xFFFF, Usage = 0x0002;
        public const int LayerCount = 2;      // user-visible layers
        public const int SlotsPerLayer = 4;   // onboard profile slots per layer on hall keyboards

        public const byte SET_PROFILE = 0x02, GET_PROFILE = 0x82;
        public const byte SET_LEDPARAM = 0x04, GET_LEDPARAM = 0x84;
        public const byte GET_REV = 0x80;

        const int Normal = 7, Dazzle = 8, MaxSpeed = 4;
        static readonly int[] PresetColors = { 0xFF0000, 0xFF8000, 0xFFFF00, 0x00FF00, 0x00FFFF, 0x0000FF, 0xFF00FF };

        public static readonly Effect[] Effects =
        {
            new Effect { Code = 0, Name = "Выключена", HasSpeed = false, HasColor = false },
            new Effect { Code = 1, Name = "Статичная", HasSpeed = false },
            new Effect { Code = 2, Name = "Дыхание" },
            new Effect { Code = 3, Name = "Неон", HasColor = false },
            new Effect { Code = 4, Name = "Волна", Variants = new[] { (0, "Вправо"), (1, "Влево"), (2, "Вниз"), (3, "Вверх") } },
            new Effect { Code = 5, Name = "Рябь" },
            new Effect { Code = 6, Name = "Капли" },
            new Effect { Code = 7, Name = "Змейка", Variants = new[] { (0, "Зигзаг"), (1, "Туда-обратно") } },
            new Effect { Code = 8, Name = "Отклик на нажатие" },
            new Effect { Code = 9, Name = "Схождение" },
            new Effect { Code = 10, Name = "Синусоида" },
            new Effect { Code = 11, Name = "Калейдоскоп", Variants = new[] { (0, "Наружу"), (1, "Внутрь") } },
            new Effect { Code = 12, Name = "Бегущая линия", Variants = new[] { (0, "Вправо"), (1, "Влево") } },
            new Effect { Code = 14, Name = "Лазер" },
            new Effect { Code = 15, Name = "Круговая волна", Variants = new[] { (0, "Против часовой"), (1, "По часовой") } },
            new Effect { Code = 16, Name = "Мерцание" },
            new Effect { Code = 17, Name = "Дождь" },
            new Effect { Code = 18, Name = "Метеор" },
            new Effect { Code = 19, Name = "Гаснущие следы" },
        };

        public static Effect EffectFor(int code) => Effects.FirstOrDefault(e => e.Code == code);

        /// <summary>checksum = ~(sum of bytes 0..n-1), stored at byte n.</summary>
        public static void Checksum(byte[] p, int n)
        {
            int sum = 0;
            for (int i = 0; i < n; i++) sum += p[i];
            p[n] = (byte)~(sum & 0xFF);
        }

        static byte[] Packet(byte cmd) { var p = new byte[64]; p[0] = cmd; return p; }

        public static byte[] GetProfile() { var p = Packet(GET_PROFILE); Checksum(p, 7); return p; }

        public static byte[] SetProfile(int slot)
        {
            var p = Packet(SET_PROFILE);
            p[1] = (byte)slot;
            Checksum(p, 7);
            return p;
        }

        public static byte[] SetLayer(int layer) => SetProfile(Math.Max(0, Math.Min(LayerCount - 1, layer)) * SlotsPerLayer);
        public static int LayerFromSlot(int slot) => Math.Max(0, Math.Min(LayerCount - 1, slot / SlotsPerLayer));

        /// <summary>Parses a GET_PROFILE answer (payload without report id). Returns the onboard slot or -1.</summary>
        public static int ParseProfile(byte[] r)
        {
            if (r == null || r.Length < 2) return -1;
            if (r[0] != GET_PROFILE && r[0] != 0) return -1;
            return r[1] < 8 ? r[1] : -1;
        }

        public static byte[] GetLedParam() { var p = Packet(GET_LEDPARAM); Checksum(p, 7); return p; }

        public static byte[] SetLedParam(Lighting l)
        {
            var p = Packet(SET_LEDPARAM);
            var eff = EffectFor(l.Mode);
            int low = l.Rainbow ? Dazzle : Normal;
            int variant = eff != null && eff.Variants.Length > 0 ? l.Variant : 0;
            p[1] = (byte)l.Mode;
            p[2] = (byte)(MaxSpeed - Clamp(l.Speed, 0, MaxSpeed));
            p[3] = (byte)Clamp(l.Brightness, 0, 4);
            p[4] = (byte)((variant << 4) | low);
            if (eff == null || eff.HasColor)
            {
                // the stock driver sends pure white as #FAFFFA
                bool white = l.R == 0xFF && l.G == 0xFF && l.B == 0xFF;
                p[5] = white ? (byte)0xFA : l.R;
                p[6] = 0xFF == l.G && white ? (byte)0xFF : l.G;
                p[7] = white ? (byte)0xFA : l.B;
            }
            Checksum(p, 8);
            return p;
        }

        /// <summary>Parses a GET_LEDPARAM answer (payload without report id).</summary>
        public static Lighting ParseLedParam(byte[] r)
        {
            if (r == null || r.Length < 8 || r[0] != GET_LEDPARAM) return null;
            var l = new Lighting
            {
                Mode = r[1],
                Speed = Clamp(MaxSpeed - r[2], 0, MaxSpeed),
                Brightness = Clamp(r[3], 0, 4),
                Variant = r[4] >> 4,
                R = r[5], G = r[6], B = r[7],
            };
            int low = r[4] & 0x0F;
            l.Rainbow = low == Dazzle;
            if (low != Dazzle && low != Normal && low < PresetColors.Length)
            {
                int c = PresetColors[low];
                l.R = (byte)(c >> 16); l.G = (byte)(c >> 8); l.B = (byte)c;
            }
            if (l.R == 0xFA && l.G == 0xFF && l.B == 0xFA) { l.R = 0xFF; l.B = 0xFF; }
            return l;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        public static string Hex(byte[] p, int n = 10) => string.Join(" ", p.Take(n).Select(b => b.ToString("X2")));

        /// <summary>Self-checks run by CI (--selftest). Returns a list of failures.</summary>
        public static List<string> SelfTest()
        {
            var fail = new List<string>();
            void Eq(string what, string got, string want) { if (got != want) fail.Add($"{what}: {got} != {want}"); }

            // checksums: vendor formula, cross-checked with debloatedfl-driver's tests
            Eq("GET_PROFILE", Hex(GetProfile(), 8), "82 00 00 00 00 00 00 7D");
            Eq("GET_LEDPARAM", Hex(GetLedParam(), 8), "84 00 00 00 00 00 00 7B");
            Eq("SET_PROFILE layer 2", Hex(SetLayer(1), 8), "02 04 00 00 00 00 00 F9");
            Eq("SET_PROFILE layer 1", Hex(SetLayer(0), 8), "02 00 00 00 00 00 00 FD");

            var l = new Lighting { Mode = 4, Speed = 3, Brightness = 4, Variant = 1, Rainbow = false, R = 0x10, G = 0x20, B = 0x30 };
            var p = SetLedParam(l);
            Eq("SET_LEDPARAM wave", Hex(p, 9), "04 04 01 04 17 10 20 30 " + ((byte)~((4 + 4 + 1 + 4 + 0x17 + 0x10 + 0x20 + 0x30) & 0xFF)).ToString("X2"));
            var back = new byte[64]; Array.Copy(p, back, 64); back[0] = GET_LEDPARAM;
            var parsed = ParseLedParam(back);
            if (parsed == null || !parsed.Equals(l)) fail.Add("LEDPARAM roundtrip");

            var white = SetLedParam(new Lighting { Mode = 1, Brightness = 2, R = 255, G = 255, B = 255 });
            Eq("white quirk", Hex(white, 8).Substring(15), "FA FF FA");
            var wb = new byte[64]; Array.Copy(white, wb, 64); wb[0] = GET_LEDPARAM;
            var wp = ParseLedParam(wb);
            if (wp == null || wp.R != 255 || wp.G != 255 || wp.B != 255) fail.Add("white roundtrip");

            var preset = new byte[64]; preset[0] = GET_LEDPARAM; preset[1] = 1; preset[4] = 2; // preset colour #2 = yellow
            var pp = ParseLedParam(preset);
            if (pp == null || pp.R != 255 || pp.G != 255 || pp.B != 0) fail.Add("preset colour");

            Eq("ParseProfile", ParseProfile(new byte[] { 0x82, 4 }).ToString(), "4");
            Eq("LayerFromSlot", LayerFromSlot(5).ToString(), "1");
            return fail;
        }
    }
}
