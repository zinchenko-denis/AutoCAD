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

[assembly: CommandClass(typeof(ACladPlugin.CladCommand))]

namespace ACladPlugin
{
    /// <summary>
    /// ATCLAD — раскладка облицовки (этап 2, ОТДЕЛЬНЫЙ бандл AClad —
    /// просьба Германа 22.07: боевой этап 1 (AFacades: ATFZONE/ATFTABLE)
    /// ставится один раз, раскладка переустанавливается сколько угодно).
    /// Данные зон читаются из Xrecord «ATFZONE» (пишет AFacades) и
    /// <dwg>_fzones.json — контракт общий, реализация продублирована
    /// сознательно (модули развязаны). Правила и диалог — ТЗ Германа
    /// 22.07 пп.1.1–1.6/2.1–2.6 (AClad/docs/CLADDING.md), движок
    /// clad_engine.exe. Параметры и хэндлы вставок пишутся ключом
    /// «ATCLAD» рядом с данными зоны (повторный запуск по той же зоне
    /// удаляет прежние камни — перегенерация).
    /// </summary>
    public class CladCommand
    {
        internal const string XKeyZone = "ATFZONE";
        internal const string XKeyClad = "ATCLAD";
        private const double CloseTol = 0.5;   // мм: зазор «замкнутости»

        [CommandMethod("ATCLAD", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            // рантайм-ошибка не должна ронять AutoCAD краш-окном
            // (прецедент 23.07: дубль ключа payload у Германа) —
            // печатаем стек копируемым текстом в командную строку
            try { RunCore(doc); }
            catch (System.Exception ex)
            {
                try
                {
                    doc.Editor.WriteMessage(
                        "\nATCLAD: внутренняя ошибка — сообщите " +
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

            // ── 1. выбор зон ATFZONE (штриховки/марки) и/или контуров
            //    (ТЗ 2.3: «выбрать контура или маркеры») ──
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите зоны облицовки (штриховки/" +
                                   "марки ATFZONE) и/или замкнутые контуры: "
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
            var zoneStale = new List<string>();
            var polyByHandle = new Dictionary<string, ObjectId>();
            var polyData = new Dictionary<string, Dictionary<string, object>>();
            var oldHandles = new HashSet<string>();
            string cladDefault = "";
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
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
                        polyData[h] = new Dictionary<string, object>
                        { { "id", h }, { "pts", pts }, { "bulges", bulges } };
                        CollectOldHandles(tr, ser, ent, oldHandles);
                        continue;
                    }

                    // штриховка/марка зоны ATFZONE (пишет AFacades)
                    string json = ReadData(tr, ent, XKeyZone);
                    if (json == null) continue;
                    var z = ser.DeserializeObject(json)
                            as Dictionary<string, object>;
                    if (z == null) continue;
                    string zid = SafeStr(Get(z, "zone_id"));
                    if (zid.Length == 0) continue;
                    if (cladDefault.Length == 0)
                        cladDefault = SafeStr(Get(z, "cladding"));
                    if (!zoneObjs.ContainsKey(zid))
                        zoneObjs[zid] = new List<ObjectId>();
                    if (!zoneObjs[zid].Contains(ent.ObjectId))
                        zoneObjs[zid].Add(ent.ObjectId);
                    CollectOldHandles(tr, ser, ent, oldHandles);

                    // сверка с фактом: контур двигали после ATFZONE →
                    // раскладка легла бы мимо
                    var hat = ent as Hatch;
                    if (hat != null)
                    {
                        var rep = Get(z, "report")
                                  as Dictionary<string, object>;
                        try
                        {
                            double fact = hat.Area / 1e6;
                            double stored = Convert.ToDouble(
                                Get(rep, "area_net_m2"),
                                CultureInfo.InvariantCulture);
                            if (Math.Abs(fact - stored) > 0.001 &&
                                !zoneStale.Contains(zid))
                                zoneStale.Add(zid);
                        }
                        catch { }
                    }
                }
                tr.Commit();
            }

            // ── 2. геометрия зон из <dwg>_fzones.json ──
            var fz = LoadFzones(ed, db, ser);
            var zonesPayload = new List<Dictionary<string, object>>();
            var partToRoot = new Dictionary<string, string>();
            var zoneMissing = new List<string>();
            foreach (var kv in zoneObjs)
            {
                string zid = kv.Key;
                if (zoneStale.Contains(zid)) continue;
                var parts = FindZoneParts(fz, zid);
                if (parts.Count == 0) { zoneMissing.Add(zid); continue; }
                foreach (var part in parts)
                {
                    string pid = SafeStr(Get(part, "id"));
                    partToRoot[pid] = zid;
                    zonesPayload.Add(new Dictionary<string, object>
                    { { "zone_id", pid }, { "zone", part } });
                    // контуры зоны из выборки в «голые» не пускаем —
                    // иначе та же зона раскладывалась бы дважды
                    string oid = MetaStr(part, "outer_contour_id");
                    if (oid != null) { polyData.Remove(oid); }
                    var ops = Get(part, "openings") as object[];
                    if (ops != null)
                        foreach (var o in ops)
                        {
                            var od = o as Dictionary<string, object>;
                            if (od != null)
                                polyData.Remove(SafeStr(Get(od, "id")));
                        }
                }
            }
            var contoursPayload = new List<Dictionary<string, object>>();
            foreach (var kv in polyData) contoursPayload.Add(kv.Value);

            if (zoneStale.Count > 0)
                ed.WriteMessage("\nЗоны изменены после обсчёта (площадь " +
                    "штриховки разошлась с сохранённой): " +
                    string.Join(", ", zoneStale.ToArray()) +
                    " — ПРОПУЩЕНЫ. Перезапустите по ним ATFZONE.");
            if (zoneMissing.Count > 0)
                ed.WriteMessage("\nНет геометрии в _fzones.json для: " +
                    string.Join(", ", zoneMissing.ToArray()) +
                    " — выберите контуры этих зон полилиниями (или " +
                    "перезапустите ATFZONE с записью JSON).");
            if (zonesPayload.Count == 0 && contoursPayload.Count == 0)
            {
                ed.WriteMessage("\nНет пригодных зон или контуров — " +
                    "нечего раскладывать.");
                return;
            }

            // ── 3. образец блока облицовки ──
            var peo = new PromptEntityOptions(
                "\nУкажите образец блока облицовки (кассету): ");
            peo.SetRejectMessage("\nЭто не вхождение блока.");
            peo.AddAllowedClass(typeof(BlockReference), false);
            var pres = ed.GetEntity(peo);
            if (pres.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            string blockName, blockLayer, dynW = null, dynH = null;
            double tileW = 0, tileH = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var br = (BlockReference)tr.GetObject(pres.ObjectId,
                                                      OpenMode.ForRead);
                var btr = (BlockTableRecord)tr.GetObject(
                    br.DynamicBlockTableRecord, OpenMode.ForRead);
                blockName = btr.Name;   // эффективное имя (динам. блоки)
                blockLayer = br.Layer;
                if (br.IsDynamicBlock)
                    foreach (DynamicBlockReferenceProperty pr in
                             br.DynamicBlockReferencePropertyCollection)
                    {
                        // В7 (Герман): имена динпараметров кассеты —
                        // «ширина» и «высота» (сверяем без регистра);
                        // видимость и прочие свойства НЕ трогаем (В8)
                        string pn = (pr.PropertyName ?? "").Trim();
                        if (string.Equals(pn, "ширина",
                                StringComparison.OrdinalIgnoreCase))
                        { dynW = pr.PropertyName; tileW = ToD(pr.Value); }
                        else if (string.Equals(pn, "высота",
                                StringComparison.OrdinalIgnoreCase))
                        { dynH = pr.PropertyName; tileH = ToD(pr.Value); }
                    }
                tr.Commit();
            }
            if (dynW == null || dynH == null)
                ed.WriteMessage("\nУ блока «" + blockName + "» не найдены " +
                    "динпараметры «ширина»/«высота» — камни будут " +
                    "вставлены без подгонки размеров.");

            // ── 3а. максимальный размер облицовки (ТЗ 2.2): дефолт — с
            //    образца; введённое программа выставит и образцу ──
            var pw = new PromptDoubleOptions(
                "\nМаксимальная ШИРИНА камня, мм: ")
            { DefaultValue = tileW >= 1 ? tileW : 600.0,
              AllowNegative = false, AllowZero = false };
            var rw = ed.GetDouble(pw);
            if (rw.Status != PromptStatus.OK) return;
            tileW = rw.Value;
            var ph = new PromptDoubleOptions(
                "\nМаксимальная ВЫСОТА камня, мм: ")
            { DefaultValue = tileH >= 1 ? tileH : 600.0,
              AllowNegative = false, AllowZero = false };
            var rh = ed.GetDouble(ph);
            if (rh.Status != PromptStatus.OK) return;
            tileH = rh.Value;

            // ── 3б. наименование облицовки → СВОЙ слой камней (ТЗ 2.1) ──
            string cladType = cladDefault.Length > 0 ? cladDefault
                                                     : blockLayer;
            if (cladType == null || cladType.Trim().Length == 0)
                cladType = "керамогранит 600х600";
            var psoT = new PromptStringOptions("\nНаименование облицовки: ")
            {
                AllowSpaces = true,
                DefaultValue = cladType,
                UseDefaultValue = true,
            };
            var rT = ed.GetString(psoT);
            if (rT.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }
            if (rT.StringResult != null &&
                rT.StringResult.Trim().Length > 0)
                cladType = rT.StringResult.Trim();
            string cladLayer = LayerName(
                cladType.ToLowerInvariant().StartsWith("облицовка")
                    ? cladType : "Облицовка " + cladType);
            ed.WriteMessage("\nСлой камней: «" + cladLayer + "».");

            // ── 4. точка старта (ТЗ 1.1/2.4): низ первого ряда, общий
            //    горизонт — горизонтальные русты всех участков совпадают ──
            var ppr = ed.GetPoint(
                "\nТочка старта раскладки (её уровень — низ первого " +
                "ряда; горизонтальные русты всех участков — от неё): ");
            if (ppr.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }
            double originY = ppr.Value.Y;
            ed.WriteMessage("\nНиз первого ряда: Y = " + F0(originY) +
                            " мм.");

            // ── 4а. русты (ТЗ 1.5) ──
            var pgv = new PromptDoubleOptions(
                "\nВертикальный руст (шов между камнями в ряду), мм: ")
            { DefaultValue = 8.0, AllowNegative = false };
            var rgv = ed.GetDouble(pgv);
            if (rgv.Status != PromptStatus.OK) return;
            var pgh = new PromptDoubleOptions(
                "\nГоризонтальный руст (шов между рядами), мм: ")
            { DefaultValue = rgv.Value, AllowNegative = false };
            var rgh = ed.GetDouble(pgh);
            if (rgh.Status != PromptStatus.OK) return;

            // ── 4б. точки вертикальных рустов (ТЗ 2.5, по желанию):
            //    через каждую точку пройдёт вертикальный руст (ось шва);
            //    точки режут раскладку на независимые участки ──
            var vjoints = new List<object>();
            while (true)
            {
                var ppo = new PromptPointOptions(
                    "\nТочка вертикального руста (Enter — продолжить): ")
                { AllowNone = true };
                var pv = ed.GetPoint(ppo);
                if (pv.Status != PromptStatus.OK) break;
                vjoints.Add(pv.Value.X);
                ed.WriteMessage("\n  вертикальный руст по X = " +
                                F0(pv.Value.X) + " (всего " +
                                vjoints.Count + ")");
            }

            // ── 4в. точки горизонтальных рустов (ТЗ 2.6 + Г3): точка =
            //    НИЗ руста, действует на весь участок: снизу облицовка
            //    приходит к русту С ПОДРЕЗКОЙ, выше руста — панели
            //    стандартной высоты (клик по верху окна = п.1.4) ──
            var hjoints = new List<object>();
            while (true)
            {
                var pho = new PromptPointOptions(
                    "\nТочка горизонтального руста (Enter — разложить): ")
                { AllowNone = true };
                var pv = ed.GetPoint(pho);
                if (pv.Status != PromptStatus.OK) break;
                hjoints.Add(pv.Value.Y);
                ed.WriteMessage("\n  горизонтальный руст по Y = " +
                                F0(pv.Value.Y) + " (всего " +
                                hjoints.Count + ")");
            }

            // ── 5. движок ──
            var payload = new Dictionary<string, object>
            {
                { "op", "cladding" },
                { "tile", new Dictionary<string, object>
                    { { "w", tileW }, { "h", tileH } } },
                { "gap", new Dictionary<string, object>
                    { { "v", rgv.Value }, { "h", rgh.Value } } },
                { "origin", new Dictionary<string, object>
                    { { "y", originY } } },
                { "vjoints", vjoints },
                { "hjoints", hjoints },
                // min_cut не передаём: дефолт движка 150 (В4, Герман)
                { "zones", zonesPayload },
                { "contours", contoursPayload },
            };
            string baseDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? ".";
            string engineExe = Path.GetFullPath(Path.Combine(
                baseDir, "..", "engine", "clad_engine.exe"));
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
            var inserts = Get(res, "inserts") as object[];
            if (inserts == null || inserts.Length == 0)
            {
                ed.WriteMessage("\nРаскладка пуста (см. замечания).");
                PrintNotes(ed, Get(res, "notes") as object[]);
                return;
            }

            // ── 6. чертёж: удалить прежние камни, вставить новые, метки ──
            var handlesByRoot = new Dictionary<string, List<string>>();
            int erased = 0, made = 0, dynFail = 0;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                                  OpenMode.ForRead);
                if (!bt.Has(blockName))
                {
                    ed.WriteMessage("\nБлок «" + blockName +
                                    "» не найден в чертеже.");
                    return;
                }
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureLayer(tr, db, cladLayer);
                // ТЗ 2.1/2.2: образец — в слой облицовки, размеры
                // образцу — введённые максимальные
                try
                {
                    var smp = (BlockReference)tr.GetObject(pres.ObjectId,
                                                   OpenMode.ForWrite);
                    smp.Layer = cladLayer;
                    if (dynW != null && dynH != null && smp.IsDynamicBlock)
                        foreach (DynamicBlockReferenceProperty pr in
                                 smp.DynamicBlockReferencePropertyCollection)
                        {
                            if (pr.ReadOnly) continue;
                            if (pr.PropertyName == dynW)
                                TrySetNum(pr, tileW);
                            else if (pr.PropertyName == dynH)
                                TrySetNum(pr, tileH);
                        }
                }
                catch { }

                // прежние камни этой зоны/контура (перегенерация)
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
                        if (!(oe is BlockReference)) continue;
                        if (!oldHandles.Contains(oe.Handle.ToString()))
                            continue;
                        oe.UpgradeOpen();
                        oe.Erase();
                        erased++;
                    }

                foreach (var itObj in inserts)
                {
                    var it = itObj as Dictionary<string, object>;
                    if (it == null) continue;
                    double x = ToD(Get(it, "x")),
                           y = ToD(Get(it, "y")),
                           w = ToD(Get(it, "w")),
                           h = ToD(Get(it, "h"));

                    var br = new BlockReference(new Point3d(x, y, 0),
                                                bt[blockName]);
                    br.Layer = cladLayer;
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    // размеры камня — динпараметрами «ширина»/«высота»
                    // (числовые; видимость не трогаем — В8)
                    if (dynW != null && dynH != null && br.IsDynamicBlock)
                    {
                        bool okW = false, okH = false;
                        foreach (DynamicBlockReferenceProperty pr in
                                 br.DynamicBlockReferencePropertyCollection)
                        {
                            if (pr.ReadOnly) continue;
                            if (pr.PropertyName == dynW)
                                okW = TrySetNum(pr, w);
                            else if (pr.PropertyName == dynH)
                                okH = TrySetNum(pr, h);
                        }
                        if (!okW || !okH) dynFail++;
                    }

                    string pid = SafeStr(Get(it, "zone"));
                    string root;
                    if (!partToRoot.TryGetValue(pid, out root)) root = pid;

                    // атрибуты (МАРКИРОВКА/ЗАХВАТКА) — из ТЕКУЩЕГО
                    // представления, ПОСЛЕ динрастяжек (паттерн ABlockGen).
                    // ЗАХВАТКА = зона этапа 1 (ответ Дениса 01.08: захватка
                    // — участок, обведённый и обсчитанный в ATFZONE; id тот
                    // же, что в сводной таблице ATFTABLE). МАРКИРОВКА камня
                    // — открытый В10 (типоразмер? ждём Германа), не трогаем.
                    var rbtr = (BlockTableRecord)tr.GetObject(
                        br.BlockTableRecord, OpenMode.ForRead);
                    if (rbtr.HasAttributeDefinitions)
                        foreach (ObjectId aid in rbtr)
                        {
                            var ad = tr.GetObject(aid, OpenMode.ForRead)
                                     as AttributeDefinition;
                            if (ad == null || ad.Constant) continue;
                            var ar = new AttributeReference();
                            ar.SetAttributeFromBlock(ad, br.BlockTransform);
                            if (ad.Tag.Trim().ToUpperInvariant()
                                == "ЗАХВАТКА")
                                ar.TextString = root;
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }

                    if (!handlesByRoot.ContainsKey(root))
                        handlesByRoot[root] = new List<string>();
                    handlesByRoot[root].Add(br.Handle.ToString());
                    made++;
                }

                // метки ATCLAD: параметры + хэндлы камней — на объекты
                // зоны (штриховка/марка) или на внешнюю полилинию контура
                foreach (var kv in handlesByRoot)
                {
                    string root = kv.Key;
                    var meta = new Dictionary<string, object>
                    {
                        { "zone_id", root },
                        { "cladding", cladType },
                        { "tile", new Dictionary<string, object>
                            { { "w", tileW }, { "h", tileH } } },
                        { "gap", new Dictionary<string, object>
                            { { "v", rgv.Value }, { "h", rgh.Value } } },
                        { "origin", new Dictionary<string, object>
                            { { "y", originY } } },
                        { "vjoints", vjoints },
                        { "hjoints", hjoints },
                        // мост к этапу 3 (ATFRAME): оси стоек и
                        // центры горизонтальных швов — из движка
                        { "joints_x", Get(res, "joints_x") },
                        { "rows_y", Get(res, "rows_y") },
                        { "block", blockName },
                        { "layer", cladLayer },
                        { "tiles", kv.Value.Count },
                        { "handles", kv.Value },
                    };
                    string mjson = ser.Serialize(meta);
                    List<ObjectId> targets;
                    if (zoneObjs.TryGetValue(root, out targets))
                        foreach (var tid in targets)
                        {
                            var te = (Entity)tr.GetObject(tid,
                                OpenMode.ForWrite);
                            StoreData(tr, te, mjson, XKeyClad);
                        }
                    else
                    {
                        // «контур <handle>» — метка на внешней полилинии
                        string oh = root.StartsWith("контур ")
                            ? root.Substring(7) : root;
                        ObjectId pid2;
                        if (polyByHandle.TryGetValue(oh, out pid2))
                        {
                            var te = (Entity)tr.GetObject(pid2,
                                OpenMode.ForWrite);
                            StoreData(tr, te, mjson, XKeyClad);
                        }
                    }
                }
                tr.Commit();
            }

            // ── 7. отчёт ──
            var sum = Get(res, "summary") as Dictionary<string, object>;
            ed.WriteMessage("\nATCLAD: камней " + made +
                " (целых " + SafeStr(Get(sum, "full")) +
                ", подрезных " + SafeStr(Get(sum, "cut")) +
                ")" + (erased > 0 ? "; прежних удалено " + erased : "") +
                "; блок «" + blockName + "», слой «" + cladLayer + "».");
            var perZone = Get(res, "per_zone") as object[];
            if (perZone != null && perZone.Length > 1)
                foreach (var pz in AggregateByRoot(perZone, partToRoot))
                    ed.WriteMessage("\n  " + pz);
            if (dynFail > 0)
                ed.WriteMessage("\n  ! у " + dynFail + " вставок не " +
                    "выставились «ширина»/«высота» — проверьте параметры " +
                    "блока.");
            PrintNotes(ed, Get(res, "notes") as object[]);
        }

        // ══ ATCLADDIM — «размеры облицовки» (просьба Германа 26.07):
        //    указать камень → программа проставляет ширины всех камней
        //    его РЯДА (или высоты СТОЛБЦА) размерной цепочкой на слое
        //    _РАЗМЕРЫ_ОБЛ (конвенция листов Германа). Паттерн размеров —
        //    AddDim ZoneCommand («Проба 4»), bbox — GeometricExtents
        //    (прецедент ReportCommand) ══

        [CommandMethod("ATCLADDIM", CommandFlags.Modal)]
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
                        "\nATCLADDIM: внутренняя ошибка — сообщите " +
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
            var pk = new PromptKeywordOptions(
                "\nЧто образмерить [Ряд/Столбец] <Ряд>: ",
                "Ряд Столбец");
            var rk = ed.GetKeywords(pk);
            bool byRow = !(rk.Status == PromptStatus.OK &&
                           rk.StringResult == "Столбец");

            var peo = new PromptEntityOptions(
                "\nУкажите камень раскладки: ");
            peo.SetRejectMessage("\nЭто не вхождение блока.");
            peo.AddAllowedClass(typeof(BlockReference), false);
            var pres = ed.GetEntity(peo);
            if (pres.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            int madeD = 0;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var smp = (BlockReference)tr.GetObject(pres.ObjectId,
                                                       OpenMode.ForRead);
                ObjectId defId = smp.DynamicBlockTableRecord;
                string layer = smp.Layer;
                Extents3d e0 = smp.GeometricExtents;
                double lo0 = byRow ? e0.MinPoint.Y : e0.MinPoint.X;
                double hi0 = byRow ? e0.MaxPoint.Y : e0.MaxPoint.X;

                var bt = (BlockTable)tr.GetObject(db.BlockTableId,
                                                  OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                // камни того же определения/слоя, чей центр попадает в
                // полосу кликнутого (ряд — по Y, столбец — по X)
                var cells = new List<Extents3d>();
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
                    Extents3d e;
                    try { e = br.GeometricExtents; }
                    catch { continue; }
                    double c = byRow
                        ? (e.MinPoint.Y + e.MaxPoint.Y) / 2.0
                        : (e.MinPoint.X + e.MaxPoint.X) / 2.0;
                    if (c > lo0 - 1 && c < hi0 + 1)
                        cells.Add(e);
                }
                if (cells.Count == 0)
                { ed.WriteMessage("\nКамни не найдены."); return; }
                EnsureLayer(tr, db, "_РАЗМЕРЫ_ОБЛ");
                // базовая линия размеров: ниже ряда / левее столбца
                double baseLo = double.MaxValue;
                foreach (var e in cells)
                    baseLo = Math.Min(baseLo, byRow ? e.MinPoint.Y
                                                    : e.MinPoint.X);
                double dl = baseLo - 300.0;
                foreach (var e in cells)
                {
                    Point3d p1, p2, dlp;
                    if (byRow)
                    {
                        p1 = new Point3d(e.MinPoint.X, e.MinPoint.Y, 0);
                        p2 = new Point3d(e.MaxPoint.X, e.MinPoint.Y, 0);
                        dlp = new Point3d(
                            (e.MinPoint.X + e.MaxPoint.X) / 2.0, dl, 0);
                    }
                    else
                    {
                        p1 = new Point3d(e.MinPoint.X, e.MinPoint.Y, 0);
                        p2 = new Point3d(e.MinPoint.X, e.MaxPoint.Y, 0);
                        dlp = new Point3d(dl,
                            (e.MinPoint.Y + e.MaxPoint.Y) / 2.0, 0);
                    }
                    var dim = new RotatedDimension(
                        byRow ? 0.0 : Math.PI / 2.0, p1, p2, dlp,
                        null, db.Dimstyle);
                    dim.SetDatabaseDefaults();
                    dim.Layer = "_РАЗМЕРЫ_ОБЛ";
                    ms.AppendEntity(dim);
                    tr.AddNewlyCreatedDBObject(dim, true);
                    madeD++;
                }
                tr.Commit();
            }
            ed.WriteMessage("\nATCLADDIM: размеров " + madeD +
                (byRow ? " (ширины ряда)." : " (высоты столбца)."));
        }

        // ── прежние камни: хэндлы из метки ATCLAD выбранного объекта ──
        private static void CollectOldHandles(Transaction tr,
            JavaScriptSerializer ser, Entity ent, HashSet<string> into)
        {
            try
            {
                string j = ReadData(tr, ent, XKeyClad);
                if (j == null) return;
                var d = ser.DeserializeObject(j) as Dictionary<string, object>;
                var hs = Get(d, "handles") as object[];
                if (hs == null) return;
                foreach (var h in hs)
                    into.Add(SafeStr(h));
            }
            catch { }
        }

        // ── <dwg>_fzones.json: id → зона facade_zone/1 ──
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

        // зона по марке: сама либо её части «Ф-N.1», «Ф-N.2» (merge) ──
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

        // per_zone частей merge-зон агрегируются к корневой марке
        private static List<string> AggregateByRoot(object[] perZone,
            Dictionary<string, string> partToRoot)
        {
            var order = new List<string>();
            var tiles = new Dictionary<string, int>();
            var full = new Dictionary<string, int>();
            var cut = new Dictionary<string, int>();
            foreach (var o in perZone)
            {
                var d = o as Dictionary<string, object>;
                if (d == null) continue;
                string pid = SafeStr(Get(d, "zone_id"));
                string root;
                if (!partToRoot.TryGetValue(pid, out root)) root = pid;
                if (!tiles.ContainsKey(root))
                {
                    order.Add(root);
                    tiles[root] = 0; full[root] = 0; cut[root] = 0;
                }
                tiles[root] += ToI(Get(d, "tiles"));
                full[root] += ToI(Get(d, "full"));
                cut[root] += ToI(Get(d, "cut"));
            }
            var outp = new List<string>();
            foreach (var r in order)
                outp.Add(r + ": " + tiles[r] + " (целых " + full[r] +
                         ", подрезных " + cut[r] + ")");
            return outp;
        }

        private static void PrintNotes(Editor ed, object[] notes)
        {
            if (notes == null || notes.Length == 0) return;
            ed.WriteMessage("\nЗамечания:");
            foreach (var n in notes)
                ed.WriteMessage("\n  · " + SafeStr(n));
        }

        // числовое динсвойство — ChangeType к типу значения (паттерн
        // TrySetDynProp ABlockGen, числовая ветка)
        private static bool TrySetNum(DynamicBlockReferenceProperty pr,
                                      double v)
        {
            try
            {
                pr.Value = Convert.ChangeType(v, pr.Value.GetType(),
                                              CultureInfo.InvariantCulture);
                return true;
            }
            catch { return false; }
        }

        // ── Xrecord на объекте: JSON чанками ≤250 символов. Контракт
        //    общий с AFacades (ключ ATFZONE читаем, ATCLAD пишем);
        //    реализация продублирована сознательно — модули развязаны ──

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

        // ── служебное (паттерн ZoneCommand/ABlockGen) ──

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

        internal static string LayerName(string cladding)
        {
            var bad = new char[] { '<', '>', '/', '\\', '"', ':', ';',
                                   '?', '*', '|', ',', '=', '`' };
            var sb = new StringBuilder();
            foreach (char c in cladding)
                sb.Append(Array.IndexOf(bad, c) >= 0 ? '_' : c);
            string s = sb.ToString().Trim();
            if (s.Length == 0) s = "ОБЛИЦОВКА";
            if (s.Length > 200) s = s.Substring(0, 200);
            return s;
        }

        // вызов движка через временные файлы (паттерн ATableSpec/ABlockGen)
        internal static string CallEngine(string engineExe, string reqJson)
        {
            string tmpIn = Path.Combine(Path.GetTempPath(),
                "aclad_in_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(),
                "aclad_out_" + Guid.NewGuid().ToString("N") + ".json");
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

        private static int ToI(object o)
        {
            try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); }
            catch { return 0; }
        }

        private static string F0(double v)
        { return v.ToString("0", CultureInfo.InvariantCulture); }
    }
}
