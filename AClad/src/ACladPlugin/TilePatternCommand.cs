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
    /// ATTILE — УНИВЕРСАЛЬНАЯ раскладка облицовки с разбежкой (23.09:
    /// заявка «шахматный порядок для любой облицовки» — керамогранит любых
    /// форматов, фиброцемент/HPL, камень, клинкер/кирпич). Выросла из
    /// мелкоштучной раскладки по образцу 09.09 (плитка 290×82): образец
    /// теперь — одна из опций (раскраска и/или сдвиги рядов), остальное
    /// задаётся в окне BondForm: формат и швы, ряды/столбцы, смещение
    /// (через ряд / лесенкой / своя последовательность, доли модуля или
    /// мм), точка отсчёта (9 точек габарита или основной стены, общая
    /// точка), руст вокруг проёмов, Г-куски, пороги, пропил.
    ///
    /// Не путать с ATCLAD: там правила ТЗ Германа 22.07 (целая по центру
    /// простенка, сдвиг на полплиты при подрезке &lt;300) — они несовместимы
    /// с регулярной перевязкой, где положение шва задаёт сетка. Движок —
    /// тот же clad_engine.exe, op="tile_pattern".
    ///
    /// Поток: зоны (ATFZONE и/или полилинии) → окно параметров (заполнено
    /// с прошлой раскладки этой зоны или последними значениями) → замена
    /// раскладки ATCLAD на этих зонах (если есть) → образец (если нужен) →
    /// общая точка (если выбрана) → движок → чертёж: целые — блоки,
    /// подрезка — контуры (или всё — динамическим блоком чертежа с
    /// «ширина»/«высота», как ATCLAD), малая подрезка — красные контуры
    /// на отдельном слое; метка «ATTILE» с параметрами, хэндлами и осями
    /// швов (joints_x/rows_y — их читает ATFRAME) — на объекты зоны.
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

            // ── 1. зоны: ATFZONE (штриховки/марки) и/или замкнутые контуры ──
            var psZ = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите ЗОНЫ облицовки (штриховки/марки " +
                                   "ATFZONE и/или замкнутые контуры; контур внутри " +
                                   "контура — проём): "
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
            var cladHandles = new HashSet<string>();
            var cladOwners = new List<ObjectId>();
            Dictionary<string, object> prevSettings = null;
            // 23.09b: прежние точки принудительных рустов — из метки ATTILE,
            // иначе из метки ATCLAD (переход с ATCLAD на ATTILE без повторных кликов)
            object[] prevV = null, prevH = null, cladV = null, cladH = null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in selZ.Value)
                {
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    bool isZone = false;
                    var pl = ent as Polyline;
                    if (pl != null)
                    {
                        int n = pl.NumberOfVertices;
                        if (n < 3) continue;
                        bool closed = pl.Closed ||
                            pl.GetPoint2dAt(0).GetDistanceTo(pl.GetPoint2dAt(n - 1)) <= CloseTol;
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
                        isZone = true;
                    }
                    else
                    {
                        string json = CladCommand.ReadData(tr, ent, CladCommand.XKeyZone);
                        if (json == null) continue;
                        var z = ser.DeserializeObject(json) as Dictionary<string, object>;
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
                        isZone = true;
                    }
                    if (!isZone) continue;
                    CollectOld(tr, ser, ent, oldHandles);
                    if (prevSettings == null)
                    {
                        var m = ReadMeta(tr, ser, ent, XKeyTile);
                        prevSettings = CladCommand.Get(m, "settings") as Dictionary<string, object>;
                        if (prevSettings != null)
                        {
                            prevV = CladCommand.Get(m, "vjoints") as object[];
                            prevH = CladCommand.Get(m, "hjoints") as object[];
                        }
                    }
                    var cm = ReadMeta(tr, ser, ent, CladCommand.XKeyClad);
                    if (cladV == null && cm != null)
                    {
                        cladV = CladCommand.Get(cm, "vjoints") as object[];
                        cladH = CladCommand.Get(cm, "hjoints") as object[];
                    }
                    var ch = CladCommand.Get(cm, "handles") as object[];
                    if (ch != null)
                    {
                        foreach (var hh in ch) cladHandles.Add(CladCommand.SafeStr(hh));
                        cladOwners.Add(ent.ObjectId);
                    }
                }
                tr.Commit();
            }
            if (zonesPayload.Count == 0 && contoursPayload.Count == 0)
            { ed.WriteMessage("\nНе выбрано ни зон, ни контуров."); return; }

            // ── 2. параметры — окно ──
            BondSettings st = prevSettings != null ? BondSettings.FromDict(prevSettings)
                                                   : BondSettings.LoadLast();
            if (prevSettings != null)
                ed.WriteMessage("\nПараметры — с прошлой раскладки этой зоны.");
            var layerNames = new List<string>();
            var dynBlocks = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in lt)
                {
                    var r = tr.GetObject(lid, OpenMode.ForRead) as LayerTableRecord;
                    if (r != null) layerNames.Add(r.Name);
                }
                var bt0 = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId bid in bt0)
                {
                    var r = tr.GetObject(bid, OpenMode.ForRead) as BlockTableRecord;
                    if (r == null || r.IsLayout || r.IsAnonymous || r.IsFromExternalReference ||
                        r.IsFromOverlayReference || !r.IsDynamicBlock) continue;
                    dynBlocks.Add(r.Name);
                }
                tr.Commit();
            }
            layerNames.Sort(StringComparer.CurrentCultureIgnoreCase);
            dynBlocks.Sort(StringComparer.CurrentCultureIgnoreCase);
            if (st.Element.Length > 0 && !dynBlocks.Contains(st.Element)) st.Element = "";
            using (var f = new BondForm(st, layerNames, dynBlocks))
            {
                var dr = AcApp.ShowModalDialog(f);
                if (dr != System.Windows.Forms.DialogResult.OK)
                { ed.WriteMessage("\nОтменено."); return; }
                st = f.Result;
            }
            st.SaveLast();

            // ── 3. раскладка ATCLAD на этих зонах — заменить ──
            if (cladHandles.Count > 0)
            {
                var pk = new PromptKeywordOptions(
                    "\nНа выбранных зонах лежит раскладка ATCLAD (" + cladHandles.Count +
                    " камней) — заменить её? ") { AllowNone = false };
                pk.Keywords.Add("Заменить");
                pk.Keywords.Add("Отмена");
                pk.Keywords.Default = "Заменить";
                var rk = ed.GetKeywords(pk);
                if (rk.Status != PromptStatus.OK || rk.StringResult == "Отмена")
                { ed.WriteMessage("\nОтменено."); return; }
            }

            // ── 4. образец: раскраска и/или сдвиги рядов ──
            bool useSample = st.ColorsBySample || st.Kind == "pattern";
            bool multi = st.ColorsBySample;
            List<object> sample = null;
            var typeColor = new Dictionary<string, int>();
            var typeTrue = new Dictionary<string, int>();
            if (useSample)
            {
                var psS = new PromptSelectionOptions
                {
                    MessageForAdding = "\nВыберите ОБРАЗЕЦ (плитки — полилинии/штриховки; " +
                                       "тип = слой, цвет = цвет слоя): "
                };
                var fS = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Operator, "<or"),
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.Start, "HATCH"),
                    new TypedValue((int)DxfCode.Operator, "or>"),
                });
                var selS = ed.GetSelection(psS, fS);
                if (selS.Status != PromptStatus.OK)
                { ed.WriteMessage("\nОтменено."); return; }
                sample = new List<object>();
                var ws = new List<double>();
                var hs = new List<double>();
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    foreach (SelectedObject so in selS.Value)
                    {
                        var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        Extents3d? ext = SafeExtents(ent);
                        if (ext == null) continue;
                        var e = ext.Value;
                        double x0 = e.MinPoint.X, y0 = e.MinPoint.Y,
                               x1 = e.MaxPoint.X, y1 = e.MaxPoint.Y;
                        if (x1 - x0 < 1 || y1 - y0 < 1) continue;
                        // без раскраски — один тип (образец задаёт только сдвиги)
                        string type = multi ? ent.Layer : st.Name;
                        sample.Add(new Dictionary<string, object>
                        { { "x0", x0 }, { "y0", y0 }, { "x1", x1 }, { "y1", y1 },
                          { "type", type } });
                        ws.Add(x1 - x0); hs.Add(y1 - y0);
                        if (multi && !typeColor.ContainsKey(type))
                            RecordTypeColor(tr, lt, ent, type, typeColor, typeTrue);
                    }
                    tr.Commit();
                }
                if (sample.Count < 2)
                { ed.WriteMessage("\nВ образце меньше двух плиток — нужен нарисованный раппорт."); return; }
                ws.Sort(); hs.Sort();
                double medW = ws[ws.Count / 2], medH = hs[hs.Count / 2];
                ed.WriteMessage("\nОбразец: " + sample.Count + " плиток" +
                    (multi ? ", типов " + typeColor.Count : "") + " (плитка ≈ " +
                    F0(medW) + "×" + F0(medH) + " мм).");
                if (Math.Abs(medW - st.W) > 2 || Math.Abs(medH - st.H) > 2)
                    ed.WriteMessage("\n  ! размер плиток образца отличается от формата окна (" +
                        F0(st.W) + "×" + F0(st.H) + ") — раскладка идёт по формату окна.");
            }

            // ── 5. общая точка ──
            var payload = st.ToEngine();
            if (st.CommonPoint)
            {
                var ppt = ed.GetPoint("\nОбщая точка отсчёта (угол/середина целой плитки " +
                                      "базового ряда, как выбрано в окне): ");
                if (ppt.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }
                var an = (Dictionary<string, object>)payload["anchor"];
                an["point"] = new Dictionary<string, object>
                { { "x", ppt.Value.X }, { "y", ppt.Value.Y } };
            }

            // ── 5б. принудительные русты (Герман 23.09: «как в ATCLAD») ──
            //    ось вертикального руста / НИЗ горизонтального (ТЗ 2.5/2.6,
            //    Г3); за рустом раскладка начинается заново от руста
            var vjoints = new List<object>();
            var hjoints = new List<object>();
            if (st.ForcedV && !AskRusts(ed, true, NonEmpty(prevV) ?? NonEmpty(cladV), vjoints)) return;
            if (st.ForcedH && !AskRusts(ed, false, NonEmpty(prevH) ?? NonEmpty(cladH), hjoints)) return;
            if (vjoints.Count > 0) payload["vjoints"] = vjoints;
            if (hjoints.Count > 0) payload["hjoints"] = hjoints;

            // ── 6. движок ──
            payload["op"] = "tile_pattern";
            if (sample != null) payload["sample"] = sample;
            else payload["types"] = new List<object> { st.Name };
            payload["zones"] = zonesPayload;
            payload["contours"] = contoursPayload;
            Dictionary<string, object> res;
            try
            {
                res = ser.DeserializeObject(
                    CladCommand.CallEngine(EngineExe(), ser.Serialize(payload)))
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

            // ── 7. чертёж ──
            int erased = 0, made = 0, dynFail = 0, smallMade = 0;
            bool dyn = st.Element.Length > 0;
            var handlesByZone = new Dictionary<string, List<string>>();
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace],
                                                        OpenMode.ForWrite);
                string dynW = null, dynH = null;
                ObjectId dynDef = ObjectId.Null;
                if (dyn)
                {
                    if (!bt.Has(st.Element))
                    { ed.WriteMessage("\nБлок «" + st.Element + "» не найден в чертеже."); return; }
                    dynDef = bt[st.Element];
                    if (!ProbeDyn(tr, ms, dynDef, out dynW, out dynH))
                    {
                        ed.WriteMessage("\nУ блока «" + st.Element + "» нет динпараметров " +
                            "«ширина»/«высота» — подрезку им не показать. Выберите другой " +
                            "блок или «Прямоугольник».");
                        return;
                    }
                }

                // слои и блоки по типам
                var types = new List<string>();
                foreach (var itObj in pieces)
                {
                    var it = itObj as Dictionary<string, object>;
                    string t = CladCommand.SafeStr(CladCommand.Get(it, "type"));
                    if (!types.Contains(t)) types.Add(t);
                }
                var fullLayer = new Dictionary<string, string>();
                var cutLayer = new Dictionary<string, string>();
                var blockOf = new Dictionary<string, ObjectId>();
                foreach (var t in types)
                {
                    int aci = typeColor.ContainsKey(t) ? typeColor[t] : 7;
                    int rgb = typeTrue.ContainsKey(t) ? typeTrue[t] : -1;
                    bool keep = !multi;   // один тип — цвет существующего слоя не трогаем
                    fullLayer[t] = MakeLayer(tr, db, st.LayerFor(t, multi), aci, rgb, keep);
                    cutLayer[t] = dyn ? fullLayer[t]
                        : MakeLayer(tr, db, st.CutLayerFor(t, multi), multi ? aci : 30, rgb, keep);
                    if (!dyn)
                        blockOf[t] = EnsureTileBlock(tr, db, bt, st.BlockFor(t, multi),
                                                     st.W, st.H, multi);
                }
                string smallLayer = MakeLayer(tr, db, st.SmallLayer(), 1, -1, true);

                // прежняя раскладка ATTILE этих зон (перегенерация) и ATCLAD (замена)
                erased += EraseByHandles(tr, db, oldHandles);
                if (cladHandles.Count > 0)
                {
                    erased += EraseByHandles(tr, db, cladHandles);
                    foreach (var oid in cladOwners)
                    {
                        var te = tr.GetObject(oid, OpenMode.ForWrite) as Entity;
                        if (te != null) CladCommand.RemoveData(tr, te, CladCommand.XKeyClad);
                    }
                }

                foreach (var itObj in pieces)
                {
                    var it = itObj as Dictionary<string, object>;
                    if (it == null) continue;
                    string type = CladCommand.SafeStr(CladCommand.Get(it, "type"));
                    string zone = CladCommand.SafeStr(CladCommand.Get(it, "zone"));
                    bool full = CladCommand.GetBool(it, "full");
                    bool small = CladCommand.GetBool(it, "small");
                    if (!fullLayer.ContainsKey(type)) continue;
                    double x = ToD(CladCommand.Get(it, "x")), y = ToD(CladCommand.Get(it, "y")),
                           w = ToD(CladCommand.Get(it, "w")), h = ToD(CladCommand.Get(it, "h"));
                    var rings = RingsOf(it, st.W, st.H);
                    bool shapedPiece = CladCommand.Get(it, "rings") != null;
                    if (dyn && !shapedPiece)
                    {
                        var br = new BlockReference(new Point3d(x, y, 0), dynDef);
                        br.Layer = fullLayer[type];
                        ms.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        bool okW = false, okH = false;
                        foreach (DynamicBlockReferenceProperty pr in
                                 br.DynamicBlockReferencePropertyCollection)
                        {
                            if (pr.ReadOnly) continue;
                            if (pr.PropertyName == dynW) okW = CladCommand.TrySetNum(pr, w);
                            else if (pr.PropertyName == dynH) okH = CladCommand.TrySetNum(pr, h);
                        }
                        if (!okW || !okH) dynFail++;
                        FillAttributes(tr, br, zone);
                        AddToMap(handlesByZone, zone, br.Handle.ToString());
                        made++;
                    }
                    else if (full)
                    {
                        var br = new BlockReference(new Point3d(x, y, 0), blockOf[type]);
                        br.Layer = fullLayer[type];
                        ms.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        AddToMap(handlesByZone, zone, br.Handle.ToString());
                        made++;
                    }
                    else
                    {
                        string lay = cutLayer[type];
                        var outer = MakePoly(rings[0], lay);
                        ms.AppendEntity(outer);
                        tr.AddNewlyCreatedDBObject(outer, true);
                        AddToMap(handlesByZone, zone, outer.Handle.ToString());
                        var inner = new ObjectIdCollection();
                        for (int k = 1; k < rings.Count; k++)
                        {
                            var ip = MakePoly(rings[k], lay);
                            ms.AppendEntity(ip);
                            tr.AddNewlyCreatedDBObject(ip, true);
                            inner.Add(ip.ObjectId);
                            AddToMap(handlesByZone, zone, ip.Handle.ToString());
                        }
                        if (multi)
                        {
                            var hh = new Hatch();
                            ms.AppendEntity(hh);
                            tr.AddNewlyCreatedDBObject(hh, true);
                            hh.SetDatabaseDefaults();
                            hh.Layer = lay;
                            hh.ColorIndex = 256;
                            hh.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                            hh.Associative = true;
                            hh.AppendLoop(HatchLoopTypes.External,
                                new ObjectIdCollection(new[] { outer.ObjectId }));
                            foreach (ObjectId iid in inner)
                                hh.AppendLoop(HatchLoopTypes.Default,
                                    new ObjectIdCollection(new[] { iid }));
                            hh.EvaluateHatch(true);
                            AddToMap(handlesByZone, zone, hh.Handle.ToString());
                        }
                        made++;
                    }
                    if (small)
                    {
                        var sp = MakePoly(rings[0], smallLayer);
                        sp.ConstantWidth = Math.Max(2.0, Math.Min(st.W, st.H) * 0.01);
                        ms.AppendEntity(sp);
                        tr.AddNewlyCreatedDBObject(sp, true);
                        AddToMap(handlesByZone, zone, sp.Handle.ToString());
                        smallMade++;
                    }
                }

                // метка ATTILE — на объекты КАЖДОГО члена зоны (объединённые
                // «A+B» тоже: грабля 09.09 — метка терялась, повтор задваивал)
                var perZone = CladCommand.Get(res, "per_zone") as object[];
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                if (perZone != null)
                    foreach (var pzObj in perZone)
                    {
                        var pz = pzObj as Dictionary<string, object>;
                        if (pz == null) continue;
                        string zid = CladCommand.SafeStr(CladCommand.Get(pz, "zone_id"));
                        List<string> hl;
                        if (!handlesByZone.TryGetValue(zid, out hl)) hl = new List<string>();
                        var meta = new Dictionary<string, object>
                        {
                            { "zone_id", zid },
                            { "members", CladCommand.Get(pz, "members") },
                            { "settings", st.ToDict() },
                            { "cladding", st.Name },
                            { "tile", new Dictionary<string, object> { { "w", st.W }, { "h", st.H } } },
                            { "gap", new Dictionary<string, object> { { "v", st.Gv }, { "h", st.Gh } } },
                            { "layer", st.LayerFor("", false) },
                            { "block", dyn ? st.Element : st.BlockFor("", false) },
                            // мост к ATFRAME: оси вертикальных швов (стойки) и
                            // центры горизонтальных (кляммеры)
                            { "joints_x", CladCommand.Get(pz, "joints_x") },
                            { "rows_y", CladCommand.Get(pz, "rows_y") },
                            { "tiles", hl.Count },
                            { "handles", hl },
                            { "stamp", stamp },
                            { "vjoints", vjoints },
                            { "hjoints", hjoints },
                        };
                        string mjson = ser.Serialize(meta);
                        var mem = CladCommand.Get(pz, "members") as object[];
                        var targets = new List<ObjectId>();
                        foreach (var mObj in mem ?? new object[] { zid })
                            foreach (var oid in OwnersOf(CladCommand.SafeStr(mObj), zoneObjs, polyByHandle))
                                if (!targets.Contains(oid)) targets.Add(oid);
                        foreach (var oid in targets)
                            CladCommand.StoreData(tr, (Entity)tr.GetObject(oid, OpenMode.ForWrite),
                                                  mjson, XKeyTile);
                    }
                tr.Commit();
            }

            // ── 8. сводка ──
            var sum = CladCommand.Get(res, "summary") as Dictionary<string, object>;
            var absorbed = CladCommand.Get(sum, "absorbed") as Dictionary<string, object>;
            var bond = CladCommand.Get(sum, "bond") as Dictionary<string, object>;
            ed.WriteMessage("\nATTILE: «" + st.Name + "» " + F0(st.W) + "×" + F0(st.H) +
                ", швы " + F1(st.Gv) + "/" + F1(st.Gh) + " мм; " +
                (st.Axis == "cols" ? "столбцы" : "ряды") + " — " +
                CladCommand.SafeStr(CladCommand.Get(bond, "text")) + ".");
            ed.WriteMessage("\n  объектов " + made + "; целых " +
                CladCommand.SafeStr(CladCommand.Get(sum, "full")) +
                ", кусков подрезки " + CladCommand.SafeStr(CladCommand.Get(sum, "cut")) +
                "; полосок <" + F0(st.MinPiece) + " мм ушло в руст: " +
                CladCommand.SafeStr(CladCommand.Get(absorbed, "count")) +
                (erased > 0 ? "; прежних удалено " + erased : "") + ".");
            ed.WriteMessage("\n  ПЛИТОК ВСЕГО (целые + заготовки после раскроя): " +
                CladCommand.SafeStr(CladCommand.Get(sum, "tiles_total")) +
                " (по площади минимум " + CladCommand.SafeStr(CladCommand.Get(sum, "tiles_by_area")) +
                "); отходы раскроя " +
                ToD(CladCommand.Get(sum, "waste_pct")).ToString("0.0", CultureInfo.InvariantCulture) +
                " % (пропил " + F1(st.Kerf) + " мм). Запас на бой/брак — отдельно.");
            if (smallMade > 0)
                ed.WriteMessage("\n  ! малая подрезка (резаный размер < " + F0(st.WarnCut) +
                    " мм): " + smallMade + " шт — красные контуры на слое «" +
                    st.SmallLayer() + "».");
            if (dynFail > 0)
                ed.WriteMessage("\n  ! у " + dynFail + " вставок не выставились «ширина»/«высота».");
            if (multi) PrintByType(ed, sum);
            PrintNotes(ed, CladCommand.Get(res, "notes") as object[]);
            ed.WriteMessage("\nСпецификация — ATSPEC по слою «" + st.LayerFor("", false) +
                (multi ? " …»" : "»") + (dyn ? " (все камни — блок «" + st.Element +
                "» с размерами)." : " (целые — блоки «" + st.BlockFor("", false) + "»)."));
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


        private static string MakeLayer(Transaction tr, Database db, string name,
                                        int aci, int rgb, bool keepIfExists)
        {
            string ln = CladCommand.LayerName(name);
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(ln))
            {
                if (keepIfExists) return ln;
                var rec = (LayerTableRecord)tr.GetObject(lt[ln], OpenMode.ForWrite);
                rec.Color = rgb >= 0
                    ? Color.FromRgb((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255))
                    : Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
                return ln;
            }
            if (!lt.IsWriteEnabled) lt.UpgradeOpen();
            var nr = new LayerTableRecord { Name = ln };
            nr.Color = rgb >= 0
                ? Color.FromRgb((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255))
                : Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
            lt.Add(nr);
            tr.AddNewlyCreatedDBObject(nr, true);
            return ln;
        }

        /// <summary>Блок целой плитки (имя уже с форматом). fill — заливка
        /// SOLID цветом слоя вставки (раскраска по образцу), иначе контур.</summary>
        private static ObjectId EnsureTileBlock(Transaction tr, Database db, BlockTable bt,
            string name, double w, double h, bool fill)
        {
            string bn = CladCommand.LayerName(name);
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
            if (fill)
            {
                var hh = new Hatch();
                rec.AppendEntity(hh);
                tr.AddNewlyCreatedDBObject(hh, true);
                hh.SetDatabaseDefaults();
                hh.Layer = "0";
                hh.ColorIndex = 0;
                hh.SetHatchPattern(HatchPatternType.PreDefined, "SOLID");
                hh.Associative = true;
                hh.AppendLoop(HatchLoopTypes.External, new ObjectIdCollection(new[] { pl.ObjectId }));
                hh.EvaluateHatch(true);
            }
            return rec.ObjectId;
        }

        // имена динпараметров «ширина»/«высота» (В7 Германа, как ATCLAD) —
        // пробной вставкой, до того как что-либо стирать
        private static bool ProbeDyn(Transaction tr, BlockTableRecord ms, ObjectId defId,
                                     out string dynW, out string dynH)
        {
            dynW = dynH = null;
            var br = new BlockReference(Point3d.Origin, defId);
            ms.AppendEntity(br);
            tr.AddNewlyCreatedDBObject(br, true);
            try
            {
                if (br.IsDynamicBlock)
                    foreach (DynamicBlockReferenceProperty pr in br.DynamicBlockReferencePropertyCollection)
                    {
                        string pn = (pr.PropertyName ?? "").Trim();
                        if (string.Equals(pn, "ширина", StringComparison.OrdinalIgnoreCase)) dynW = pr.PropertyName;
                        else if (string.Equals(pn, "высота", StringComparison.OrdinalIgnoreCase)) dynH = pr.PropertyName;
                    }
            }
            finally { br.Erase(); }
            return dynW != null && dynH != null;
        }

        // атрибуты вставки — из текущего представления, ЗАХВАТКА = зона (как ATCLAD)
        private static void FillAttributes(Transaction tr, BlockReference br, string zone)
        {
            var rbtr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
            if (!rbtr.HasAttributeDefinitions) return;
            foreach (ObjectId aid in rbtr)
            {
                var ad = tr.GetObject(aid, OpenMode.ForRead) as AttributeDefinition;
                if (ad == null || ad.Constant) continue;
                var ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                if (ad.Tag.Trim().ToUpperInvariant() == "ЗАХВАТКА") ar.TextString = zone;
                br.AttributeCollection.AppendAttribute(ar);
                tr.AddNewlyCreatedDBObject(ar, true);
            }
        }

        /// <summary>Стереть объекты по хэндлам (любые типы). Возвращает число.</summary>
        private static object[] NonEmpty(object[] a)
        { return a != null && a.Length > 0 ? a : null; }

        // Точки принудительных рустов (как ATCLAD 2.5/2.6). Если в метке зоны
        // есть прежние — сначала вопрос классическим конструктором кейвордов
        // (грабля 18.07r: дефолт по не-OK). false — пользователь отменил (Esc).
        private static bool AskRusts(Editor ed, bool vertical, object[] prev, List<object> outList)
        {
            string what = vertical ? "вертикального руста (ось шва)" : "горизонтального руста (низ руста)";
            string many = vertical ? "Вертикальные" : "Горизонтальные";
            if (prev != null)
            {
                var pk = new PromptKeywordOptions("\n" + many + " русты: прежние " + prev.Length +
                    " шт [Прежние/Новые/Нет] <Прежние>: ", "Прежние Новые Нет");
                var rk = ed.GetKeywords(pk);
                if (rk.Status == PromptStatus.Cancel) { ed.WriteMessage("\nОтменено."); return false; }
                string kw = rk.Status == PromptStatus.OK && !string.IsNullOrEmpty(rk.StringResult)
                    ? rk.StringResult : "Прежние";
                if (kw == "Нет") return true;
                if (kw == "Прежние")
                {
                    foreach (var v in prev)
                    {
                        try { outList.Add(Convert.ToDouble(v, CultureInfo.InvariantCulture)); }
                        catch { }
                    }
                    ed.WriteMessage("\n  " + many.ToLower() + " русты — прежние: " + outList.Count + " шт.");
                    return true;
                }
            }
            while (true)
            {
                var po = new PromptPointOptions("\nТочка " + what + " (Enter — " +
                    (outList.Count == 0 ? "без них" : "дальше") + "): ") { AllowNone = true };
                var pr = ed.GetPoint(po);
                if (pr.Status == PromptStatus.Cancel) { ed.WriteMessage("\nОтменено."); return false; }
                if (pr.Status != PromptStatus.OK) return true;
                double v = vertical ? pr.Value.X : pr.Value.Y;
                outList.Add(v);
                ed.WriteMessage("\n  " + (vertical ? "вертикальный руст X = " : "горизонтальный руст Y = ") +
                                F0(v) + " (всего " + outList.Count + ")");
            }
        }

        internal static int EraseByHandles(Transaction tr, Database db, IEnumerable<string> handles)
        {
            int n = 0;
            foreach (var hs in handles)
            {
                long v;
                if (!long.TryParse(hs, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v))
                    continue;
                ObjectId id;
                if (!db.TryGetObjectId(new Handle(v), out id) || id.IsNull || id.IsErased)
                    continue;
                var e = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                if (e == null) continue;
                e.Erase();
                n++;
            }
            return n;
        }

        private static Dictionary<string, object> ReadMeta(Transaction tr,
            JavaScriptSerializer ser, Entity ent, string key)
        {
            try
            {
                string j = CladCommand.ReadData(tr, ent, key);
                return j == null ? null : ser.DeserializeObject(j) as Dictionary<string, object>;
            }
            catch { return null; }
        }

        private static List<ObjectId> OwnersOf(string member,
            Dictionary<string, List<ObjectId>> zoneObjs, Dictionary<string, ObjectId> polyByHandle)
        {
            var outp = new List<ObjectId>();
            List<ObjectId> zl;
            if (zoneObjs.TryGetValue(member, out zl)) outp.AddRange(zl);
            string oh = member.StartsWith("контур ") ? member.Substring(7) : member;
            ObjectId pid;
            if (polyByHandle.TryGetValue(oh, out pid)) outp.Add(pid);
            return outp;
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

        private static List<List<double[]>> RingsOf(Dictionary<string, object> it,
                                                    double tileW, double tileH)
        {
            var outp = new List<List<double[]>>();
            var rings = CladCommand.Get(it, "rings") as object[];
            if (rings != null)
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
            if (outp.Count == 0)
            {
                double x = ToD(CladCommand.Get(it, "x")), y = ToD(CladCommand.Get(it, "y")),
                       w = ToD(CladCommand.Get(it, "w")), hh = ToD(CladCommand.Get(it, "h"));
                if (w <= 0) w = tileW;
                if (hh <= 0) hh = tileH;
                outp.Add(new List<double[]>
                { new[] { x, y }, new[] { x + w, y }, new[] { x + w, y + hh }, new[] { x, y + hh } });
            }
            return outp;
        }

        private static Extents3d? SafeExtents(Entity ent)
        {
            try { return ent.GeometricExtents; }
            catch { return null; }
        }

        internal static void CollectOld(Transaction tr, JavaScriptSerializer ser,
                                        Entity ent, HashSet<string> into)
        {
            var m = ReadMeta(tr, ser, ent, XKeyTile);
            var hs = CladCommand.Get(m, "handles") as object[];
            if (hs == null) return;
            foreach (var h in hs) into.Add(CladCommand.SafeStr(h));
        }

        private static void AddToMap(Dictionary<string, List<string>> m, string key, string val)
        {
            if (!m.ContainsKey(key)) m[key] = new List<string>();
            m[key].Add(val);
        }

        private static void PrintByType(Autodesk.AutoCAD.EditorInput.Editor ed,
                                        Dictionary<string, object> sum)
        {
            var bt = CladCommand.Get(sum, "by_type") as Dictionary<string, object>;
            if (bt == null) return;
            foreach (var kv in bt)
            {
                var d = kv.Value as Dictionary<string, object>;
                if (d == null) continue;
                ed.WriteMessage("\n  " + kv.Key + ": целых " +
                    CladCommand.SafeStr(CladCommand.Get(d, "full")) +
                    ", кусков " + CladCommand.SafeStr(CladCommand.Get(d, "cut")) +
                    ", заготовок на подрезку " + CladCommand.SafeStr(CladCommand.Get(d, "blanks_cut")) +
                    ", итого плиток " + CladCommand.SafeStr(CladCommand.Get(d, "tiles_total")) +
                    ", отходы " + ToD(CladCommand.Get(d, "waste_pct")).ToString("0.0", CultureInfo.InvariantCulture) + " %");
            }
        }

        private static void PrintNotes(Autodesk.AutoCAD.EditorInput.Editor ed, object[] notes)
        {
            if (notes == null) return;
            foreach (var n in notes)
                ed.WriteMessage("\n  · " + CladCommand.SafeStr(n));
        }

        private static string EngineExe()
        {
            string baseDir = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(
                baseDir, "..", "engine", "clad_engine.exe"));
        }

        private static double ToD(object o)
        {
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return 0.0; }
        }

        private static string F0(double v) { return v.ToString("0", CultureInfo.InvariantCulture); }
        private static string F1(double v) { return v.ToString("0.#", CultureInfo.InvariantCulture); }
    }
}
