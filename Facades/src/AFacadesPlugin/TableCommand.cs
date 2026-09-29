using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AFacadesPlugin.TableCommand))]

namespace AFacadesPlugin
{
    /// <summary>
    /// ATFTABLE — сформировать ведомость зон облицовки ПОТОМ (фидбэк
    /// Германа 19.07): выбрать штриховки и/или марки зон (данные зоны живут
    /// в их Xrecord «ATFZONE») → таблица площадей и погонажей. Если
    /// ассоциативная штриховка изменилась после обсчёта (контур двигали),
    /// её фактическая площадь разойдётся с сохранённой — команда
    /// предупредит и пометит строку звёздочкой.
    /// </summary>
    public class TableCommand
    {
        [CommandMethod("ATFTABLE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            // 24.09 (рецензия): ошибка не должна обрывать команду молча
            try { RunCore(doc); }
            catch (System.Exception ex)
            {
                try
                {
                    doc.Editor.WriteMessage("\nATFTABLE: внутренняя ошибка — сообщите " +
                        "разработчику.\n" + ex.ToString() + "\n");
                }
                catch { }
            }
        }

        private void RunCore(Autodesk.AutoCAD.ApplicationServices.Document doc)
        {
            var ed = doc.Editor;
            var db = doc.Database;

            // 29.09n (Герман): какая ведомость — зон (как было) или ведомость работ по форме
            // (штукатурный / вентилируемый фасад: «после выбора программа понимает, по какой форме
            // ей создавать таблицу»)
            var k0 = ed.GetKeywords(new PromptKeywordOptions(
                "\nВедомость [Зон/Штукатурка/Вентфасад] <Зон>: ", "Зон Штукатурка Вентфасад"));
            if (k0.Status == PromptStatus.Cancel)
            { ed.WriteMessage("\nОтменено."); return; }
            string kind = (k0.Status == PromptStatus.OK && !string.IsNullOrEmpty(k0.StringResult))
                          ? k0.StringResult : "Зон";
            bool works = kind != "Зон";
            // 29.09n (Герман): «штриховки находятся в отдельном слое — выбор слоя из уже
            // существующих слоёв»; прежний выбор объектами — вторым вариантом
            var k1 = ed.GetKeywords(new PromptKeywordOptions(
                "\nЗоны для ведомости [Слой/Объекты] <Слой>: ", "Слой Объекты"));
            if (k1.Status == PromptStatus.Cancel)
            { ed.WriteMessage("\nОтменено."); return; }
            bool byLayer = !(k1.Status == PromptStatus.OK && k1.StringResult == "Объекты");
            var ids = new List<ObjectId>();
            if (byLayer)
            {
                if (!PickByLayer(ed, db, ids, works ? (kind == "Штукатурка" ? "штукатурный фасад"
                                                                             : "вентилируемый фасад")
                                                     : "ведомость зон"))
                    return;
            }
            else
            {
                var pso = new PromptSelectionOptions
                {
                    MessageForAdding = "\nВыберите штриховки и/или марки зон " +
                                       "для ведомости: "
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
                foreach (SelectedObject so in sel.Value) ids.Add(so.ObjectId);
            }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var zones = new List<Dictionary<string, object>>();
            // 29.09 (Герман): парапеты ATFZONE — данные в метке их марки
            var parapets = new List<Dictionary<string, object>>();
            var seenP = new HashSet<string>();
            var seen = new HashSet<string>();
            var byId = new Dictionary<string, Dictionary<string, object>>();
            var stale = new List<string>();
            int noData = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (var oid in ids)
                {
                    var ent = tr.GetObject(oid, OpenMode.ForRead)
                              as Entity;
                    if (ent == null) continue;
                    string pj = ZoneCommand.ReadZoneData(tr, ent, ZoneCommand.ParapetKey);
                    if (pj != null)
                    {
                        var pd = ser.DeserializeObject(pj) as Dictionary<string, object>;
                        string pid = ZoneCommand.SafeStr(ZoneCommand.Get(pd, "id"));
                        if (pd != null && seenP.Add(pid)) parapets.Add(pd);
                        continue;
                    }
                    string json = ZoneCommand.ReadZoneData(tr, ent);
                    if (json == null) { noData++; continue; }
                    var z = ser.DeserializeObject(json)
                            as Dictionary<string, object>;
                    if (z == null) { noData++; continue; }
                    string id = ZoneCommand.SafeStr(
                        ZoneCommand.Get(z, "zone_id"));
                    if (id.Length == 0) continue;

                    // сверка с фактом: изменилась ли штриховка после обсчёта.
                    // 24.09 (рецензия): сверка — ДО отсева повторов зоны:
                    // раньше, если первой в выборке шла марка, штриховка той
                    // же зоны отсеивалась и изменение не замечалось
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
                            if (Math.Abs(fact - stored) > 0.001 && !stale.Contains(id))
                                stale.Add(id);
                        }
                        catch { }
                    }
                    // 29.09n: слой зоны — слой её штриховки (по нему — группы ведомости работ)
                    if (seen.Contains(id))
                    {
                        if (hat != null) byId[id]["_layer"] = ent.Layer;
                        continue;
                    }
                    seen.Add(id);
                    z["_layer"] = ent.Layer;
                    byId[id] = z;
                    zones.Add(z);
                }
                tr.Commit();
            }
            // 29.09n: при выборе слоем парапеты берутся отдельно (они на своём слое)
            if (byLayer && !AskParapets(ed, db, ser, parapets, seenP))
                return;
            foreach (var z in zones)
                if (stale.Contains(ZoneCommand.SafeStr(ZoneCommand.Get(z, "zone_id"))))
                    z["stale"] = true;
            if (zones.Count == 0 && parapets.Count == 0)
            {
                ed.WriteMessage("\nВ выборке нет объектов с данными зон " +
                    "(нужны штриховки/марки, созданные ATFZONE)." +
                    (noData > 0 ? " Без данных: " + noData + "." : ""));
                return;
            }

            // порядок строк = порядок марок (нумерация уже по правилу
            // «снизу вверх, справа налево»); натуральная сортировка по
            // числовому хвосту марки
            zones.Sort((a, b) =>
            {
                string ia = ZoneCommand.SafeStr(ZoneCommand.Get(a, "zone_id"));
                string ib = ZoneCommand.SafeStr(ZoneCommand.Get(b, "zone_id"));
                int na = TailNum(ia), nb = TailNum(ib);
                if (na != nb) return na.CompareTo(nb);
                return string.CompareOrdinal(ia, ib);
            });

            if (works)
            {
                MakeWorks(ed, db, kind, zones, parapets);
                if (stale.Count > 0)
                    ed.WriteMessage("\nВНИМАНИЕ: зоны изменены после обсчёта: " +
                        string.Join(", ", stale.ToArray()) + " — перезапустите ATFZONE по этим зонам.");
                return;
            }

            // куда вывести (фидбэк Германа №2 п.2: выгрузка в Excel);
            // классический конструктор (messageAndKeywords, globals) —
            // 18.07r: перегрузки Keywords.Add/Default/AllowNone уронили
            // компиляцию v3
            var pko = new PromptKeywordOptions(
                "\nКуда вывести ведомость [Чертеж/Ексель/Оба] <Чертеж>: ",
                "Чертеж Ексель Оба");
            var kres = ed.GetKeywords(pko);
            // 24.09 (рецензия): Esc — отмена команды, а не выбор «Чертеж»
            if (kres.Status == PromptStatus.Cancel)
            { ed.WriteMessage("\nОтменено."); return; }
            string mode = (kres.Status == PromptStatus.OK &&
                           kres.StringResult != null &&
                           kres.StringResult.Length > 0)
                          ? kres.StringResult : "Чертеж";
            bool toDwg = mode != "Ексель";
            bool toXls = mode == "Ексель" || mode == "Оба";

            // пометить изменённые зоны звёздочкой у марки
            foreach (var z in zones)
                if (z.ContainsKey("stale"))
                    z["zone_id"] = ZoneCommand.SafeStr(
                        ZoneCommand.Get(z, "zone_id")) + " *";

            if (toDwg)
            {
                var pdo = new PromptDoubleOptions(
                    "\nВысота текста таблицы, мм: ")
                { DefaultValue = 250.0, AllowNegative = false,
                  AllowZero = false };
                var hres = ed.GetDouble(pdo);
                if (hres.Status == PromptStatus.Cancel)
                { ed.WriteMessage("\nОтменено."); return; }
                double textH = hres.Status == PromptStatus.OK
                               ? hres.Value : 250.0;
                var ppr = ed.GetPoint("\nТочка вставки таблицы: ");
                if (ppr.Status != PromptStatus.OK)
                { ed.WriteMessage("\nОтменено."); return; }
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(
                        SymbolUtilityServices.GetBlockModelSpaceId(db),
                        OpenMode.ForWrite);
                    ZoneCommand.InsertTable(tr, db, btr, ppr.Value, zones, parapets,
                                            textH);
                    tr.Commit();
                }
            }
            if (toXls)
                ExportXlsx(ed, db, zones, parapets);

            ed.WriteMessage("\nATFTABLE: строк " + zones.Count +
                (parapets.Count > 0 ? ", парапет " + ZoneTable.ParapetRows(parapets)[0][1] + " м.п." : "") + "." +
                (noData > 0 ? " Пропущено объектов без данных: " + noData + "."
                            : ""));
            if (stale.Count > 0)
                ed.WriteMessage("\nВНИМАНИЕ: зоны изменены после обсчёта " +
                    "(площадь штриховки разошлась с сохранённой): " +
                    string.Join(", ", stale.ToArray()) +
                    " — помечены «*», перезапустите ATFZONE по этим зонам.");
        }

        // ── 29.09n: выбор слоёв со штриховками зон ──
        private static bool PickByLayer(Editor ed, Database db, List<ObjectId> ids, string purpose)
        {
            var count = new Dictionary<string, int>();
            var perLayer = new Dictionary<string, List<ObjectId>>();
            var seenZone = new Dictionary<string, HashSet<string>>();
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),
                                                        OpenMode.ForRead);
                foreach (ObjectId oid in ms)
                {
                    var ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (!(ent is Hatch) && !(ent is MText)) continue;
                    string json = ZoneCommand.ReadZoneData(tr, ent);
                    if (json == null) continue;
                    var z = ser.DeserializeObject(json) as Dictionary<string, object>;
                    string zid = ZoneCommand.SafeStr(ZoneCommand.Get(z, "zone_id"));
                    List<ObjectId> lst;
                    if (!perLayer.TryGetValue(ent.Layer, out lst))
                    {
                        perLayer[ent.Layer] = lst = new List<ObjectId>();
                        seenZone[ent.Layer] = new HashSet<string>();
                        count[ent.Layer] = 0;
                    }
                    lst.Add(oid);
                    if (zid.Length > 0 && seenZone[ent.Layer].Add(zid)) count[ent.Layer]++;
                }
                tr.Commit();
            }
            if (count.Count == 0)
            {
                ed.WriteMessage("\nВ чертеже нет штриховок и марок зон ATFZONE — сначала ATFZONE.");
                return false;
            }
            List<string> chosen;
            using (var f = new LayerPickForm(count, null, purpose))
            {
                if (AcApp.ShowModalDialog(f) != System.Windows.Forms.DialogResult.OK)
                { ed.WriteMessage("\nОтменено."); return false; }
                chosen = f.Selected;
            }
            foreach (var l in chosen) ids.AddRange(perLayer[l]);
            ed.WriteMessage("\n  слои: " + string.Join(", ", chosen.ToArray()) + ".");
            return chosen.Count > 0;
        }

        // ── 29.09n: парапеты при выборе слоем — все в чертеже, выбором или без них ──
        private static bool AskParapets(Editor ed, Database db, JavaScriptSerializer ser,
            List<Dictionary<string, object>> parapets, HashSet<string> seenP)
        {
            var all = new List<Dictionary<string, object>>();
            var idsAll = new List<ObjectId>();
            double lm = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db),
                                                        OpenMode.ForRead);
                var seen = new HashSet<string>();
                foreach (ObjectId oid in ms)
                {
                    var ent = tr.GetObject(oid, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    string pj = ZoneCommand.ReadZoneData(tr, ent, ZoneCommand.ParapetKey);
                    if (pj == null) continue;
                    var pd = ser.DeserializeObject(pj) as Dictionary<string, object>;
                    string pid = ZoneCommand.SafeStr(ZoneCommand.Get(pd, "id"));
                    if (pd == null || !seen.Add(pid)) continue;
                    all.Add(pd);
                    idsAll.Add(oid);
                    lm += Dbl(ZoneCommand.Get(pd, "top_m"));
                }
                tr.Commit();
            }
            if (all.Count == 0) return true;
            var k = ed.GetKeywords(new PromptKeywordOptions(
                "\nПарапет: в чертеже " + all.Count + " контур(ов), " + WorkStatement.F3(lm) +
                " м.п. В ведомость [Все/Выбрать/Нет] <Все>: ", "Все Выбрать Нет"));
            if (k.Status == PromptStatus.Cancel) { ed.WriteMessage("\nОтменено."); return false; }
            string m = k.Status == PromptStatus.OK && !string.IsNullOrEmpty(k.StringResult) ? k.StringResult : "Все";
            if (m == "Нет") return true;
            if (m == "Все")
            {
                foreach (var pd in all)
                    if (seenP.Add(ZoneCommand.SafeStr(ZoneCommand.Get(pd, "id")))) parapets.Add(pd);
                return true;
            }
            var sel = ed.GetSelection(new PromptSelectionOptions
            { MessageForAdding = "\nВыберите парапеты (контуры или их линии): " });
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return false; }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    string pj = ent == null ? null : ZoneCommand.ReadZoneData(tr, ent, ZoneCommand.ParapetKey);
                    if (pj == null) continue;
                    var pd = ser.DeserializeObject(pj) as Dictionary<string, object>;
                    if (pd != null && seenP.Add(ZoneCommand.SafeStr(ZoneCommand.Get(pd, "id")))) parapets.Add(pd);
                }
                tr.Commit();
            }
            return true;
        }

        private static double Dbl(object o)
        {
            if (o == null) return 0;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        // ── 29.09n: ведомость работ по форме Германа (штукатурный / вентилируемый фасад) ──
        private static string TemplateFile(string file)
        {
            // своя форма конструктора (%APPDATA%\AFacades\templates) — раньше формы из бандла
            string user = System.IO.Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData), "AFacades", "templates", file);
            if (System.IO.File.Exists(user)) return user;
            string dll = System.IO.Path.GetDirectoryName(typeof(TableCommand).Assembly.Location) ?? ".";
            string b = System.IO.Path.GetFullPath(System.IO.Path.Combine(dll, "..", "templates", file));
            return System.IO.File.Exists(b) ? b : null;
        }

        private static void MakeWorks(Editor ed, Database db, string kind,
            List<Dictionary<string, object>> zones, List<Dictionary<string, object>> parapets)
        {
            string name = kind == "Штукатурка" ? "shtukaturka" : "vent";
            string json = TemplateFile(name + ".json");
            if (json == null)
            { ed.WriteMessage("\nНет карты формы " + name + ".json (бандл AFacades: Contents\\templates)."); return; }
            WorkStatement.Config cfg;
            try { cfg = WorkStatement.LoadConfig(json); }
            catch (System.Exception ex) { ed.WriteMessage("\nКарта формы " + json + " не читается: " + ex.Message); return; }
            string tpl = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(json) ?? ".", cfg.Template);
            if (!System.IO.File.Exists(tpl)) { ed.WriteMessage("\nНет формы " + tpl); return; }

            // группы — по слою штриховки (несколько слоёв: утеплитель делится по толщине из имени)
            var groups = new List<WorkStatement.Group>();
            var gl = new Dictionary<string, WorkStatement.Group>();
            foreach (var z in zones)
            {
                string layer = ZoneCommand.SafeStr(ZoneCommand.Get(z, "_layer"));
                WorkStatement.Group g;
                if (!gl.TryGetValue(layer, out g))
                {
                    gl[layer] = g = new WorkStatement.Group { Layer = layer };
                    groups.Add(g);
                }
                var rep = ZoneCommand.Get(z, "report") as Dictionary<string, object>;
                if (rep != null) g.Reports.Add(rep);
            }
            double par = 0;
            foreach (var p in parapets) par += Dbl(ZoneCommand.Get(p, "top_m"));

            string dwg = db.Filename;
            var sfd = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = (string.IsNullOrEmpty(dwg) ? "ведомость" : System.IO.Path.GetFileNameWithoutExtension(dwg)) +
                           "_ведомость работ_" + (name == "vent" ? "НВФ" : "штукатурка") + ".xlsx",
                InitialDirectory = string.IsNullOrEmpty(dwg) ? null : System.IO.Path.GetDirectoryName(dwg),
            };
            if (sfd.ShowDialog() != System.Windows.Forms.DialogResult.OK) { ed.WriteMessage("\nОтменено."); return; }
            WorkStatement.Result r;
            try { r = WorkStatement.Build(tpl, cfg, groups, par, sfd.FileName); }
            catch (System.Exception ex) { ed.WriteMessage("\nВедомость не записана: " + ex.Message); return; }
            ed.WriteMessage("\nATFTABLE: " + cfg.Title + " — зон " + zones.Count + " со слоёв «" +
                string.Join("», «", new List<string>(gl.Keys).ToArray()) + "»" +
                (parapets.Count > 0 ? ", парапетов " + parapets.Count : "") + ".");
            foreach (var l in r.Lines) ed.WriteMessage("\n  " + l);
            foreach (var l in r.Manual) ed.WriteMessage("\n  вручную: " + l);
            ed.WriteMessage("\n  файл: " + sfd.FileName);
            // превью и правка — в Excel (формулы формы пересчитываются при открытии)
            try { System.Diagnostics.Process.Start(sfd.FileName); }
            catch { ed.WriteMessage("\n  (Excel не открылся — откройте файл вручную)"); }
        }

        private static int TailNum(string s)
        {
            var m = Regex.Match(s ?? "", @"(\d+)\s*\*?\s*$");
            int n;
            if (m.Success && int.TryParse(m.Groups[1].Value, out n)) return n;
            return int.MaxValue;
        }

        // ── выгрузка ведомости в .xlsx (29.09: те же колонки, что в чертеже — ZoneTable) ──
        private static void ExportXlsx(Editor ed, Database db,
            List<Dictionary<string, object>> zones,
            List<Dictionary<string, object>> parapets)
        {
            try
            {
                string dwg = db.Filename;
                string defName = (string.IsNullOrEmpty(dwg)
                    ? "ведомость" : System.IO.Path.GetFileNameWithoutExtension(
                        dwg) + "_ведомость") + ".xlsx";
                var sfd = new System.Windows.Forms.SaveFileDialog
                {
                    Filter = "Excel (*.xlsx)|*.xlsx",
                    FileName = defName,
                    InitialDirectory = string.IsNullOrEmpty(dwg)
                        ? null : System.IO.Path.GetDirectoryName(dwg),
                };
                if (sfd.ShowDialog() !=
                    System.Windows.Forms.DialogResult.OK) return;
                XlsxWriter.Write(sfd.FileName, "Ведомость зон", XlsxRows(zones, parapets));
                ed.WriteMessage("\nВедомость выгружена: " + sfd.FileName);
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage("\nExcel не записан: " + ex.Message);
            }
        }

        internal static List<object[]> XlsxRows(List<Dictionary<string, object>> zones,
                                               List<Dictionary<string, object>> parapets)
        {
            var rows = new List<object[]> { ZoneTable.Group, ZoneTable.Sub };
            foreach (var r in ZoneTable.ZoneRows(zones,
                         z => ZoneCommand.SafeStr(ZoneCommand.Get(z, "zone_id")),
                         z => ZoneCommand.SafeStr(ZoneCommand.Get(z, "cladding"))))
                rows.Add(Numeric(r, 2));
            var pr = ZoneTable.ParapetRows(parapets);
            if (pr.Count > 0)
            {
                rows.Add(new object[] { "" });
                rows.Add(new object[] { ZoneTable.ParapetTitle });
                rows.Add(ZoneTable.ParapetHead);
                foreach (var r in pr) rows.Add(Numeric(r, 1));
            }
            return rows;
        }

        // числа — числами (Excel считает суммы сам), подписи — текстом
        private static object[] Numeric(string[] r, int from)
        {
            var o = new object[r.Length];
            for (int i = 0; i < r.Length; i++)
            {
                double v;
                o[i] = i >= from && double.TryParse(r[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                       ? (object)v : r[i];
            }
            return o;
        }

        private static double D(Dictionary<string, object> d, string key)
        {
            try
            {
                return Convert.ToDouble(ZoneCommand.Get(d, key),
                                        CultureInfo.InvariantCulture);
            }
            catch { return 0.0; }
        }
    }
}
