using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AFramePlugin.FrameCommand))]

namespace AFramePlugin
{
    /// <summary>
    /// ATFRAME — монтажная схема подсистемы НВФ по зонам с меткой
    /// «ATCLAD» (раскладка согласована): стойки по осям вертикальных
    /// рустов (joints_x из метки), несущие кронштейны на отметках
    /// перекрытий (центр — В15), рядовые равномерно (В16), кляммеры
    /// по центрам горизонтальных швов (rows_y). Знаки — СВОИ блоки
    /// (В13; несущий = реже у Германа: ромб с крестом; поменять
    /// местами — SignDrawers). Слои — конвенция Германа _01_ПС_*.
    /// Метка «ATFRAME» (параметры + хэндлы) — перегенерация тем же
    /// выбором удаляет прежнее. Инфраструктура Xrecord/CallEngine
    /// продублирована из AClad СОЗНАТЕЛЬНО (модули развязаны).
    /// </summary>
    public class FrameCommand
    {
        internal const string XKeyClad = "ATCLAD";
        internal const string XKeyFrame = "ATFRAME";
        private const double CloseTol = 0.5;

        private const string LayerRails = "_01_ПС_НАПРАВЛЯЮЩИЕ";
        private const string LayerBrackets = "_01_ПС_кронштейны";
        private const string LayerClamps = "_01_ПС_КЛЯММЕРЫ";

        private const string BlkMain = "AFRAME_КР_НЕСУЩИЙ";
        private const string BlkRow = "AFRAME_КР_РЯДОВОЙ";
        private const string BlkClampStart = "AFRAME_КЛЯММЕР_СТАРТ";
        private const string BlkClampRow = "AFRAME_КЛЯММЕР_РЯД";

        [CommandMethod("ATFRAME", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try { RunCore(doc); }
            catch (System.Exception ex)
            {
                try
                {
                    doc.Editor.WriteMessage(
                        "\nATFRAME: внутренняя ошибка — сообщите " +
                        "разработчику текст ниже.\n" + ex.ToString() + "\n");
                }
                catch { }
            }
        }

        private void RunCore(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            var db = doc.Database;
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            // ── 1. выбор зон с меткой ATCLAD (штриховки/марки/контуры) ──
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите зоны с раскладкой ATCLAD " +
                                   "(штриховки/марки/контуры): "
            };
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Operator, "<or"),
                new TypedValue((int)DxfCode.Start, "HATCH"),
                new TypedValue((int)DxfCode.Start, "MTEXT"),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                new TypedValue((int)DxfCode.Operator, "or>"),
            });
            var sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            var zoneObjs = new Dictionary<string, List<ObjectId>>();
            var polyByHandle = new Dictionary<string, ObjectId>();
            var polyData = new Dictionary<string, Dictionary<string, object>>();
            var joints = new List<double>();
            var rowsY = new List<double>();
            var oldHandles = new HashSet<string>();
            int noClad = 0;
            int oldMeta = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                int metas = 0;
                foreach (SelectedObject so in sel.Value)
                {
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead)
                              as Entity;
                    if (ent == null) continue;
                    string mjson = ReadData(tr, ent, XKeyClad);
                    var m = mjson == null ? null
                        : ser.DeserializeObject(mjson)
                          as Dictionary<string, object>;
                    if (m != null)
                    {
                        var jx = Get(m, "joints_x") as object[];
                        var ry = Get(m, "rows_y") as object[];
                        if (jx == null)
                        {
                            oldMeta++;   // метка сборки до 24.07
                        }
                        else
                        {
                            metas++;
                            foreach (var v in jx) joints.Add(ToD(v));
                            if (ry != null)
                                foreach (var v in ry)
                                    rowsY.Add(ToD(v));
                        }
                    }
                    else noClad++;
                    // прежняя подсистема (метка ATFRAME) — с любого
                    CollectOldHandles(tr, ser, ent, oldHandles);

                    // замкнутые полилинии — геометрия ВСЕГДА, метка не
                    // нужна (26.07: окна-полилинии без метки выпадали
                    // → стойки и кронштейны шли сквозь проёмы!)
                    var pl = ent as Polyline;
                    if (pl != null)
                    {
                        int n = pl.NumberOfVertices;
                        if (n < 3) continue;
                        bool closed = pl.Closed ||
                            pl.GetPoint2dAt(0).GetDistanceTo(
                                pl.GetPoint2dAt(n - 1)) <= CloseTol;
                        if (!closed) continue;
                        var pts = new List<object>();
                        for (int i = 0; i < n; i++)
                        {
                            Point2d p = pl.GetPoint2dAt(i);
                            pts.Add(new[] { p.X, p.Y });
                        }
                        string h = pl.Handle.ToString();
                        polyByHandle[h] = pl.ObjectId;
                        polyData[h] = new Dictionary<string, object>
                        { { "id", h }, { "pts", pts } };
                        continue;
                    }
                    if (m == null) continue;
                    string zid = SafeStr(Get(m, "zone_id"));
                    if (zid.Length == 0) continue;
                    if (!zoneObjs.ContainsKey(zid))
                        zoneObjs[zid] = new List<ObjectId>();
                    if (!zoneObjs[zid].Contains(ent.ObjectId))
                        zoneObjs[zid].Add(ent.ObjectId);
                }
                tr.Commit();
                // фидбэк Германа 27.07 (главный): ATFRAME должна
                // работать БЕЗ AClad (раскладка старой сборки, чужой
                // файл, переустановка) — оси задаются шагом или
                // точками по геометрии контуров
                if (metas == 0)
                {
                    if (oldMeta > 0)
                        ed.WriteMessage("\nМетки ATCLAD старой " +
                            "сборки (без осей рустов).");
                    else if (polyData.Count > 0)
                        ed.WriteMessage("\nМеток ATCLAD нет — " +
                            "работаем по контурам без раскладки.");
                    if (polyData.Count == 0)
                    {
                        ed.WriteMessage("\nНет ни меток ATCLAD, ни " +
                            "замкнутых контуров — выберите контуры " +
                            "зоны или сделайте раскладку.");
                        return;
                    }
                }
            }
            if (joints.Count == 0)
            {
                double bbx0 = double.MaxValue, bbx1 = double.MinValue;
                double bby0 = double.MaxValue, bby1 = double.MinValue;
                foreach (var pd in polyData.Values)
                {
                    var pts0 = pd["pts"] as List<object>;
                    if (pts0 == null) continue;
                    foreach (var po in pts0)
                    {
                        var xy = po as double[];
                        if (xy == null || xy.Length < 2) continue;
                        if (xy[0] < bbx0) bbx0 = xy[0];
                        if (xy[0] > bbx1) bbx1 = xy[0];
                        if (xy[1] < bby0) bby0 = xy[1];
                        if (xy[1] > bby1) bby1 = xy[1];
                    }
                }
                var pkj = new PromptKeywordOptions(
                    "\nОси стоек [Шаг/Точки/Отмена] <Шаг>: ",
                    "Шаг Точки Отмена");
                var rkj = ed.GetKeywords(pkj);
                string jkey = (rkj.Status == PromptStatus.OK &&
                               rkj.StringResult != null &&
                               rkj.StringResult.Length > 0)
                              ? rkj.StringResult : "Шаг";
                if (jkey == "Отмена") return;
                if (jkey == "Точки")
                {
                    while (true)
                    {
                        var pjo = new PromptPointOptions(
                            "\nТочка на оси стойки (Enter — дальше): ")
                        { AllowNone = true };
                        var pjv = ed.GetPoint(pjo);
                        if (pjv.Status != PromptStatus.OK) break;
                        joints.Add(pjv.Value.X);
                        ed.WriteMessage("\n  ось X = " +
                            F0(pjv.Value.X) + " (всего " +
                            joints.Count + ")");
                    }
                }
                else
                {
                    var pfo = new PromptPointOptions(
                        "\nТочка ПЕРВОЙ оси стойки: ");
                    var pfv = ed.GetPoint(pfo);
                    if (pfv.Status != PromptStatus.OK) return;
                    double jstep = AskD(ed,
                        "Шаг осей (плита + шов), мм", 608.0);
                    if (jstep < 50) jstep = 608.0;
                    double margin = 150.0;
                    for (double jx0 = pfv.Value.X;
                         jx0 >= bbx0 + margin; jx0 -= jstep)
                        joints.Add(jx0);
                    for (double jx0 = pfv.Value.X + jstep;
                         jx0 <= bbx1 - margin; jx0 += jstep)
                        joints.Add(jx0);
                    ed.WriteMessage("\n  осей по шагу " + F0(jstep) +
                        ": " + joints.Count);
                }
                if (joints.Count == 0)
                { ed.WriteMessage("\nОсей нет — отмена."); return; }
                if (rowsY.Count == 0)
                {
                    double rstep = AskD(ed, "Шаг горизонтальных швов " +
                        "для кляммеров, мм (0 — без кляммеров)", 605.0);
                    if (rstep >= 50)
                        for (double ry0 = bby0 + rstep;
                             ry0 < bby1; ry0 += rstep)
                            rowsY.Add(ry0);
                }
            }
            if (oldMeta > 0)
                ed.WriteMessage("\nМетка ATCLAD старой сборки: " + oldMeta +
                    " объект(ов) — их оси не учтены (перегенерируйте " +
                    "ATCLAD).");
            if (zoneObjs.Count == 0 && polyData.Count == 0)
            { ed.WriteMessage("\nНет пригодных зон."); return; }

            // ── 2. геометрия зон из <dwg>_fzones.json ──
            var fz = LoadFzones(ed, db, ser);
            var zonesPayload = new List<Dictionary<string, object>>();
            var partToRoot = new Dictionary<string, string>();
            foreach (var kv in zoneObjs)
            {
                var parts = FindZoneParts(fz, kv.Key);
                if (parts.Count == 0)
                {
                    ed.WriteMessage("\nНет геометрии в _fzones.json для " +
                        kv.Key + " — зона пропущена.");
                    continue;
                }
                foreach (var part in parts)
                {
                    string pid = SafeStr(Get(part, "id"));
                    partToRoot[pid] = kv.Key;
                    zonesPayload.Add(new Dictionary<string, object>
                    { { "zone_id", pid }, { "zone", part } });
                }
            }
            var contoursPayload = new List<Dictionary<string, object>>();
            foreach (var kv in polyData) contoursPayload.Add(kv.Value);
            if (zonesPayload.Count == 0 && contoursPayload.Count == 0)
            { ed.WriteMessage("\nНет геометрии зон."); return; }

            // ── 3. ТИП подсистемы (ТЗ Германа 26.07: вертикальная /
            //    межэтажная / ортогональная; комбинации — разными
            //    запусками). Классический конструктор кейвордов,
            //    дефолт по не-OK — грабля 18.07r ──
            var pko = new PromptKeywordOptions(
                "\nТип подсистемы [Вертикальная/Межэтажная/" +
                "Ортогональная] <Вертикальная>: ",
                "Вертикальная Межэтажная Ортогональная");
            var rk = ed.GetKeywords(pko);
            string typKey = (rk.Status == PromptStatus.OK &&
                             rk.StringResult != null &&
                             rk.StringResult.Length > 0)
                            ? rk.StringResult : "Вертикальная";
            bool interFloor = typKey == "Межэтажная";
            bool ortho = typKey == "Ортогональная";
            string subType = interFloor ? "interfloor"
                : ortho ? "ortho" : "vertical";
            string sysName = interFloor ? "Межэтажная"
                : ortho ? "Ортогональная" : "Standart";

            // углы здания (фидбэк Германа 27.07): угловая зона
            // (1500) отсчитывается от УКАЗАННЫХ внешних углов, а не
            // от краёв зоны; Enter без точек — по краям контуров
            var cornersX = new List<object>();
            while (true)
            {
                var pco = new PromptPointOptions(
                    "\nТочка на ВНЕШНЕМ углу здания (Enter — дальше): ")
                { AllowNone = true };
                var pcv = ed.GetPoint(pco);
                if (pcv.Status != PromptStatus.OK) break;
                cornersX.Add(pcv.Value.X);
                ed.WriteMessage("\n  угол X = " + F0(pcv.Value.X) +
                                " (всего " + cornersX.Count + ")");
            }
            if (cornersX.Count == 0)
                ed.WriteMessage("\n  углы не указаны — угловые зоны " +
                    "по краям контуров.");

            double? railStepCorner = null;
            bool manualStep = false;

            // ── 3п. ПРОФИЛЬ направляющих (письмо Германа 01.08 п.2):
            //    видимость динблока = марка профиля. Вертикальная:
            //    Авто (подбор расчётом по допустимым В-ш) или явный
            //    выбор; межэтажная: НСП-1/НСП-2; ортогональная: ШП/
            //    ZП ставятся движком автоматом ──
            string railProfile = null, nspType = null;
            if (subType == "vertical")
            {
                var pkp = new PromptKeywordOptions(
                    "\nПрофиль направляющей — Авто(подбор), ГП-40-40, " +
                    "ГП-60-40, ШП-60-20 [Авто/ГП40/ГП60/ШП60] <Авто>: ",
                    "Авто ГП40 ГП60 ШП60");
                var rpk = ed.GetKeywords(pkp);
                string kp = rpk.Status == PromptStatus.OK &&
                            rpk.StringResult != null
                            ? rpk.StringResult : "";
                if (kp == "ГП40") railProfile = "ГП-40-40";
                else if (kp == "ГП60") railProfile = "ГП-60-40";
                else if (kp == "ШП60") railProfile = "ШП-60-20";
            }
            else if (interFloor)
            {
                var pkp = new PromptKeywordOptions(
                    "\nВертикальный профиль межэтажной [НСП1/НСП2] " +
                    "<НСП1>: ", "НСП1 НСП2");
                var rpk = ed.GetKeywords(pkp);
                nspType = (rpk.Status == PromptStatus.OK &&
                           rpk.StringResult == "НСП2")
                          ? "НСП-2" : "НСП-1";
            }

            // ── 3р. ЭТАП 4 (целевой порядок Дениса 24.07): шаги
            //    кронштейнов СЧИТАЮТСЯ модулем расчёта несущей
            //    способности (frame_calc, методика «Вектор фасад») —
            //    конструктор даёт только исходные. «Вручную» —
            //    прежний путь со справочником/правкой шагов ──
            var sysOverride = new Dictionary<string, object>
            { { "name", sysName } };
            Dictionary<string, object> calcDict = null;
            var pkr = new PromptKeywordOptions(
                "\nШаги кронштейнов [Расчет/Вручную] <Расчет>: ",
                "Расчет Вручную");
            var rkr = ed.GetKeywords(pkr);
            bool manual = rkr.Status == PromptStatus.OK &&
                          rkr.StringResult == "Вручную";
            if (!manual)
            {
                var pkw = new PromptKeywordOptions(
                    "\nВетровой район [Ia/I/II/III/IV/V] <II>: ",
                    "Ia I II III IV V");
                var rw = ed.GetKeywords(pkw);
                string windReg = (rw.Status == PromptStatus.OK &&
                                  rw.StringResult != null &&
                                  rw.StringResult.Length > 0)
                                 ? rw.StringResult : "II";
                var pkt = new PromptKeywordOptions(
                    "\nТип местности по СП 20.13330 [A/B/C] <B>: ",
                    "A B C");
                var rt = ed.GetKeywords(pkt);
                string terr = (rt.Status == PromptStatus.OK &&
                               rt.StringResult != null &&
                               rt.StringResult.Length > 0)
                              ? rt.StringResult : "B";
                double hgt = AskD(ed, "Высота здания, м", 30.0);
                double qcl = AskD(ed, "Вес облицовки, кг/м2",
                                  interFloor ? 8.0 : 25.0);
                double off = AskD(ed, "Вынос облицовки, мм", 230.0);
                double na = AskD(ed, "Усилие вырыва анкера по ТС/" +
                                     "акту, Н", 3000.0);
                if (subType == "vertical")
                {
                    sysOverride["name"] = "Вектор-1";
                    ed.WriteMessage("\n  расчётный пресет: Вектор-1 " +
                        "(КР2-70 + УК-70-1,2 + ГП-40-40-1,2).");
                }
                calcDict = new Dictionary<string, object>
                {
                    { "wind_region", windReg },
                    { "terrain", terr },
                    { "height", hgt },
                    { "q_clad", qcl },
                    { "offset", off },
                    { "na_max", na },
                };
            }
            else
            {
                // дефолты для подтверждения (источник истины движок,
                // тут только стартовые значения диалога)
                double stepMain = 800.0;
                double stepCorner = 800.0;
                double railGap = 10.0;
                double startOff = 300.0;
                double cornerZone = 1500.0;

                stepMain = AskD(ed, "Шаг кронштейнов в РЯДОВОЙ " +
                                "зоне, мм", stepMain);
                stepCorner = AskD(ed, "Шаг кронштейнов в УГЛОВОЙ " +
                                  "зоне (1500 от угла), мм", stepCorner);
                sysOverride["bracket_step"] = stepMain;
                sysOverride["bracket_step_corner"] = stepCorner;
                // 04.08 (Герман п.1): шаг, заданный РУКАМИ, ставится
                // буквально — без «размазывания» остатка по пролётам
                // (В16 давал 798 вместо заданных 800)
                manualStep = true;
                // 04.08 (Герман п.2): шаг СТОЕК в угловой зоне —
                // первая в 100 мм от указанного угла, дальше этим
                // шагом; Enter — стойки только по осям рустов
                double railStepC = AskD(ed, "Шаг СТОЕК в УГЛОВОЙ зоне, "
                                        + "мм (0 — только по рустам)",
                                        0.0);
                if (railStepC > 1.0) railStepCorner = railStepC;
                var pkc = new PromptKeywordOptions(
                    "\nПрочее: старт " + F0(startOff) + ", зазор " +
                    "стыка " + F0(railGap) + ", угловая зона " +
                    F0(cornerZone) + " [Принять/Изменить] <Принять>: ",
                    "Принять Изменить");
                var rkc = ed.GetKeywords(pkc);
                if (rkc.Status == PromptStatus.OK &&
                    rkc.StringResult == "Изменить")
                {
                    startOff = AskD(ed, "Первый кронштейн от низа " +
                                        "стойки, мм", startOff);
                    railGap = AskD(ed, "Зазор стыка направляющих, мм",
                                   railGap);
                    cornerZone = AskD(ed, "Ширина угловой зоны, мм",
                                      cornerZone);
                    sysOverride["bracket_start_offset"] = startOff;
                    sysOverride["rail_gap"] = railGap;
                    sysOverride["corner_zone"] = cornerZone;
                }
            }

            // ── 3а. знаки: условные или ОБРАЗЦЫ боевых блоков
            //    Германа с чертежа (ТЗ 26.07 п.3: тип и длина
            //    кронштейна в имени блока, «Кронш КР1-70-100») ──
            ObjectId smpMain = ObjectId.Null, smpRow = ObjectId.Null;
            ObjectId smpClampRow = ObjectId.Null,
                     smpClampStart = ObjectId.Null,
                     smpClampSide = ObjectId.Null,
                     smpClampCombo = ObjectId.Null,
                     smpRail = ObjectId.Null;
            var pks = new PromptKeywordOptions(
                "\nЗнаки кронштейнов [Условные/Образцы] <Условные>: ",
                "Условные Образцы");
            var rks = ed.GetKeywords(pks);
            if (rks.Status == PromptStatus.OK &&
                rks.StringResult == "Образцы")
            {
                // фидбэк Германа 27.07 (п.4): образцы и для
                // кляммеров, и для направляющей (его библиотека
                // блоков); Enter по любому — условный знак
                smpMain = PickBlock(ed, db,
                    "\nОбразец НЕСУЩЕГО кронштейна (Enter — усл.): ");
                smpRow = PickBlock(ed, db,
                    "\nОбразец ОПОРНОГО кронштейна (Enter — усл.): ");
                smpClampRow = PickBlock(ed, db,
                    "\nОбразец кляммера РЯДОВОГО (Enter — усл.): ");
                smpClampStart = PickBlock(ed, db,
                    "\nОбразец кляммера СТАРТОВОГО (Enter — усл.): ");
                smpClampSide = PickBlock(ed, db,
                    "\nОбразец кляммера БОКОВОГО (Enter — усл.): ");
                smpClampCombo = PickBlock(ed, db,
                    "\nОбразец кляммера КОМБИНИРОВАННОГО " +
                    "(Enter — усл.): ");
                smpRail = PickBlock(ed, db,
                    "\nОбразец НАПРАВЛЯЮЩЕЙ — динамический блок " +
                    "(Enter — прямоугольники): ");
            }

            // ── 4. отметки перекрытий (несущие; стык направляющих) ──
            var floors = new List<object>();
            while (true)
            {
                var ppo = new PromptPointOptions(
                    "\nТочка на отметке перекрытия (Enter — дальше): ")
                { AllowNone = true };
                var pv = ed.GetPoint(ppo);
                if (pv.Status != PromptStatus.OK) break;
                floors.Add(pv.Value.Y);
                ed.WriteMessage("\n  перекрытие Y = " + F0(pv.Value.Y) +
                                " (всего " + floors.Count + ")");
            }
            // ТЗ Германа 26.07 п.5: кляммеры — только для керамогранита,
            // решает конструктор (классический конструктор кейвордов)
            var pkl = new PromptKeywordOptions(
                "\nРаскладывать кляммеры (только для керамогранита) " +
                "[Да/Нет] <Да>: ", "Да Нет");
            var rkl = ed.GetKeywords(pkl);
            if (rkl.Status == PromptStatus.OK &&
                rkl.StringResult == "Нет")
                rowsY.Clear();

            // 26.07 (фидбэк Германа): без отметок направляющие выходили
            // «бесконечными» — предлагаем автоперекрытия шагом этажа
            double floorStep = 0.0;
            if (floors.Count == 0)
            {
                floorStep = AskD(ed, "Отметок нет. Высота этажа для " +
                    "автоматических перекрытий, мм (0 — без перекрытий)",
                    3000.0);
                if (floorStep < 1) floorStep = 0.0;
            }

            // ── 5. движок ──
            joints.Sort();
            rowsY.Sort();
            var payload = new Dictionary<string, object>
            {
                { "op", "frame" },
                { "sub_type", subType },
                { "system", sysOverride },
                { "zones", zonesPayload },
                { "contours", contoursPayload },
                { "joints_x", joints },
                { "floors_y", floors },
                { "rows_y", rowsY },
                { "floor_step", floorStep },
                { "rail_profile", railProfile },
                { "nsp_type", nspType },
            };
            if (calcDict != null) payload["calc"] = calcDict;
            if (cornersX.Count > 0) payload["corners_x"] = cornersX;
            // ГРАБЛЯ-13: новые поля запроса пробрасывать ЯВНО
            if (railStepCorner.HasValue)
                payload["rail_step_corner"] = railStepCorner.Value;
            if (manualStep) payload["exact_step"] = true;
            string baseDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? ".";
            string engineExe = Path.GetFullPath(Path.Combine(
                baseDir, "..", "engine", "frame_engine.exe"));
            Dictionary<string, object> res;
            try
            {
                res = ser.DeserializeObject(CallEngine(
                    engineExe, ser.Serialize(payload)))
                    as Dictionary<string, object>;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nОшибка движка: " + ex.Message);
                return;
            }
            if (res == null || !GetBool(res, "ok"))
            {
                ed.WriteMessage("\nДвижок отказал: " + SafeStr(
                    Get(res, "error")));
                PrintNotes(ed, Get(res, "notes") as object[]);
                return;
            }
            var rails = Get(res, "rails") as object[];
            var hrails = Get(res, "hrails") as object[];
            var brackets = Get(res, "brackets") as object[];
            var clamps = Get(res, "clamps") as object[];
            var fittings = Get(res, "fittings") as object[];
            var sysUsed = Get(res, "system_used")
                          as Dictionary<string, object>;
            double railW = sysUsed != null ? ToD(Get(sysUsed, "rail_width"))
                                           : 40.0;
            if (railW < 1) railW = 40.0;

            // ── 6. чертёж ──
            var handlesByRoot = new Dictionary<string, List<string>>();
            int erased = 0, made = 0;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                                  OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureLayer(tr, db, LayerRails);
                EnsureLayer(tr, db, LayerBrackets);
                EnsureLayer(tr, db, LayerClamps);
                ObjectId blkMain = smpMain.IsNull
                    ? EnsureSignBlock(tr, db, BlkMain, true) : smpMain;
                ObjectId blkRow = smpRow.IsNull
                    ? EnsureSignBlock(tr, db, BlkRow, false) : smpRow;
                ObjectId clStart = smpClampStart.IsNull
                    ? EnsureClampBlock(tr, db, BlkClampStart, true)
                    : smpClampStart;
                ObjectId clRow = smpClampRow.IsNull
                    ? EnsureClampBlock(tr, db, BlkClampRow, false)
                    : smpClampRow;
                ObjectId fitIns = EnsureFitBlock(tr, db,
                                                 "AFRAME_ВСТАВКА", true);
                ObjectId fitSc = EnsureFitBlock(tr, db,
                                                "AFRAME_СКОБА", false);

                // прежняя подсистема этих зон (перегенерация)
                if (oldHandles.Count > 0)
                    foreach (ObjectId oid in ms)
                    {
                        Entity oe;
                        try
                        {
                            oe = tr.GetObject(oid, OpenMode.ForRead)
                                 as Entity;
                        }
                        catch { continue; }
                        if (oe == null) continue;
                        if (!oldHandles.Contains(oe.Handle.ToString()))
                            continue;
                        oe.UpgradeOpen();
                        oe.Erase();
                        erased++;
                    }

                // направляющие — прямоугольники по оси
                if (rails != null)
                    foreach (var ro in rails)
                    {
                        var r = ro as Dictionary<string, object>;
                        if (r == null) continue;
                        double x = ToD(Get(r, "x")),
                               y0 = ToD(Get(r, "y0")),
                               y1 = ToD(Get(r, "y1"));
                        string rprof = SafeStr(Get(r, "profile"));
                        if (!smpRail.IsNull)
                        {
                            string rh = InsertRailBlock(tr, ms, smpRail,
                                x, y0, y1 - y0, LayerRails, rprof);
                            if (rh != null)
                            {
                                Remember(handlesByRoot, partToRoot,
                                         SafeStr(Get(r, "zone")), rh);
                                made++;
                                continue;
                            }
                        }
                        var pl = new Polyline();
                        pl.AddVertexAt(0, new Point2d(x - railW / 2, y0),
                                       0, 0, 0);
                        pl.AddVertexAt(1, new Point2d(x + railW / 2, y0),
                                       0, 0, 0);
                        pl.AddVertexAt(2, new Point2d(x + railW / 2, y1),
                                       0, 0, 0);
                        pl.AddVertexAt(3, new Point2d(x - railW / 2, y1),
                                       0, 0, 0);
                        pl.Closed = true;
                        pl.Layer = LayerRails;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        Remember(handlesByRoot, partToRoot,
                                 SafeStr(Get(r, "zone")),
                                 pl.Handle.ToString());
                        made++;
                    }

                // горизонтальные профили (НГП/ГП/СП межэтажной и
                // ортогональной) — прямоугольники по оси
                if (hrails != null)
                    foreach (var ro in hrails)
                    {
                        var r = ro as Dictionary<string, object>;
                        if (r == null) continue;
                        double y = ToD(Get(r, "y")),
                               hx0 = ToD(Get(r, "x0")),
                               hx1 = ToD(Get(r, "x1"));
                        var pl = new Polyline();
                        pl.AddVertexAt(0, new Point2d(hx0, y - railW / 2),
                                       0, 0, 0);
                        pl.AddVertexAt(1, new Point2d(hx1, y - railW / 2),
                                       0, 0, 0);
                        pl.AddVertexAt(2, new Point2d(hx1, y + railW / 2),
                                       0, 0, 0);
                        pl.AddVertexAt(3, new Point2d(hx0, y + railW / 2),
                                       0, 0, 0);
                        pl.Closed = true;
                        pl.Layer = LayerRails;
                        ms.AppendEntity(pl);
                        tr.AddNewlyCreatedDBObject(pl, true);
                        Remember(handlesByRoot, partToRoot,
                                 SafeStr(Get(r, "zone")),
                                 pl.Handle.ToString());
                        made++;
                    }

                // кронштейны — блоки-знаки или образцы Германа
                InsertSigns(tr, ms, brackets, blkMain, blkRow,
                            "несущий", "кронштейн ", LayerBrackets,
                            handlesByRoot, partToRoot, ref made);
                // кляммеры: 4 вида (ТЗ 26.07)
                var clampBlk = new Dictionary<string, ObjectId>
                {
                    { "стартовый", clStart },
                    { "рядовой", clRow },
                    { "боковой", smpClampSide.IsNull
                        ? EnsureClampBlock(tr, db,
                            "AFRAME_КЛЯММЕР_БОК", false, true)
                        : smpClampSide },
                    { "комбинированный", smpClampCombo.IsNull
                        ? EnsureClampBlock(tr, db,
                            "AFRAME_КЛЯММЕР_КОМБИ", true, true)
                        : smpClampCombo },
                };
                var clampDx = new Dictionary<ObjectId, double>();
                if (clamps != null)
                    foreach (var io2 in clamps)
                    {
                        var it = io2 as Dictionary<string, object>;
                        if (it == null) continue;
                        string ck = SafeStr(Get(it, "kind"));
                        string cpid = SafeStr(Get(it, "zone"));
                        ObjectId bid;
                        if (!clampBlk.TryGetValue(ck, out bid))
                            bid = clRow;
                        // знак центрируем по оси профиля: у блоков
                        // Германа база кляммера не в центре (04.08)
                        double cdx;
                        if (!clampDx.TryGetValue(bid, out cdx))
                        {
                            cdx = CenterOffsetX(tr, bid);
                            clampDx[bid] = cdx;
                        }
                        var br2 = new BlockReference(
                            new Point3d(ToD(Get(it, "x")) - cdx,
                                        ToD(Get(it, "y")), 0), bid);
                        br2.Layer = LayerClamps;
                        ms.AppendEntity(br2);
                        tr.AddNewlyCreatedDBObject(br2, true);
                        FillAttrs(tr, br2, ("кляммер " + ck).Trim(),
                                  RootOf(partToRoot, cpid));
                        Remember(handlesByRoot, partToRoot, cpid,
                                 br2.Handle.ToString());
                        made++;
                    }
                // метизы межэтажной: вставки и скобы С1
                InsertSigns(tr, ms, fittings, fitIns, fitSc,
                            "вставка", "", LayerBrackets,
                            handlesByRoot, partToRoot, ref made);

                // метка ATFRAME на объекты зоны / контуры
                foreach (var kv in handlesByRoot)
                {
                    string root = kv.Key;
                    var meta = new Dictionary<string, object>
                    {
                        { "zone_id", root },
                        { "system", sysName },
                        { "floors_y", floors },
                        { "count", kv.Value.Count },
                        { "handles", kv.Value },
                    };
                    string mj = ser.Serialize(meta);
                    List<ObjectId> targets;
                    if (zoneObjs.TryGetValue(root, out targets))
                        foreach (var tid in targets)
                        {
                            var te = (Entity)tr.GetObject(tid,
                                OpenMode.ForWrite);
                            StoreData(tr, te, mj, XKeyFrame);
                        }
                    else
                    {
                        string oh = root.StartsWith("контур ")
                            ? root.Substring(7) : root;
                        ObjectId pid2;
                        if (polyByHandle.TryGetValue(oh, out pid2))
                        {
                            var te = (Entity)tr.GetObject(pid2,
                                OpenMode.ForWrite);
                            StoreData(tr, te, mj, XKeyFrame);
                        }
                    }
                }
                tr.Commit();
            }

            // ── 7. отчёт ──
            var sum = Get(res, "summary") as Dictionary<string, object>;
            ed.WriteMessage("\nATFRAME (" + typKey + "): вертикальных " +
                SafeStr(Get(sum, "rails")) + " (" +
                SafeStr(Get(sum, "rails_lm")) + " м.п., хлыстов ~" +
                SafeStr(Get(sum, "rail_stock_est")) +
                "), горизонтальных " + SafeStr(Get(sum, "hrails")) +
                " (" + SafeStr(Get(sum, "hrails_lm")) + " м.п.)" +
                ", кронштейнов несущих " +
                SafeStr(Get(sum, "brackets_main")) +
                " / рядовых " + SafeStr(Get(sum, "brackets_row")) +
                ", кляммеров " + SafeStr(Get(sum, "clamps_start")) +
                "+" + SafeStr(Get(sum, "clamps_row")) + "+" +
                SafeStr(Get(sum, "clamps_side")) + "+" +
                SafeStr(Get(sum, "clamps_combo")) + ", метизов " +
                SafeStr(Get(sum, "fittings")) +
                (erased > 0 ? "; прежних удалено " + erased : "") + ".");
            PrintCalcReport(ed, Get(res, "calc_report")
                            as Dictionary<string, object>);
            PrintNotes(ed, Get(res, "notes") as object[]);
        }

        // отчёт этапа 4: шаги по расчёту + цепочка проверок с
        // запасами («условие выполнено» — как в статрасчётах
        // «Вектор фасад»)
        private static void PrintCalcReport(Editor ed,
            Dictionary<string, object> rep)
        {
            if (rep == null) return;
            var steps = Get(rep, "steps") as Dictionary<string, object>;
            if (steps != null)
                ed.WriteMessage("\nРАСЧЁТ ШАГОВ (несущая способность): " +
                    "рядовая зона " + SafeStr(Get(steps, "main")) +
                    " / угловая " + SafeStr(Get(steps, "corner")) +
                    " мм.");
            // п.2 (Герман 30.07): подобранный профиль и ЧТО режет шаг.
            // На боевых числах узкое место — анкер, а не сечение:
            // конструктору важно видеть это, иначе он думает, что
            // кронштейнов много из-за слабого профиля.
            var prof = Get(rep, "profile") as Dictionary<string, object>;
            if (prof != null)
                ed.WriteMessage("\n  профиль подобран: рядовая " +
                    SafeStr(Get(prof, "row")) + " / угловая " +
                    SafeStr(Get(prof, "corner")) + ".");
            var bind = Get(rep, "binding") as Dictionary<string, object>;
            if (bind != null)
                foreach (var zk2 in new[] { "row", "corner" })
                {
                    var bo = Get(bind, zk2) as object[];
                    string zn = zk2 == "row" ? "рядовой" : "угловой";
                    if (bo == null || bo.Length < 3)
                    {
                        ed.WriteMessage("\n  в " + zn + " зоне шаг упёрся " +
                            "в конструктивный предел системы.");
                        continue;
                    }
                    ed.WriteMessage("\n  в " + zn + " зоне шаг ограничен: " +
                        SafeStr(bo[0]) + " (" + SafeStr(bo[1]) + " при " +
                        "допустимых " + SafeStr(bo[2]) + ") — увеличить " +
                        "шаг можно только усилив этот узел.");
                }
            foreach (var zk in new[] { "row", "corner" })
            {
                var ch = Get(rep, zk) as Dictionary<string, object>;
                if (ch == null) continue;
                var checks = Get(ch, "checks") as object[];
                if (checks == null) continue;
                var sb = new StringBuilder();
                sb.Append(zk == "row" ? "\n  рядовая (W_p="
                          : "\n  угловая (W_p=");
                sb.Append(SafeStr(Get(ch, "w_p")));
                sb.Append(" кг/м2): ");
                bool first = true;
                foreach (var co in checks)
                {
                    var c = co as Dictionary<string, object>;
                    if (c == null) continue;
                    if (!first) sb.Append("; ");
                    first = false;
                    sb.Append(SafeStr(Get(c, "name")));
                    sb.Append(" ");
                    sb.Append(SafeStr(Get(c, "value")));
                    sb.Append("/");
                    sb.Append(SafeStr(Get(c, "limit")));
                }
                sb.Append(" — условия выполнены.");
                ed.WriteMessage(sb.ToString());
            }
        }

        // вставка блоков-знаков (kind mainKind → блок main, иначе row);
        // МАРКИРОВКА = markPrefix + kind движка («кронштейн несущий»,
        // «вставка», «скоба С1»)
        /// <summary>
        /// Смещение геометрического ЦЕНТРА определения блока по X от
        /// его базовой точки (04.08, замечание Германа: «рядовые
        /// кляммеры рисуются со смещением от центра профиля»).
        ///
        /// В его библиотеке база кляммера стоит НЕ в центре: у
        /// «рядовой!КЛР-1-н» геометрия идёт X 0..112.5 (центр 56.25),
        /// у «стартовый!КЛС-1-н» — X 13.5..99.0 (центр 56.3), а у
        /// углового и комбинированного база уже в центре. На его
        /// полигоне вставки рядовых и стартовых стоят ровно на −56.3
        /// от оси профиля, угловые и комбинированные — на нуле: то
        /// есть он центрирует ЗНАК по оси, компенсируя базу вручную.
        /// Мы ставили блок точкой вставки на ось, поэтому знак уезжал
        /// вправо на пол-ширины. Считаем по КРИВЫМ определения (тексты
        /// и атрибуты в центр знака не входят) и сдвигаем вставку на
        /// −dx. Только по X: по Y у профилей Германа база «низ-центр»
        /// осмысленная, её трогать нельзя.
        /// </summary>
        private static double CenterOffsetX(Transaction tr, ObjectId blkId)
        {
            if (blkId.IsNull) return 0.0;
            try
            {
                var btr = tr.GetObject(blkId, OpenMode.ForRead)
                          as BlockTableRecord;
                if (btr == null) return 0.0;
                double x0 = double.MaxValue, x1 = double.MinValue;
                foreach (ObjectId eid in btr)
                {
                    var ent = tr.GetObject(eid, OpenMode.ForRead)
                              as Entity;
                    if (!(ent is Curve)) continue;
                    try
                    {
                        Extents3d ex = ent.GeometricExtents;
                        if (ex.MinPoint.X < x0) x0 = ex.MinPoint.X;
                        if (ex.MaxPoint.X > x1) x1 = ex.MaxPoint.X;
                    }
                    catch { }
                }
                if (x1 < x0) return 0.0;
                return (x0 + x1) / 2.0;
            }
            catch { return 0.0; }
        }

        private static void InsertSigns(Transaction tr,
            BlockTableRecord ms, object[] items, ObjectId blkA,
            ObjectId blkB, string kindA, string markPrefix, string layer,
            Dictionary<string, List<string>> handlesByRoot,
            Dictionary<string, string> partToRoot, ref int made)
        {
            if (items == null) return;
            foreach (var io in items)
            {
                var it = io as Dictionary<string, object>;
                if (it == null) continue;
                double x = ToD(Get(it, "x")), y = ToD(Get(it, "y"));
                string kind = SafeStr(Get(it, "kind"));
                string pid = SafeStr(Get(it, "zone"));
                bool isA = kind == kindA;
                var br = new BlockReference(new Point3d(x, y, 0),
                                            isA ? blkA : blkB);
                br.Layer = layer;
                // п.12 (Герман 30.07, «знаки разного размера — сбой»):
                // масштаб вставки жёстко 1:1. Если знаки всё равно
                // разной величины — различаются САМИ ОБРАЗЦЫ (блоки
                // нарисованы в разном масштабе), лечится образцами.
                br.ScaleFactors = new Scale3d(1.0, 1.0, 1.0);
                ms.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                FillAttrs(tr, br, (markPrefix + kind).Trim(),
                          RootOf(partToRoot, pid));
                Remember(handlesByRoot, partToRoot, pid,
                         br.Handle.ToString());
                made++;
            }
        }

        // атрибуты знака: ATTDEF'ы определения → ATTRIB'ы вставки
        // (паттерн ABlockGen/AClad, «из текущего представления»);
        // МАРКИРОВКА = вид элемента, ЗАХВАТКА = зона этапа 1 (ответ
        // Дениса 01.08: захватка — участок, обведённый и обсчитанный
        // в ATFZONE; id тот же, что в сводной таблице ATFTABLE, так
        // что ведомость подсистемы по захваткам бьётся с площадями).
        private static void FillAttrs(Transaction tr, BlockReference br,
                                      string mark, string zahv)
        {
            var rbtr = (BlockTableRecord)tr.GetObject(
                br.BlockTableRecord, OpenMode.ForRead);
            if (!rbtr.HasAttributeDefinitions) return;
            foreach (ObjectId aid in rbtr)
            {
                var ad = tr.GetObject(aid, OpenMode.ForRead)
                         as AttributeDefinition;
                if (ad == null || ad.Constant) continue;
                var ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                string tag = ad.Tag.Trim().ToUpperInvariant();
                if (tag == "МАРКИРОВКА") ar.TextString = mark;
                else if (tag == "ЗАХВАТКА") ar.TextString = zahv;
                br.AttributeCollection.AppendAttribute(ar);
                tr.AddNewlyCreatedDBObject(ar, true);
            }
        }

        private static string RootOf(
            Dictionary<string, string> partToRoot, string pid)
        {
            string root;
            return partToRoot.TryGetValue(pid, out root) ? root : pid;
        }

        // ATTDEF «МАРКИРОВКА» и «ЗАХВАТКА» в НАШЕМ определении знака
        // (невидимые — вид знаков не меняется; значения ставит
        // FillAttrs). Старому чертежу недостающие домешиваются;
        // определения ОБРАЗЦОВ (блоки Германа) НЕ правим — чужие
        // блоки, у них заполняются только уже имеющиеся атрибуты.
        private static void EnsureMarkDef(Transaction tr, Database db,
                                          ObjectId btrId)
        {
            var btr = (BlockTableRecord)tr.GetObject(btrId,
                                                     OpenMode.ForRead);
            var have = new HashSet<string>();
            foreach (ObjectId id in btr)
            {
                var ad0 = tr.GetObject(id, OpenMode.ForRead)
                          as AttributeDefinition;
                if (ad0 != null)
                    have.Add(ad0.Tag.Trim().ToUpperInvariant());
            }
            bool up = false;
            foreach (string tag in new[] { "МАРКИРОВКА", "ЗАХВАТКА" })
            {
                if (have.Contains(tag)) continue;
                if (!up) { btr.UpgradeOpen(); up = true; }
                var ad = new AttributeDefinition(Point3d.Origin, "",
                    tag, tag == "МАРКИРОВКА" ? "Маркировка"
                                             : "Захватка", db.Textstyle);
                ad.Invisible = true;
                ad.Height = 35.0;
                ad.Layer = "0";
                btr.AppendEntity(ad);
                tr.AddNewlyCreatedDBObject(ad, true);
            }
        }

        private static void Remember(
            Dictionary<string, List<string>> handlesByRoot,
            Dictionary<string, string> partToRoot, string pid, string h)
        {
            string root;
            if (!partToRoot.TryGetValue(pid, out root)) root = pid;
            if (!handlesByRoot.ContainsKey(root))
                handlesByRoot[root] = new List<string>();
            handlesByRoot[root].Add(h);
        }

        // ── блоки-знаки (В13: свои условные; несущий = реже у Германа
        //    → ромб с прямым крестом; рядовой — квадрат с X; в круге
        //    r75 оба; сущности на слое «0» — наследуют слой вставки) ──

        private static ObjectId EnsureSignBlock(Transaction tr,
            Database db, string name, bool main)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                              OpenMode.ForRead);
            if (bt.Has(name))
            {
                ObjectId ex = bt[name];
                EnsureMarkDef(tr, db, ex);
                return ex;
            }
            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name };
            ObjectId id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            double c = 106.07;   // 75·√2 — ромб = квадрат 150 на 45°
            if (main)
            {
                AddPoly(tr, btr, true, -c, 0, 0, c, c, 0, 0, -c);
                AddPoly(tr, btr, false, -c, 0, c, 0);
                AddPoly(tr, btr, false, 0, c, 0, -c);
            }
            else
            {
                AddPoly(tr, btr, true, -75, -75, 75, -75, 75, 75, -75, 75);
                AddPoly(tr, btr, false, -75, 75, 75, -75);
                AddPoly(tr, btr, false, -75, -75, 75, 75);
            }
            var ci = new Circle(Point3d.Origin, Vector3d.ZAxis, 75.0);
            ci.Layer = "0";
            btr.AppendEntity(ci);
            tr.AddNewlyCreatedDBObject(ci, true);
            EnsureMarkDef(tr, db, id);
            return id;
        }

        private static ObjectId EnsureClampBlock(Transaction tr,
            Database db, string name, bool start, bool second = false)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                              OpenMode.ForRead);
            if (bt.Has(name))
            {
                ObjectId ex = bt[name];
                EnsureMarkDef(tr, db, ex);
                return ex;
            }
            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name };
            ObjectId id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            AddPoly(tr, btr, true, -50, -6, 50, -6, 50, 6, -50, 6);
            if (start)
                AddPoly(tr, btr, false, 0, -6, 0, -40);
            if (second)
                // боковой — ножка вбок; комбинированный — вниз и вбок
                AddPoly(tr, btr, false, 50, 0, 84, 0);
            EnsureMarkDef(tr, db, id);
            return id;
        }

        // метизы: «вставка» (квадрат 40 с диагоналями) и «скоба С1»
        // (Г-уголок) — знаки межэтажной подсистемы
        private static ObjectId EnsureFitBlock(Transaction tr,
            Database db, string name, bool ins)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                              OpenMode.ForRead);
            if (bt.Has(name))
            {
                ObjectId ex = bt[name];
                EnsureMarkDef(tr, db, ex);
                return ex;
            }
            bt.UpgradeOpen();
            var btr = new BlockTableRecord { Name = name };
            ObjectId id = bt.Add(btr);
            tr.AddNewlyCreatedDBObject(btr, true);
            if (ins)
            {
                AddPoly(tr, btr, true, -20, -20, 20, -20, 20, 20,
                        -20, 20);
                AddPoly(tr, btr, false, -20, -20, 20, 20);
                AddPoly(tr, btr, false, -20, 20, 20, -20);
            }
            else
                AddPoly(tr, btr, false, -25, 25, -25, -25, 25, -25);
            EnsureMarkDef(tr, db, id);
            return id;
        }

        private static void AddPoly(Transaction tr, BlockTableRecord btr,
                                    bool closed, params double[] xy)
        {
            var pl = new Polyline();
            for (int i = 0; i < xy.Length / 2; i++)
                pl.AddVertexAt(i, new Point2d(xy[2 * i], xy[2 * i + 1]),
                               0, 0, 0);
            pl.Closed = closed;
            pl.Layer = "0";
            btr.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        // выбор образца-блока; Enter/Esc — ObjectId.Null (условный)
        private static ObjectId PickBlock(Editor ed, Database db,
                                          string prompt)
        {
            var pe = new PromptEntityOptions(prompt);
            pe.SetRejectMessage("\nЭто не вхождение блока.");
            pe.AddAllowedClass(typeof(BlockReference), false);
            var re = ed.GetEntity(pe);
            if (re.Status != PromptStatus.OK) return ObjectId.Null;
            using (var tr0 = db.TransactionManager.StartTransaction())
            {
                var b = (BlockReference)tr0.GetObject(
                    re.ObjectId, OpenMode.ForRead);
                ObjectId id = b.DynamicBlockTableRecord;
                tr0.Commit();
                return id;
            }
        }

        // вставка направляющей динблоком Германа (база низ-центр,
        // 3000): СНАЧАЛА видимость = марка профиля (письмо Германа
        // 01.08: «в названии видимости прописаны чёткие названия
        // направляющих»), ПОТОМ растяжка = Double-свойство с
        // НАИБОЛЬШИМ текущим значением (имена динпараметров у всех
        // свои — эвристика; не нашли/ошибка — null → прямоугольник)
        private static string InsertRailBlock(Transaction tr,
            BlockTableRecord ms, ObjectId btrId, double x, double y0,
            double len, string layer, string profile)
        {
            try
            {
                var br = new BlockReference(
                    new Point3d(x, y0, 0), btrId) { Layer = layer };
                ms.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                // видимость: свойство ищем ПО ЗНАЧЕНИЯМ (в чьих
                // AllowedValues есть марка) — сопоставление
                // нормализованное (регистр/дефисы/лат-кир, Z↔З;
                // «ГП-40-40-1,2» ~ состояние «ГП-40-40»; точное
                // совпадение важнее, из префиксных — кратчайшее,
                // чтобы ШП-60-20 не взял ШП-60-20-20)
                string want = Norm(profile);
                if (want.Length > 0)
                    foreach (DynamicBlockReferenceProperty pr in
                             br.DynamicBlockReferencePropertyCollection)
                    {
                        if (pr.ReadOnly) continue;
                        object[] av;
                        try { av = pr.GetAllowedValues(); }
                        catch { continue; }
                        if (av == null || av.Length == 0) continue;
                        object bestV = null;
                        int bestScore = int.MinValue;
                        foreach (object v in av)
                        {
                            string nv = Norm(SafeStr(v));
                            if (nv.Length == 0) continue;
                            if (nv != want && !want.StartsWith(nv) &&
                                !nv.StartsWith(want)) continue;
                            int score = nv == want
                                ? int.MaxValue : -nv.Length;
                            if (score > bestScore)
                            { bestScore = score; bestV = v; }
                        }
                        if (bestV != null)
                        {
                            try { pr.Value = bestV; } catch { }
                            break;
                        }
                    }
                DynamicBlockReferenceProperty best = null;
                foreach (DynamicBlockReferenceProperty pr in
                         br.DynamicBlockReferencePropertyCollection)
                {
                    if (pr.ReadOnly) continue;
                    if (!(pr.Value is double)) continue;
                    if (best == null ||
                        (double)pr.Value > (double)best.Value)
                        best = pr;
                }
                if (best != null)
                    try { best.Value = len; } catch { }
                return br.Handle.ToString();
            }
            catch { return null; }
        }

        // нормализация марки: верхний регистр, только буквы/цифры,
        // латинские двойники → кириллица (в т.ч. Z→З: «ZП-40-20»
        // Германа = расчётный «ЗП-40-20»)
        private static string Norm(string s)
        {
            if (s == null) return "";
            const string lat = "ABCEHKMOPTXZ";
            const string cyr = "АВСЕНКМОРТХЗ";
            var sb = new StringBuilder();
            foreach (char c0 in s.ToUpperInvariant())
            {
                char c = c0;
                int i = lat.IndexOf(c);
                if (i >= 0) c = cyr[i];
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }

        private static double AskD(Editor ed, string prompt, double def)
        {
            var po = new PromptDoubleOptions("\n" + prompt + ": ")
            { DefaultValue = def, AllowNegative = false };
            var r = ed.GetDouble(po);
            return r.Status == PromptStatus.OK ? r.Value : def;
        }

        // ══ ATFRAMEDIM — размеры между ЦЕНТРАМИ кронштейнов столбца/
        //    ряда (письмо Германа 01.08 п.3, по аналогии с ATCLADDIM).
        //    Центр знака = точка вставки; берутся вставки ТОГО ЖЕ
        //    определения на ТОМ ЖЕ слое в полосе ±100 мм от кликнутого
        //    (минимальный шаг осей на полигоне 225 — не зацепим
        //    соседнюю ось). Размеры цепочкой на слое _РАЗМЕРЫ_ПС ══
        [CommandMethod("ATFRAMEDIM", CommandFlags.Modal)]
        public void RunDim()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            try { RunDimCore(doc); }
            catch (System.Exception ex)
            {
                try
                {
                    doc.Editor.WriteMessage(
                        "\nATFRAMEDIM: внутренняя ошибка — сообщите " +
                        "разработчику.\n" + ex.ToString() + "\n");
                }
                catch { }
            }
        }

        private void RunDimCore(
            Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            var db = doc.Database;
            // 04.08 (Герман): те же два замечания, что и к ATCLADDIM —
            // размеры ставились по ВСЕМУ чертежу и слой был жёстким.
            // Сначала область (Enter — весь чертёж), затем форма с
            // выбором слоя из существующих.
            var area = new HashSet<ObjectId>();
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите область (рамкой) — " +
                                   "или Enter, чтобы взять весь чертёж: "
            };
            var asel = ed.GetSelection(pso, new SelectionFilter(
                new[] { new TypedValue((int)DxfCode.Start, "INSERT") }));
            if (asel.Status == PromptStatus.OK)
                foreach (SelectedObject so in asel.Value)
                    if (so != null) area.Add(so.ObjectId);
            ed.WriteMessage(area.Count > 0
                ? "\nОбласть: {0} вхождений."
                : "\nОбласть не задана — весь чертёж.",
                area.Count);

            var layers = new List<string>();
            using (var trl = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)trl.GetObject(db.LayerTableId,
                                                   OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    var ltr = trl.GetObject(lid, OpenMode.ForRead)
                              as LayerTableRecord;
                    if (ltr != null) layers.Add(ltr.Name);
                }
                trl.Commit();
            }
            layers.Sort(StringComparer.CurrentCultureIgnoreCase);
            bool byCol;
            string dimLayer;
            using (var f = new DimForm("Размеры подсистемы", layers,
                                       "_РАЗМЕРЫ_ПС"))
            {
                // в форме первым пунктом «Ряд» — для подсистемы
                // привычнее столбец, поэтому переставляем выбор
                f.SelectColumnFirst();
                var dr = AcApp.ShowModalDialog(f);
                if (dr != System.Windows.Forms.DialogResult.OK)
                { ed.WriteMessage("\nОтменено."); return; }
                byCol = !f.ByRow;
                dimLayer = f.LayerName;
            }
            if (dimLayer.Length == 0) dimLayer = "_РАЗМЕРЫ_ПС";

            var peo = new PromptEntityOptions(
                "\nУкажите кронштейн (знак): ");
            peo.SetRejectMessage("\nЭто не вхождение блока.");
            peo.AddAllowedClass(typeof(BlockReference), false);
            var pres = ed.GetEntity(peo);
            if (pres.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            const double TOL = 100.0;
            int made = 0;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var smp = (BlockReference)tr.GetObject(pres.ObjectId,
                                                       OpenMode.ForRead);
                ObjectId defId = smp.DynamicBlockTableRecord;
                string layer = smp.Layer;
                Point3d p0 = smp.Position;
                var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                                  OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var pts = new List<Point3d>();
                foreach (ObjectId oid in ms)
                {
                    Entity oe;
                    try
                    {
                        oe = tr.GetObject(oid, OpenMode.ForRead)
                             as Entity;
                    }
                    catch { continue; }
                    var br = oe as BlockReference;
                    if (br == null || br.Layer != layer) continue;
                    if (br.DynamicBlockTableRecord != defId) continue;
                    if (area.Count > 0 && !area.Contains(oid)) continue;
                    Point3d p = br.Position;
                    double d = byCol ? Math.Abs(p.X - p0.X)
                                     : Math.Abs(p.Y - p0.Y);
                    if (d <= TOL) pts.Add(p);
                }
                if (pts.Count < 2)
                {
                    ed.WriteMessage("\nВ этой полосе меньше двух " +
                                    "кронштейнов.");
                    return;
                }
                pts.Sort((a, b) => byCol ? a.Y.CompareTo(b.Y)
                                         : a.X.CompareTo(b.X));
                EnsureLayer(tr, db, dimLayer);
                double baseLo = double.MaxValue;
                foreach (var p in pts)
                    baseLo = Math.Min(baseLo, byCol ? p.X : p.Y);
                double dl = baseLo - 300.0;
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    double dist = byCol ? pts[i + 1].Y - pts[i].Y
                                        : pts[i + 1].X - pts[i].X;
                    if (dist < 1.0) continue;   // задвоенный знак
                    Point3d dlp = byCol
                        ? new Point3d(dl,
                            (pts[i].Y + pts[i + 1].Y) / 2.0, 0)
                        : new Point3d(
                            (pts[i].X + pts[i + 1].X) / 2.0, dl, 0);
                    var dim = new RotatedDimension(
                        byCol ? Math.PI / 2.0 : 0.0,
                        pts[i], pts[i + 1], dlp, null, db.Dimstyle);
                    dim.SetDatabaseDefaults();
                    dim.Layer = dimLayer;
                    ms.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    made++;
                }
                tr.Commit();
            }
            ed.WriteMessage("\nATFRAMEDIM: размеров " + made +
                (byCol ? " (столбец, между центрами)."
                       : " (ряд, между центрами)."));
        }

        // ── прежняя подсистема: хэндлы из метки ATFRAME выбранного ──
        private static void CollectOldHandles(Transaction tr,
            JavaScriptSerializer ser, Entity ent, HashSet<string> into)
        {
            try
            {
                string j = ReadData(tr, ent, XKeyFrame);
                if (j == null) return;
                var d = ser.DeserializeObject(j) as Dictionary<string, object>;
                var hs = Get(d, "handles") as object[];
                if (hs == null) return;
                foreach (var h in hs)
                    into.Add(SafeStr(h));
            }
            catch { }
        }

        // ── <dwg>_fzones.json (копия паттерна AClad — сознательно) ──
        private static Dictionary<string, Dictionary<string, object>>
            LoadFzones(Editor ed, Database db, JavaScriptSerializer ser)
        {
            var map = new Dictionary<string, Dictionary<string, object>>();
            try
            {
                string dwg = db.Filename;
                string dir = string.IsNullOrEmpty(dwg)
                    ? Path.GetTempPath() : Path.GetDirectoryName(dwg);
                string name = string.IsNullOrEmpty(dwg)
                    ? "atfzone" : Path.GetFileNameWithoutExtension(dwg);
                string path = Path.Combine(dir ?? ".", name + "_fzones.json");
                if (!File.Exists(path)) return map;
                var all = ser.DeserializeObject(
                    File.ReadAllText(path, Encoding.UTF8)) as object[];
                if (all == null) return map;
                foreach (var z in all)
                {
                    var zd = z as Dictionary<string, object>;
                    if (zd == null) continue;
                    string id = SafeStr(Get(zd, "id"));
                    if (id.Length > 0) map[id] = zd;
                }
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\n_fzones.json не прочитан: " + ex.Message);
            }
            return map;
        }

        private static List<Dictionary<string, object>> FindZoneParts(
            Dictionary<string, Dictionary<string, object>> fz, string zid)
        {
            var parts = new List<Dictionary<string, object>>();
            Dictionary<string, object> exact;
            if (fz.TryGetValue(zid, out exact)) parts.Add(exact);
            else
                foreach (var kv in fz)
                    if (kv.Key.StartsWith(zid + ".") ||
                        string.Equals(MetaStr(kv.Value, "group"), zid,
                                      StringComparison.Ordinal))
                        parts.Add(kv.Value);
            return parts;
        }

        private static string MetaStr(Dictionary<string, object> zd,
                                      string key)
        {
            var meta = Get(zd, "meta") as Dictionary<string, object>;
            if (meta == null) return null;
            object v = Get(meta, key);
            return v == null ? null : SafeStr(v);
        }

        private static void PrintNotes(Editor ed, object[] notes)
        {
            if (notes == null || notes.Length == 0) return;
            ed.WriteMessage("\nЗамечания:");
            foreach (var n in notes)
                ed.WriteMessage("\n  · " + SafeStr(n));
        }

        // ── Xrecord: JSON чанками ≤250 (контракт общий с AClad) ──

        internal static void StoreData(Transaction tr, Entity ent,
                                       string json, string key)
        {
            if (ent.ExtensionDictionary.IsNull)
                ent.CreateExtensionDictionary();
            var ext = (DBDictionary)tr.GetObject(ent.ExtensionDictionary,
                                                 OpenMode.ForWrite);
            var rb = new ResultBuffer();
            for (int i = 0; i < json.Length; i += 250)
                rb.Add(new TypedValue((int)DxfCode.Text,
                    json.Substring(i, Math.Min(250, json.Length - i))));
            var xr = new Xrecord { Data = rb };
            if (ext.Contains(key))
            {
                var old = (Xrecord)tr.GetObject(ext.GetAt(key),
                                                OpenMode.ForWrite);
                old.Data = rb;
            }
            else
            {
                ext.SetAt(key, xr);
                tr.AddNewlyCreatedDBObject(xr, true);
            }
        }

        internal static string ReadData(Transaction tr, Entity ent,
                                        string key)
        {
            if (ent.ExtensionDictionary.IsNull) return null;
            var ext = (DBDictionary)tr.GetObject(ent.ExtensionDictionary,
                                                 OpenMode.ForRead);
            if (!ext.Contains(key)) return null;
            var xr = (Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForRead);
            if (xr.Data == null) return null;
            var sb = new StringBuilder();
            foreach (TypedValue tv in xr.Data)
                if (tv.TypeCode == (int)DxfCode.Text)
                    sb.Append(SafeStr(tv.Value));
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static void EnsureLayer(Transaction tr, Database db,
                                        string name)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId,
                                              OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var rec = new LayerTableRecord { Name = name };
            lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        // вызов движка через временные файлы (паттерн соседей)
        internal static string CallEngine(string engineExe, string reqJson)
        {
            string tmpIn = Path.Combine(Path.GetTempPath(),
                "aframe_in_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(),
                "aframe_out_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmpIn, reqJson, new UTF8Encoding(false));
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = engineExe,
                    Arguments = "\"" + tmpIn + "\" \"" + tmpOut + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (!File.Exists(tmpOut))
                        throw new ApplicationException(
                            err.Length > 0 ? err
                            : "движок вернул код " + p.ExitCode);
                }
                return File.ReadAllText(tmpOut, Encoding.UTF8);
            }
            finally { TryDelete(tmpIn); TryDelete(tmpOut); }
        }

        private static void TryDelete(string p)
        { try { if (File.Exists(p)) File.Delete(p); } catch { } }

        internal static object Get(Dictionary<string, object> d, string key)
        { object v; return (d != null && d.TryGetValue(key, out v)) ? v : null; }

        internal static bool GetBool(Dictionary<string, object> d, string key)
        { try { return Convert.ToBoolean(Get(d, key)); } catch { return false; } }

        internal static string SafeStr(object o)
        { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }

        private static double ToD(object o)
        {
            try
            {
                return Convert.ToDouble(o, CultureInfo.InvariantCulture);
            }
            catch { return 0.0; }
        }

        private static string F0(double v)
        { return v.ToString("0", CultureInfo.InvariantCulture); }
    }
}
