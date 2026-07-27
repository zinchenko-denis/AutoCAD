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
                if (metas == 0)
                {
                    if (oldMeta > 0)
                        ed.WriteMessage("\nМетки ATCLAD старой сборки " +
                            "(без осей рустов) — перегенерируйте " +
                            "раскладку ATCLAD новой сборкой и повторите.");
                    else
                        ed.WriteMessage("\nСреди выбранного нет ни " +
                            "одной метки ATCLAD — сначала раскладка.");
                    return;
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
                calcDict = new Dictionary<string, object>
                {
                    { "wind_region", windReg },
                    { "terrain", terr },
                    { "height", hgt },
                    { "q_clad", qcl },
                    { "offset", off },
                    { "na_max", na },
                };
                if (interFloor)
                    calcDict["b_corner"] = AskD(ed, "Шаг направляющих " +
                        "в угловой зоне, мм", 450.0);
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

                var pkc = new PromptKeywordOptions(
                    "\nШаги «" + typKey + "»: расчётный " + F0(stepMain) +
                    ", угловой " + F0(stepCorner) + ", старт " +
                    F0(startOff) + ", зазор стыка " + F0(railGap) +
                    ", угловая зона " + F0(cornerZone) +
                    " [Принять/Изменить] <Принять>: ",
                    "Принять Изменить");
                var rkc = ed.GetKeywords(pkc);
                if (rkc.Status == PromptStatus.OK &&
                    rkc.StringResult == "Изменить")
                {
                    stepMain = AskD(ed, "Расчётный шаг кронштейнов, мм",
                                    stepMain);
                    stepCorner = AskD(ed, "Шаг в угловой зоне, мм",
                                      stepCorner);
                    startOff = AskD(ed, "Первый кронштейн от низа " +
                                        "стойки, мм", startOff);
                    railGap = AskD(ed, "Зазор стыка направляющих, мм",
                                   railGap);
                    cornerZone = AskD(ed, "Ширина угловой зоны, мм",
                                      cornerZone);
                    sysOverride["bracket_step"] = stepMain;
                    sysOverride["bracket_step_corner"] = stepCorner;
                    sysOverride["bracket_start_offset"] = startOff;
                    sysOverride["rail_gap"] = railGap;
                    sysOverride["corner_zone"] = cornerZone;
                }
            }

            // ── 3а. знаки: условные или ОБРАЗЦЫ боевых блоков
            //    Германа с чертежа (ТЗ 26.07 п.3: тип и длина
            //    кронштейна в имени блока, «Кронш КР1-70-100») ──
            ObjectId smpMain = ObjectId.Null, smpRow = ObjectId.Null;
            var pks = new PromptKeywordOptions(
                "\nЗнаки кронштейнов [Условные/Образцы] <Условные>: ",
                "Условные Образцы");
            var rks = ed.GetKeywords(pks);
            if (rks.Status == PromptStatus.OK &&
                rks.StringResult == "Образцы")
            {
                var pe1 = new PromptEntityOptions(
                    "\nОбразец НЕСУЩЕГО кронштейна: ");
                pe1.SetRejectMessage("\nЭто не вхождение блока.");
                pe1.AddAllowedClass(typeof(BlockReference), false);
                var re1 = ed.GetEntity(pe1);
                var pe2 = new PromptEntityOptions(
                    "\nОбразец ОПОРНОГО (рядового) кронштейна: ");
                pe2.SetRejectMessage("\nЭто не вхождение блока.");
                pe2.AddAllowedClass(typeof(BlockReference), false);
                var re2 = ed.GetEntity(pe2);
                if (re1.Status == PromptStatus.OK &&
                    re2.Status == PromptStatus.OK)
                    using (var tr0 = db.TransactionManager
                           .StartTransaction())
                    {
                        var b1 = (BlockReference)tr0.GetObject(
                            re1.ObjectId, OpenMode.ForRead);
                        var b2 = (BlockReference)tr0.GetObject(
                            re2.ObjectId, OpenMode.ForRead);
                        smpMain = b1.DynamicBlockTableRecord;
                        smpRow = b2.DynamicBlockTableRecord;
                        tr0.Commit();
                    }
                else
                    ed.WriteMessage("\nОбразцы не указаны — будут " +
                                    "условные знаки.");
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
            };
            if (calcDict != null) payload["calc"] = calcDict;
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
                ObjectId clStart = EnsureClampBlock(tr, db, BlkClampStart,
                                                    true);
                ObjectId clRow = EnsureClampBlock(tr, db, BlkClampRow,
                                                  false);
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
                            "несущий", LayerBrackets, handlesByRoot,
                            partToRoot, ref made);
                // кляммеры: 4 вида (ТЗ 26.07)
                var clampBlk = new Dictionary<string, ObjectId>
                {
                    { "стартовый", clStart },
                    { "рядовой", clRow },
                    { "боковой", EnsureClampBlock(tr, db,
                        "AFRAME_КЛЯММЕР_БОК", false, true) },
                    { "комбинированный", EnsureClampBlock(tr, db,
                        "AFRAME_КЛЯММЕР_КОМБИ", true, true) },
                };
                if (clamps != null)
                    foreach (var io2 in clamps)
                    {
                        var it = io2 as Dictionary<string, object>;
                        if (it == null) continue;
                        ObjectId bid;
                        if (!clampBlk.TryGetValue(
                                SafeStr(Get(it, "kind")), out bid))
                            bid = clRow;
                        var br2 = new BlockReference(
                            new Point3d(ToD(Get(it, "x")),
                                        ToD(Get(it, "y")), 0), bid);
                        br2.Layer = LayerClamps;
                        ms.AppendEntity(br2);
                        tr.AddNewlyCreatedDBObject(br2, true);
                        Remember(handlesByRoot, partToRoot,
                                 SafeStr(Get(it, "zone")),
                                 br2.Handle.ToString());
                        made++;
                    }
                // метизы межэтажной: вставки и скобы С1
                InsertSigns(tr, ms, fittings, fitIns, fitSc,
                            "вставка", LayerBrackets, handlesByRoot,
                            partToRoot, ref made);

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

        // вставка блоков-знаков (kind mainKind → блок main, иначе row)
        private static void InsertSigns(Transaction tr,
            BlockTableRecord ms, object[] items, ObjectId blkA,
            ObjectId blkB, string kindA, string layer,
            Dictionary<string, List<string>> handlesByRoot,
            Dictionary<string, string> partToRoot, ref int made)
        {
            if (items == null) return;
            foreach (var io in items)
            {
                var it = io as Dictionary<string, object>;
                if (it == null) continue;
                double x = ToD(Get(it, "x")), y = ToD(Get(it, "y"));
                bool isA = SafeStr(Get(it, "kind")) == kindA;
                var br = new BlockReference(new Point3d(x, y, 0),
                                            isA ? blkA : blkB);
                br.Layer = layer;
                ms.AppendEntity(br);
                tr.AddNewlyCreatedDBObject(br, true);
                Remember(handlesByRoot, partToRoot,
                         SafeStr(Get(it, "zone")), br.Handle.ToString());
                made++;
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
            if (bt.Has(name)) return bt[name];
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
            return id;
        }

        private static ObjectId EnsureClampBlock(Transaction tr,
            Database db, string name, bool start, bool second = false)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                              OpenMode.ForRead);
            if (bt.Has(name)) return bt[name];
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
            return id;
        }

        // метизы: «вставка» (квадрат 40 с диагоналями) и «скоба С1»
        // (Г-уголок) — знаки межэтажной подсистемы
        private static ObjectId EnsureFitBlock(Transaction tr,
            Database db, string name, bool ins)
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                              OpenMode.ForRead);
            if (bt.Has(name)) return bt[name];
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

        private static double AskD(Editor ed, string prompt, double def)
        {
            var po = new PromptDoubleOptions("\n" + prompt + ": ")
            { DefaultValue = def, AllowNegative = false };
            var r = ed.GetDouble(po);
            return r.Status == PromptStatus.OK ? r.Value : def;
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
