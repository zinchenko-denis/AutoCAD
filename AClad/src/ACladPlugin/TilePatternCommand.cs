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
            // 24.09 (независимая рецензия): прежняя раскладка — ПО ВЛАДЕЛЬЦАМ
            // (объект зоны/контура с меткой). Удаляется только то, что
            // заменяется результатом этого запуска: зона, которую движок
            // пропустил, сохраняет прежнюю раскладку; метка, СКОПИРОВАННАЯ
            // вместе с объектом (COPY), чужие плитки не трогает.
            var own = new OwnedLabels();
            var cladOwn = new OwnedLabels();
            var zoneStale = new List<string>();
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
                            zoneObjs[zid] = new List<ObjectId>();
                        if (!zoneObjs[zid].Contains(ent.ObjectId))
                            zoneObjs[zid].Add(ent.ObjectId);
                        isZone = true;
                    }
                    if (!isZone) continue;
                    own.Collect(tr, ser, ent, XKeyTile);
                    cladOwn.Collect(tr, ser, ent, CladCommand.XKeyClad);
                    // зону двигали/меняли после ATFZONE — геометрия в _fzones.json
                    // устарела (как у ATCLAD): такую зону не раскладываем
                    var hat = ent as Hatch;
                    if (hat != null)
                    {
                        string hj = CladCommand.ReadData(tr, ent, CladCommand.XKeyZone);
                        var hz = hj == null ? null : ser.DeserializeObject(hj) as Dictionary<string, object>;
                        var hrep = CladCommand.Get(hz, "report") as Dictionary<string, object>;
                        string hzid = CladCommand.SafeStr(CladCommand.Get(hz, "zone_id"));
                        try
                        {
                            double fact = hat.Area / 1e6;
                            double stored = Convert.ToDouble(CladCommand.Get(hrep, "area_net_m2"),
                                                             CultureInfo.InvariantCulture);
                            if (hzid.Length > 0 && Math.Abs(fact - stored) > 0.001 && !zoneStale.Contains(hzid))
                                zoneStale.Add(hzid);
                        }
                        catch { }
                    }
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
                }
                tr.Commit();
            }
            // прежняя раскладка была ОБЩЕЙ для нескольких контуров/зон (слитые
            // смежные «A+B»), а выбрана часть — добираем остальных участников:
            // иначе плитки соседа удалились бы вместе с меткой, а заново
            // разложилась бы только выбранная часть
            int added = ExpandMembers(db, ser, own, zoneObjs, polyByHandle, contoursPayload);
            if (added > 0)
                ed.WriteMessage("\nПрежняя раскладка была общей для нескольких контуров — " +
                    "перекладываю их вместе (добавлено " + added + ").");
            if (own.Copied + cladOwn.Copied > 0)
                ed.WriteMessage("\nМетка раскладки скопирована вместе с объектом (" +
                    (own.Copied + cladOwn.Copied) + " шт.) — плитки оригинала не трогаю.");
            // 23.09c (ревью 23.09, Денис: «почини до сборки»): геометрия зон
            // ATFZONE — из <dwg>_fzones.json, как у ATCLAD. Раньше в движок
            // уходила метка ATFZONE (там нет геометрии) — по штриховке/марке
            // раскладка отказывала «нет пригодных зон». Контуры самой зоны из
            // выборки в «голые» не пускаем — иначе зона разложилась бы дважды.
            var partToRoot = new Dictionary<string, string>();
            // полилинии зон ATFZONE, попавшие в выборку рамкой: в «голые» не
            // идут, но их прежняя раскладка (прошлый запуск по полилиниям)
            // заменяется вместе с зоной — хэндл полилинии → корень зоны
            var dropRoot = new Dictionary<string, string>();
            if (zoneObjs.Count > 0)
            {
                var fz = CladCommand.LoadFzones(ed, db, ser);
                var dropIds = new HashSet<string>();
                var missing = new List<string>();
                foreach (var kv in zoneObjs)
                {
                    if (zoneStale.Contains(kv.Key)) continue;
                    var parts = CladCommand.FindZoneParts(fz, kv.Key);
                    if (parts.Count == 0) { missing.Add(kv.Key); continue; }
                    foreach (var part in parts)
                    {
                        string pid = CladCommand.SafeStr(CladCommand.Get(part, "id"));
                        partToRoot[pid] = kv.Key;
                        zonesPayload.Add(new Dictionary<string, object>
                        { { "zone_id", pid }, { "zone", part } });
                        string oid = CladCommand.MetaStr(part, "outer_contour_id");
                        if (oid != null) { dropIds.Add(oid); dropRoot[oid] = kv.Key; }
                        var ops = CladCommand.Get(part, "openings") as object[];
                        if (ops != null)
                            foreach (var o in ops)
                            {
                                var od = o as Dictionary<string, object>;
                                if (od == null) continue;
                                string opid = CladCommand.SafeStr(CladCommand.Get(od, "id"));
                                dropIds.Add(opid);
                                dropRoot[opid] = kv.Key;
                            }
                    }
                }
                if (dropIds.Count > 0)
                    contoursPayload.RemoveAll(c =>
                    {
                        var cd = c as Dictionary<string, object>;
                        return cd != null && dropIds.Contains(CladCommand.SafeStr(CladCommand.Get(cd, "id")));
                    });
                if (missing.Count > 0)
                    ed.WriteMessage("\nНет геометрии в _fzones.json для " + string.Join(", ", missing.ToArray()) +
                        " — зона пропущена (повторите ATFZONE или выберите её контуры).");
                if (zoneStale.Count > 0)
                    ed.WriteMessage("\nЗона изменена после ATFZONE (площадь штриховки не совпала): " +
                        string.Join(", ", zoneStale.ToArray()) + " — пропущена, повторите ATFZONE.");
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
            int cladCount = cladOwn.HandleCount();
            if (cladCount > 0)
            {
                var pk = new PromptKeywordOptions(
                    "\nНа выбранных зонах лежит раскладка ATCLAD (" + cladCount +
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
            // 23.09 (Герман, письмом): образец можно рисовать БЛОКАМИ, в т.ч.
            // динамическими; тип = слой блока, и раскладка кладёт плитки на
            // тот же слой тем же блоком (с его видимостью и прочими свойствами)
            var sampleElem = new Dictionary<string, SampleElem>();
            if (useSample)
            {
                var psS = new PromptSelectionOptions
                {
                    MessageForAdding = "\nВыберите ОБРАЗЕЦ (плитки — полилинии, штриховки или " +
                                       "блоки, в т.ч. динамические; тип = слой): "
                };
                var fS = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.Operator, "<or"),
                    new TypedValue((int)DxfCode.Start, "LWPOLYLINE"),
                    new TypedValue((int)DxfCode.Start, "HATCH"),
                    new TypedValue((int)DxfCode.Start, "INSERT"),
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
                        var sbr = ent as BlockReference;
                        // габарит блока — по КРИВЫМ определения (атрибуты и тексты
                        // не расширяют плитку — грабля 29)
                        Extents3d? ext = sbr != null ? CladCommand.CellExtents(tr, sbr) : SafeExtents(ent);
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
                        if (sbr != null && !sampleElem.ContainsKey(type))
                            sampleElem[type] = SampleElem.From(tr, sbr, e);
                    }
                    tr.Commit();
                }
                if (sample.Count < 2)
                { ed.WriteMessage("\nВ образце меньше двух плиток — нужен нарисованный раппорт."); return; }
                ws.Sort(); hs.Sort();
                double medW = ws[ws.Count / 2], medH = hs[hs.Count / 2];
                ed.WriteMessage("\nОбразец: " + sample.Count + " плиток" +
                    (multi ? ", типов " + typeColor.Count : "") + " (плитка ≈ " +
                    F0(medW) + "×" + F0(medH) + " мм)" +
                    (sampleElem.Count > 0 ? ", из них блоками — типов " + sampleElem.Count : "") + ".");
                foreach (var kv in sampleElem)
                    if (!kv.Value.Usable)
                        ed.WriteMessage("\n  ! блок образца типа «" + kv.Key + "» повёрнут/отражён/масштабирован — " +
                            "этим блоком не кладу (прямоугольник/элемент окна).");
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
            var sampleLayersUsed = new List<string>();
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
                var lt0 = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                bool onSample = multi && st.SampleLayers;
                // элемент типа — блок образца (кроме повёрнутых); без раскраски —
                // единственный блок образца, если элемент в окне не выбран
                var elemOf = new Dictionary<string, SampleElem>();
                foreach (var t in types)
                {
                    SampleElem se;
                    if (onSample && sampleElem.TryGetValue(t, out se) && se.Usable)
                        elemOf[t] = se;
                    else if (!multi && !dyn && sampleElem.Count == 1)
                        foreach (var v in sampleElem.Values) if (v.Usable) elemOf[t] = v;
                }
                foreach (var se in elemOf.Values) se.Probe(tr, ms);
                // обычный (не динамический) блок образца годится только для целых
                // плиток своего размера — иначе прямоугольник/элемент окна
                foreach (var t in new List<string>(elemOf.Keys))
                {
                    var se = elemOf[t];
                    if (!se.Dyn && (Math.Abs(se.Wd - st.W) > 1.0 || Math.Abs(se.Hd - st.H) > 1.0))
                    {
                        ed.WriteMessage("\n  ! блок образца типа «" + t + "» " + F0(se.Wd) + "×" + F0(se.Hd) +
                            " не динамический и не формата " + F0(st.W) + "×" + F0(st.H) +
                            " — целые плитки этого типа будут прямоугольником/элементом окна.");
                        elemOf.Remove(t);
                    }
                }
                foreach (var t in types)
                {
                    int aci = typeColor.ContainsKey(t) ? typeColor[t] : 7;
                    int rgb = typeTrue.ContainsKey(t) ? typeTrue[t] : -1;
                    bool keep = !multi;   // один тип — цвет существующего слоя не трогаем
                    SampleElem te;
                    bool hasTe = elemOf.TryGetValue(t, out te);
                    if (onSample && lt0.Has(t))
                    {
                        // 23.09 (Герман): тот же слой, что у образца — новых слоёв нет
                        fullLayer[t] = cutLayer[t] = t;
                        if (!sampleLayersUsed.Contains(t)) sampleLayersUsed.Add(t);
                    }
                    else
                    {
                        fullLayer[t] = MakeLayer(tr, db, st.LayerFor(t, multi), aci, rgb, keep);
                        cutLayer[t] = (dyn || (hasTe && te.Dyn)) ? fullLayer[t]
                            : MakeLayer(tr, db, st.CutLayerFor(t, multi), multi ? aci : 30, rgb, keep);
                    }
                    if (!dyn && !hasTe)
                        blockOf[t] = EnsureTileBlock(tr, db, bt, st.BlockFor(t, multi),
                                                     st.W, st.H, multi);
                }
                string smallLayer = null;   // слой подсветки малой подрезки — только если она есть

                // прежняя раскладка ATTILE этих зон (перегенерация) и ATCLAD (замена) —
                // ТОЛЬКО у владельцев, которые получили новый результат
                var okOwners = SuccessOwners(res, partToRoot, zoneObjs, polyByHandle);
                var dropOk = new List<ObjectId>();
                foreach (var kv in dropRoot)
                {
                    ObjectId pid0;
                    List<ObjectId> zl;
                    if (!polyByHandle.TryGetValue(kv.Key, out pid0) ||
                        !zoneObjs.TryGetValue(kv.Value, out zl)) continue;
                    bool rootOk = false;
                    foreach (var zo in zl) if (okOwners.Contains(zo)) { rootOk = true; break; }
                    if (rootOk) { okOwners.Add(pid0); dropOk.Add(pid0); }
                }
                var region = SelectionRegion(tr, zoneObjs, polyByHandle);
                int keptOut = 0;
                erased += own.EraseFor(tr, db, okOwners, region, ref keptOut);
                erased += cladOwn.EraseFor(tr, db, okOwners, region, ref keptOut);
                foreach (var oid in cladOwn.Owners)
                    if (okOwners.Contains(oid))
                    {
                        var te = tr.GetObject(oid, OpenMode.ForWrite) as Entity;
                        if (te != null) CladCommand.RemoveData(tr, te, CladCommand.XKeyClad);
                    }
                // у полилиний зоны старую метку снять — носитель теперь сама зона
                foreach (var pid0 in dropOk)
                {
                    var pe = tr.GetObject(pid0, OpenMode.ForWrite) as Entity;
                    if (pe != null) CladCommand.RemoveData(tr, pe, XKeyTile);
                }
                int keptZones = own.CountNotIn(okOwners) + cladOwn.CountNotIn(okOwners);
                if (keptZones > 0)
                    ed.WriteMessage("\nНе разложено (см. замечания): прежняя раскладка сохранена у " +
                        keptZones + " объект(ов) зон.");
                if (keptOut > 0)
                    ed.WriteMessage("\nПо старой метке " + keptOut + " прежних элементов лежат вне " +
                        "выбранных зон — не удалены (метка скопирована или зону переносили).");

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
                    SampleElem te;
                    bool hasTe = elemOf.TryGetValue(type, out te);
                    bool useDyn = !shapedPiece && (hasTe ? te.Dyn : dyn);
                    if (useDyn)
                    {
                        ObjectId defId = hasTe ? te.Def : dynDef;
                        string pw = hasTe ? te.W : dynW, ph = hasTe ? te.H : dynH;
                        double ox = hasTe ? te.OffX : 0.0, oy = hasTe ? te.OffY : 0.0;
                        var br = new BlockReference(new Point3d(x - ox, y - oy, 0), defId);
                        br.Layer = fullLayer[type];
                        ms.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        if (hasTe) te.ApplyLook(br);     // видимость/цвет/прочие свойства образца
                        bool okW = false, okH = false;
                        foreach (DynamicBlockReferenceProperty pr in
                                 br.DynamicBlockReferencePropertyCollection)
                        {
                            if (pr.ReadOnly) continue;
                            if (pr.PropertyName == pw) okW = CladCommand.TrySetNum(pr, w);
                            else if (pr.PropertyName == ph) okH = CladCommand.TrySetNum(pr, h);
                        }
                        if (!okW || !okH) dynFail++;
                        FillAttributes(tr, br, RootName(zone, partToRoot));
                        AddToMap(handlesByZone, zone, br.Handle.ToString());
                        made++;
                    }
                    else if (full)
                    {
                        bool sampleStatic = hasTe && !te.Dyn;
                        var br = new BlockReference(sampleStatic
                            ? new Point3d(x - te.OffX, y - te.OffY, 0) : new Point3d(x, y, 0),
                            sampleStatic ? te.Def : blockOf[type]);
                        br.Layer = fullLayer[type];
                        ms.AppendEntity(br);
                        tr.AddNewlyCreatedDBObject(br, true);
                        if (sampleStatic)
                        {
                            te.ApplyLook(br);
                            FillAttributes(tr, br, RootName(zone, partToRoot));
                        }
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
                        if (smallLayer == null) smallLayer = MakeLayer(tr, db, st.SmallLayer(), 1, -1, true);
                        var sp = MakePoly(rings[0], smallLayer);
                        sp.ConstantWidth = Math.Max(2.0, Math.Min(st.W, st.H) * 0.01);
                        ms.AppendEntity(sp);
                        tr.AddNewlyCreatedDBObject(sp, true);
                        AddToMap(handlesByZone, zone, sp.Handle.ToString());
                        smallMade++;
                    }
                }

                // метка ATTILE — ОДНА на объект-владельца: объединение всех частей
                // и слитых зон, попавших на него (24.09: несмежные части одной
                // зоны ATFZONE раньше затирали метку друг друга — повтор
                // задваивал забытую часть); owner — хэндл самого объекта
                // (метка, скопированная COPY, узнаётся по несовпадению)
                var perZone = CladCommand.Get(res, "per_zone") as object[];
                string stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                var agg = new Dictionary<ObjectId, LabelAgg>();
                if (perZone != null)
                    foreach (var pzObj in perZone)
                    {
                        var pz = pzObj as Dictionary<string, object>;
                        if (pz == null) continue;
                        string zid = CladCommand.SafeStr(CladCommand.Get(pz, "zone_id"));
                        List<string> hl;
                        if (!handlesByZone.TryGetValue(zid, out hl)) hl = new List<string>();
                        var mem = CladCommand.Get(pz, "members") as object[];
                        foreach (var mObj in mem ?? new object[] { zid })
                        {
                            string mm = CladCommand.SafeStr(mObj), root;
                            if (partToRoot.TryGetValue(mm, out root)) mm = root;
                            foreach (var oid in OwnersOf(mm, zoneObjs, polyByHandle))
                            {
                                LabelAgg la;
                                if (!agg.TryGetValue(oid, out la)) agg[oid] = la = new LabelAgg();
                                la.Add(zid, mem, hl, CladCommand.Get(pz, "joints_x") as object[],
                                       CladCommand.Get(pz, "rows_y") as object[]);
                            }
                        }
                    }
                foreach (var kv in agg)
                {
                    var ent = (Entity)tr.GetObject(kv.Key, OpenMode.ForWrite);
                    var la = kv.Value;
                    var meta = new Dictionary<string, object>
                    {
                        { "schema", 2 },
                        { "owner", ent.Handle.ToString() },
                        { "zone_id", string.Join("+", la.Zones.ToArray()) },
                        { "members", la.Members },
                        { "settings", st.ToDict() },
                        { "cladding", st.Name },
                        { "tile", new Dictionary<string, object> { { "w", st.W }, { "h", st.H } } },
                        { "gap", new Dictionary<string, object> { { "v", st.Gv }, { "h", st.Gh } } },
                        { "layer", st.LayerFor("", false) },
                        { "block", dyn ? st.Element : st.BlockFor("", false) },
                        // мост к ATFRAME: оси вертикальных швов (стойки) и
                        // центры горизонтальных (кляммеры) — всех частей владельца
                        { "joints_x", la.Jx },
                        { "rows_y", la.Ry },
                        { "tiles", la.Handles.Count },
                        { "handles", la.Handles },
                        { "stamp", stamp },
                        { "vjoints", vjoints },
                        { "hjoints", hjoints },
                    };
                    CladCommand.StoreData(tr, ent, ser.Serialize(meta), XKeyTile);
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
            if (sampleLayersUsed.Count > 0)
                ed.WriteMessage("\nПлитки — на слоях образца: " + string.Join(", ", sampleLayersUsed.ToArray()) +
                    " (новые слои не создавались); спецификация — ATSPEC по этим слоям.");
            else
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
        // 23.09 (Герман): блок образца — элемент своего типа: то же определение
        // (динамическое — с «ширина»/«высота» для подрезки), та же видимость,
        // цвет и прочие динсвойства, что у плитки образца
        internal class SampleElem
        {
            public ObjectId Def;
            public bool Usable;            // без поворота, отражения и масштаба
            public bool Dyn;               // есть «ширина»/«высота» — режем им же
            public string W, H;            // имена динсвойств размера
            public double OffX, OffY;      // точка вставки → левый нижний угол плитки
            public double Wd, Hd;          // размер плитки образца
            public Autodesk.AutoCAD.Colors.Color Color;
            public readonly Dictionary<string, object> Props = new Dictionary<string, object>();
            private bool _probed;

            public static SampleElem From(Transaction tr, BlockReference br, Extents3d ext)
            {
                var se = new SampleElem();
                se.Def = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var sc = br.ScaleFactors;
                se.Usable = Math.Abs(br.Rotation) < 1e-6 && Math.Abs(sc.X - 1) < 1e-6 &&
                            Math.Abs(sc.Y - 1) < 1e-6 && Math.Abs(sc.Z - 1) < 1e-6;
                se.OffX = ext.MinPoint.X - br.Position.X;
                se.OffY = ext.MinPoint.Y - br.Position.Y;
                se.Wd = ext.MaxPoint.X - ext.MinPoint.X;
                se.Hd = ext.MaxPoint.Y - ext.MinPoint.Y;
                se.Color = br.Color;
                try
                {
                    if (br.IsDynamicBlock)
                        foreach (DynamicBlockReferenceProperty pr in br.DynamicBlockReferencePropertyCollection)
                            if (!pr.ReadOnly && pr.PropertyName != "Origin")
                                se.Props[pr.PropertyName] = pr.Value;
                }
                catch { }
                return se;
            }

            public void Probe(Transaction tr, BlockTableRecord ms)
            {
                if (_probed) return;
                _probed = true;
                string w, h;
                Dyn = ProbeDyn(tr, ms, Def, out w, out h);
                W = w;
                H = h;
            }

            public void ApplyLook(BlockReference br)
            {
                try { if (Color != null) br.Color = Color; } catch { }
                if (!br.IsDynamicBlock) return;
                foreach (DynamicBlockReferenceProperty pr in br.DynamicBlockReferencePropertyCollection)
                {
                    if (pr.ReadOnly || pr.PropertyName == W || pr.PropertyName == H) continue;
                    object v;
                    if (Props.TryGetValue(pr.PropertyName, out v))
                        try { pr.Value = v; } catch { }
                }
            }
        }

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
        // ЗАХВАТКА — имя зоны этапа 1: части слитой зоны («Ф-1.1+Ф-1.2») → «Ф-1»
        private static string RootName(string zone, Dictionary<string, string> partToRoot)
        {
            if (string.IsNullOrEmpty(zone) || partToRoot.Count == 0) return zone;
            var outp = new List<string>();
            foreach (var part in zone.Split('+'))
            {
                string r;
                string nm = partToRoot.TryGetValue(part, out r) ? r : part;
                if (!outp.Contains(nm)) outp.Add(nm);
            }
            return string.Join("+", outp.ToArray());
        }

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

        // ── 24.09: владельцы прежней раскладки ──
        internal class OwnedLabels
        {
            // доверенные метки (owner = хэндл объекта) и старые (без owner)
            public readonly Dictionary<ObjectId, List<string>> Trusted = new Dictionary<ObjectId, List<string>>();
            public readonly Dictionary<ObjectId, List<string>> Legacy = new Dictionary<ObjectId, List<string>>();
            public readonly Dictionary<ObjectId, List<string>> Members = new Dictionary<ObjectId, List<string>>();
            public int Copied;

            public IEnumerable<ObjectId> Owners
            {
                get
                {
                    foreach (var k in Trusted.Keys) yield return k;
                    foreach (var k in Legacy.Keys) yield return k;
                }
            }

            public void Collect(Transaction tr, JavaScriptSerializer ser, Entity ent, string key)
            {
                if (Trusted.ContainsKey(ent.ObjectId) || Legacy.ContainsKey(ent.ObjectId)) return;
                var m = ReadMeta(tr, ser, ent, key);
                var hs = CladCommand.Get(m, "handles") as object[];
                if (hs == null) return;
                string owner = CladCommand.SafeStr(CladCommand.Get(m, "owner"));
                if (owner.Length > 0 && owner != ent.Handle.ToString()) { Copied++; return; }
                var l = new List<string>();
                foreach (var h in hs) l.Add(CladCommand.SafeStr(h));
                (owner.Length > 0 ? Trusted : Legacy)[ent.ObjectId] = l;
                var mem = CladCommand.Get(m, "members") as object[];
                if (mem != null)
                {
                    var ml = new List<string>();
                    foreach (var x in mem) ml.Add(CladCommand.SafeStr(x));
                    Members[ent.ObjectId] = ml;
                }
            }

            public int EraseFor(Transaction tr, Database db, HashSet<ObjectId> ok, Extents3d? region, ref int keptOut)
            {
                int n = 0;
                foreach (var kv in Trusted)
                    if (ok.Contains(kv.Key)) n += EraseByHandles(tr, db, kv.Value);
                foreach (var kv in Legacy)
                    if (ok.Contains(kv.Key)) n += EraseInside(tr, db, kv.Value, region, ref keptOut);
                return n;
            }

            public int HandleCount()
            {
                var hs = new HashSet<string>();
                foreach (var l in Trusted.Values) foreach (var h in l) hs.Add(h);
                foreach (var l in Legacy.Values) foreach (var h in l) hs.Add(h);
                return hs.Count;
            }

            public int CountNotIn(HashSet<ObjectId> ok)
            {
                int n = 0;
                foreach (var k in Owners) if (!ok.Contains(k)) n++;
                return n;
            }
        }

        internal class LabelAgg
        {
            public readonly List<string> Zones = new List<string>();
            public readonly List<string> Members = new List<string>();
            public readonly List<string> Handles = new List<string>();
            public readonly List<object> Jx = new List<object>();
            public readonly List<object> Ry = new List<object>();
            private readonly HashSet<string> _h = new HashSet<string>();

            public void Add(string zid, object[] mem, List<string> hl, object[] jx, object[] ry)
            {
                if (!Zones.Contains(zid)) Zones.Add(zid);
                foreach (var mo in mem ?? new object[] { zid })
                {
                    string ms = CladCommand.SafeStr(mo);
                    if (!Members.Contains(ms)) Members.Add(ms);
                }
                foreach (var h in hl) if (_h.Add(h)) Handles.Add(h);
                if (jx != null) foreach (var v in jx) if (!Jx.Contains(v)) Jx.Add(v);
                if (ry != null) foreach (var v in ry) if (!Ry.Contains(v)) Ry.Add(v);
            }
        }

        // владельцы, у которых есть результат этого запуска (зона в per_zone)
        private static HashSet<ObjectId> SuccessOwners(Dictionary<string, object> res,
            Dictionary<string, string> partToRoot, Dictionary<string, List<ObjectId>> zoneObjs,
            Dictionary<string, ObjectId> polyByHandle)
        {
            var ok = new HashSet<ObjectId>();
            var perZone = CladCommand.Get(res, "per_zone") as object[];
            if (perZone == null) return ok;
            foreach (var pzObj in perZone)
            {
                var pz = pzObj as Dictionary<string, object>;
                if (pz == null) continue;
                string zid = CladCommand.SafeStr(CladCommand.Get(pz, "zone_id"));
                var mem = CladCommand.Get(pz, "members") as object[];
                foreach (var mObj in mem ?? new object[] { zid })
                {
                    string mm = CladCommand.SafeStr(mObj), root;
                    if (partToRoot.TryGetValue(mm, out root)) mm = root;
                    foreach (var oid in OwnersOf(mm, zoneObjs, polyByHandle)) ok.Add(oid);
                }
            }
            return ok;
        }

        // габарит выбранных зон (+100 мм) — для старых меток без owner:
        // удаляем только элементы, лежащие на выбранных зонах
        internal static Extents3d? SelectionRegion(Transaction tr,
            Dictionary<string, List<ObjectId>> zoneObjs, Dictionary<string, ObjectId> polyByHandle)
        {
            Extents3d? r = null;
            var ids = new List<ObjectId>(polyByHandle.Values);
            foreach (var l in zoneObjs.Values) ids.AddRange(l);
            foreach (var oid in ids)
            {
                try
                {
                    var e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (e == null || e is MText || e is DBText) continue;
                    var ex = e.GeometricExtents;
                    if (r == null) r = ex;
                    else { var t = r.Value; t.AddExtents(ex); r = t; }
                }
                catch { }
            }
            if (r == null) return null;
            var v = new Vector3d(100, 100, 0);
            return new Extents3d(r.Value.MinPoint - v, r.Value.MaxPoint + v);
        }

        internal static int EraseInside(Transaction tr, Database db, IEnumerable<string> handles,
                                        Extents3d? region, ref int keptOut)
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
                var e = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                if (e == null) continue;
                if (region != null)
                {
                    Point3d c;
                    try
                    {
                        var ex = e.GeometricExtents;
                        c = new Point3d(0.5 * (ex.MinPoint.X + ex.MaxPoint.X), 0.5 * (ex.MinPoint.Y + ex.MaxPoint.Y), 0);
                    }
                    catch { continue; }
                    var r = region.Value;
                    if (c.X < r.MinPoint.X || c.X > r.MaxPoint.X || c.Y < r.MinPoint.Y || c.Y > r.MaxPoint.Y)
                    { keptOut++; continue; }
                }
                e.UpgradeOpen();
                e.Erase();
                n++;
            }
            return n;
        }

        // добрать остальных участников прежней общей раскладки (слитые A+B):
        // «контур <хэндл>» — полилиния по хэндлу; иначе — зона ATFZONE по имени
        private static int ExpandMembers(Database db, JavaScriptSerializer ser, OwnedLabels own,
            Dictionary<string, List<ObjectId>> zoneObjs, Dictionary<string, ObjectId> polyByHandle,
            List<object> contoursPayload)
        {
            var want = new List<string>();
            foreach (var ml in own.Members.Values)
                foreach (var m in ml)
                {
                    string root = m;
                    int dot = root.LastIndexOf('.');
                    bool isPoly = root.StartsWith("контур ");
                    if (isPoly && polyByHandle.ContainsKey(root.Substring(7))) continue;
                    if (!isPoly && zoneObjs.ContainsKey(root)) continue;
                    if (!isPoly && dot > 0 && zoneObjs.ContainsKey(root.Substring(0, dot))) continue;
                    if (!want.Contains(root)) want.Add(root);
                }
            if (want.Count == 0) return 0;
            int added = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var zoneNames = new HashSet<string>();
                foreach (var w in want)
                {
                    if (!w.StartsWith("контур ")) { zoneNames.Add(w); continue; }
                    long v;
                    ObjectId id;
                    if (!long.TryParse(w.Substring(7), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) ||
                        !db.TryGetObjectId(new Handle(v), out id) || id.IsNull || id.IsErased)
                        continue;
                    var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (pl == null || pl.NumberOfVertices < 3) continue;
                    var pts = new List<object>();
                    var bulges = new List<object>();
                    for (int i = 0; i < pl.NumberOfVertices; i++)
                    {
                        Point2d p = pl.GetPoint2dAt(i);
                        pts.Add(new[] { p.X, p.Y });
                        bulges.Add(pl.GetBulgeAt(i));
                    }
                    string h = pl.Handle.ToString();
                    polyByHandle[h] = pl.ObjectId;
                    contoursPayload.Add(new Dictionary<string, object>
                    { { "id", h }, { "pts", pts }, { "bulges", bulges } });
                    own.Collect(tr, ser, pl, XKeyTile);
                    added++;
                }
                if (zoneNames.Count > 0)
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId oid in ms)
                    {
                        var e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (e == null || !(e is Hatch || e is MText)) continue;
                        string zj = CladCommand.ReadData(tr, e, CladCommand.XKeyZone);
                        if (zj == null) continue;
                        var zd = ser.DeserializeObject(zj) as Dictionary<string, object>;
                        string zid = CladCommand.SafeStr(CladCommand.Get(zd, "zone_id"));
                        string hit = null;
                        foreach (var zn in zoneNames)
                            if (zn == zid || zn.StartsWith(zid + ".")) { hit = zid; break; }
                        if (hit == null) continue;
                        if (!zoneObjs.ContainsKey(hit)) { zoneObjs[hit] = new List<ObjectId>(); added++; }
                        if (!zoneObjs[hit].Contains(oid)) zoneObjs[hit].Add(oid);
                        own.Collect(tr, ser, e, XKeyTile);
                    }
                }
                tr.Commit();
            }
            return added;
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
