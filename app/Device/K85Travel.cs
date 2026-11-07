using System;
using System.Collections.Generic;
using System.Linq;

namespace AttackOnShadow.Device
{
    /// <summary>One physical key: its index in the K85 key matrix and where it is drawn (in key units).</summary>
    public sealed class KeyDef
    {
        public int Matrix;
        public string Label;
        public int Row;
        public double X, W;
    }

    /// <summary>
    /// Per-key travel settings of the hall-effect switches, as stored in the keyboard:
    /// one byte per matrix index for every parameter (see docs/protocol-k85.md).
    /// </summary>
    public sealed class TravelMap
    {
        public const int Size = 128;               // two 64-byte pages
        public byte[] Mode = new byte[Size];       // key mode, bit 7 = Rapid Trigger
        public byte[] Act = new byte[Size];        // actuation point, (raw + 1) / 10 mm
        public byte[] Rel = new byte[Size];        // release point, (raw + 1) / 10 mm
        public byte[] RtPress = new byte[Size];    // RT press sensitivity, raw / 10 mm
        public byte[] RtRelease = new byte[Size];  // RT release sensitivity, raw / 10 mm
        public byte[] Dead = new byte[Size];       // bottom dead zone, 0 = off, else (raw + 1) / 10 mm
        public int KeyboardMode = -1;              // 0 comfort, 1 sensitive, 2 gaming, 3 custom
        public byte ModeFlag1, ModeFlag2;          // "keyboard disabled" / "gamepad" flags of that command

        public TravelMap Clone()
        {
            var c = (TravelMap)MemberwiseClone();
            c.Mode = (byte[])Mode.Clone(); c.Act = (byte[])Act.Clone(); c.Rel = (byte[])Rel.Clone();
            c.RtPress = (byte[])RtPress.Clone(); c.RtRelease = (byte[])RtRelease.Clone(); c.Dead = (byte[])Dead.Clone();
            return c;
        }

        public byte[] Array(int param) =>
            param == K85Travel.P_ACT ? Act : param == K85Travel.P_REL ? Rel : param == K85Travel.P_RTPRESS ? RtPress
            : param == K85Travel.P_RTRELEASE ? RtRelease : param == K85Travel.P_DEAD ? Dead : param == K85Travel.P_MODE ? Mode : null;

        public bool Rapid(int m) => (Mode[m] & 0x80) != 0;
        /// <summary>True for plain keys (or snap); DKS / mod-tap / toggle keys only take the RT settings.</summary>
        public bool Plain(int m) { int k = Mode[m] & 0x7F; return k == 0 || k == 7; }
    }

    /// <summary>What the user edits for the selected keys, in millimetres.</summary>
    public sealed class KeyTravel
    {
        public double Actuation = 1.9, Release = 1.9, RtPress = 0.2, RtRelease = 0.3, DeadZone = 3.7;
        public bool Rapid;
    }

    public static class K85Travel
    {
        // command numbers (class mYe of the stock driver, which the K85 class extends)
        public const byte SET_REPORT = 0x04, GET_REPORT = 0x84;
        public const byte SET_DEBOUNCE = 0x11, GET_DEBOUNCE = 0x91;
        public const byte SET_MAG_ARR = 0x65, GET_MAG_ARR = 0xE5;
        public const byte SET_KEY_MAG_MODE = 0x1D, GET_KEY_MAG_MODE = 0x9D;
        public const byte SET_CAL = 0x1C, GET_CAL = 0x9C, SET_CALMAX = 0x1E, GET_CALMAX = 0x9E;

        // parameter ids of the array commands
        public const int P_ACT = 0, P_REL = 1, P_RTPRESS = 2, P_RTRELEASE = 3, P_DEAD = 6, P_MODE = 7;
        public static readonly int[] Params = { P_MODE, P_ACT, P_REL, P_DEAD, P_RTPRESS, P_RTRELEASE };
        public const int ReadPages = 2, ReadPageSize = 64, WritePageSize = 56;

        public const double TravelMin = 0.1, TravelMax = 4.0, RtMin = 0.2, RtMax = 2.5, DeadMax = 4.0;
        public static readonly int[] Rates = { 125, 250, 500, 1000 };
        public const int DebounceMin = 0, DebounceMax = 10;

        public static readonly string[] KeyboardModes = { "Комфорт", "Чувствительный", "Игровой", "Свой" };

        static KeyDef K(int m, string label, int row, double x, double w) => new KeyDef { Matrix = m, Label = label, Row = row, X = x, W = w };

        /// <summary>The 82 magnetic keys of the K85 (75 %, ANSI). The knob is not a hall switch.</summary>
        public static readonly KeyDef[] Layout =
        {
            K(0, "Esc", 0, 0, 1),
            K(12, "F1", 0, 1.5, 1),
            K(18, "F2", 0, 2.5, 1),
            K(24, "F3", 0, 3.5, 1),
            K(30, "F4", 0, 4.5, 1),
            K(36, "F5", 0, 5.75, 1),
            K(42, "F6", 0, 6.75, 1),
            K(48, "F7", 0, 7.75, 1),
            K(54, "F8", 0, 8.75, 1),
            K(60, "F9", 0, 10, 1),
            K(66, "F10", 0, 11, 1),
            K(72, "F11", 0, 12, 1),
            K(78, "F12", 0, 13, 1),
            K(84, "Del", 0, 15, 1),
            K(1, "`", 1, 0, 1),
            K(7, "1", 1, 1, 1),
            K(13, "2", 1, 2, 1),
            K(19, "3", 1, 3, 1),
            K(25, "4", 1, 4, 1),
            K(31, "5", 1, 5, 1),
            K(37, "6", 1, 6, 1),
            K(43, "7", 1, 7, 1),
            K(49, "8", 1, 8, 1),
            K(55, "9", 1, 9, 1),
            K(61, "0", 1, 10, 1),
            K(67, "-", 1, 11, 1),
            K(73, "=", 1, 12, 1),
            K(79, "Bksp", 1, 13, 2),
            K(85, "Home", 1, 15, 1),
            K(2, "Tab", 2, 0, 1.5),
            K(8, "Q", 2, 1.5, 1),
            K(14, "W", 2, 2.5, 1),
            K(20, "E", 2, 3.5, 1),
            K(26, "R", 2, 4.5, 1),
            K(32, "T", 2, 5.5, 1),
            K(38, "Y", 2, 6.5, 1),
            K(44, "U", 2, 7.5, 1),
            K(50, "I", 2, 8.5, 1),
            K(56, "O", 2, 9.5, 1),
            K(62, "P", 2, 10.5, 1),
            K(68, "[", 2, 11.5, 1),
            K(74, "]", 2, 12.5, 1),
            K(80, "\\", 2, 13.5, 1.5),
            K(86, "PgUp", 2, 15, 1),
            K(3, "Caps", 3, 0, 1.75),
            K(9, "A", 3, 1.75, 1),
            K(15, "S", 3, 2.75, 1),
            K(21, "D", 3, 3.75, 1),
            K(27, "F", 3, 4.75, 1),
            K(33, "G", 3, 5.75, 1),
            K(39, "H", 3, 6.75, 1),
            K(45, "J", 3, 7.75, 1),
            K(51, "K", 3, 8.75, 1),
            K(57, "L", 3, 9.75, 1),
            K(63, ";", 3, 10.75, 1),
            K(69, "'", 3, 11.75, 1),
            K(81, "Enter", 3, 12.75, 2.25),
            K(87, "PgDn", 3, 15, 1),
            K(4, "Shift", 4, 0, 2.25),
            K(16, "Z", 4, 2.25, 1),
            K(22, "X", 4, 3.25, 1),
            K(28, "C", 4, 4.25, 1),
            K(34, "V", 4, 5.25, 1),
            K(40, "B", 4, 6.25, 1),
            K(46, "N", 4, 7.25, 1),
            K(52, "M", 4, 8.25, 1),
            K(58, ",", 4, 9.25, 1),
            K(64, ".", 4, 10.25, 1),
            K(70, "/", 4, 11.25, 1),
            K(76, "Shift", 4, 12.25, 1.75),
            K(82, "↑", 4, 14, 1),
            K(88, "End", 4, 15, 1),
            K(5, "Ctrl", 5, 0, 1.25),
            K(17, "Win", 5, 1.25, 1.25),
            K(23, "Alt", 5, 2.5, 1.25),
            K(41, "", 5, 3.75, 6.25),
            K(59, "Alt", 5, 10, 1),
            K(65, "Fn", 5, 11, 1),
            K(71, "Ctrl", 5, 12, 1),
            K(77, "←", 5, 13, 1),
            K(83, "↓", 5, 14, 1),
            K(89, "→", 5, 15, 1),
        };

        public const double LayoutWidth = 16, LayoutRows = 6;

        // ---------- conversions ----------
        public static int Round10(double mm) => (int)Math.Round(mm * 10, MidpointRounding.AwayFromZero);
        public static byte TravelRaw(double mm) => (byte)Clamp(Round10(mm) - 1, 0, 39);
        public static double TravelMm(byte raw) => (raw + 1) / 10.0;
        public static byte RtRaw(double mm) => (byte)Clamp(Round10(mm), 1, 40);
        public static double RtMm(byte raw) => raw / 10.0;
        public static byte DeadRaw(double mm) => mm <= 0 ? (byte)0 : (byte)Clamp(Round10(mm) - 1, 0, 39);
        public static double DeadMm(byte raw) => raw == 0 ? 0 : (raw + 1) / 10.0;

        public static byte RateCode(int hz) => hz >= 1000 ? (byte)1 : hz >= 500 ? (byte)2 : hz >= 250 ? (byte)4 : (byte)8;
        public static int RateHz(int code) => code == 1 ? 1000 : code == 2 ? 500 : code == 4 ? 250 : code == 8 ? 125 : -1;

        // ---------- packets ----------
        static byte[] Packet(byte cmd, params byte[] args)
        {
            var p = new byte[64];
            p[0] = cmd;
            for (int i = 0; i < args.Length; i++) p[1 + i] = args[i];
            K85Protocol.Checksum(p, 7);
            return p;
        }

        public static byte[] SetReportRate(int slot, int hz) => Packet(SET_REPORT, (byte)slot, RateCode(hz));
        public static byte[] GetReportRate(int slot) => Packet(GET_REPORT, (byte)slot);
        public static int ParseReportRate(byte[] r) => r != null && r.Length > 2 && r[0] == GET_REPORT ? RateHz(r[2]) : -1;

        public static byte[] SetDebounce(int ms) => Packet(SET_DEBOUNCE, 0, (byte)Clamp(ms, 0, 50));
        public static byte[] GetDebounce() => Packet(GET_DEBOUNCE);
        public static int ParseDebounce(byte[] r) => r != null && r.Length > 2 && r[0] == GET_DEBOUNCE ? r[2] : -1;

        public static byte[] GetKeyboardMode() => Packet(GET_KEY_MAG_MODE);
        public static byte[] SetKeyboardMode(int mode, byte f1, byte f2) => Packet(SET_KEY_MAG_MODE, (byte)mode, f1, f2);

        public static byte[] Calibrate(bool on) => Packet(SET_CAL, on ? (byte)1 : (byte)0);
        public static byte[] CalibrateMax(bool on) => Packet(SET_CALMAX, on ? (byte)1 : (byte)0);
        public static byte[] GetCal(int page) => Packet(GET_CAL, (byte)page);
        public static byte[] GetCalMax(int page) => Packet(GET_CALMAX, (byte)page);

        /// <summary>Read one 64-value page of a per-key parameter.</summary>
        public static byte[] GetArray(int param, int page) => Packet(GET_MAG_ARR, (byte)param, 1, (byte)page);

        /// <summary>Write one 56-value page; "last" marks the final packet of a batch.</summary>
        public static byte[] SetArray(int param, int page, bool last, byte[] all)
        {
            var p = Packet(SET_MAG_ARR, (byte)param, 1, (byte)page, last ? (byte)1 : (byte)0);
            for (int i = 0; i < WritePageSize; i++)
            {
                int k = page * WritePageSize + i;
                p[8 + i] = k < all.Length ? all[k] : (byte)0;
            }
            return p;
        }

        /// <summary>
        /// Checks one read page. The keyboard answers array reads with the bare
        /// values (no command echo); an echo means the format is not what we expect.
        /// </summary>
        public static string CheckPage(int param, byte[] r)
        {
            if (r == null || r.Length < ReadPageSize) return "короткий ответ";
            if (r[0] == GET_MAG_ARR && r[1] == param) return "ответ с эхом команды";
            return null;
        }

        /// <summary>Checks the decoded values of the real keys; null if they look sane.</summary>
        public static string Validate(TravelMap m)
        {
            foreach (var k in Layout)
            {
                int i = k.Matrix;
                if (m.Act[i] > 39 || m.Rel[i] > 39 || m.Dead[i] > 39 || m.RtPress[i] > 40 || m.RtRelease[i] > 40)
                    return $"клавиша {k.Label}: {m.Act[i]}/{m.Rel[i]}/{m.Dead[i]}/{m.RtPress[i]}/{m.RtRelease[i]}";
                int mode = m.Mode[i] & 0x7F;
                if (mode == 1 || mode > 7) return $"клавиша {k.Label}: режим {m.Mode[i]:X2}";
            }
            return null;
        }

        /// <summary>The packets that turn "before" into "after" (only changed pages are written).</summary>
        public static List<byte[]> Diff(TravelMap before, TravelMap after)
        {
            var list = new List<byte[]>();
            foreach (int p in Params)
            {
                var a = before.Array(p); var b = after.Array(p);
                for (int page = 0; page < ReadPages; page++)
                {
                    bool changed = false;
                    for (int i = page * WritePageSize; i < Math.Min(Size, (page + 1) * WritePageSize); i++)
                        if (a[i] != b[i]) { changed = true; break; }
                    if (changed) list.Add(SetArray(p, page, false, b));
                }
            }
            if (list.Count > 0)
            {
                var last = list[list.Count - 1];
                last[4] = 1;                   // the keyboard commits the batch on this flag
                K85Protocol.Checksum(last, 7); // byte 4 is part of the checksum
            }
            return list;
        }

        const int Size = TravelMap.Size;

        /// <summary>Applies the edited settings to the given keys.</summary>
        public static void Apply(TravelMap m, IEnumerable<int> keys, KeyTravel t)
        {
            foreach (int i in keys)
            {
                if (m.Plain(i))
                {
                    m.Act[i] = TravelRaw(t.Actuation);
                    m.Rel[i] = TravelRaw(t.Release);
                    m.Dead[i] = DeadRaw(t.DeadZone);
                }
                m.Mode[i] = (byte)((m.Mode[i] & 0x7F) | (t.Rapid ? 0x80 : 0));
                m.RtPress[i] = RtRaw(t.RtPress);
                m.RtRelease[i] = RtRaw(t.RtRelease);
            }
        }

        /// <summary>Settings of one key, for showing in the editor.</summary>
        public static KeyTravel Read(TravelMap m, int i) => new KeyTravel
        {
            Actuation = Math.Max(TravelMin, TravelMm(m.Act[i])),
            Release = Math.Max(TravelMin, TravelMm(m.Rel[i])),
            DeadZone = DeadMm(m.Dead[i]),
            RtPress = Math.Max(RtMin, Math.Min(RtMax, RtMm(m.RtPress[i]))),
            RtRelease = Math.Max(RtMin, Math.Min(RtMax, RtMm(m.RtRelease[i]))),
            Rapid = m.Rapid(i),
        };

        /// <summary>Calibration progress per matrix index (0..1), as the stock driver computes it.</summary>
        public static double[] CalProgress(short[] max, short[] cal)
        {
            var r = new double[Math.Min(max.Length, cal.Length)];
            for (int i = 0; i < r.Length; i++) r[i] = Math.Max(0, Math.Min(1, Math.Abs(max[i] - cal[i]) / 400.0));
            return r;
        }

        public static short[] ParseInt16Page(byte[] r)
        {
            var v = new short[32];
            for (int i = 0; i < 32 && 2 * i + 1 < r.Length; i++) v[i] = (short)(r[2 * i] | (r[2 * i + 1] << 8));
            return v;
        }

        /// <summary>A plausible map for demo mode and screenshots.</summary>
        public static TravelMap Demo()
        {
            var m = new TravelMap { KeyboardMode = 3 };
            foreach (var k in Layout)
            {
                m.Act[k.Matrix] = 18; m.Rel[k.Matrix] = 18; m.Dead[k.Matrix] = 36;
                m.RtPress[k.Matrix] = 2; m.RtRelease[k.Matrix] = 3;
            }
            foreach (var l in new[] { "W", "A", "S", "D" })
            {
                int i = Layout.First(k => k.Label == l).Matrix;
                m.Act[i] = 4; m.Rel[i] = 4; m.Mode[i] = 0x80; m.RtPress[i] = 1; m.RtRelease[i] = 1;
            }
            return m;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        public static void SelfTest(List<string> fail)
        {
            void Eq(string what, string got, string want) { if (got != want) fail.Add($"{what}: {got} != {want}"); }
            string H(byte[] p, int n) => K85Protocol.Hex(p, n);
            byte Sum(params int[] b) => (byte)(~b.Sum() & 0xFF);

            Eq("report 1000 Hz", H(SetReportRate(0, 1000), 8), "04 00 01 00 00 00 00 " + Sum(4, 0, 1).ToString("X2"));
            Eq("report 125 Hz layer 2", H(SetReportRate(4, 125), 8), "04 04 08 00 00 00 00 " + Sum(4, 4, 8).ToString("X2"));
            Eq("report parse", ParseReportRate(new byte[] { 0x84, 0, 2 }).ToString(), "500");
            Eq("debounce 5", H(SetDebounce(5), 8), "11 00 05 00 00 00 00 " + Sum(0x11, 5).ToString("X2"));
            Eq("debounce parse", ParseDebounce(new byte[] { 0x91, 0, 7 }).ToString(), "7");
            Eq("get array", H(GetArray(P_ACT, 1), 8), "E5 00 01 01 00 00 00 " + Sum(0xE5, 1, 1).ToString("X2"));
            Eq("cal on", H(Calibrate(true), 8), "1C 01 00 00 00 00 00 " + Sum(0x1C, 1).ToString("X2"));
            Eq("calmax off", H(CalibrateMax(false), 8), "1E 00 00 00 00 00 00 " + Sum(0x1E).ToString("X2"));
            Eq("mode custom", H(SetKeyboardMode(3, 0, 1), 8), "1D 03 00 01 00 00 00 " + Sum(0x1D, 3, 1).ToString("X2"));

            // conversions match the stock driver: travel = 10*mm - 1, RT = 10*mm, dead zone 3.7 mm = 36
            Eq("travel 1.9", TravelRaw(1.9).ToString(), "18");
            Eq("travel 0.1", TravelRaw(0.1).ToString(), "0");
            Eq("travel 4.0", TravelRaw(4.0).ToString(), "39");
            Eq("travel back", TravelMm(18).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), "1.9");
            Eq("rt 0.2", RtRaw(0.2).ToString(), "2");
            Eq("dead 3.7", DeadRaw(3.7).ToString(), "36");
            Eq("dead off", DeadRaw(0).ToString(), "0");

            // layout: 82 distinct keys, all inside the two pages and the 16x6 grid
            Eq("layout keys", Layout.Length.ToString(), "82");
            Eq("layout distinct", Layout.Select(k => k.Matrix).Distinct().Count().ToString(), "82");
            if (Layout.Any(k => k.Matrix >= WritePageSize * ReadPages || k.X + k.W > LayoutWidth + 1e-9 || k.Row >= LayoutRows))
                fail.Add("layout out of range");
            Eq("Esc matrix", Layout.First(k => k.Label == "Esc").Matrix.ToString(), "0");
            Eq("Space matrix", Layout.First(k => k.Label == "").Matrix.ToString(), "41");

            // write packets: header, page split, last flag
            var before = Demo(); var after = before.Clone();
            int w = Layout.First(k => k.Label == "W").Matrix, right = Layout.First(k => k.Label == "→").Matrix;
            Apply(after, new[] { w, right }, new KeyTravel { Actuation = 1.0, Release = 1.0, DeadZone = 3.7, Rapid = true, RtPress = 0.2, RtRelease = 0.3 });
            var pk = Diff(before, after);
            Eq("diff packets", string.Join(",", pk.Select(p => $"{p[1]}:{p[3]}:{p[4]}")), "7:1:0,0:0:0,0:1:0,1:0:0,1:1:0,2:0:0,3:0:1");
            var first = pk[1];
            Eq("set array header", H(first, 8), "65 00 01 00 00 00 00 " + Sum(0x65, 0, 1, 0).ToString("X2"));
            Eq("set array value", first[8 + w].ToString(), "9");
            var last = pk[pk.Count - 1];
            Eq("last header", H(last, 8), "65 03 01 00 01 00 00 " + Sum(0x65, 3, 1, 0, 1).ToString("X2"));
            Eq("last value", last[8 + w].ToString(), "3");
            Eq("page 2 value", pk[2][8 + right - WritePageSize].ToString(), "9");
            Eq("no-op diff", Diff(before, before.Clone()).Count.ToString(), "0");
            if (Validate(before) != null) fail.Add("demo map invalid: " + Validate(before));
            var bad = before.Clone(); bad.Act[0] = 0xE5;
            if (Validate(bad) == null) fail.Add("validate accepts garbage");
            var echo = new byte[64]; echo[0] = GET_MAG_ARR; echo[1] = P_DEAD;
            if (CheckPage(P_DEAD, echo) == null) fail.Add("echo page accepted");
            // DKS key keeps its travel, takes RT
            var dks = before.Clone(); dks.Mode[w] = 2;
            Apply(dks, new[] { w }, new KeyTravel { Actuation = 3.0, Rapid = true, RtPress = 0.5, RtRelease = 0.5 });
            Eq("dks", $"{dks.Act[w]}:{dks.Mode[w]:X2}:{dks.RtPress[w]}", "4:82:5");
            var cal = CalProgress(new short[] { 1000, 1000, 0 }, new short[] { 1000, 600, 900 });
            Eq("cal progress", string.Join(",", cal.Select(c => c.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture))), "0,1,1");
        }
    }
}
