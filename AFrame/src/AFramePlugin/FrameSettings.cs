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
        // 26.09 (Денис): что облицовываем. Керамогранит/композит — прежний
        // алгоритм стоек по швам раскладки; кляммеры — только керамогранит.
        // Композит (АКП): пока только каркас, узел крепления не реализован.
        // Бетонная/клинкерная
        // плитка — вертикальные направляющие заданным шагом (не по швам), на
        // них горизонтальные шины, кляммеров нет.
        // 29.09c (Герман, ответ по №27): у плитки те же три типа подсистемы и
        // те же шаги кронштейнов (расчёт/вручную); шины трёх видов (29.09l:
        // стартовая и над проёмами, концевая и под проёмами), хлысты до 2500 со
        // стыком на направляющей; марка шины — от проекта, своя у бетонной и у
        // клинкерной; углы здания — как у всех, шаг направляющих в угловой
        // зоне — свой (0 — как рядовой)
        public string Cladding = "porcelain";  // porcelain | composite | concrete | clinker
        public double TileStepH = 600, TileStepHCorner = 0, TileWhip = 2500;
        public string RailBrandConcrete = "", RailBrandClinker = "";
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
        public FrameSolutionSelection SolutionSelection; // explicit historical source declaration; null preserves legacy behavior
        public FrameParameterContext ProjectParametersContext; // this owner only; never an all-zone map
        public string Signs = "cond";          // cond | samples

        public const double DefStartOff = 300, DefRailGap = 10, DefCornerZone = 1500;
        public static readonly string[] Modes = { "all", "frame", "clamps" };
        public static readonly string[] Claddings = { "porcelain", "composite", "concrete", "clinker" };
        public static readonly string[] CladdingTitles = { "керамогранит", "композит", "бетонная плитка", "клинкерная плитка" };
        private static readonly string[] CladdingUnder = { "керамогранит", "композит", "бетонную плитку", "клинкерную плитку" };
        public static readonly string[] SubTypes = { "vertical", "interfloor", "ortho" };
        public static readonly string[] VertProfiles = { "Авто", "ГП-40-40", "ГП-60-40", "ШП-60-20" };
        public static readonly string[] NspProfiles = { "НСП-1", "НСП-2" };
        public static readonly string[] Winds = { "Ia", "I", "II", "III", "IV", "V" };
        public static readonly string[] Terrains = { "A", "B", "C" };

        // ── то, что раньше давали ответы в командной строке ──
        /// <summary>Бетонная или клинкерная плитка — подсистема под шины.</summary>
        public bool IsTile { get { return Cladding == "concrete" || Cladding == "clinker"; } }
        public bool IsComposite { get { return Cladding == "composite"; } }
        // Рабочая методика Вектор из переданных расчётов: АКП 1.2,
        // КГ/клинкер 1.1. Для бетонной плитки сохранён рабочий фактор 1.1;
        // применимость к конкретному изделию подтверждает инженер.
        // Контракт с frame_calc.cladding_gamma проверяется доменными тестами.
        public double CladdingLoadFactor { get { return IsComposite ? 1.2 : 1.1; } }
        /// <summary>Тип, который уходит в движок (29.09c: у плитки — любой из трёх, как у всех).</summary>
        public string EffSubType { get { return SubType; } }
        public bool InterFloor { get { return EffSubType == "interfloor"; } }
        public bool Ortho { get { return EffSubType == "ortho"; } }
        public bool Manual { get { return Steps == "manual"; } }
        public bool ClampsOnly { get { return Mode == "clamps" && !IsTile; } }

        /// <summary>Марка шины текущей плитки (Герман 29.09: «меняется от проекта к проекту и
        /// зависит от бетонной или клинкерной плитки») — хранится отдельно для каждой.</summary>
        public string RailBrand
        {
            get { return Cladding == "clinker" ? RailBrandClinker : Cladding == "concrete" ? RailBrandConcrete : ""; }
            set
            {
                // без Trim: поле окна пишет сюда на каждое нажатие — пробел между словами
                // иначе съедался бы; обрезаем при записи в метку и в запрос движку
                string v = value ?? "";
                if (Cladding == "clinker") RailBrandClinker = v;
                else if (Cladding == "concrete") RailBrandConcrete = v;
            }
        }

        public string CladdingTitle
        {
            get { int k = Array.IndexOf(Claddings, Cladding); return k >= 0 ? CladdingTitles[k] : Cladding; }
        }

        /// <summary>Как в прежнем сообщении: «ATFRAME (Вертикальная): …».</summary>
        public string TypeTitle
        {
            get
            {
                string t = InterFloor ? "Межэтажная" : Ortho ? "Ортогональная" : "Вертикальная";
                if (!IsTile) return t;
                int k = Array.IndexOf(Claddings, Cladding);
                return t + " под " + (k >= 0 ? CladdingUnder[k] : Cladding);
            }
        }

        public string SysName
        {
            get { return SolutionSelection != null ? "Вектор-1" : InterFloor ? "Межэтажная" : Ortho ? "Ортогональная" : "Standart"; }
        }

        /// <summary>Марка вертикальной направляющей (null — «Авто», подбор расчётом).</summary>
        public string RailProfileOrNull
        {
            get
            {
                if (EffSubType != "vertical") return null;
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
            var d = new Dictionary<string, object> { { "name", SolutionSelection == null ? SysName : "Вектор-1" } };
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
            if (Manual) return null;           // 29.09c: у плитки — как у всех (расчёт или вручную)
            return new Dictionary<string, object>
            {
                { "wind_region", WindRegion }, { "terrain", Terrain }, { "height", Height },
                { "q_clad", QClad }, { "offset", Offset }, { "na_max", NaMax },
                { "gamma_clad", CladdingLoadFactor },
            };
        }

        /// <summary>Параметры запроса движку, которые задаёт окно (без геометрии зон, осей,
        /// рядов, углов и отметок — их даёт чертёж). 29.09 (синтетика «в ролях»): одна логика
        /// для команды ATFRAME и для проверок tools/roles — ГРАБЛЯ-13 (поле окна не дошло до
        /// движка) ловится прогоном, а не в AutoCAD. floorsPicked — сколько отметок перекрытий
        /// указано на чертеже (0 — межэтажная берёт шаг этажа из окна).</summary>
        public Dictionary<string, object> EngineParams(int floorsPicked)
        {
            string solutionReason = ValidateSolutionSelection();
            if (solutionReason != null) throw new FrameSolutionSelectionException(solutionReason);
            bool clampsOnly = ClampsOnly;
            var d = new Dictionary<string, object>
            {
                { "sub_type", EffSubType },
                { "system", SysOverride() },
                { "rail_profile", SolutionSelection == null ? RailProfileOrNull : null },
                { "nsp_type", NspTypeOrNull },
                { "cladding", Cladding },
            };
            var calc = clampsOnly ? null : CalcDict();
            if (calc != null) d["calc"] = calc;
            if (Manual && !clampsOnly)
            {
                // 04.08 (Герман п.1): шаг, заданный РУКАМИ, ставится буквально
                d["exact_step"] = true;
                // 04.08 (Герман п.2): шаг СТОЕК в угловой зоне (0 — по рустам)
                if (!IsTile && RailStepCorner > 1.0) d["rail_step_corner"] = RailStepCorner;
            }
            // Герман 07.08 (В-ад): отметки не указаны — хлысты снизу; автоперекрытия шагом
            // этажа — только у межэтажной
            d["floor_step"] = floorsPicked == 0 && InterFloor && FloorStep >= 1 ? FloorStep : 0.0;
            if (Mode == "frame") d["parts"] = "frame";
            if (IsTile)
            {
                // 29.09c (Герман): шаг направляющих (и в угловой зоне), хлыст и марка шины
                d["tile_step_x"] = TileStepH;
                if (TileStepHCorner > 0) d["tile_step_x_corner"] = TileStepHCorner;
                d["tile_whip"] = TileWhip;
                d["tile_rail_brand"] = RailBrand.Trim();
                if (RowStep >= 50) d["tile_row_step"] = RowStep;   // зоны без раскладки
                d["parts"] = "frame";
            }
            if (clampsOnly) d["parts"] = "clamps";
            if (SolutionSelection != null) d["solution_selection"] = SolutionSelection.ToDict();
            return d;
        }

        /// <summary>Смена схемы каркаса не меняет массу выбранной облицовки.
        /// Число 25 само по себе не отличает ручной ввод от умолчания.</summary>
        public void SetSubType(string sub)
        {
            string old = SubType;
            SubType = OneOf(sub, "vertical", SubTypes);
            if (old == SubType) return;
            string[] allowed = ProfilesFor(SubType);
            if (Array.IndexOf(allowed, Profile) < 0) Profile = allowed.Length > 0 ? allowed[0] : "";
        }

        public static string[] ProfilesFor(string sub)
        {
            if (sub == "vertical") return VertProfiles;
            if (sub == "interfloor") return NspProfiles;
            return new string[0];                         // ортогональная — ШП/ZП автоматом
        }

        public string ValidateSolutionSelection()
        {
            if (ProjectParametersContext != null)
            {
                try { ProjectParametersContext.ValidateSelection(SolutionSelection); }
                catch (FrameSolutionSelectionException error) { return error.Message; }
            }
            if (SolutionSelection == null) return null;
            string reason = SolutionSelection.Validate();
            if (reason != null) return reason;
            if (EffSubType != "vertical" || Cladding != "porcelain")
                return "Выбранное историческое решение ограничено вертикальной схемой под керамогранит, узел 4.2.1. Для другой схемы нужен отдельный подтверждённый выбор.";
            if (!ClampsOnly && !Manual)
                return "Автоматический расчёт выбранных изделий по АТР Вектор-1 2015 недоступен: соответствие расчётным сечениям примера 2026 не подтверждено. Используйте ручные шаги по отдельному инженерному расчёту.";
            return null;
        }

        public string Validate(bool hasLayout)
        {
            string solutionReason = ValidateSolutionSelection();
            if (solutionReason != null) return solutionReason;
            if (Array.IndexOf(Modes, Mode) < 0) return "Неизвестный режим «" + Mode + "».";
            if (Array.IndexOf(Claddings, Cladding) < 0) return "Неизвестная облицовка «" + Cladding + "».";
            if (IsComposite && Mode != "frame")
                return "Для композита (АКП) узел крепления облицовки пока не реализован. Выберите «только подсистему (без кляммеров)».";
            if (IsTile)
            {
                if (TileStepH < 100 || TileStepH > 3000) return "Шаг вертикальных направляющих — от 100 до 3000 мм.";
                if (TileStepHCorner != 0 && (TileStepHCorner < 100 || TileStepHCorner > 3000))
                    return "Шаг направляющих в угловой зоне — 0 (как рядовой) или от 100 до 3000 мм.";
                if (TileWhip < 300 || TileWhip > 12000) return "Хлыст шины — от 300 до 12000 мм.";
                if (!hasLayout && RowStep < 50)
                    return "Шины ставятся по рядам плитки, а у зон нет раскладки ATTILE — задайте шаг рядов от 50 мм.";
            }
            if (!ClampsOnly && !Manual)
            {
                if (EffSubType == "vertical" && Profile == "ГП-60-40")
                    return "Для ГП-60-40 не подтверждены расчётное сечение и масса. Выберите профиль с известными характеристиками или ручные шаги по отдельному инженерному расчёту.";
                if (InterFloor && NspTypeOrNull == "НСП-2")
                    return "Для НСП-2 не подтверждено расчётное сечение. Выберите НСП-1 или ручные шаги по отдельному инженерному расчёту.";
                if (!Finite(Height) || !Finite(QClad) || !Finite(Offset) || !Finite(NaMax))
                    return "Расчётные исходные данные должны быть конечными числами.";
                if (Height < 1 || Height > 500) return "Высота здания — от 1 до 500 м.";
                if (QClad <= 0 || QClad > 500) return "Вес облицовки — больше 0 и не больше 500 кг/м².";
                if (Offset < 20 || Offset > 1000) return "Плечо расчёта — от 20 до 1000 мм.";
                if (NaMax < 100) return "Усилие вырыва анкера — не меньше 100 Н.";
                if (InterFloor) return InterfloorCalculationLimit;
            }
            if (!ClampsOnly && Manual)
            {
                if (StepMain < 100 || StepCorner < 100) return "Шаг кронштейнов — не меньше 100 мм.";
                if (StartOff < 0 || RailGap < 0 || CornerZone < 0) return "Старт, зазор и угловая зона — не отрицательные.";
                if (!IsTile && RailStepCorner != 0 && RailStepCorner < 50) return "Шаг стоек в угловой зоне — 0 (по рустам) или от 50 мм.";
            }
            if (!hasLayout && !IsTile)
            {
                if (Axes == "step" && AxisStep < 50) return "Шаг осей стоек — не меньше 50 мм.";
                if (RowStep != 0 && RowStep < 50) return "Шаг горизонтальных швов — 0 (без кляммеров) или от 50 мм.";
            }
            if (InterFloor && !AskFloors && FloorStep < 1 && !ClampsOnly)
                return "Межэтажной нужны отметки перекрытий: укажите их после ОК или задайте высоту этажа.";
            return null;
        }

        private const string InterfloorCalculationLimit =
            "Автоматический расчёт межэтажной подсистемы недоступен: не подтверждены непрерывность направляющих через стыки и неподвижные/подвижные соединения. Пересечения НСП с НГП не подтверждают расчётную схему. Ручная расстановка доступна по отдельному проектному расчёту.";

        /// <summary>Что произойдёт по «Разложить» — текст для окна.</summary>
        public string Describe(bool hasLayout)
        {
            string solutionReason = ValidateSolutionSelection();
            if (solutionReason != null) return solutionReason;
            if (!ClampsOnly && !Manual && EffSubType == "vertical" && Profile == "ГП-60-40")
                return "Расчёт ГП-60-40 недоступен: нужны подтверждённые характеристики сечения и масса профиля. Расчёт по ГП-40-40 не подтверждает выбранный ГП-60-40. Выберите другой профиль либо ручной режим по отдельному инженерному расчёту.";
            if (!ClampsOnly && !Manual && InterFloor)
                return InterfloorCalculationLimit;
            var sb = new StringBuilder();
            if (IsTile)
            {
                sb.Append(TypeTitle).Append(" подсистема: вертикальные направляющие шагом ").Append(F(TileStepH))
                  .Append(" мм от края зоны, не по швам (первая и последняя — в 100 мм от краёв")
                  .Append(TileStepHCorner > 0 ? "; в угловых зонах — шагом " + F(TileStepHCorner)
                                              : "; в угловых зонах — тем же шагом (в угловой зоне 0)")
                  .Append("; у окон — у каждой грани)")
                  .Append(Manual ? "; кронштейны вручную: рядовая " + F(StepMain) + ", угловая " + F(StepCorner) + " мм"
                                 : "; кронштейны по расчёту: район " + WindRegion + ", местность " + Terrain + ", " +
                                   F(Height) + " м, облицовка " + F(QClad) + " кг/м², плечо расчёта " + F(Offset) +
                                   " мм, анкер " + F(NaMax) + " Н");
                if (SubType == "vertical") sb.Append(SolutionSelection == null ? "; профиль " + (Profile == "Авто" ? "подбором" : Profile) : "; профиль заявлен в каталоге, физическое назначение не подтверждено");
                if (InterFloor) sb.Append("; ").Append(Profile);
                if (!Manual) sb.Append("; коэффициент веса ").Append(F(CladdingLoadFactor));
                sb.Append(". Шины: стартовая по низу зоны и над проёмами, рядовые по центрам горизонтальных швов")
                  .Append(hasLayout ? " (ряды — из раскладки зон)" : " (шагом " + F(RowStep) + " мм от низа зоны)")
                  .Append(", концевая по верху и под проёмами; хлысты не длиннее ").Append(F(TileWhip))
                  .Append(" мм, стык на направляющей; марка — ")
                  .Append(RailBrand.Trim().Length > 0 ? "«" + RailBrand.Trim() + "»" : "не задана").Append(". Кляммеров нет.");
                var aft = new List<string>();
                if (AskCorners) aft.Add("внешние углы здания");
                if (Signs == "samples") aft.Add("образцы знаков");
                if (AskFloors) aft.Add("отметки перекрытий");
                if (aft.Count > 0) sb.Append(" После «Разложить» указать: ").Append(string.Join(", ", aft.ToArray())).Append(".");
                if (InterFloor) sb.Append(" Без отметок — перекрытия шагом этажа " + F(FloorStep) + " мм.");
                if (Manual) sb.Append(" Несущая способность при ручном шаге программой не проверяется.");
                return sb.ToString();
            }
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
                                   F(Height) + " м, облицовка " + F(QClad) + " кг/м², плечо расчёта " + F(Offset) +
                                   " мм, анкер " + F(NaMax) + " Н");
                if (SubType == "vertical") sb.Append(SolutionSelection == null ? "; профиль " + (Profile == "Авто" ? "подбором" : Profile) : "; профиль заявлен в каталоге, физическое назначение не подтверждено");
                if (InterFloor) sb.Append("; ").Append(Profile);
                if (!Manual) sb.Append("; коэффициент веса ").Append(F(CladdingLoadFactor));
                sb.Append(".");
            }
            if (IsComposite) sb.Append(" Узел крепления АКП в этот результат не входит.");
            if (Manual && !ClampsOnly) sb.Append(" Несущая способность при ручном шаге программой не проверяется.");
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
        private static bool Finite(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        public void ApplyProjectParameters(FrameParameterResolution resolution)
        {
            if (resolution == null) throw new FrameSolutionSelectionException("Не разрешены параметры проекта для зоны.");
            SolutionSelection = resolution.Selection;
            ProjectParametersContext = resolution.Context;
        }
        public void DetachProjectParameters()
        {
            // A saved inherited snapshot is not a local declaration. A genuine
            // unbound 2B1 choice has no context and remains available as before.
            if (ProjectParametersContext != null) SolutionSelection = null;
            ProjectParametersContext = null;
        }

        // ── хранение ──
        public Dictionary<string, object> ToDict()
        {
            var result = new Dictionary<string, object>
            {
                { "cladding", Cladding }, { "tile_step_h", TileStepH }, { "tile_step_h_corner", TileStepHCorner },
                { "tile_whip", TileWhip }, { "rail_brand_concrete", RailBrandConcrete.Trim() },
                { "rail_brand_clinker", RailBrandClinker.Trim() },
                { "mode", Mode }, { "sub_type", SubType }, { "profile", Profile }, { "steps", Steps },
                { "wind_region", WindRegion }, { "terrain", Terrain }, { "height", Height },
                { "q_clad", QClad }, { "offset", Offset }, { "na_max", NaMax },
                { "step_main", StepMain }, { "step_corner", StepCorner }, { "rail_step_corner", RailStepCorner },
                { "start_off", StartOff }, { "rail_gap", RailGap }, { "corner_zone", CornerZone },
                { "axes", Axes }, { "axis_step", AxisStep }, { "row_step", RowStep },
                { "ask_corners", AskCorners }, { "ask_floors", AskFloors }, { "floor_step", FloorStep },
                { "signs", Signs },
            };
            if (SolutionSelection != null) result["solution_selection"] = SolutionSelection.ToDict();
            if (ProjectParametersContext != null)
            {
                ProjectParametersContext.ValidateSelection(SolutionSelection);
                result["project_parameters_context"] = ProjectParametersContext.ToDict();
            }
            return result;
        }

        // Global convenience settings never transport a project declaration to
        // a different drawing. Drawing metadata uses the complete ToDict above.
        public Dictionary<string, object> ToLastDict()
        {
            var result = ToDict(); result.Remove("solution_selection"); result.Remove("project_parameters_context"); return result;
        }
        public static FrameSettings FromLastDict(Dictionary<string, object> value)
        {
            if (value == null) return new FrameSettings();
            var copy = new Dictionary<string, object>(value); copy.Remove("solution_selection"); copy.Remove("project_parameters_context");
            return FromDict(copy);
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
            s.Cladding = OneOf(S(d, "cladding", s.Cladding), "porcelain", Claddings);
            s.TileStepH = D(d, "tile_step_h", s.TileStepH);
            s.TileStepHCorner = D(d, "tile_step_h_corner", s.TileStepHCorner);
            s.TileWhip = D(d, "tile_whip", s.TileWhip);
            s.RailBrandConcrete = S(d, "rail_brand_concrete", s.RailBrandConcrete).Trim();
            s.RailBrandClinker = S(d, "rail_brand_clinker", s.RailBrandClinker).Trim();
            s.Mode = OneOf(S(d, "mode", s.Mode), "all", Modes);
            s.SubType = OneOf(S(d, "sub_type", s.SubType), "vertical", SubTypes);
            string[] pr = ProfilesFor(s.SubType);
            s.Profile = OneOf(S(d, "profile", s.Profile), pr.Length > 0 ? pr[0] : "", pr);
            s.Steps = OneOf(S(d, "steps", s.Steps), "calc", "calc", "manual");
            s.WindRegion = OneOf(S(d, "wind_region", s.WindRegion), "II", Winds);
            s.Terrain = OneOf(S(d, "terrain", s.Terrain), "B", Terrains);
            s.Height = D(d, "height", s.Height);
            s.QClad = D(d, "q_clad", s.QClad);
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
            object selection;
            if (d.TryGetValue("solution_selection", out selection)) s.SolutionSelection = FrameSolutionSelection.FromDict(selection);
            object context;
            if (d.TryGetValue("project_parameters_context", out context))
            {
                if (context == null) throw new FrameSolutionSelectionException("Проектный снимок параметров повреждён; он не заменяется локальным выбором.");
                s.ProjectParametersContext = FrameParameterContext.FromDict(context);
                s.ProjectParametersContext.ValidateSelection(s.SolutionSelection);
            }
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
                    return FromLastDict(new JavaScriptSerializer().DeserializeObject(
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
                File.WriteAllText(p, new JavaScriptSerializer().Serialize(ToLastDict()), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
