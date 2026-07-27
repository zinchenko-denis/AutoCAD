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

            // ── 3. система (справочник движка). Классический
            //    конструктор, дефолт по не-OK/пустому — грабля 18.07r:
            //    Keywords.Add/Default роняли компиляцию ──
            var pko = new PromptKeywordOptions(
                "\nСистема подсистемы НВФ [Вектор1/Стандарт/" +
                "Межэтажная] <Стандарт>: ",
                "Вектор1 Стандарт Межэтажная");
            var rk = ed.GetKeywords(pko);
            string sysKey = (rk.Status == PromptStatus.OK &&
                             rk.StringResult != null &&
                             rk.StringResult.Length > 0)
                            ? rk.StringResult : "Стандарт";
            string sysName = sysKey == "Вектор1" ? "Вектор-1"
                : sysKey == "Межэтажная" ? "Межэтажная"
                : "Standart";

            // дефолты для подтверждения (дублируют systems.json —
            // источник истины движок, тут только стартовые значения
            // диалога; расчётный модуль этапа 4 заменит их)
            double stepMain = sysName == "Вектор-1" ? 1200.0 : 800.0;
            double stepCorner = 800.0;
            double railGap = 10.0;
            double startOff = 300.0;
            double cornerZone = sysName == "Вектор-1" ? 608.0 : 605.0;
            bool interFloor = sysName == "Межэтажная";

            var pkc = new PromptKeywordOptions(
                "\nШаги «" + sysName + "»: рядовой " + F0(stepMain) +
                ", угловой " + F0(stepCorner) + ", старт " + F0(startOff) +
                ", зазор стыка " + F0(railGap) + ", угловая зона " +
                F0(cornerZone) + " [Принять/Изменить] <Принять>: ",
                "Принять Изменить");
            var rkc = ed.GetKeywords(pkc);
            var sysOverride = new Dictionary<string, object>
            { { "name", sysName } };
            if (rkc.Status == PromptStatus.OK &&
                rkc.StringResult == "Изменить" && !interFloor)
            {
                stepMain = AskD(ed, "Шаг рядовых кронштейнов, мм",
                                stepMain);
                stepCorner = AskD(ed, "Шаг в угловой зоне, мм",
                                  stepCorner);
                startOff = AskD(ed, "Первый кронштейн от низа стойки, мм",
                                startOff);
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
                { "system", sysOverride },
                { "zones", zonesPayload },
                { "contours", contoursPayload },
                { "joints_x", joints },
                { "floors_y", floors },
                { "rows_y", rowsY },
                { "floor_step", floorStep },
            };
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
            var brackets = Get(res, "brackets") as object[];
            var clamps = Get(res, "clamps") as object[];
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
                ObjectId blkMain = EnsureSignBlock(tr, db, BlkMain, true);
                ObjectId blkRow = EnsureSignBlock(tr, db, BlkRow, false);
                ObjectId clStart = EnsureClampBlock(tr, db, BlkClampStart,
                                                    true);
                ObjectId clRow = EnsureClampBlock(tr, db, BlkClampRow,
                                                  false);

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

                // кронштейны и кляммеры — блоки-знаки
                InsertSigns(tr, ms, brackets, blkMain, blkRow,
                            "несущий", LayerBrackets, handlesByRoot,
                            partToRoot, ref made);
                InsertSigns(tr, ms, clamps, clStart, clRow,
                            "стартовый", LayerClamps, handlesByRoot,
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
            ed.WriteMessage("\nATFRAME: направляющих " +
                SafeStr(Get(sum, "rails")) + " (" +
                SafeStr(Get(sum, "rails_lm")) + " м.п., хлыстов ~" +
                SafeStr(Get(sum, "rail_stock_est")) + "), кронштейнов " +
                "несущих " + SafeStr(Get(sum, "brackets_main")) +
                " / рядовых " + SafeStr(Get(sum, "brackets_row")) +
                ", кляммеров " + SafeStr(Get(sum, "clamps_start")) +
                "+" + SafeStr(Get(sum, "clamps_row")) +
                (erased > 0 ? "; прежних удалено " + erased : "") + ".");
            PrintNotes(ed, Get(res, "notes") as object[]);
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
            Database db, string name, bool start)
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
