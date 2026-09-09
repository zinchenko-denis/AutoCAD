using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(ACladPlugin.TilePatternCommand))]

namespace ACladPlugin
{
    /// <summary>
    /// ATTILE — раскладка МЕЛКОШТУЧНОЙ облицовки (плитка/кирпич) с
    /// перевязкой в шахматку и раскраской по типовому ОБРАЗЦУ-раппорту
    /// из чертежа (модуль AClad, третья команда после ATCLAD/ATCLADDIM).
    ///
    /// Отличие от ATCLAD (кассеты): камень мелкий, много типов-цветов,
    /// рисунок задаёт нарисованный образец (6×10 и т.п.), ряды сдвинуты
    /// на долю модуля, датум — угол ОСНОВНОЙ стены (целые плитки на
    /// стену, подрезка на выступы/откосы). Движок — тот же
    /// clad_engine.exe, op="tile_pattern" (tile_pattern.py). Родилось
    /// из задания Германа 09.09 (бетонная плитка 290×82, руст 7).
    ///
    /// Поток: выбрать образец (крашеные плитки) → размеры плитки и русты
    /// → способ отсчёта датума → выбрать зоны (ATFZONE и/или полилинии)
    /// → движок → крашеные плитки по слоям типов, подрезка отдельно,
    /// полоски &lt;порога на свой слой, сводка. Метка «ATTILE» с
    /// параметрами и хэндлами — на объекты зоны (перегенерация).
    /// </summary>
    public class TilePatternCommand
    {
        internal const string XKeyTile = "ATTILE";
        private const double CloseTol = 0.5;

        [CommandMethod("ATTILE", CommandFlags.Modal | CommandFlags.UsePickSet)]
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
                        "\nATTILE: внутренняя ошибка — сообщите разработчику " +
                        "текст ниже.\n" + ex.ToString() + "\n");
                }
                catch { }
            }
        }

        private void RunCore(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            var db = doc.Database;
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            // ── 1. ОБРАЗЕЦ раскраски: крашеные плитки (полилинии/штриховки).
            //    Тип плитки = слой объекта; цвет типа = цвет слоя. Движок по
            //    сетке образца строит раппорт (ряды снизу вверх, плитки
            //    справа налево, сдвиг рядов). ──
            var psSample = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите ОБРАЗЕЦ раскладки (крашеные " +
                                   "плитки — полилинии/штриховки по цветам): "
            };
            var fSample = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Operator, "<or"),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                new TypedValue((int)DxfCode.Start, "HATCH"),
                new TypedValue((int)DxfCode.Operator, "or>"),
            });
            var selS = ed.GetSelection(psSample, fSample);
            if (selS.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            var sample = new List<object>();
            var typeColor = new Dictionary<string, int>();   // тип → ACI
            var typeTrue = new Dictionary<string, int>();     // тип → RGB (или -1)
            double medW = 0, medH = 0;
            var ws = new List<double>();
            var hs = new List<double>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId,
                                                  OpenMode.ForRead);
                foreach (SelectedObject so in selS.Value)
                {
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead)
                              as Entity;
                    if (ent == null) continue;
                    Extents3d? ext = SafeExtents(ent);
                    if (ext == null) continue;
                    var e = ext.Value;
                    double x0 = e.MinPoint.X, y0 = e.MinPoint.Y,
                           x1 = e.MaxPoint.X, y1 = e.MaxPoint.Y;
                    if (x1 - x0 < 1 || y1 - y0 < 1) continue;
                    string type = ent.Layer;
                    sample.Add(new Dictionary<string, object>
                    { { "x0", x0 }, { "y0", y0 }, { "x1", x1 }, { "y1", y1 },
                      { "type", type } });
                    ws.Add(x1 - x0); hs.Add(y1 - y0);
                    if (!typeColor.ContainsKey(type))
                        RecordTypeColor(tr, lt, ent, type, typeColor, typeTrue);
                }
                tr.Commit();
            }
            if (sample.Count < 2)
            { ed.WriteMessage("\nВ образце меньше двух плиток — нужен " +
                              "нарисованный раппорт."); return; }
            ws.Sort(); hs.Sort();
            medW = ws[ws.Count / 2]; medH = hs[hs.Count / 2];
            ed.WriteMessage("\nОбразец: " + sample.Count + " плиток, типов " +
                            typeColor.Count + " (размер плитки ≈ " +
                            F0(medW) + "×" + F0(medH) + " мм).");

            // ── 2. размеры плитки и русты ──
            var pw = new PromptDoubleOptions("\nШирина плитки, мм: ")
            { DefaultValue = medW >= 1 ? Math.Round(medW) : 290.0,
              AllowNegative = false, AllowZero = false };
            var rw = ed.GetDouble(pw);
            if (rw.Status != PromptStatus.OK) return;
            double tileW = rw.Value;

            var ph = new PromptDoubleOptions("\nВысота плитки, мм: ")
            { DefaultValue = medH >= 1 ? Math.Round(medH) : 82.0,
              AllowNegative = false, AllowZero = false };
            var rh = ed.GetDouble(ph);
            if (rh.Status != PromptStatus.OK) return;
            double tileH = rh.Value;

            var pgv = new PromptDoubleOptions("\nРуст (шов) вертикальный, мм: ")
            { DefaultValue = 7.0, AllowNegative = false };
            var rgv = ed.GetDouble(pgv);
            if (rgv.Status != PromptStatus.OK) return;
            var pgh = new PromptDoubleOptions("\nРуст (шов) горизонтальный, мм: ")
            { DefaultValue = rgv.Value, AllowNegative = false };
            var rgh = ed.GetDouble(pgh);
            if (rgh.Status != PromptStatus.OK) return;

            var pmp = new PromptDoubleOptions(
                "\nПорог тонкой полоски (меньший габарит куска — на свой " +
                "слой, вне счёта плиток), мм: ")
            { DefaultValue = 10.0, AllowNegative = false };
            var rmp = ed.GetDouble(pmp);
            if (rmp.Status != PromptStatus.OK) return;
            double minPiece = rmp.Value;

            // ── 3. датум: угол Стены (целые на стену), Габарит (буквально
            //    справа-снизу) или общая Точка (общий горизонт зон) ──
            var pko = new PromptKeywordOptions(
                "\nОтсчёт раскладки от: ")
            { AllowNone = false };
            pko.Keywords.Add("Стена");
            pko.Keywords.Add("Габарит");
            pko.Keywords.Add("Точка");
            pko.Keywords.Default = "Стена";
            var pk = ed.GetKeywords(pko);
            if (pk.Status != PromptStatus.OK) return;
            string mode = pk.StringResult == "Габарит" ? "bbox"
                        : pk.StringResult == "Точка" ? "point" : "wall";
            var datum = new Dictionary<string, object> { { "mode", mode } };
            if (mode == "point")
            {
                var ppt = ed.GetPoint(
                    "\nОбщая точка отсчёта (правый-низ; её горизонт — низ " +
                    "первого ряда всех зон): ");
                if (ppt.Status != PromptStatus.OK) return;
                datum["x"] = ppt.Value.X;
                datum["y"] = ppt.Value.Y;
            }

            // ── 4. зоны: ATFZONE (штриховки/марки) и/или замкнутые контуры ──
            var psZ = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите ЗОНЫ облицовки (штриховки/марки " +
                                   "ATFZONE и/или замкнутые контуры): "
            };
            var fZ = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Operator, "<or"),
                new TypedValue((int)DxfCode.Start, "HATCH"),
                new TypedValue((int)DxfCode.Start, "MTEXT"),
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                new TypedValue((int)DxfCode.Operator, "or>"),
            });
            var selZ = ed.GetSelection(psZ, fZ);
            if (selZ.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            var zonesPayload = new List<object>();
            var contoursPayload = new List<object>();
            var zoneObjs = new Dictionary<string, List<ObjectId>>();
            var polyByHandle = new Dictionary<string, ObjectId>();
            var oldHandles = new HashSet<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in selZ.Value)
                {
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead)
                              as Entity;
                    if (ent == null) continue;

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
                        var bulges = new List<object>();
                        for (int i = 0; i < n; i++)
                        {
                            Point2d p = pl.GetPoint2dAt(i);
                            pts.Add(new[] { p.X, p.Y });
                            bulges.Add(pl.GetBulgeAt(i));
                        }
                        string h = pl.Handle.ToString();
                        polyByHandle[h] = pl.ObjectId;
                        contoursPayload.Add(new Dictionary<string, object>
                        { { "id", h }, { "pts", pts }, { "bulges", bulges } });
                        CollectOld(tr, ser, ent, oldHandles);
                        continue;
                    }

                    string json = CladCommand.ReadData(tr, ent,
                        CladCommand.XKeyZone);
                    if (json == null) continue;
                    var z = ser.DeserializeObject(json)
                            as Dictionary<string, object>;
                    if (z == null) continue;
                    string zid = CladCommand.SafeStr(CladCommand.Get(z, "zone_id"));
                    if (zid.Length == 0) continue;
                    if (!zoneObjs.ContainsKey(zid))
                    {
                        zoneObjs[zid] = new List<ObjectId>();
                        zonesPayload.Add(new Dictionary<string, object>
                        { { "zone_id", zid }, { "zone", z } });
                    }
                    if (!zoneObjs[zid].Contains(ent.ObjectId))
                        zoneObjs[zid].Add(ent.ObjectId);
                    CollectOld(tr, ser, ent, oldHandles);
                }
                tr.Commit();
            }
            if (zonesPayload.Count == 0 && contoursPayload.Count == 0)
            { ed.WriteMessage("\nНе выбрано ни зон, ни контуров."); return; }

            // ── 5. движок ──
            var payload = new Dictionary<string, object>
            {
                { "op", "tile_pattern" },
                { "tile", new Dictionary<string, object>
                    { { "w", tileW }, { "h", tileH } } },
                { "gap", new Dictionary<string, object>
                    { { "v", rgv.Value }, { "h", rgh.Value } } },
                { "sample", sample },
                { "datum", datum },
                { "min_piece", minPiece },
                { "zones", zonesPayload },
                { "contours", contoursPayload },
            };
            string engineExe = EngineExe();
            Dictionary<string, object> res;
            try
            {
                res = ser.DeserializeObject(
                    CladCommand.CallEngine(engineExe, ser.Serialize(payload)))
                    as Dictionary<string, object>;
            }
            catch (System.Exception ex)
            { ed.WriteMessage("\nОшибка движка: " + ex.Message); return; }
            if (res == null || !CladCommand.GetBool(res, "ok"))
            {
                ed.WriteMessage("\nДвижок отказал: " +
                    CladCommand.SafeStr(CladCommand.Get(res, "error")));
                PrintNotes(ed, CladCommand.Get(res, "notes") as object[]);
                return;
            }
            var pieces = CladCommand.Get(res, "pieces") as object[];
            if (pieces == null || pieces.Length == 0)
            {
                ed.WriteMessage("\nРаскладка пуста (см. замечания).");
                PrintNotes(ed, CladCommand.Get(res, "notes") as object[]);
                return;
            }

            // ── 6. чертёж: удалить прежние плитки, вставить новые ──
            int erased = 0, made = 0;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                                  OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // слои по типам (цвет = из образца) + блоки-плитки целых
                var fullLayer = new Dictionary<string, string>();
                var cutLayer = new Dictionary<string, string>();
                var tinyLayer = new Dictionary<string, string>();
                var blockOfType = new Dictionary<string, ObjectId>();
                foreach (var kv in typeColor)
                {
                    string t = kv.Key;
                    string lf = "Плитка_" + t;
                    string lc = "Плитка_" + t + "_ПОДРЕЗКА";
                    string lt2 = "Плитка_" + t + "_ПОЛОСКИ";
                    fullLayer[t] = MakeLayer(tr, db, lf, kv.Value,
                        typeTrue.ContainsKey(t) ? typeTrue[t] : -1);
                    cutLayer[t] = MakeLayer(tr, db, lc, kv.Value,
                        typeTrue.ContainsKey(t) ? typeTrue[t] : -1);
                    tinyLayer[t] = MakeLayer(tr, db, lt2, kv.Value,
                        typeTrue.ContainsKey(t) ? typeTrue[t] : -1);
                    blockOfType[t] = EnsureTileBlock(tr, db, bt, t, tileW,
                        tileH, fullLayer[t]);
                }

                // прежние плитки этих зон/контуров (перегенерация)
                if (oldHandles.Count > 0)
                    foreach (ObjectId oid in ms)
                    {
                        Entity oe;
                        try { oe = tr.GetObject(oid, OpenMode.ForRead) as Entity; }
                        catch { continue; }
                        if (oe == null) continue;
                        if (!oldHandles.Contains(oe.Handle.ToString())) continue;
                        oe.UpgradeOpen();
                        oe.Erase();
                        erased++;
                    }

                var handlesByZone = new Dictionary<string, List<string>>();
                foreach (var pObj in pieces)
                {
                    var it = pObj as Dictionary<string, object>;
                    if (it == null) continue;
                    string type = CladCommand.SafeStr(CladCommand.Get(it, "type"));
                    string zone = CladCommand.SafeStr(CladCommand.Get(it, "zone"));
                    bool full = CladCommand.GetBool(it, "full");
                    bool tiny = CladCommand.GetBool(it, "tiny");
                    if (!fullLayer.ContainsKey(type)) continue;

                    if (full)
                    {
                        double x = ToD(CladCommand.Get(it, "x")),
                               y = ToD(CladCommand.Get(it, "y"));
                        var br = new BlockReference(new Point3d(x, y, 0),
                                                    blockOfType[type]);
                        br.Layer = fullLayer[type];
                        ms.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        AddToMap(handlesByZone, zone, br.Handle.ToString());
                        made++;
                    }
                    else
                    {
                        string lay = tiny ? tinyLayer[type] : cutLayer[type];
                        // внешнее кольцо куска (Г-кусок у угла окна — тоже
                        // одним замкнутым контуром внешней границы)
                        var ring = RingsOf(it, tileW, tileH)[0];
                        var poly = MakePoly(ring, lay);
                        ms.AppendEntity(poly);
                        tr.AddNewlyCreatedDBObject(poly, true);
                        var hh = new Hatch();
                        ms.AppendEntity(hh);
                        tr.AddNewlyCreatedDBObject(hh, true);
                        hh.SetDatabaseDefaults();
                        hh.Layer = lay;
                        hh.ColorIndex = 256;   // ByLayer
                        hh.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                        hh.Associative = true;
                        hh.AppendLoop(HatchLoopTypes.External,
                            new ObjectIdCollection(new[] { poly.ObjectId }));
                        hh.EvaluateHatch(true);
                        AddToMap(handlesByZone, zone, poly.Handle.ToString());
                        AddToMap(handlesByZone, zone, hh.Handle.ToString());
                        made++;
                    }
                }

                // метка ATTILE: параметры + хэндлы — на объекты зоны/контур
                foreach (var kv in handlesByZone)
                {
                    var meta = new Dictionary<string, object>
                    {
                        { "zone_id", kv.Key },
                        { "tile", new Dictionary<string, object>
                            { { "w", tileW }, { "h", tileH } } },
                        { "gap", new Dictionary<string, object>
                            { { "v", rgv.Value }, { "h", rgh.Value } } },
                        { "datum", datum }, { "min_piece", minPiece },
                        { "pattern", CladCommand.Get(res, "summary") is Dictionary<string, object> sm
                            ? CladCommand.Get(sm, "pattern") : null },
                        { "handles", kv.Value },
                    };
                    string mjson = ser.Serialize(meta);
                    List<ObjectId> targets;
                    if (zoneObjs.TryGetValue(kv.Key, out targets))
                        foreach (var tid in targets)
                            CladCommand.StoreData(tr,
                                (Entity)tr.GetObject(tid, OpenMode.ForWrite),
                                mjson, XKeyTile);
                    else
                    {
                        string oh = kv.Key.StartsWith("контур ")
                            ? kv.Key.Substring(7) : kv.Key;
                        ObjectId pid;
                        if (polyByHandle.TryGetValue(oh, out pid))
                            CladCommand.StoreData(tr,
                                (Entity)tr.GetObject(pid, OpenMode.ForWrite),
                                mjson, XKeyTile);
                    }
                }
                tr.Commit();
            }

            // ── 7. сводка ──
            var sum = CladCommand.Get(res, "summary") as Dictionary<string, object>;
            ed.WriteMessage("\nATTILE: объектов " + made +
                "; целых " + CladCommand.SafeStr(CladCommand.Get(sum, "full")) +
                ", подрезка " + CladCommand.SafeStr(CladCommand.Get(sum, "cut")) +
                ", полосок <" + F0(minPiece) + " " +
                CladCommand.SafeStr(CladCommand.Get(sum, "tiny")) +
                (erased > 0 ? "; прежних удалено " + erased : "") + ".");
            PrintByType(ed, sum);
            PrintNotes(ed, CladCommand.Get(res, "notes") as object[]);
            ed.WriteMessage("\nСпецификацию снять командой ATSPEC по слоям " +
                "«Плитка_*» (целые плитки — блоки по типам).");
        }

        // ── помощники ──

        private static void RecordTypeColor(Transaction tr, LayerTable lt,
            Entity ent, string type, Dictionary<string, int> aci,
            Dictionary<string, int> rgb)
        {
            int a = 7, tc = -1;
            var c = ent.Color;
            if (c != null && c.IsByLayer)
            {
                if (lt.Has(ent.Layer))
                {
                    var lr = tr.GetObject(lt[ent.Layer], OpenMode.ForRead)
                             as LayerTableRecord;
                    if (lr != null)
                    {
                        a = lr.Color.IsByAci ? lr.Color.ColorIndex : 7;
                        if (!lr.Color.IsByAci && !lr.Color.IsByLayer)
                            tc = (lr.Color.Red << 16) | (lr.Color.Green << 8)
                                 | lr.Color.Blue;
                    }
                }
            }
            else if (c != null)
            {
                a = c.IsByAci ? c.ColorIndex : 7;
                if (!c.IsByAci)
                    tc = (c.Red << 16) | (c.Green << 8) | c.Blue;
            }
            aci[type] = a;
            if (tc >= 0) rgb[type] = tc;
        }

        private static string MakeLayer(Transaction tr, Database db,
            string name, int aci, int rgb)
        {
            string ln = CladCommand.LayerName(name);
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            LayerTableRecord rec;
            if (lt.Has(ln))
                rec = (LayerTableRecord)tr.GetObject(lt[ln], OpenMode.ForWrite);
            else
            {
                if (!lt.IsWriteEnabled) lt.UpgradeOpen();
                rec = new LayerTableRecord { Name = ln };
                lt.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
            }
            rec.Color = rgb >= 0
                ? Color.FromRgb((byte)((rgb >> 16) & 255),
                    (byte)((rgb >> 8) & 255), (byte)(rgb & 255))
                : Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
            return ln;
        }

        private static ObjectId EnsureTileBlock(Transaction tr, Database db,
            BlockTable bt, string type, double w, double h, string layer)
        {
            string bn = "Плитка_" + type;
            if (bt.Has(bn)) return bt[bn];
            if (!bt.IsWriteEnabled) bt.UpgradeOpen();
            var rec = new BlockTableRecord { Name = bn, Origin = Point3d.Origin };
            bt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(0, 0), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d(w, 0), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d(w, h), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(0, h), 0, 0, 0);
            pl.Closed = true;
            pl.Layer = "0";
            pl.ColorIndex = 0;   // ByBlock
            rec.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
            var hh = new Hatch();
            rec.AppendEntity(hh);
            tr.AddNewlyCreatedDBObject(hh, true);
            hh.SetDatabaseDefaults();
            hh.Layer = "0";
            hh.ColorIndex = 0;   // ByBlock — цвет даёт слой вставки
            hh.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
            hh.Associative = true;
            hh.AppendLoop(HatchLoopTypes.External,
                new ObjectIdCollection(new[] { pl.ObjectId }));
            hh.EvaluateHatch(true);
            return rec.ObjectId;
        }

        private static Polyline MakePoly(List<double[]> ring, string layer)
        {
            var pl = new Polyline();
            for (int i = 0; i < ring.Count; i++)
                pl.AddVertexAt(i, new Point2d(ring[i][0], ring[i][1]), 0, 0, 0);
            pl.Closed = true;
            pl.Layer = layer;
            pl.ColorIndex = 256;
            return pl;
        }

        private static List<List<double[]>> RingsOf(
            Dictionary<string, object> it, double tileW, double tileH)
        {
            var outp = new List<List<double[]>>();
            var rings = CladCommand.Get(it, "rings") as object[];
            if (rings != null)
            {
                foreach (var rObj in rings)
                {
                    var r = rObj as object[];
                    if (r == null) continue;
                    var ring = new List<double[]>();
                    foreach (var pObj in r)
                    {
                        var p = pObj as object[];
                        if (p == null || p.Length < 2) continue;
                        ring.Add(new[] { ToD(p[0]), ToD(p[1]) });
                    }
                    if (ring.Count >= 3) outp.Add(ring);
                }
            }
            if (outp.Count == 0)
            {
                double x = ToD(CladCommand.Get(it, "x")),
                       y = ToD(CladCommand.Get(it, "y")),
                       w = ToD(CladCommand.Get(it, "w")),
                       hh = ToD(CladCommand.Get(it, "h"));
                if (w <= 0) w = tileW;
                if (hh <= 0) hh = tileH;
                outp.Add(new List<double[]>
                { new[] { x, y }, new[] { x + w, y },
                  new[] { x + w, y + hh }, new[] { x, y + hh } });
            }
            return outp;
        }

        private static Extents3d? SafeExtents(Entity ent)
        {
            try { return ent.GeometricExtents; }
            catch { return null; }
        }

        private static void CollectOld(Transaction tr, JavaScriptSerializer ser,
            Entity ent, HashSet<string> into)
        {
            string json = CladCommand.ReadData(tr, ent, XKeyTile);
            if (json == null) return;
            var m = ser.DeserializeObject(json) as Dictionary<string, object>;
            var hs = CladCommand.Get(m, "handles") as object[];
            if (hs == null) return;
            foreach (var h in hs) into.Add(CladCommand.SafeStr(h));
        }

        private static void AddToMap(Dictionary<string, List<string>> m,
            string key, string val)
        {
            if (!m.ContainsKey(key)) m[key] = new List<string>();
            m[key].Add(val);
        }

        private static void PrintByType(Editor ed, Dictionary<string, object> sum)
        {
            var bt = CladCommand.Get(sum, "by_type") as Dictionary<string, object>;
            if (bt == null) return;
            foreach (var kv in bt)
            {
                var d = kv.Value as Dictionary<string, object>;
                if (d == null) continue;
                ed.WriteMessage("\n  " + kv.Key + ": целых " +
                    CladCommand.SafeStr(CladCommand.Get(d, "full")) +
                    ", подрезка " + CladCommand.SafeStr(CladCommand.Get(d, "cut")) +
                    ", заготовок " + CladCommand.SafeStr(CladCommand.Get(d, "blanks")));
            }
        }

        private static void PrintNotes(Editor ed, object[] notes)
        {
            if (notes == null) return;
            foreach (var n in notes)
                ed.WriteMessage("\n  · " + CladCommand.SafeStr(n));
        }

        private static string EngineExe()
        {
            string baseDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? ".";
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(
                baseDir, "..", "engine", "clad_engine.exe"));
        }

        private static double ToD(object o)
        {
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return 0.0; }
        }

        private static string F0(double v)
        { return v.ToString("0", CultureInfo.InvariantCulture); }
    }
}
