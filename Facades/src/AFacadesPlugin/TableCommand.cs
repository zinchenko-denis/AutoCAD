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
                new TypedValue((int)DxfCode.Operator, "or>"),
            });
            var sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var zones = new List<Dictionary<string, object>>();
            // 29.09 (Герман): парапеты ATFZONE — данные в метке их марки
            var parapets = new List<Dictionary<string, object>>();
            var seenP = new HashSet<string>();
            var seen = new HashSet<string>();
            var stale = new List<string>();
            int noData = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead)
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
                    if (seen.Contains(id)) continue;
                    seen.Add(id);
                    zones.Add(z);
                }
                tr.Commit();
            }
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
                (parapets.Count > 0 ? ", парапетов " + parapets.Count : "") + "." +
                (noData > 0 ? " Пропущено объектов без данных: " + noData + "."
                            : ""));
            if (stale.Count > 0)
                ed.WriteMessage("\nВНИМАНИЕ: зоны изменены после обсчёта " +
                    "(площадь штриховки разошлась с сохранённой): " +
                    string.Join(", ", stale.ToArray()) +
                    " — помечены «*», перезапустите ATFZONE по этим зонам.");
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
