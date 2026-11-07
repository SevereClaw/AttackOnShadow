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

        // Command numbers of the K85's protocol class (class NA in the stock driver
        // overrides the base table: 0x02/0x04 there mean RESET and REPORT RATE).
        public const byte SET_PROFILE = 0x05, GET_PROFILE = 0x85;
        public const byte SET_LEDPARAM = 0x07, GET_LEDPARAM = 0x87;
        public const byte GET_REV = 0x80;
        public const byte SET_USERPIC = 0x0C;
        public const int UserPictureMode = 13;    // "LightUserPicture": shows the picture stored in slot (variant)
        public const int PictureSlots = 5;
        public const int PicturePages = 7;        // 7 x 56 bytes = RGB for matrix indices 0..130
        const int PictureLength = 384;            // stock driver: 64 * floor(512 / 4 * 3 / 64)

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
            new Effect { Code = UserPictureMode, Name = "Своя картинка", HasSpeed = false, HasColor = false,
                         Variants = new[] { (0, "Слот 1"), (1, "Слот 2"), (2, "Слот 3"), (3, "Слот 4"), (4, "Слот 5") } },
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
            if (l.Mode == UserPictureMode)
            {
                // the stock driver sends: no speed, slot in the high nibble, colour 00 C8 C8
                p[3] = (byte)Clamp(l.Brightness, 0, 4);
                p[4] = (byte)(Clamp(l.Variant, 0, PictureSlots - 1) << 4);
                p[6] = 0xC8; p[7] = 0xC8;
                Checksum(p, 8);
                return p;
            }
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

        /// <summary>
        /// The packets that store a per-key picture into the slot that is shown now.
        /// rgb holds 3 bytes per matrix index.
        /// </summary>
        public static List<byte[]> SetUserPicture(byte[] rgb)
        {
            var list = new List<byte[]>();
            for (int page = 0; page < PicturePages; page++)
            {
                var p = Packet(SET_USERPIC);
                p[2] = PictureLength & 0xFF; p[3] = PictureLength >> 8; p[4] = (byte)page;
                Checksum(p, 7);
                for (int i = 0; i < 56; i++)
                {
                    int k = page * 56 + i;
                    if (k < rgb.Length) p[8 + i] = rgb[k];
                }
                list.Add(p);
            }
            return list;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        public static string Hex(byte[] p, int n = 10) => string.Join(" ", p.Take(n).Select(b => b.ToString("X2")));

        /// <summary>Self-checks run by CI (--selftest). Returns a list of failures.</summary>
        public static List<string> SelfTest()
        {
            var fail = new List<string>();
            void Eq(string what, string got, string want) { if (got != want) fail.Add($"{what}: {got} != {want}"); }

            // checksums: vendor formula, cross-checked with debloatedfl-driver's tests
            Eq("GET_PROFILE", Hex(GetProfile(), 8), "85 00 00 00 00 00 00 7A");
            Eq("GET_LEDPARAM", Hex(GetLedParam(), 8), "87 00 00 00 00 00 00 78"); // same bytes as debloatedfl-driver
            Eq("SET_PROFILE layer 2", Hex(SetLayer(1), 8), "05 04 00 00 00 00 00 F6");
            Eq("SET_PROFILE layer 1", Hex(SetLayer(0), 8), "05 00 00 00 00 00 00 FA");
            // the two commands that must never be sent by mistake
            if (SetLayer(0)[0] == 0x02 || SetLedParam(new Lighting())[0] == 0x04) fail.Add("RESET/REPORT command number used");

            var l = new Lighting { Mode = 4, Speed = 3, Brightness = 4, Variant = 1, Rainbow = false, R = 0x10, G = 0x20, B = 0x30 };
            var p = SetLedParam(l);
            Eq("SET_LEDPARAM wave", Hex(p, 9), "07 04 01 04 17 10 20 30 " + ((byte)(~(7 + 4 + 1 + 4 + 0x17 + 0x10 + 0x20 + 0x30) & 0xFF)).ToString("X2"));
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

            var pic = new byte[3 * 128]; pic[3 * 41] = 0x11; pic[3 * 89 + 2] = 0x22; // space red, right arrow blue
            var pk = SetUserPicture(pic);
            Eq("picture pages", pk.Count.ToString(), "7");
            Eq("picture header", Hex(pk[2], 8), "0C 00 80 01 02 00 00 " + ((byte)(~(0x0C + 0x80 + 1 + 2) & 0xFF)).ToString("X2"));
            Eq("picture space", pk[2][8 + 3 * 41 - 112].ToString("X2"), "11");
            Eq("picture arrow", pk[4][8 + 3 * 89 + 2 - 224].ToString("X2"), "22");
            var up = SetLedParam(new Lighting { Mode = UserPictureMode, Brightness = 4, Variant = 2 });
            Eq("user picture mode", Hex(up, 9), "07 0D 00 04 20 00 C8 C8 " + ((byte)(~(7 + 13 + 4 + 0x20 + 0xC8 + 0xC8) & 0xFF)).ToString("X2"));
            var upb = new byte[64]; Array.Copy(up, upb, 64); upb[0] = GET_LEDPARAM;
            var upp = ParseLedParam(upb);
            if (upp == null || upp.Mode != UserPictureMode || upp.Variant != 2) fail.Add("user picture roundtrip");

            Eq("ParseProfile", ParseProfile(new byte[] { 0x85, 4 }).ToString(), "4");
            Eq("LayerFromSlot", LayerFromSlot(5).ToString(), "1");
            return fail;
        }
    }
}
