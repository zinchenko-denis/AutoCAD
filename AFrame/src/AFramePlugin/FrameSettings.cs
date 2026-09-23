using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AFramePlugin
{
    /// <summary>
    /// Параметры ATFRAME одним окном (Герман 23.09: «такое же диалоговое
    /// окно, как у ATTILE, с выбором раскладки кляммеров после подсистемы
    /// или только кляммеров по существующей облицовке»). Чистая логика без
    /// AutoCAD — собирается и проверяется под mono (tools/frame_ui).
    /// Значения по умолчанию = прежние ответы по Enter в командной строке,
    /// поэтому «Разложить» без правок даёт прежний запрос движку.
    /// </summary>
    public class FrameSettings
    {
        public string Mode = "all";            // all | frame | clamps
        public string SubType = "vertical";    // vertical | interfloor | ortho
        public string Profile = "Авто";        // верт.: Авто|ГП-40-40|ГП-60-40|ШП-60-20; межэт.: НСП-1|НСП-2
        public string Steps = "calc";          // calc | manual
        public string WindRegion = "II";
        public string Terrain = "B";
        public double Height = 30, QClad = 25, Offset = 230, NaMax = 3000;
        public double StepMain = 800, StepCorner = 800, RailStepCorner = 0;
        public double StartOff = 300, RailGap = 10, CornerZone = 1500;
        public string Axes = "step";           // step | points — только если у зон нет раскладки
        public double AxisStep = 608, RowStep = 605;
        public bool AskCorners = true, AskFloors = true;
        public double FloorStep = 3000;        // межэтажная без отметок
        public string Signs = "cond";          // cond | samples

        public const double DefStartOff = 300, DefRailGap = 10, DefCornerZone = 1500;
        public static readonly string[] Modes = { "all", "frame", "clamps" };
        public static readonly string[] SubTypes = { "vertical", "interfloor", "ortho" };
        public static readonly string[] VertProfiles = { "Авто", "ГП-40-40", "ГП-60-40", "ШП-60-20" };
        public static readonly string[] NspProfiles = { "НСП-1", "НСП-2" };
        public static readonly string[] Winds = { "Ia", "I", "II", "III", "IV", "V" };
        public static readonly string[] Terrains = { "A", "B", "C" };

        // ── то, что раньше давали ответы в командной строке ──
        public bool InterFloor { get { return SubType == "interfloor"; } }
        public bool Ortho { get { return SubType == "ortho"; } }
        public bool Manual { get { return Steps == "manual"; } }
        public bool ClampsOnly { get { return Mode == "clamps"; } }

        /// <summary>Как в прежнем сообщении: «ATFRAME (Вертикальная): …».</summary>
        public string TypeTitle
        {
            get { return InterFloor ? "Межэтажная" : Ortho ? "Ортогональная" : "Вертикальная"; }
        }

        public string SysName
        {
            get { return InterFloor ? "Межэтажная" : Ortho ? "Ортогональная" : "Standart"; }
        }

        /// <summary>Марка вертикальной направляющей (null — «Авто», подбор расчётом).</summary>
        public string RailProfileOrNull
        {
            get
            {
                if (SubType != "vertical") return null;
                foreach (var p in VertProfiles)
                    if (p == Profile && p != "Авто") return p;
                return null;
            }
        }

        /// <summary>НСП межэтажной (null для других типов).</summary>
        public string NspTypeOrNull
        {
            get { return InterFloor ? (Profile == "НСП-2" ? "НСП-2" : "НСП-1") : null; }
        }

        /// <summary>Прочие параметры ручного режима — только если их меняли
        /// (иначе движок берёт значения системы, как по «Принять»).</summary>
        public bool MiscChanged
        {
            get
            {
                return Math.Abs(StartOff - DefStartOff) > 1e-9 ||
                       Math.Abs(RailGap - DefRailGap) > 1e-9 ||
                       Math.Abs(CornerZone - DefCornerZone) > 1e-9;
            }
        }

        public Dictionary<string, object> SysOverride()
        {
            var d = new Dictionary<string, object> { { "name", SysName } };
            if (!Manual)
            {
                if (SubType == "vertical") d["name"] = "Вектор-1";   // расчётный пресет, как раньше
                return d;
            }
            d["bracket_step"] = StepMain;
            d["bracket_step_corner"] = StepCorner;
            if (MiscChanged)
            {
                d["bracket_start_offset"] = StartOff;
                d["rail_gap"] = RailGap;
                d["corner_zone"] = CornerZone;
            }
            return d;
        }

        public Dictionary<string, object> CalcDict()
        {
            if (Manual) return null;
            return new Dictionary<string, object>
            {
                { "wind_region", WindRegion }, { "terrain", Terrain }, { "height", Height },
                { "q_clad", QClad }, { "offset", Offset }, { "na_max", NaMax },
            };
        }

        /// <summary>Смена типа: вес облицовки по умолчанию был разный
        /// (межэтажная 8, остальные 25) — меняем, только если не трогали.</summary>
        public void SetSubType(string sub)
        {
            string old = SubType;
            SubType = OneOf(sub, "vertical", SubTypes);
            if (old == SubType) return;
            if (InterFloor && Math.Abs(QClad - 25) < 1e-9) QClad = 8;
            else if (old == "interfloor" && Math.Abs(QClad - 8) < 1e-9) QClad = 25;
            string[] allowed = ProfilesFor(SubType);
            if (Array.IndexOf(allowed, Profile) < 0) Profile = allowed.Length > 0 ? allowed[0] : "";
        }

        public static string[] ProfilesFor(string sub)
        {
            if (sub == "vertical") return VertProfiles;
            if (sub == "interfloor") return NspProfiles;
            return new string[0];                         // ортогональная — ШП/ZП автоматом
        }

        public string Validate(bool hasLayout)
        {
            if (Array.IndexOf(Modes, Mode) < 0) return "Неизвестный режим «" + Mode + "».";
            if (!ClampsOnly && !Manual)
            {
                if (Height < 1 || Height > 500) return "Высота здания — от 1 до 500 м.";
                if (QClad <= 0 || QClad > 500) return "Вес облицовки — больше 0 и не больше 500 кг/м².";
                if (Offset < 20 || Offset > 1000) return "Вынос облицовки — от 20 до 1000 мм.";
                if (NaMax < 100) return "Усилие вырыва анкера — не меньше 100 Н.";
            }
            if (!ClampsOnly && Manual)
            {
                if (StepMain < 100 || StepCorner < 100) return "Шаг кронштейнов — не меньше 100 мм.";
                if (StartOff < 0 || RailGap < 0 || CornerZone < 0) return "Старт, зазор и угловая зона — не отрицательные.";
                if (RailStepCorner != 0 && RailStepCorner < 50) return "Шаг стоек в угловой зоне — 0 (по рустам) или от 50 мм.";
            }
            if (!hasLayout)
            {
                if (Axes == "step" && AxisStep < 50) return "Шаг осей стоек — не меньше 50 мм.";
                if (RowStep != 0 && RowStep < 50) return "Шаг горизонтальных швов — 0 (без кляммеров) или от 50 мм.";
            }
            if (InterFloor && !AskFloors && FloorStep < 1 && !ClampsOnly)
                return "Межэтажной нужны отметки перекрытий: укажите их после ОК или задайте высоту этажа.";
            return null;
        }

        /// <summary>Что произойдёт по «Разложить» — текст для окна.</summary>
        public string Describe(bool hasLayout)
        {
            var sb = new StringBuilder();
            if (ClampsOnly)
                sb.Append("Только кляммеры: на существующие направляющие зоны (из прошлой раскладки ATFRAME, " +
                          "иначе — выбрать на чертеже) по текущей облицовке; прежние кляммеры зоны заменяются, " +
                          "направляющие и кронштейны не трогаются.");
            else
            {
                sb.Append(TypeTitle).Append(" подсистема");
                if (Mode == "frame") sb.Append(" без кляммеров");
                else sb.Append(" и кляммеры");
                sb.Append(Manual ? "; шаги вручную: рядовая " + F(StepMain) + ", угловая " + F(StepCorner) + " мм"
                                 : "; шаги по расчёту: район " + WindRegion + ", местность " + Terrain + ", " +
                                   F(Height) + " м, облицовка " + F(QClad) + " кг/м², вынос " + F(Offset) +
                                   " мм, анкер " + F(NaMax) + " Н");
                if (SubType == "vertical") sb.Append("; профиль ").Append(Profile == "Авто" ? "подбором" : Profile);
                if (InterFloor) sb.Append("; ").Append(Profile);
                sb.Append(".");
            }
            sb.Append(hasLayout ? " Оси стоек и швы — из раскладки зон."
                                : " Раскладки у зон нет: оси — " + (Axes == "step" ? "шагом " + F(AxisStep) + " мм от первой оси"
                                                                                  : "точками") +
                                  (RowStep > 0 ? ", швы шагом " + F(RowStep) + " мм." : ", без горизонтальных швов (без кляммеров)."));
            var after = new List<string>();
            if (!hasLayout) after.Add(Axes == "step" ? "первую ось" : "оси стоек");
            if (AskCorners && !ClampsOnly) after.Add("внешние углы здания");
            if (Signs == "samples") after.Add(ClampsOnly ? "образцы кляммеров" : "образцы знаков");
            if (AskFloors) after.Add("отметки перекрытий");
            if (after.Count > 0) sb.Append(" После «Разложить» указать: ").Append(string.Join(", ", after.ToArray())).Append(".");
            if (InterFloor && !ClampsOnly)
                sb.Append(" Без отметок — перекрытия шагом этажа " + F(FloorStep) + " мм.");
            return sb.ToString();
        }

        private static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        // ── хранение ──
        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "mode", Mode }, { "sub_type", SubType }, { "profile", Profile }, { "steps", Steps },
                { "wind_region", WindRegion }, { "terrain", Terrain }, { "height", Height },
                { "q_clad", QClad }, { "offset", Offset }, { "na_max", NaMax },
                { "step_main", StepMain }, { "step_corner", StepCorner }, { "rail_step_corner", RailStepCorner },
                { "start_off", StartOff }, { "rail_gap", RailGap }, { "corner_zone", CornerZone },
                { "axes", Axes }, { "axis_step", AxisStep }, { "row_step", RowStep },
                { "ask_corners", AskCorners }, { "ask_floors", AskFloors }, { "floor_step", FloorStep },
                { "signs", Signs },
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

        public static FrameSettings FromDict(Dictionary<string, object> d)
        {
            var s = new FrameSettings();
            if (d == null) return s;
            s.Mode = OneOf(S(d, "mode", s.Mode), "all", Modes);
            s.SubType = OneOf(S(d, "sub_type", s.SubType), "vertical", SubTypes);
            string[] pr = ProfilesFor(s.SubType);
            s.Profile = OneOf(S(d, "profile", s.Profile), pr.Length > 0 ? pr[0] : "", pr);
            s.Steps = OneOf(S(d, "steps", s.Steps), "calc", "calc", "manual");
            s.WindRegion = OneOf(S(d, "wind_region", s.WindRegion), "II", Winds);
            s.Terrain = OneOf(S(d, "terrain", s.Terrain), "B", Terrains);
            s.Height = D(d, "height", s.Height);
            s.QClad = D(d, "q_clad", s.InterFloor ? 8 : s.QClad);
            s.Offset = D(d, "offset", s.Offset);
            s.NaMax = D(d, "na_max", s.NaMax);
            s.StepMain = D(d, "step_main", s.StepMain);
            s.StepCorner = D(d, "step_corner", s.StepCorner);
            s.RailStepCorner = D(d, "rail_step_corner", s.RailStepCorner);
            s.StartOff = D(d, "start_off", s.StartOff);
            s.RailGap = D(d, "rail_gap", s.RailGap);
            s.CornerZone = D(d, "corner_zone", s.CornerZone);
            s.Axes = OneOf(S(d, "axes", s.Axes), "step", "step", "points");
            s.AxisStep = D(d, "axis_step", s.AxisStep);
            s.RowStep = D(d, "row_step", s.RowStep);
            s.AskCorners = B(d, "ask_corners", s.AskCorners);
            s.AskFloors = B(d, "ask_floors", s.AskFloors);
            s.FloorStep = D(d, "floor_step", s.FloorStep);
            s.Signs = OneOf(S(d, "signs", s.Signs), "cond", "cond", "samples");
            return s;
        }

        public FrameSettings Clone() { return FromDict(ToDict()); }

        private static string LastPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AFrame");
            return Path.Combine(dir, "frame_settings.json");
        }

        /// <summary>Последние значения окна (%APPDATA%\AFrame\frame_settings.json).</summary>
        public static FrameSettings LoadLast()
        {
            try
            {
                string p = LastPath();
                if (File.Exists(p))
                    return FromDict(new JavaScriptSerializer().DeserializeObject(
                        File.ReadAllText(p, Encoding.UTF8)) as Dictionary<string, object>);
            }
            catch { }
            return new FrameSettings();
        }

        public void SaveLast()
        {
            try
            {
                string p = LastPath();
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                File.WriteAllText(p, new JavaScriptSerializer().Serialize(ToDict()), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
