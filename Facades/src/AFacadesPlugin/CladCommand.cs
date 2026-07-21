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

[assembly: CommandClass(typeof(AFacadesPlugin.CladCommand))]

namespace AFacadesPlugin
{
    /// <summary>
    /// ATCLAD — этап 2 (ОТДЕЛЬНАЯ команда — требование Германа 21.07):
    /// раскладка облицовки универсальным блоком (кассетой) по зонам
    /// ATFZONE и/или замкнутым полилиниям. Сценарий Дениса 20.07 +
    /// ответы Германа В1–В8 (Facades/docs/NEXT.md):
    /// выбор зон/контуров → образец блока (размер камня и имена
    /// динпараметров «ширина»/«высота» читаются с образца, В7; параметр
    /// видимости НЕ трогаем, В8) → отметка старта (общий горизонт) →
    /// русты → способ раскладки (от проёмов / от края) → вставки блока
    /// на слое образца; параметры и хэндлы вставок пишутся ключом
    /// «ATCLAD» в extension dictionary штриховки/марки зоны (повторный
    /// запуск по той же зоне удаляет прежние камни — перегенерация).
    /// </summary>
    public class CladCommand
    {
        internal const string XKeyClad = "ATCLAD";
        private const double CloseTol = 0.5;   // мм: зазор «замкнутости»

        [CommandMethod("ATCLAD", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            // ── 1. выбор зон ATFZONE (штриховки/марки) и/или контуров ──
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

            // зоны: zone_id → объекты зоны (для меток/удаления старого);
            // полилинии: handle → (id, pts, bulges) — кандидаты в контуры
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

                    // штриховка/марка зоны ATFZONE
                    string json = ZoneCommand.ReadZoneData(tr, ent);
                    if (json == null) continue;
                    var z = ser.DeserializeObject(json)
                            as Dictionary<string, object>;
                    if (z == null) continue;
                    string zid = ZoneCommand.SafeStr(
                        ZoneCommand.Get(z, "zone_id"));
                    if (zid.Length == 0) continue;
                    if (cladDefault.Length == 0)
                        cladDefault = ZoneCommand.SafeStr(
                            ZoneCommand.Get(z, "cladding"));
                    if (!zoneObjs.ContainsKey(zid))
                        zoneObjs[zid] = new List<ObjectId>();
                    if (!zoneObjs[zid].Contains(ent.ObjectId))
                        zoneObjs[zid].Add(ent.ObjectId);
                    CollectOldHandles(tr, ser, ent, oldHandles);

                    // сверка с фактом (паттерн ATFTABLE): контур двигали
                    // после ATFZONE → раскладка легла бы мимо
                    var hat = ent as Hatch;
                    if (hat != null)
                    {
                        var rep = ZoneCommand.Get(z, "report")
                                  as Dictionary<string, object>;
                        try
                        {
                            double fact = hat.Area / 1e6;
                            double stored = Convert.ToDouble(
                                ZoneCommand.Get(rep, "area_net_m2"),
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
                if (zoneStale.Contains(zid)) continue;   // сообщение ниже
                var parts = FindZoneParts(fz, zid);
                if (parts.Count == 0) { zoneMissing.Add(zid); continue; }
                foreach (var part in parts)
                {
                    string pid = ZoneCommand.SafeStr(
                        ZoneCommand.Get(part, "id"));
                    partToRoot[pid] = zid;
                    zonesPayload.Add(new Dictionary<string, object>
                    { { "zone_id", pid }, { "zone", part } });
                    // контуры зоны из выборки в «голые» не пускаем —
                    // иначе та же зона раскладывалась бы дважды
                    string oid = MetaStr(part, "outer_contour_id");
                    if (oid != null) { polyData.Remove(oid); }
                    var ops = ZoneCommand.Get(part, "openings") as object[];
                    if (ops != null)
                        foreach (var o in ops)
                        {
                            var od = o as Dictionary<string, object>;
                            if (od != null)
                                polyData.Remove(ZoneCommand.SafeStr(
                                    ZoneCommand.Get(od, "id")));
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
                    "вставлены без подгонки размеров, размер запрошу " +
                    "числами.");
            if (tileW < 1 || tileH < 1)
            {
                var pw = new PromptDoubleOptions(
                    "\nШирина камня, мм: ")
                { DefaultValue = 600.0, AllowNegative = false,
                  AllowZero = false };
                var rw = ed.GetDouble(pw);
                if (rw.Status != PromptStatus.OK) return;
                tileW = rw.Value;
                var ph = new PromptDoubleOptions(
                    "\nВысота камня, мм: ")
                { DefaultValue = 600.0, AllowNegative = false,
                  AllowZero = false };
                var rh = ed.GetDouble(ph);
                if (rh.Status != PromptStatus.OK) return;
                tileH = rh.Value;
            }
            else
                ed.WriteMessage("\nКамень с образца: " + F0(tileW) + "×" +
                    F0(tileH) + " мм, блок «" + blockName + "», слой «" +
                    blockLayer + "».");

            // ── 4. отметка старта (общий горизонт), русты, способ ──
            var ppr = ed.GetPoint(
                "\nТочка уровня старта облицовки (низ первого ряда): ");
            if (ppr.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }
            double datum = ppr.Value.Y;
            ed.WriteMessage("\nОтметка старта: Y = " + F0(datum) + " мм.");

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

            // классический конструктор (messageAndKeywords, globals) —
            // грабля 18.07r
            var pko = new PromptKeywordOptions(
                "\nСпособ раскладки [Проемы/Край] <Проемы>: ",
                "Проемы Край");
            var kres = ed.GetKeywords(pko);
            if (kres.Status == PromptStatus.Cancel) return;
            string mode = (kres.Status == PromptStatus.OK &&
                           kres.StringResult == "Край") ? "edge" : "openings";

            // ── 5. движок ──
            var payload = new Dictionary<string, object>
            {
                { "op", "cladding" },
                { "tile", new Dictionary<string, object>
                    { { "w", tileW }, { "h", tileH } } },
                { "gap", new Dictionary<string, object>
                    { { "v", rgv.Value }, { "h", rgh.Value } } },
                { "datum", datum },
                { "mode", mode },
                // min_cut не передаём: дефолт движка 150 (В4, Герман)
                { "zones", zonesPayload },
                { "contours", contoursPayload },
            };
            string baseDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)
                ?? ".";
            string engineExe = Path.GetFullPath(Path.Combine(
                baseDir, "..", "engine", "facades_engine.exe"));
            Dictionary<string, object> res;
            try
            {
                res = ser.DeserializeObject(ZoneCommand.CallEngine(
                    engineExe, ser.Serialize(payload)))
                    as Dictionary<string, object>;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nОшибка движка: " + ex.Message);
                return;
            }
            if (res == null || !ZoneCommand.GetBool(res, "ok"))
            {
                ed.WriteMessage("\nДвижок отказал: " + ZoneCommand.SafeStr(
                    ZoneCommand.Get(res, "error")));
                PrintNotes(ed, ZoneCommand.Get(res, "notes") as object[]);
                return;
            }
            var inserts = ZoneCommand.Get(res, "inserts") as object[];
            if (inserts == null || inserts.Length == 0)
            {
                ed.WriteMessage("\nРаскладка пуста (см. замечания).");
                PrintNotes(ed, ZoneCommand.Get(res, "notes") as object[]);
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
                    double x = ToD(ZoneCommand.Get(it, "x")),
                           y = ToD(ZoneCommand.Get(it, "y")),
                           w = ToD(ZoneCommand.Get(it, "w")),
                           h = ToD(ZoneCommand.Get(it, "h"));

                    var br = new BlockReference(new Point3d(x, y, 0),
                                                bt[blockName]);
                    br.Layer = blockLayer;
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

                    // атрибуты (МАРКИРОВКА/ЗАХВАТКА) — из ТЕКУЩЕГО
                    // представления, ПОСЛЕ динрастяжек (паттерн ABlockGen)
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
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }

                    string pid = ZoneCommand.SafeStr(
                        ZoneCommand.Get(it, "zone"));
                    string root;
                    if (!partToRoot.TryGetValue(pid, out root)) root = pid;
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
                        { "tile", new Dictionary<string, object>
                            { { "w", tileW }, { "h", tileH } } },
                        { "gap", new Dictionary<string, object>
                            { { "v", rgv.Value }, { "h", rgh.Value } } },
                        { "datum", datum },
                        { "mode", mode },
                        { "block", blockName },
                        { "layer", blockLayer },
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
                            ZoneCommand.StoreZoneData(tr, te, mjson,
                                                      XKeyClad);
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
                            ZoneCommand.StoreZoneData(tr, te, mjson,
                                                      XKeyClad);
                        }
                    }
                }
                tr.Commit();
            }

            // ── 7. отчёт ──
            var sum = ZoneCommand.Get(res, "summary")
                      as Dictionary<string, object>;
            ed.WriteMessage("\nATCLAD: камней " + made +
                " (целых " + ZoneCommand.SafeStr(ZoneCommand.Get(sum, "full")) +
                ", подрезных " + ZoneCommand.SafeStr(ZoneCommand.Get(sum, "cut")) +
                ")" + (erased > 0 ? "; прежних удалено " + erased : "") +
                "; блок «" + blockName + "», слой «" + blockLayer + "».");
            var perZone = ZoneCommand.Get(res, "per_zone") as object[];
            if (perZone != null && perZone.Length > 1)
                foreach (var pz in AggregateByRoot(perZone, partToRoot))
                    ed.WriteMessage("\n  " + pz);
            if (dynFail > 0)
                ed.WriteMessage("\n  ! у " + dynFail + " вставок не " +
                    "выставились «ширина»/«высота» — проверьте параметры " +
                    "блока.");
            PrintNotes(ed, ZoneCommand.Get(res, "notes") as object[]);
        }

        // ── прежние камни: хэндлы из метки ATCLAD выбранного объекта ──
        private static void CollectOldHandles(Transaction tr,
            JavaScriptSerializer ser, Entity ent, HashSet<string> into)
        {
            try
            {
                string j = ZoneCommand.ReadZoneData(tr, ent, XKeyClad);
                if (j == null) return;
                var d = ser.DeserializeObject(j) as Dictionary<string, object>;
                var hs = ZoneCommand.Get(d, "handles") as object[];
                if (hs == null) return;
                foreach (var h in hs)
                    into.Add(ZoneCommand.SafeStr(h));
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
                    string id = ZoneCommand.SafeStr(ZoneCommand.Get(zd, "id"));
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
            var meta = ZoneCommand.Get(zd, "meta") as Dictionary<string, object>;
            if (meta == null) return null;
            object v = ZoneCommand.Get(meta, key);
            return v == null ? null : ZoneCommand.SafeStr(v);
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
                string pid = ZoneCommand.SafeStr(ZoneCommand.Get(d, "zone_id"));
                string root;
                if (!partToRoot.TryGetValue(pid, out root)) root = pid;
                if (!tiles.ContainsKey(root))
                {
                    order.Add(root);
                    tiles[root] = 0; full[root] = 0; cut[root] = 0;
                }
                tiles[root] += ToI(ZoneCommand.Get(d, "tiles"));
                full[root] += ToI(ZoneCommand.Get(d, "full"));
                cut[root] += ToI(ZoneCommand.Get(d, "cut"));
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
                ed.WriteMessage("\n  · " + ZoneCommand.SafeStr(n));
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
