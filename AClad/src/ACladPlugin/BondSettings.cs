using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ACladPlugin
{
    /// <summary>
    /// Параметры универсальной разбежки (ATTILE, 23.09: заявка «шахматный
    /// порядок для любой облицовки»). ЧИСТАЯ логика без AutoCAD API —
    /// собирается и проверяется под mono (tools/attile_ui): пресеты
    /// материалов, разбор долей/мм («1/3», «0,25», «200»), запрос движку
    /// (tile_pattern: bond/axis/anchor/gap_around/shaped/warn_cut),
    /// имена слоёв/блоков, запоминание последних значений, текст
    /// описания и сдвиг ряда для окна просмотра — ТА ЖЕ формула, что
    /// tile_pattern.resolve_bond (сверяется тестом).
    /// </summary>
    public class BondSettings
    {
        public string Material = "Керамогранит на НВФ";
        public string Name = "Керамогранит 600×1200";
        /// <summary>"" — прямоугольник (блоки создаст программа); иначе
        /// имя динамического блока чертежа с параметрами ширина/высота.</summary>
        public string Element = "";
        public double W = 600, H = 1200, Gv = 8, Gh = 8;
        public string Axis = "rows";          // rows | cols
        public string Kind = "alternate";     // none|alternate|step|sequence|pattern
        public string Value = "1/2";          // как ввёл пользователь
        public string Units = "frac";         // frac (доля модуля) | mm
        public string Dir = "+";              // + вправо/вверх, - влево/вниз
        public string Sequence = "0; 1/3; 2/3";
        public bool ColorsBySample = false;
        public string AnchorH = "R", AnchorV = "B";   // L|C|R, B|C|T
        public string Center = "tile";        // tile | joint
        public string Ref = "bbox";           // bbox | wall
        public bool CommonPoint = false;
        public bool Merge = true;
        public bool GapAround = true;
        public string Shaped = "split_joint"; // keep | split | split_joint
        public double MinPiece = 10, WarnCut = 150, Kerf = 3;

        // ── пресеты материалов ──
        public class Preset
        {
            public string Title, DefaultName;
            public string[] Formats;
            public double Gv, Gh, MinPiece, WarnCut, Kerf;
            public bool GapAround;
            public string Shaped;
        }

        public static readonly Preset[] Presets =
        {
            new Preset { Title = "Керамогранит на НВФ", DefaultName = "Керамогранит",
                Formats = new[] { "600×1200", "1200×600", "600×600", "300×600", "600×300",
                                  "800×800", "400×800", "800×1600", "1600×800",
                                  "750×1500", "1200×1200", "1200×2400", "600×1800" },
                Gv = 8, Gh = 8, GapAround = true, Shaped = "split_joint",
                MinPiece = 10, WarnCut = 150, Kerf = 3 },
            new Preset { Title = "Фиброцемент / HPL на НВФ", DefaultName = "Фиброцемент",
                Formats = new[] { "1200×3000", "1250×3000", "1220×2440", "1500×3050",
                                  "600×3000", "3000×1200", "3000×600", "190×3600" },
                Gv = 8, Gh = 8, GapAround = true, Shaped = "split_joint",
                MinPiece = 10, WarnCut = 150, Kerf = 3 },
            new Preset { Title = "Натуральный камень", DefaultName = "Камень",
                Formats = new[] { "600×300", "300×600", "600×600", "800×400", "900×600",
                                  "1200×600" },
                Gv = 5, Gh = 5, GapAround = true, Shaped = "split_joint",
                MinPiece = 10, WarnCut = 100, Kerf = 4 },
            new Preset { Title = "Клинкер / кирпич / плитка", DefaultName = "Плитка",
                Formats = new[] { "290×82", "240×71", "240×52", "250×65", "250×88",
                                  "215×65", "290×85", "240×115" },
                Gv = 10, Gh = 10, GapAround = false, Shaped = "keep",
                MinPiece = 10, WarnCut = 30, Kerf = 3 },
            new Preset { Title = "Другое (свои значения)", DefaultName = "Облицовка",
                Formats = new[] { "600×600", "600×1200", "1200×600" },
                Gv = 8, Gh = 8, GapAround = true, Shaped = "split_joint",
                MinPiece = 10, WarnCut = 100, Kerf = 3 },
        };

        public static Preset FindPreset(string title)
        {
            foreach (var p in Presets)
                if (p.Title == title) return p;
            return Presets[0];
        }

        /// <summary>Значения пресета (формат — первый из списка).</summary>
        public void ApplyPreset(Preset p)
        {
            Material = p.Title;
            double w, h;
            if (TryParseFormat(p.Formats[0], out w, out h)) { W = w; H = h; }
            Gv = p.Gv; Gh = p.Gh; GapAround = p.GapAround; Shaped = p.Shaped;
            MinPiece = p.MinPiece; WarnCut = p.WarnCut; Kerf = p.Kerf;
            Name = p.DefaultName + " " + F(W) + "×" + F(H);
        }

        // ── разбор чисел ──
        public static bool TryParseNum(string s, out double v)
        {
            v = 0;
            if (s == null) return false;
            string t = s.Trim().Replace(" ", "").Replace(",", ".");
            if (t.EndsWith("мм", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(0, t.Length - 2);
            if (t.Length == 0) return false;
            int k = t.IndexOf('/');
            if (k > 0)
            {
                double a, b;
                if (!double.TryParse(t.Substring(0, k), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out a)) return false;
                if (!double.TryParse(t.Substring(k + 1), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out b)) return false;
                if (Math.Abs(b) < 1e-12) return false;
                v = a / b;
                return true;
            }
            return double.TryParse(t, NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out v);
        }

        /// <summary>«600×1200», «600x1200», «600*1200», «600 х 1200».</summary>
        public static bool TryParseFormat(string s, out double w, out double h)
        {
            w = h = 0;
            if (s == null) return false;
            string t = s.Replace(" ", "").Replace('×', 'x').Replace('х', 'x')
                        .Replace('Х', 'x').Replace('X', 'x').Replace('*', 'x');
            var parts = t.Split('x');
            if (parts.Length != 2) return false;
            return TryParseNum(parts[0], out w) && TryParseNum(parts[1], out h)
                   && w > 0 && h > 0;
        }

        public double Module { get { return Axis == "cols" ? H + Gh : W + Gv; } }

        /// <summary>Смещение (alternate/step) в долях модуля.</summary>
        public double Fraction()
        {
            double v;
            if (!TryParseNum(Value, out v)) return 0;
            return Units == "mm" ? v / Module : v;
        }

        public List<double> SequenceFractions()
        {
            var outp = new List<double>();
            if (Sequence == null) return outp;
            foreach (var part in Sequence.Split(';'))
            {
                if (part.Trim().Length == 0) continue;
                double v;
                if (!TryParseNum(part, out v)) return new List<double>();
                outp.Add(Units == "mm" ? v / Module : v);
            }
            return outp;
        }

        private static double Frac(double x)
        {
            double f = x - Math.Floor(x);
            return (f < 1e-9 || f > 1.0 - 1e-9) ? 0.0 : f;
        }

        private static int Mod(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }

        /// <summary>Сдвиг ряда (столбца) j в долях модуля — формула движка
        /// tile_pattern.resolve_bond (для «по образцу» просмотр — ½).</summary>
        public double ShiftAt(int j)
        {
            double sign = Dir == "-" ? -1.0 : 1.0;
            switch (Kind)
            {
                case "none":
                    return 0.0;
                case "alternate":
                case "step":
                {
                    double f = Fraction();
                    if (f < 0) { f = -f; sign = -sign; }
                    f = Frac(f);
                    if (Kind == "alternate")
                        return Mod(j, 2) == 0 ? 0.0 : Frac(sign * f);
                    return Frac(sign * j * f);
                }
                case "sequence":
                {
                    var seq = SequenceFractions();
                    if (seq.Count == 0) return 0.0;
                    double b = seq[0];
                    return Frac(sign * Frac(seq[Mod(j, seq.Count)] - b));
                }
                default:
                    return Mod(j, 2) == 0 ? 0.0 : 0.5;
            }
        }

        /// <summary>Период рисунка в рядах (0 — не повторяется до 1000).</summary>
        public int Period()
        {
            switch (Kind)
            {
                case "none": return 1;
                case "alternate": return Frac(Math.Abs(Fraction())) > 1e-9 ? 2 : 1;
                case "sequence": return Math.Max(1, SequenceFractions().Count);
                case "pattern": return 0;
            }
            double f = Frac(Math.Abs(Fraction()));
            for (int p = 1; p <= 1000; p++)
                if (Frac(p * f + 1e-7) < 2e-7) return p;
            return 0;
        }

        /// <summary>Ошибка ввода человеческим текстом или null.</summary>
        public string Validate()
        {
            if (!(W > 0) || !(H > 0)) return "Формат: ширина и высота должны быть больше нуля.";
            if (W > 20000 || H > 20000) return "Формат больше 20 м — проверьте единицы (мм).";
            if (Gv < 0 || Gh < 0) return "Швы не могут быть отрицательными.";
            if (MinPiece < 0 || WarnCut < 0 || Kerf < 0) return "Пороги и пропил — не меньше нуля.";
            if (Kind == "alternate" || Kind == "step")
            {
                double v;
                if (!TryParseNum(Value, out v))
                    return "Величина смещения: число, дробь («1/3») или мм («200»).";
                if (Units == "frac" && Math.Abs(v) >= 1.0)
                    return "Доля смещения должна быть меньше 1 (для мм выберите «мм»).";
            }
            if (Kind == "sequence" && SequenceFractions().Count == 0)
                return "Последовательность: значения через «;», например «0; 1/3; 2/3» или «0; 200; 400».";
            if (Element.Length > 0 && Shaped == "keep")
                return "Блок из чертежа — только прямоугольники: Г-куски нужно резать.";
            if (Name == null || Name.Trim().Length == 0) return "Укажите наименование облицовки.";
            return null;
        }

        // ── запрос движку ──
        public Dictionary<string, object> ToEngine()
        {
            var bond = new Dictionary<string, object> { { "kind", Kind }, { "dir", Dir },
                                                        { "units", Units } };
            if (Kind == "alternate" || Kind == "step")
            {
                double v;
                TryParseNum(Value, out v);
                bond["value"] = v;
            }
            else if (Kind == "sequence")
            {
                var seq = new List<object>();
                foreach (var part in Sequence.Split(';'))
                {
                    double v;
                    if (part.Trim().Length > 0 && TryParseNum(part, out v)) seq.Add(v);
                }
                bond["sequence"] = seq;
            }
            return new Dictionary<string, object>
            {
                { "tile", new Dictionary<string, object> { { "w", W }, { "h", H } } },
                { "gap", new Dictionary<string, object> { { "v", Gv }, { "h", Gh } } },
                { "axis", Axis },
                { "bond", bond },
                { "anchor", new Dictionary<string, object>
                    { { "h", AnchorH }, { "v", AnchorV }, { "center", Center },
                      { "ref", Ref } } },
                { "gap_around", GapAround },
                { "shaped", Element.Length > 0 && Shaped == "keep" ? "split_joint" : Shaped },
                { "min_piece", MinPiece },
                { "warn_cut", WarnCut },
                { "kerf", Kerf },
                { "merge_touching", Merge },
            };
        }

        // ── имена слоёв и блоков ──
        public static string Sanitize(string s)
        {
            var bad = new char[] { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|',
                                   ',', '=', '`' };
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string r = sb.ToString().Trim();
            if (r.Length == 0) r = "ОБЛИЦОВКА";
            if (r.Length > 200) r = r.Substring(0, 200);
            return r;
        }

        /// <summary>Слой облицовки: «Облицовка &lt;наименование&gt;» (как
        /// ATCLAD, ТЗ 2.1), при раскраске по образцу — «… &lt;тип&gt;».</summary>
        public string LayerFor(string type, bool multi)
        {
            string b = (Name ?? "").Trim();
            if (!b.StartsWith("облицовка", StringComparison.OrdinalIgnoreCase))
                b = "Облицовка " + b;
            if (multi && !string.IsNullOrEmpty(type)) b += " " + type;
            return Sanitize(b);
        }

        public string CutLayerFor(string type, bool multi)
        { return Sanitize(LayerFor(type, multi) + "_ПОДРЕЗКА"); }

        public string SmallLayer()
        { return Sanitize(LayerFor("", false) + "_МАЛАЯ ПОДРЕЗКА"); }

        /// <summary>Блок целой плитки: наименование (+ тип) + формат —
        /// разные форматы в одном чертеже не путаются (грабля 09.09:
        /// «Плитка_Т1» без формата брал размер первого запуска).</summary>
        public string BlockFor(string type, bool multi)
        {
            string fmt = F(W) + "x" + F(H);
            string b = (Name ?? "").Trim();
            if (multi && !string.IsNullOrEmpty(type)) b += " " + type;
            string norm = b.Replace('×', 'x').Replace('х', 'x').Replace(" ", "");
            if (norm.IndexOf(fmt, StringComparison.OrdinalIgnoreCase) < 0) b += " " + fmt;
            return Sanitize(b);
        }

        // ── описание (окно и сводка) ──
        public static string FracText(double f)
        {
            if (Math.Abs(f) < 1e-9) return "0";
            for (int q = 1; q <= 12; q++)
            {
                int p = (int)Math.Round(f * q);
                if (p != 0 && Math.Abs((double)p / q - f) < 1e-6)
                {
                    int g = Gcd(Math.Abs(p), q);
                    return q / g > 1 ? (p / g) + "/" + (q / g) : (p / g).ToString(CultureInfo.InvariantCulture);
                }
            }
            return f.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

        public string Describe()
        {
            bool cols = Axis == "cols";
            string way = Dir == "-" ? (cols ? "вниз" : "влево") : (cols ? "вверх" : "вправо");
            string unit = cols ? "столбец" : "ряд";
            double m = Module;
            string mod = "модуль " + F(W + Gv) + " × " + F(H + Gh) + " мм (плитка + шов)";
            int per = Period();
            string perT = per > 0 ? "период " + per + " " + (cols ? "столбц." : "ряд.") :
                          (Kind == "pattern" ? "" : "рисунок не повторяется");
            switch (Kind)
            {
                case "none":
                    return "Без смещения: швы в линию (шов в шов). " + mod + ".";
                case "alternate":
                {
                    double f = Frac(Math.Abs(Fraction()));
                    if (f < 1e-9) return "Смещение 0 — швы в линию. " + mod + ".";
                    return "Через " + (cols ? "столбец" : "ряд") + ": каждый второй " + unit +
                        " сдвинут " + way + " на " + FracText(f) + " модуля (" + F(f * m) +
                        " мм)" + (Math.Abs(f - 0.5) < 1e-9 ? " — стык по центру плитки соседнего " +
                        (cols ? "столбца" : "ряда") : "") + ". " + perT + ". " + mod + ".";
                }
                case "step":
                {
                    double f = Frac(Math.Abs(Fraction()));
                    return "Лесенкой: каждый " + (cols ? "столбец правее" : "ряд выше") +
                        " сдвинут " + way + " на " + FracText(f) + " модуля (" + F(f * m) +
                        " мм) относительно предыдущего. " + perT + ". " + mod + ".";
                }
                case "sequence":
                {
                    var seq = SequenceFractions();
                    var sb = new StringBuilder();
                    double b = seq.Count > 0 ? seq[0] : 0;
                    for (int k = 0; k < seq.Count; k++)
                    {
                        if (k > 0) sb.Append("; ");
                        sb.Append(F(Frac(seq[k] - b) * m));
                    }
                    return "Своя последовательность (" + way + "): сдвиги " + unit +
                        "ов " + sb + " мм, затем повтор. " + perT + ". " + mod + ".";
                }
                default:
                    return "По образцу: сдвиги рядов берутся с нарисованного образца " +
                        "(в окне показано ½). " + mod + ".";
            }
        }

        // ── хранение ──
        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "material", Material }, { "name", Name }, { "element", Element },
                { "w", W }, { "h", H }, { "gv", Gv }, { "gh", Gh }, { "axis", Axis },
                { "kind", Kind }, { "value", Value }, { "units", Units }, { "dir", Dir },
                { "sequence", Sequence }, { "colors_by_sample", ColorsBySample },
                { "anchor_h", AnchorH }, { "anchor_v", AnchorV }, { "center", Center },
                { "ref", Ref }, { "common_point", CommonPoint }, { "merge", Merge },
                { "gap_around", GapAround }, { "shaped", Shaped },
                { "min_piece", MinPiece }, { "warn_cut", WarnCut }, { "kerf", Kerf },
            };
        }

        private static string S(Dictionary<string, object> d, string k, string def)
        {
            object o;
            return d != null && d.TryGetValue(k, out o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : def;
        }

        private static double D(Dictionary<string, object> d, string k, double def)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        private static bool B(Dictionary<string, object> d, string k, bool def)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToBoolean(o, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        private static string OneOf(string v, string def, params string[] allowed)
        {
            foreach (var a in allowed) if (a == v) return v;
            return def;
        }

        public static BondSettings FromDict(Dictionary<string, object> d)
        {
            var s = new BondSettings();
            if (d == null) return s;
            s.Material = S(d, "material", s.Material);
            s.Name = S(d, "name", s.Name);
            s.Element = S(d, "element", "");
            s.W = D(d, "w", s.W); s.H = D(d, "h", s.H);
            s.Gv = D(d, "gv", s.Gv); s.Gh = D(d, "gh", s.Gh);
            s.Axis = OneOf(S(d, "axis", s.Axis), "rows", "rows", "cols");
            s.Kind = OneOf(S(d, "kind", s.Kind), "alternate", "none", "alternate", "step",
                           "sequence", "pattern");
            s.Value = S(d, "value", s.Value);
            s.Units = OneOf(S(d, "units", s.Units), "frac", "frac", "mm");
            s.Dir = OneOf(S(d, "dir", s.Dir), "+", "+", "-");
            s.Sequence = S(d, "sequence", s.Sequence);
            s.ColorsBySample = B(d, "colors_by_sample", false);
            s.AnchorH = OneOf(S(d, "anchor_h", s.AnchorH), "R", "L", "C", "R");
            s.AnchorV = OneOf(S(d, "anchor_v", s.AnchorV), "B", "B", "C", "T");
            s.Center = OneOf(S(d, "center", s.Center), "tile", "tile", "joint");
            s.Ref = OneOf(S(d, "ref", s.Ref), "bbox", "bbox", "wall");
            s.CommonPoint = B(d, "common_point", false);
            s.Merge = B(d, "merge", true);
            s.GapAround = B(d, "gap_around", true);
            s.Shaped = OneOf(S(d, "shaped", s.Shaped), "split_joint", "keep", "split", "split_joint");
            s.MinPiece = D(d, "min_piece", s.MinPiece);
            s.WarnCut = D(d, "warn_cut", s.WarnCut);
            s.Kerf = D(d, "kerf", s.Kerf);
            return s;
        }

        public BondSettings Clone() { return FromDict(ToDict()); }

        public static string SettingsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "AClad");
            return Path.Combine(dir, "attile_settings.json");
        }

        public static BondSettings LoadLast()
        {
            try
            {
                string p = SettingsPath();
                if (!File.Exists(p)) return new BondSettings();
                var ser = new JavaScriptSerializer();
                var d = ser.DeserializeObject(File.ReadAllText(p, Encoding.UTF8))
                        as Dictionary<string, object>;
                return FromDict(d);
            }
            catch { return new BondSettings(); }
        }

        public void SaveLast()
        {
            try
            {
                string p = SettingsPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, new JavaScriptSerializer().Serialize(ToDict()),
                                  Encoding.UTF8);
            }
            catch { }
        }

        public static string F(double v)
        {
            return Math.Abs(v - Math.Round(v)) < 1e-9
                ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture)
                : v.ToString("0.#", CultureInfo.InvariantCulture);
        }
    }
}
