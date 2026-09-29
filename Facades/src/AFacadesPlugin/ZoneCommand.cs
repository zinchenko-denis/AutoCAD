using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AFacadesPlugin.ZoneCommand))]

namespace AFacadesPlugin
{
    /// <summary>
    /// ATFZONE — этап 1 по ТЗ Германа: пользователь выбирает замкнутые
    /// полилинии (внешние контуры зон облицовки + контуры проёмов внутри),
    /// движок группирует их по вложенности, валидирует и считает площади/
    /// погонажи; команда создаёт слой с именем облицовки, переносит контуры,
    /// штрихует зоны с вычетом проёмов, ставит марку+площадь, вставляет
    /// таблицу и пишет *_fzones.json (вход следующих модулей).
    /// </summary>
    public class ZoneCommand
    {
        private const double CloseTol = 0.5;   // мм: зазор «замкнутости»

        [CommandMethod("ATFZONE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            // 24.09 (рецензия): ошибка штриховки/слоя/повреждённой метки
            // обрывала команду без объяснения
            try { RunCore(doc); }
            catch (System.Exception ex)
            {
                try
                {
                    doc.Editor.WriteMessage("\nATFZONE: внутренняя ошибка — сообщите " +
                        "разработчику.\n" + ex.ToString() + "\n");
                }
                catch { }
            }
        }

        private void RunCore(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            var db = doc.Database;

            // ── 1. выбор контуров (внешние + проёмы одной рамкой) ──
            var pso = new PromptSelectionOptions
            {
                MessageForAdding = "\nВыберите замкнутые контуры зон " +
                                   "облицовки и проёмов внутри них: "
            };
            var filter = new SelectionFilter(new[]
            { new TypedValue((int)DxfCode.Start, "LWPOLYLINE") });
            var sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            var contours = new List<Dictionary<string, object>>();
            var idByHandle = new Dictionary<string, ObjectId>();
            var skipped = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    var pl = tr.GetObject(so.ObjectId, OpenMode.ForRead)
                             as Polyline;
                    if (pl == null) continue;
                    int n = pl.NumberOfVertices;
                    if (n < 3) { skipped.Add(Hnd(pl) + " (<3 вершин)"); continue; }
                    var pts = new List<object>();
                    var bulges = new List<object>();
                    for (int i = 0; i < n; i++)
                    {
                        Point2d p = pl.GetPoint2dAt(i);
                        pts.Add(new[] { p.X, p.Y });
                        bulges.Add(pl.GetBulgeAt(i));
                    }
                    bool closed = pl.Closed;
                    if (!closed)
                    {
                        Point2d a = pl.GetPoint2dAt(0),
                                b = pl.GetPoint2dAt(n - 1);
                        closed = a.GetDistanceTo(b) <= CloseTol;
                    }
                    if (!closed)
                    { skipped.Add(Hnd(pl) + " (не замкнута)"); continue; }
                    string h = Hnd(pl);
                    idByHandle[h] = pl.ObjectId;
                    contours.Add(new Dictionary<string, object>
                    { { "id", h }, { "pts", pts }, { "bulges", bulges } });
                }
                tr.Commit();
            }
            if (contours.Count == 0)
            {
                ed.WriteMessage("\nНет пригодных контуров." + SkippedMsg(skipped));
                return;
            }

            // ── 2. параметры (кнопка «+ Добавить контуры» — фидбэк №2 п.3:
            //    забыл контур — доклкнуть, не выбирая всё заново).
            //    07.08 (просьба Германа): в комбо наименования — ещё и
            //    существующие слои чертежа (выбор слоя из списка) ──
            var layerNames = new List<string>();
            using (var trl = db.TransactionManager.StartTransaction())
            {
                var ltz = (LayerTable)trl.GetObject(db.LayerTableId,
                                                    OpenMode.ForRead);
                foreach (ObjectId lid in ltz)
                {
                    var ltr = trl.GetObject(lid, OpenMode.ForRead)
                              as LayerTableRecord;
                    if (ltr != null) layerNames.Add(ltr.Name);
                }
                trl.Commit();
            }
            layerNames.Sort(StringComparer.CurrentCultureIgnoreCase);
            // 24.09 (рецензия): после перезапуска AutoCAD нумерация начиналась
            // с 1 и могла повторить марку, уже стоящую в чертеже (второй
            // «Ф-1» затирал геометрию первого в _fzones.json). Предлагаем
            // следующий свободный номер; совпадения — вопрос после расчёта.
            var existingIds = ExistingZoneIds(db);
            int maxTail = 0;
            foreach (var zid0 in existingIds)
            {
                int t0 = TailNumber(zid0);
                if (t0 > maxTail) maxTail = t0;
            }
            if (maxTail + 1 > ZoneForm.NextStart) ZoneForm.NextStart = maxTail + 1;
            ZoneForm form = new ZoneForm(contours.Count, layerNames);
            form.AddPicker = delegate ()
            {
                // паттерн ATableSpec (ReportBuilderForm): try/finally + End.
                // Фидбэк Германа 21.07 п.3: прежний выбор НЕ пропадает —
                // накапливается (дедуп по хэндлу); чтобы это было ВИДНО,
                // уже выбранные контуры подсвечиваются на время добора,
                // а подсказка называет их число
                EditorUserInteraction ui = null;
                var lit = new List<ObjectId>(idByHandle.Values);
                try
                {
                    ui = ed.StartUserInteraction(form);
                    using (var trH = db.TransactionManager.StartTransaction())
                    {
                        foreach (var hid in lit)
                            try
                            {
                                ((Entity)trH.GetObject(hid,
                                    OpenMode.ForRead)).Highlight();
                            }
                            catch { }
                        trH.Commit();
                    }
                    var psoAdd = new PromptSelectionOptions
                    {
                        MessageForAdding = "\nУже выбрано контуров: " +
                            contours.Count + " (подсвечены, сохраняются)" +
                            " — укажите добавляемые: "
                    };
                    var selAdd = ed.GetSelection(psoAdd, filter);
                    if (selAdd.Status == PromptStatus.OK)
                        using (var tr2 = db.TransactionManager
                                           .StartTransaction())
                        {
                            foreach (SelectedObject so in selAdd.Value)
                            {
                                var pl = tr2.GetObject(so.ObjectId,
                                    OpenMode.ForRead) as Polyline;
                                if (pl == null) continue;
                                string h = Hnd(pl);
                                if (idByHandle.ContainsKey(h)) continue;
                                int n = pl.NumberOfVertices;
                                if (n < 3)
                                { skipped.Add(h + " (<3 вершин)"); continue; }
                                var pts = new List<object>();
                                var bulges = new List<object>();
                                for (int i = 0; i < n; i++)
                                {
                                    Point2d p = pl.GetPoint2dAt(i);
                                    pts.Add(new[] { p.X, p.Y });
                                    bulges.Add(pl.GetBulgeAt(i));
                                }
                                bool closed = pl.Closed ||
                                    pl.GetPoint2dAt(0).GetDistanceTo(
                                        pl.GetPoint2dAt(n - 1)) <= CloseTol;
                                if (!closed)
                                { skipped.Add(h + " (не замкнута)"); continue; }
                                idByHandle[h] = pl.ObjectId;
                                contours.Add(new Dictionary<string, object>
                                { { "id", h }, { "pts", pts },
                                  { "bulges", bulges } });
                            }
                            tr2.Commit();
                        }
                }
                finally
                {
                    // снять временную подсветку прежних
                    using (var trH = db.TransactionManager
                                       .StartTransaction())
                    {
                        foreach (var hid in lit)
                            try
                            {
                                ((Entity)trH.GetObject(hid,
                                    OpenMode.ForRead)).Unhighlight();
                            }
                            catch { }
                        trH.Commit();
                    }
                    if (ui != null) ui.End();
                }
                return contours.Count;
            };
            if (AcApp.ShowModalDialog(form) != DialogResult.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            // ── 2б. типы проёмов и парапет (29.09, просьба Германа: «разбить проёмы на
            //    окна, витражи и двери»; «выбор пользователю зоны парапета»). Чтобы меньше
            //    кликать — спрашиваем только витражи и двери, остальные проёмы — окна;
            //    парапет — свои замкнутые контуры. Флажок в окне выключает вопросы ──
            var kinds = new Dictionary<string, object>();
            var parapetContours = new List<Dictionary<string, object>>();
            var parapetIds = new Dictionary<string, ObjectId>();
            if (form.AskKinds)
            {
                if (contours.Count > 1)
                {
                    List<string> vit, drs;
                    if (!PickHandles(ed, "\nВыберите ВИТРАЖИ среди проёмов (Enter — нет): ",
                                     filter, idByHandle, out vit))
                    { ed.WriteMessage("\nОтменено."); return; }
                    foreach (var h in vit) kinds[h] = "vitrage";
                    if (!PickHandles(ed, "\nВыберите ДВЕРИ среди проёмов (Enter — нет): ",
                                     filter, idByHandle, out drs))
                    { ed.WriteMessage("\nОтменено."); return; }
                    int both = 0;
                    foreach (var h in drs) { if (kinds.ContainsKey(h)) both++; kinds[h] = "door"; }
                    if (both > 0)
                        ed.WriteMessage("\n  " + both + " проём(ов) выбраны и витражом, и дверью — считаю дверью.");
                    ed.WriteMessage("\n  витражей " + (vit.Count - both) + ", дверей " + drs.Count +
                                    "; остальные проёмы — окна.");
                }
                if (!PickParapets(ed, db, filter, parapetContours, parapetIds, skipped))
                { ed.WriteMessage("\nОтменено."); return; }
                if (parapetIds.Count > 0)
                {
                    // контур, выбранный и зоной/проёмом, и парапетом, — только парапет
                    int moved = contours.RemoveAll(c => parapetIds.ContainsKey(SafeStr(Get(c, "id"))));
                    foreach (var h in parapetIds.Keys) { idByHandle.Remove(h); kinds.Remove(h); }
                    if (moved > 0)
                        ed.WriteMessage("\n  " + moved + " контур(ов) выбраны и зоной, и парапетом — считаю парапетом.");
                    ed.WriteMessage("\n  контуров парапета: " + parapetIds.Count + ".");
                }
            }
            if (contours.Count == 0 && parapetContours.Count == 0)
            { ed.WriteMessage("\nНет контуров зон."); return; }

            // ── 3. движок ──
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var payload = new Dictionary<string, object>
            {
                { "op", "zones" },
                { "contours", contours },
                { "cladding", form.Cladding },
                { "zone_prefix", form.Prefix },
                { "start_index", form.StartIndex },
                { "units", "mm" },
                { "merge", form.MergeZones },
                { "opening_kinds", kinds },
                { "parapets", parapetContours },
            };
            string baseDir = Path.GetDirectoryName(
                Assembly.GetExecutingAssembly().Location) ?? ".";
            string engineExe = Path.GetFullPath(Path.Combine(
                baseDir, "..", "engine", "facades_engine.exe"));
            Dictionary<string, object> res;
            try
            {
                res = ser.DeserializeObject(
                    CallEngine(engineExe, ser.Serialize(payload)))
                    as Dictionary<string, object>;
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nОшибка движка: " + ex.Message);
                return;
            }
            if (res == null || !GetBool(res, "ok"))
            {
                ed.WriteMessage("\nДвижок отказал: " +
                    SafeStr(Get(res, "error")) + SkippedMsg(skipped));
                return;
            }

            PrintIssues(ed, Get(res, "issues") as object[], "разбор контуров");
            var failed = Get(res, "failed") as object[];
            if (failed != null)
                foreach (var f in failed)
                {
                    var fd = f as Dictionary<string, object>;
                    if (fd == null) continue;
                    ed.WriteMessage("\nЗона " + SafeStr(Get(fd, "zone_id")) +
                        " (контур " + SafeStr(Get(fd, "outer_id")) +
                        ") НЕ ПРИНЯТА:");
                    PrintIssues(ed, Get(fd, "issues") as object[], null);
                }

            var zones = (Get(res, "zones") as object[]) ?? new object[0];
            var parapets = (Get(res, "parapets") as object[]) ?? new object[0];
            if (zones.Length == 0 && parapets.Length == 0)
            {
                ed.WriteMessage("\nНи одной валидной зоны." + SkippedMsg(skipped));
                return;
            }

            var dupIds = new List<string>();
            foreach (var zo in zones)
            {
                string zid1 = SafeStr(Get(zo as Dictionary<string, object>, "zone_id"));
                if (existingIds.Contains(zid1)) dupIds.Add(zid1);
            }
            if (dupIds.Count > 0)
            {
                var pkd = new PromptKeywordOptions(
                    "\nМарки уже есть в чертеже: " + string.Join(", ", dupIds.ToArray()) +
                    " — создать ещё раз [Да/Нет] <Нет>: ", "Да Нет");
                var rkd = ed.GetKeywords(pkd);
                if (rkd.Status != PromptStatus.OK || rkd.StringResult != "Да")
                {
                    ed.WriteMessage("\nОтменено — задайте другой начальный номер или префикс.");
                    return;
                }
            }

            // ── 4. точка таблицы (до транзакции — один undo-шаг на всё) ──
            Point3d tablePt = Point3d.Origin;
            bool doTable = form.MakeTable;
            if (doTable)
            {
                var ppr = ed.GetPoint("\nТочка вставки таблицы (Esc — отмена команды): ");
                if (ppr.Status == PromptStatus.OK) tablePt = ppr.Value;
                else if (ppr.Status == PromptStatus.Cancel)
                { ed.WriteMessage("\nОтменено — зоны не созданы."); return; }
                else doTable = false;
            }

            // ── 5. правки чертежа ──
            string layer = LayerName(form.Cladding);
            int made = 0, linesMade = 0, linesErased = 0;
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                EnsureLayer(tr, db, layer);
                var btr = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);

                foreach (var zo in zones)
                {
                    var z = zo as Dictionary<string, object>;
                    if (z == null) continue;
                    var rep = Get(z, "report") as Dictionary<string, object>;
                    string zoneId = SafeStr(Get(z, "zone_id"));

                    // внешние контуры (при объединении их несколько) и проёмы
                    var outIds = new List<ObjectId>();
                    var outHs = Get(z, "outer_ids") as object[];
                    if (outHs != null)
                        foreach (var oh in outHs)
                        {
                            string s = SafeStr(oh);
                            if (idByHandle.ContainsKey(s))
                                outIds.Add(idByHandle[s]);
                        }
                    if (outIds.Count == 0) continue;
                    var holeIds = new List<ObjectId>();
                    var opIds = Get(z, "opening_ids") as object[];
                    if (opIds != null)
                        foreach (var oh in opIds)
                        {
                            string s = SafeStr(oh);
                            if (idByHandle.ContainsKey(s))
                                holeIds.Add(idByHandle[s]);
                        }
                    foreach (var lid in outIds)
                    {
                        var ent = (Entity)tr.GetObject(lid, OpenMode.ForWrite);
                        ent.Layer = layer;
                    }
                    // 29.09 (Герман): контуры проёмов — на слои по типу (окна / витражи /
                    // двери); прежние линии схемы этих проёмов (повторный ATFZONE) — долой
                    var okinds = Get(z, "opening_kinds") as Dictionary<string, object>;
                    var holeByH = new Dictionary<string, Entity>();
                    if (opIds != null)
                        foreach (var oh in opIds)
                        {
                            string s = SafeStr(oh);
                            if (!idByHandle.ContainsKey(s)) continue;
                            var ent = (Entity)tr.GetObject(idByHandle[s], OpenMode.ForWrite);
                            ent.Layer = OpeningLayer(tr, db, SafeStr(Get(okinds, s)));
                            linesErased += EraseOldLines(tr, db, ser, ent);
                            holeByH[s] = ent;
                        }

                    // ЕДИНАЯ штриховка зоны (при объединении — все части
                    // одной штриховкой, фидбэк №2 п.1) с вычетом проёмов
                    var hat = new Hatch();
                    btr.AppendEntity(hat);
                    tr.AddNewlyCreatedDBObject(hat, true);
                    hat.SetDatabaseDefaults();
                    hat.Layer = layer;
                    hat.ColorIndex = form.ColorAci;
                    if (form.Pattern != "SOLID")
                        hat.PatternScale = form.PatternScale;
                    // имя образца может быть введено руками и отсутствовать
                    // в acad.pat/acadiso.pat — фолбэк ANSI31 (21.07 п.2)
                    try
                    {
                        hat.SetHatchPattern(HatchPatternType.PreDefined,
                            form.Pattern.Length > 0 ? form.Pattern
                                                    : "ANSI31");
                    }
                    catch
                    {
                        hat.SetHatchPattern(HatchPatternType.PreDefined,
                                            "ANSI31");
                        ed.WriteMessage("\nШтриховка «" + form.Pattern +
                            "» не найдена среди образцов — применена " +
                            "ANSI31.");
                    }
                    hat.HatchStyle = HatchStyle.Normal;
                    hat.Associative = true;
                    foreach (var oid in outIds)
                        hat.AppendLoop(HatchLoopTypes.External,
                            new ObjectIdCollection(new[] { oid }));
                    foreach (var hid in holeIds)
                        hat.AppendLoop(HatchLoopTypes.Default,
                            new ObjectIdCollection(new[] { hid }));
                    hat.EvaluateHatch(true);

                    // марка + площадь у ПРАВОГО ВЕРХНЕГО угла области
                    // (фидбэк Германа 21.07 п.1, было — центроид): движок
                    // отдаёт правый верхний угол контура, текст прижат к
                    // нему изнутри (TopRight + отступ полвысоты текста).
                    // Наименование облицовки в марке НЕ пишем (19.07) —
                    // только марка и S нетто; облицовка = имя слоя
                    var lp = Get(z, "label_pt") as object[];
                    double lx = lp != null ? ToD(lp[0]) : 0,
                           ly = lp != null ? ToD(lp[1]) : 0;
                    double off = 0.5 * form.TextHeight;
                    var mt = new MText
                    {
                        Location = new Point3d(lx - off, ly - off, 0),
                        TextHeight = form.TextHeight,
                        Layer = layer,
                        Attachment = AttachmentPoint.TopRight,
                        Contents = zoneId + @"\PS = " +
                                   F3(Get(rep, "area_net_m2")) + " м²",
                    };
                    btr.AppendEntity(mt);
                    tr.AddNewlyCreatedDBObject(mt, true);

                    // данные зоны — в Xrecord штриховки И марки (фидбэк
                    // Германа 19.07: отчёт должен собираться ПОТОМ командой
                    // ATFTABLE по выбору штриховок/марок)
                    string zdata = ser.Serialize(new Dictionary<string, object>
                    {
                        { "zone_id", zoneId },
                        { "cladding", form.Cladding },
                        { "report", rep },
                    });
                    StoreZoneData(tr, hat, zdata);
                    StoreZoneData(tr, mt, zdata);

                    // 29.09 (Герман): исполнительная схема — откосы и отливы окон, откосы
                    // дверей, примыкания витражей — полилиниями по кромкам проёмов на своих
                    // слоях; хэндлы линий — в метке проёма (повтор ATFZONE их заменит)
                    var lns = Get(z, "lines") as object[];
                    var newByHole = new Dictionary<string, List<string>>();
                    if (lns != null)
                        foreach (var lo in lns)
                        {
                            var ld = lo as Dictionary<string, object>;
                            var lpts = Get(ld, "pts") as object[];
                            if (ld == null || lpts == null || lpts.Length < 2) continue;
                            string lcat = SafeStr(Get(ld, "cat"));
                            var lpl = new Polyline();
                            for (int vi = 0; vi < lpts.Length; vi++)
                            {
                                var xy = lpts[vi] as object[];
                                if (xy == null || xy.Length < 2) continue;
                                lpl.AddVertexAt(lpl.NumberOfVertices, new Point2d(ToD(xy[0]), ToD(xy[1])), 0, 0, 0);
                            }
                            if (lpl.NumberOfVertices < 2) { lpl.Dispose(); continue; }
                            lpl.Layer = LineLayer(tr, db, lcat);
                            btr.AppendEntity(lpl);
                            tr.AddNewlyCreatedDBObject(lpl, true);
                            string loid = SafeStr(Get(ld, "opening_id"));
                            List<string> lst;
                            if (!newByHole.TryGetValue(loid, out lst)) newByHole[loid] = lst = new List<string>();
                            lst.Add(Hnd(lpl));
                            linesMade++;
                        }
                    foreach (var kvh in holeByH)
                    {
                        List<string> lst;
                        if (!newByHole.TryGetValue(kvh.Key, out lst)) lst = new List<string>();
                        StoreZoneData(tr, kvh.Value, ser.Serialize(lst), LinesKey);
                    }

                    // линейные размеры по образцу Германа («Проба 4»):
                    // высота каждой части; цепочка по низу + габарит — только
                    // при разрывах нижней кромки (двери/проёмы в пол)
                    if (form.MakeDims)
                    {
                        var dimsArr = Get(z, "dims") as object[];
                        if (dimsArr != null)
                            DrawDims(tr, db, btr, dimsArr, form.TextHeight);
                    }
                    made++;
                }

                // 29.09 (Герман): парапеты — контуры на слой «Парапет», метка у правого
                // верхнего угла; данные — в метке (для ATFTABLE). 29.09k (Герман, ответ на
                // 9з–9и PDF №27): только погонные метры по верху — без площади («развёртки
                // на разных объектах разные») и без марок/нумерации — «просто парапет»
                var parapetRows = new List<Dictionary<string, object>>();
                foreach (var po in parapets)
                {
                    var pd = po as Dictionary<string, object>;
                    string pid = SafeStr(Get(pd, "id"));
                    ObjectId poid;
                    if (pd == null || !parapetIds.TryGetValue(pid, out poid)) continue;
                    var pent = (Entity)tr.GetObject(poid, OpenMode.ForWrite);
                    pent.Layer = EnsureNamed(tr, db, LayerParapet, 2);
                    string ptop = F3(Get(pd, "top_m"));
                    var plp = Get(pd, "label_pt") as object[];
                    double off2 = 0.5 * form.TextHeight;
                    var pmt = new MText
                    {
                        Location = new Point3d((plp != null ? ToD(plp[0]) : 0) - off2,
                                               (plp != null ? ToD(plp[1]) : 0) - off2, 0),
                        TextHeight = form.TextHeight,
                        Layer = LayerParapet,
                        Attachment = AttachmentPoint.TopRight,
                        Contents = @"Парапет\PL = " + ptop + " м.п.",
                    };
                    btr.AppendEntity(pmt);
                    tr.AddNewlyCreatedDBObject(pmt, true);
                    StoreZoneData(tr, pmt, ser.Serialize(pd), ParapetKey);
                    parapetRows.Add(pd);
                    var pws = Get(pd, "warnings") as object[];
                    if (pws != null)
                        foreach (var pw in pws)
                            ed.WriteMessage("\n  ! парапет (L = " + ptop + " м.п.): " + SafeStr(pw));
                }

                if (doTable)
                {
                    var rowsData = new List<Dictionary<string, object>>();
                    foreach (var zo in zones)
                    {
                        var z = zo as Dictionary<string, object>;
                        if (z != null) rowsData.Add(z);
                    }
                    InsertTable(tr, db, btr, tablePt, rowsData, parapetRows,
                                form.TextHeight);
                }

                tr.Commit();
            }

            // ── 6. JSON рядом с чертежом ──
            if (form.WriteJson)
                WriteZonesJson(ed, db, ser, Get(res, "zones_full") as object[]);

            ZoneForm.NextStart = form.StartIndex + made;
            var sum = Get(res, "summary") as Dictionary<string, object>;
            ed.WriteMessage("\nATFZONE: зон " + made +
                "; S нетто " + F3(Get(sum, "area_net_total_m2")) + " м²" +
                "; отливы " + F3(Get(sum, "sills_total_m")) + " м.п." +
                "; откосы " + F3(Get(sum, "jambs_total_m")) + " м.п." +
                "; слой «" + layer + "»." + SkippedMsg(skipped));
            // 29.09 (Герман): проёмы по типам, погонаж по категориям, парапеты, линии схемы
            ed.WriteMessage("\n  проёмы: окна " + SafeStr(Get(sum, "window_count")) + " (" +
                F3(Get(sum, "window_area_m2")) + " м²), витражи " + SafeStr(Get(sum, "vitrage_count")) +
                " (" + F3(Get(sum, "vitrage_area_m2")) + " м²), двери " + SafeStr(Get(sum, "door_count")) +
                " (" + F3(Get(sum, "door_area_m2")) + " м²); откосы окон " + F3(Get(sum, "window_slopes_m")) +
                ", отливы окон " + F3(Get(sum, "window_sills_m")) + ", откосы дверей " +
                F3(Get(sum, "door_slopes_m")) + ", примыкания витражей " +
                F3(ToD(Get(sum, "vitrage_side_m")) + ToD(Get(sum, "vitrage_top_m")) +
                   ToD(Get(sum, "vitrage_bottom_m"))) + " м.п." +
                (parapets.Length > 0 ? "; парапет " + F3(Get(sum, "parapets_top_m")) + " м.п. по верху (контуров " +
                 parapets.Length + ")" : "") +
                "; линий схемы " + linesMade + (linesErased > 0 ? " (прежних заменено " + linesErased + ")" : "") + ".");
        }

        // ── таблица площадей и погонажей (общая с ATFTABLE) ──
        internal static void InsertTable(Transaction tr, Database db,
            BlockTableRecord btr, Point3d pt,
            List<Dictionary<string, object>> zones, double h)
        { InsertTable(tr, db, btr, pt, zones, null, h); }

        // 29.09 (Герман: «все площади — в типовую таблицу, в свободной и удобной форме»):
        // проёмы по типам, погонаж по категориям (ZoneTable — без AutoCAD), шапка в две
        // строки со слияниями; парапет — отдельной таблицей под основной (29.09k: одной
        // строкой «Парапет» — длина по верху, м.п.)
        internal static void InsertTable(Transaction tr, Database db,
            BlockTableRecord btr, Point3d pt,
            List<Dictionary<string, object>> zones,
            List<Dictionary<string, object>> parapets, double h)
        {
            var data = ZoneTable.ZoneRows(zones, z => SafeStr(Get(z, "zone_id")),
                                          z => SafeStr(Get(z, "cladding")));
            int cols = ZoneTable.Cols;
            int rows = 3 + data.Count;         // заголовок + 2 строки шапки + зоны + итого
            var tb = new Table();
            tb.TableStyle = db.Tablestyle;
            tb.SetSize(rows, cols);
            tb.Position = pt;
            for (int c = 0; c < cols; c++) tb.Columns[c].Width = (c == 0 ? 6 : c == 1 ? 16 : 6.5) * h;
            for (int r = 0; r < rows; r++) tb.Rows[r].Height = 2.2 * h;
            tb.Rows[2].Height = 3.2 * h;
            tb.Cells[0, 0].TextString = "Ведомость зон облицовки";
            for (int c = 0; c < cols; c++)
            {
                tb.Cells[1, c].TextString = ZoneTable.Group[c];
                tb.Cells[2, c].TextString = ZoneTable.Sub[c];
            }
            foreach (var m in ZoneTable.HeaderMerges)
                tb.MergeCells(CellRange.Create(tb, 1 + m[0], m[1], 1 + m[2], m[3]));
            for (int i = 0; i < data.Count; i++)
                for (int c = 0; c < cols; c++)
                    tb.Cells[3 + i, c].TextString = data[i][c];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    tb.Cells[r, c].TextHeight = h;
            tb.GenerateLayout();
            btr.AppendEntity(tb);
            tr.AddNewlyCreatedDBObject(tb, true);

            var prow = ZoneTable.ParapetRows(parapets);
            if (prow.Count == 0) return;
            int pc = ZoneTable.ParapetHead.Length;
            var tp = new Table();
            tp.TableStyle = db.Tablestyle;
            tp.SetSize(prow.Count + 2, pc);
            tp.Position = new Point3d(pt.X, pt.Y - tb.Height - 2 * h, pt.Z);
            double[] pw = { 12 * h, 14 * h };
            for (int c = 0; c < pc; c++) tp.Columns[c].Width = pw[c];
            for (int r = 0; r < prow.Count + 2; r++) tp.Rows[r].Height = 2.2 * h;
            tp.Cells[0, 0].TextString = ZoneTable.ParapetTitle;
            for (int c = 0; c < pc; c++) tp.Cells[1, c].TextString = ZoneTable.ParapetHead[c];
            for (int i = 0; i < prow.Count; i++)
                for (int c = 0; c < pc; c++)
                    tp.Cells[2 + i, c].TextString = prow[i][c];
            for (int r = 0; r < prow.Count + 2; r++)
                for (int c = 0; c < pc; c++)
                    tp.Cells[r, c].TextHeight = h;
            tp.GenerateLayout();
            btr.AppendEntity(tp);
            tr.AddNewlyCreatedDBObject(tp, true);
        }

        // ── *_fzones.json: merge по id зон ──
        private static void WriteZonesJson(Editor ed, Database db,
            JavaScriptSerializer ser, object[] zonesFull)
        {
            if (zonesFull == null || zonesFull.Length == 0) return;
            try
            {
                string dwg = db.Filename;
                string dir = string.IsNullOrEmpty(dwg)
                    ? Path.GetTempPath() : Path.GetDirectoryName(dwg);
                string name = string.IsNullOrEmpty(dwg)
                    ? "atfzone" : Path.GetFileNameWithoutExtension(dwg);
                string path = Path.Combine(dir ?? ".", name + "_fzones.json");

                // 24.09 (рецензия): зона заменяется ЦЕЛИКОМ — все её прежние
                // части. Раньше оставались записи, чей id не совпал буквально:
                // обычная «Ф-1» → объединённая «Ф-1.1/.2» оставляла старую
                // «Ф-1» (раскладка брала её), 3 части → 2 оставляли «.3»
                var all = new List<object>();
                var newRoots = new HashSet<string>();
                foreach (var z in zonesFull)
                {
                    var zd = z as Dictionary<string, object>;
                    if (zd != null) newRoots.Add(ZoneRoot(zd));
                }
                if (File.Exists(path))
                {
                    var old = ser.DeserializeObject(
                        File.ReadAllText(path, Encoding.UTF8)) as object[];
                    if (old != null)
                        foreach (var z in old)
                        {
                            var zd = z as Dictionary<string, object>;
                            if (zd == null) continue;
                            string oid = SafeStr(Get(zd, "id"));
                            bool replaced = newRoots.Contains(ZoneRoot(zd)) || newRoots.Contains(oid);
                            foreach (var r in newRoots)
                                if (oid.StartsWith(r + ".")) { replaced = true; break; }
                            if (!replaced) all.Add(zd);
                        }
                }
                all.AddRange(zonesFull);
                File.WriteAllText(path, ser.Serialize(all),
                                  new UTF8Encoding(false));
                ed.WriteMessage("\nЗоны сохранены: " + path);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nJSON не записан: " + ex.Message);
            }
        }

        // ── линейные размеры зоны (слой «_РАЗМЕРЫ» — конвенция Германа) ──
        private static void DrawDims(Transaction tr, Database db,
            BlockTableRecord btr, object[] dimsArr, double h)
        {
            EnsureLayer(tr, db, "_РАЗМЕРЫ");
            foreach (var dobj in dimsArr)
            {
                var d = dobj as Dictionary<string, object>;
                if (d == null) continue;
                var hd = Get(d, "height") as Dictionary<string, object>;
                if (hd != null)
                {
                    double x = ToD(Get(hd, "x")),
                           y0 = ToD(Get(hd, "y0")),
                           y1 = ToD(Get(hd, "y1"));
                    AddDim(tr, btr, db, Math.PI / 2.0,
                        new Point3d(x, y0, 0), new Point3d(x, y1, 0),
                        new Point3d(x - 2.0 * h, (y0 + y1) / 2.0, 0));
                }
                var bd = Get(d, "bottom") as Dictionary<string, object>;
                if (bd != null)
                {
                    double y = ToD(Get(bd, "y"));
                    var xso = Get(bd, "xs") as object[];
                    if (xso == null || xso.Length < 3) continue;
                    var xs = new List<double>();
                    foreach (var xo in xso) xs.Add(ToD(xo));
                    for (int i = 0; i + 1 < xs.Count; i++)
                        AddDim(tr, btr, db, 0.0,
                            new Point3d(xs[i], y, 0),
                            new Point3d(xs[i + 1], y, 0),
                            new Point3d((xs[i] + xs[i + 1]) / 2.0,
                                        y - 2.2 * h, 0));
                    AddDim(tr, btr, db, 0.0,
                        new Point3d(xs[0], y, 0),
                        new Point3d(xs[xs.Count - 1], y, 0),
                        new Point3d((xs[0] + xs[xs.Count - 1]) / 2.0,
                                    y - 4.4 * h, 0));
                }
            }
        }

        private static void AddDim(Transaction tr, BlockTableRecord btr,
            Database db, double rot, Point3d p1, Point3d p2, Point3d dl)
        {
            var dim = new RotatedDimension(rot, p1, p2, dl, null,
                                           db.Dimstyle);
            dim.SetDatabaseDefaults();
            dim.Layer = "_РАЗМЕРЫ";
            btr.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);
        }

        // ── Xrecord на объекте: JSON чанками ≤250 символов. Ключ «ATFZONE»
        //    — данные зоны; «ATCLAD» (CladCommand) — параметры раскладки
        //    рядом в том же extension dictionary (стык этапа 2, NEXT §Стык) ──

        internal const string XKey = "ATFZONE";

        // корень зоны: meta.group у части объединённой зоны, иначе id
        private static string ZoneRoot(Dictionary<string, object> zd)
        {
            var meta = Get(zd, "meta") as Dictionary<string, object>;
            string g = SafeStr(Get(meta, "group"));
            return g.Length > 0 ? g : SafeStr(Get(zd, "id"));
        }

        private static int TailNumber(string s)
        {
            int i = s.Length;
            while (i > 0 && char.IsDigit(s[i - 1])) i--;
            int v;
            return i < s.Length && int.TryParse(s.Substring(i), out v) ? v : 0;
        }

        // марки зон, уже стоящих в чертеже (метки ATFZONE на штриховках/марках)
        private static HashSet<string> ExistingZoneIds(Database db)
        {
            var ids = new HashSet<string>();
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    foreach (ObjectId oid in ms)
                    {
                        var e = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                        if (!(e is Hatch) && !(e is MText)) continue;
                        string j = ReadZoneData(tr, e);
                        if (j == null) continue;
                        var zd = ser.DeserializeObject(j) as Dictionary<string, object>;
                        string zid = SafeStr(Get(zd, "zone_id"));
                        if (zid.Length > 0) ids.Add(zid);
                    }
                    tr.Commit();
                }
            }
            catch { }
            return ids;
        }

        internal static void StoreZoneData(Transaction tr, Entity ent,
                                           string json)
        { StoreZoneData(tr, ent, json, XKey); }

        internal static void StoreZoneData(Transaction tr, Entity ent,
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

        internal static string ReadZoneData(Transaction tr, Entity ent)
        { return ReadZoneData(tr, ent, XKey); }

        internal static string ReadZoneData(Transaction tr, Entity ent,
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

        // ── служебное ──

        // 29.09 (Герман): слои схемы — его названия; цвет — только при создании слоя
        internal const string LinesKey = "ATFZONE_LINES";
        internal const string ParapetKey = "ATFZONE_PARAPET";
        private const string LayerParapet = "Парапет";
        private static readonly string[] SchemeLayers =
        {
            "Оконные откосы", "Оконные отливы", "Примыкание к витражам",
        };

        private static string EnsureNamed(Transaction tr, Database db, string name, short aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return name;
            lt.UpgradeOpen();
            var rec = new LayerTableRecord { Name = name };
            rec.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                Autodesk.AutoCAD.Colors.ColorMethod.ByAci, aci);
            lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return name;
        }

        private static string OpeningLayer(Transaction tr, Database db, string kind)
        {
            if (kind == "vitrage") return EnsureNamed(tr, db, "Контур витражей", 3);
            if (kind == "door") return EnsureNamed(tr, db, "Контур дверей", 30);
            return EnsureNamed(tr, db, "Контур окон", 4);
        }

        private static string LineLayer(Transaction tr, Database db, string cat)
        {
            if (cat == "window_sill") return EnsureNamed(tr, db, SchemeLayers[1], 5);
            if (cat.StartsWith("vitrage")) return EnsureNamed(tr, db, SchemeLayers[2], 6);
            return EnsureNamed(tr, db, SchemeLayers[0], 1);      // откосы окон и дверей
        }

        // линии схемы, созданные прошлым ATFZONE для этого проёма (хэндлы — в метке
        // проёма): удалить, если ещё на месте и на слоях схемы
        private static int EraseOldLines(Transaction tr, Database db, JavaScriptSerializer ser, Entity hole)
        {
            string j = ReadZoneData(tr, hole, LinesKey);
            if (j == null) return 0;
            int n = 0;
            object[] arr = null;
            try { arr = ser.DeserializeObject(j) as object[]; } catch { }
            if (arr == null) return 0;
            foreach (var ho in arr)
                try
                {
                    var id = db.GetObjectId(false, new Handle(Convert.ToInt64(SafeStr(ho), 16)), 0);
                    if (id.IsNull || id.IsErased) continue;
                    var e = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                    if (e == null || Array.IndexOf(SchemeLayers, e.Layer) < 0) continue;
                    e.Erase();
                    n++;
                }
                catch { }
            return n;
        }

        // выбор среди уже выбранных контуров; false — Esc (отмена команды)
        private static bool PickHandles(Editor ed, string msg, SelectionFilter filter,
            Dictionary<string, ObjectId> known, out List<string> handles)
        {
            handles = new List<string>();
            var res = ed.GetSelection(new PromptSelectionOptions { MessageForAdding = msg }, filter);
            if (res.Status == PromptStatus.Cancel) return false;
            if (res.Status != PromptStatus.OK) return true;          // Enter — нет
            int other = 0;
            foreach (SelectedObject so in res.Value)
            {
                string h = so.ObjectId.Handle.ToString();
                if (known.ContainsKey(h)) { if (!handles.Contains(h)) handles.Add(h); }
                else other++;
            }
            if (other > 0)
                ed.WriteMessage("\n  " + other + " объект(ов) не из выбранных контуров — пропущены.");
            return true;
        }

        private static bool PickParapets(Editor ed, Database db, SelectionFilter filter,
            List<Dictionary<string, object>> outList, Dictionary<string, ObjectId> ids,
            List<string> skipped)
        {
            var res = ed.GetSelection(new PromptSelectionOptions
            { MessageForAdding = "\nВыберите контуры ПАРАПЕТА (Enter — нет): " }, filter);
            if (res.Status == PromptStatus.Cancel) return false;
            if (res.Status != PromptStatus.OK) return true;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in res.Value)
                {
                    var pl = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Polyline;
                    if (pl == null) continue;
                    string h = Hnd(pl);
                    int n = pl.NumberOfVertices;
                    if (n < 3) { skipped.Add(h + " (парапет, <3 вершин)"); continue; }
                    bool closed = pl.Closed ||
                        pl.GetPoint2dAt(0).GetDistanceTo(pl.GetPoint2dAt(n - 1)) <= CloseTol;
                    if (!closed) { skipped.Add(h + " (парапет не замкнут)"); continue; }
                    var pts = new List<object>();
                    var bulges = new List<object>();
                    for (int i = 0; i < n; i++)
                    {
                        Point2d p = pl.GetPoint2dAt(i);
                        pts.Add(new[] { p.X, p.Y });
                        bulges.Add(pl.GetBulgeAt(i));
                    }
                    if (ids.ContainsKey(h)) continue;
                    ids[h] = pl.ObjectId;
                    outList.Add(new Dictionary<string, object>
                    { { "id", h }, { "pts", pts }, { "bulges", bulges } });
                }
                tr.Commit();
            }
            return true;
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

        private static string Hnd(DBObject o)
        { return o.Handle.ToString(); }

        private static string SkippedMsg(List<string> skipped)
        {
            return skipped.Count == 0 ? "" :
                "\nПропущены незамкнутые/негодные: " +
                string.Join(", ", skipped.ToArray()) + ".";
        }

        private static void PrintIssues(Editor ed, object[] issues,
                                        string title)
        {
            if (issues == null || issues.Length == 0) return;
            if (title != null) ed.WriteMessage("\nЗамечания (" + title + "):");
            foreach (var io in issues)
            {
                var d = io as Dictionary<string, object>;
                if (d == null) continue;
                ed.WriteMessage("\n  [" + SafeStr(Get(d, "level")) + "] " +
                    SafeStr(Get(d, "where")) + ": " + SafeStr(Get(d, "msg")));
            }
        }

        // вызов движка через временные файлы (паттерн ATableSpec/ABlockGen)
        internal static string CallEngine(string engineExe, string reqJson)
        {
            string tmpIn = Path.Combine(Path.GetTempPath(),
                "atf_in_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(),
                "atf_out_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmpIn, reqJson, new UTF8Encoding(false));
                var psi = new ProcessStartInfo
                {
                    FileName = engineExe,
                    Arguments = "\"" + tmpIn + "\" \"" + tmpOut + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    // 23.09 (ревью): без срока зависший движок вешал AutoCAD
                    // навсегда; stderr читаем асинхронно, чтобы срок работал
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(120 * 1000))
                    {
                        try { p.Kill(); } catch { }
                        throw new ApplicationException("движок не ответил за 120 с — " +
                            "процесс остановлен (очень большой фасад? разбейте выбор на части)");
                    }
                    p.WaitForExit();
                    string err = errTask.Result ?? "";
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

        private static double ToD(object o)
        { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }

        private static string F3(object o)
        {
            try
            {
                return ToD(o).ToString("0.000",
                    CultureInfo.GetCultureInfo("ru-RU"));
            }
            catch { return "?"; }
        }

        internal static string SafeStr(object o)
        { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }
    }
}
