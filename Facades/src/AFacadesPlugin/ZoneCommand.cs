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
            //    забыл контур — доклкнуть, не выбирая всё заново) ──
            ZoneForm form = new ZoneForm(contours.Count);
            form.AddPicker = delegate ()
            {
                using (var ui = ed.StartUserInteraction(form))
                {
                    var psoAdd = new PromptSelectionOptions
                    { MessageForAdding = "\nДобавьте контуры: " };
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
                    ui.End();
                }
                return contours.Count;
            };
            if (AcApp.ShowModalDialog(form) != DialogResult.OK)
            { ed.WriteMessage("\nОтменено."); return; }

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

            var zones = Get(res, "zones") as object[];
            if (zones == null || zones.Length == 0)
            {
                ed.WriteMessage("\nНи одной валидной зоны." + SkippedMsg(skipped));
                return;
            }

            // ── 4. точка таблицы (до транзакции — один undo-шаг на всё) ──
            Point3d tablePt = Point3d.Origin;
            bool doTable = form.MakeTable;
            if (doTable)
            {
                var ppr = ed.GetPoint("\nТочка вставки таблицы: ");
                if (ppr.Status == PromptStatus.OK) tablePt = ppr.Value;
                else doTable = false;
            }

            // ── 5. правки чертежа ──
            string layer = LayerName(form.Cladding);
            int made = 0;
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
                    foreach (var lid in holeIds)
                    {
                        var ent = (Entity)tr.GetObject(lid, OpenMode.ForWrite);
                        ent.Layer = layer;
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
                    hat.SetHatchPattern(HatchPatternType.PreDefined,
                        form.Pattern.Length > 0 ? form.Pattern : "ANSI31");
                    hat.HatchStyle = HatchStyle.Normal;
                    hat.Associative = true;
                    foreach (var oid in outIds)
                        hat.AppendLoop(HatchLoopTypes.External,
                            new ObjectIdCollection(new[] { oid }));
                    foreach (var hid in holeIds)
                        hat.AppendLoop(HatchLoopTypes.Default,
                            new ObjectIdCollection(new[] { hid }));
                    hat.EvaluateHatch(true);

                    // марка + площадь в центроиде (фидбэк Германа 19.07:
                    // наименование облицовки в марке НЕ пишем — только
                    // марка и S нетто; облицовка остаётся именем слоя)
                    var lp = Get(z, "label_pt") as object[];
                    double lx = lp != null ? ToD(lp[0]) : 0,
                           ly = lp != null ? ToD(lp[1]) : 0;
                    var mt = new MText
                    {
                        Location = new Point3d(lx, ly, 0),
                        TextHeight = form.TextHeight,
                        Layer = layer,
                        Attachment = AttachmentPoint.MiddleCenter,
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

                if (doTable)
                {
                    var rowsData = new List<Dictionary<string, object>>();
                    foreach (var zo in zones)
                    {
                        var z = zo as Dictionary<string, object>;
                        if (z != null) rowsData.Add(z);
                    }
                    InsertTable(tr, db, btr, tablePt, rowsData,
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
        }

        // ── таблица площадей и погонажей (общая с ATFTABLE) ──
        internal static void InsertTable(Transaction tr, Database db,
            BlockTableRecord btr, Point3d pt,
            List<Dictionary<string, object>> zones, double h)
        {
            int rows = zones.Count + 3;        // title + header + zones + итого
            var tb = new Table();
            tb.TableStyle = db.Tablestyle;
            tb.SetSize(rows, 7);
            tb.Position = pt;
            // заголовки — терминология Германа (фидбэк №2 п.5)
            string[] head = { "Марка", "Облицовка", "S участка, м²",
                              "S проёмов, м²", "S облицовки, м²",
                              "Отливы, м.п.", "Откосы, м.п." };
            double[] w = { 6 * h, 18 * h, 7 * h, 7 * h, 7 * h, 7 * h, 7 * h };
            for (int c = 0; c < 7; c++) tb.Columns[c].Width = w[c];
            for (int r = 0; r < rows; r++) tb.Rows[r].Height = 2.2 * h;

            tb.Cells[0, 0].TextString = "Ведомость зон облицовки";
            for (int c = 0; c < 7; c++)
                tb.Cells[1, c].TextString = head[c];

            double tGross = 0, tOp = 0, tNet = 0, tSill = 0, tJamb = 0;
            for (int i = 0; i < zones.Count; i++)
            {
                var z = zones[i];
                var rep = Get(z, "report") as Dictionary<string, object>;
                double g = ToD(Get(rep, "area_outer_m2")),
                       o = ToD(Get(rep, "openings_total_m2")),
                       n = ToD(Get(rep, "area_net_m2")),
                       s = ToD(Get(rep, "sills_total_m")),
                       j = ToD(Get(rep, "jambs_total_m"));
                tGross += g; tOp += o; tNet += n; tSill += s; tJamb += j;
                int r = 2 + i;
                tb.Cells[r, 0].TextString = SafeStr(Get(z, "zone_id"));
                tb.Cells[r, 1].TextString = SafeStr(Get(z, "cladding"));
                tb.Cells[r, 2].TextString = F3(g);
                tb.Cells[r, 3].TextString = F3(o);
                tb.Cells[r, 4].TextString = F3(n);
                tb.Cells[r, 5].TextString = F3(s);
                tb.Cells[r, 6].TextString = F3(j);
            }
            int last = rows - 1;
            tb.Cells[last, 0].TextString = "ИТОГО";
            tb.Cells[last, 2].TextString = F3(tGross);
            tb.Cells[last, 3].TextString = F3(tOp);
            tb.Cells[last, 4].TextString = F3(tNet);
            tb.Cells[last, 5].TextString = F3(tSill);
            tb.Cells[last, 6].TextString = F3(tJamb);

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < 7; c++)
                    tb.Cells[r, c].TextHeight = h;

            tb.GenerateLayout();
            btr.AppendEntity(tb);
            tr.AddNewlyCreatedDBObject(tb, true);
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

                var all = new List<object>();
                var newIds = new HashSet<string>();
                foreach (var z in zonesFull)
                {
                    var zd = z as Dictionary<string, object>;
                    if (zd != null) newIds.Add(SafeStr(Get(zd, "id")));
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
                            if (!newIds.Contains(SafeStr(Get(zd, "id"))))
                                all.Add(zd);
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
                                           db.Dimstyle)
            { Layer = "_РАЗМЕРЫ" };
            btr.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);
        }

        // ── Xrecord «ATFZONE» на объекте: JSON зоны чанками ≤250 символов ──

        internal const string XKey = "ATFZONE";

        internal static void StoreZoneData(Transaction tr, Entity ent,
                                           string json)
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
            if (ext.Contains(XKey))
            {
                var old = (Xrecord)tr.GetObject(ext.GetAt(XKey),
                                                OpenMode.ForWrite);
                old.Data = rb;
            }
            else
            {
                ext.SetAt(XKey, xr);
                tr.AddNewlyCreatedDBObject(xr, true);
            }
        }

        internal static string ReadZoneData(Transaction tr, Entity ent)
        {
            if (ent.ExtensionDictionary.IsNull) return null;
            var ext = (DBDictionary)tr.GetObject(ent.ExtensionDictionary,
                                                 OpenMode.ForRead);
            if (!ext.Contains(XKey)) return null;
            var xr = (Xrecord)tr.GetObject(ext.GetAt(XKey), OpenMode.ForRead);
            if (xr.Data == null) return null;
            var sb = new StringBuilder();
            foreach (TypedValue tv in xr.Data)
                if (tv.TypeCode == (int)DxfCode.Text)
                    sb.Append(SafeStr(tv.Value));
            return sb.Length > 0 ? sb.ToString() : null;
        }

        // ── служебное ──

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
