// ATSPECREPORT — "свой отчёт": выбрать блоки -> окно с выражениями по столбцам
// (аналог "Шаблона отчёта" СПДС) -> движок dxf_spec (action=report) -> AcDbTable.
//
// Каркас (этап 1): один шаблон отчёта (источник-фильтр по слою/атрибуту, столбцы-
// выражения, группировка по столбцу, сортировка). Производные строки (несколько
// шаблонов в одном отчёте) и реактор авто-пересчёта по правке блока — следующими
// этапами; движок их уже поддерживает (templates[] / action=report).
//
// ВНИМАНИЕ: компилируется на Windows (NuGet AutoCAD.NET 24.0.0 = AutoCAD 2021).
// В песочнице не собиралось — проверяется автосборкой GitHub Actions (workflow check).
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013-2024).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
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

[assembly: CommandClass(typeof(AtSpecPlugin.ReportCommands))]

namespace AtSpecPlugin
{
    public class ReportCommands
    {
        [CommandMethod("ATSPECREPORT")]
        public void AtSpecReport()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            // --- 1. выбор блоков ---
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var pso = new PromptSelectionOptions { MessageForAdding = "\nВыберите блоки: " };
            PromptSelectionResult sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            // --- 2. собрать записи блоков (имя/слой/атрибуты) -> формат контракта ---
            var records = new List<Dictionary<string, object>>();
            var layerSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var fieldSet = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    if (so == null) continue;
                    var br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    var attrs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (ObjectId arId in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(arId, OpenMode.ForRead) as AttributeReference;
                        if (ar != null) { attrs[ar.Tag] = ar.TextString; fieldSet.Add(ar.Tag); }
                    }
                    // Динамические свойства блока (ручки): у доборников и т.п. длина/ширина —
                    // это параметры, а не ATTRIB; без них Object.«Длина» пустой. ATTRIB в приоритете.
                    if (br.IsDynamicBlock)
                    {
                        foreach (DynamicBlockReferenceProperty dp in br.DynamicBlockReferencePropertyCollection)
                        {
                            string pn = dp.PropertyName;
                            if (string.IsNullOrEmpty(pn) || attrs.ContainsKey(pn)) continue;
                            attrs[pn] = Convert.ToString(dp.Value, System.Globalization.CultureInfo.InvariantCulture);
                            fieldSet.Add(pn);
                        }
                    }
                    layerSet.Add(br.Layer);
                    records.Add(new Dictionary<string, object>
                    {
                        { "name", EffectiveName(tr, br) },
                        { "layer", br.Layer },
                        { "attributes", attrs }
                    });
                }
                tr.Commit();
            }
            if (records.Count == 0) { ed.WriteMessage("\nСреди выбранного нет блоков."); return; }

            // --- 3. найти движок рядом с DLL ---
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string engineExe = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "dxf_spec.exe"));
            string configYaml = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "mapping.yaml"));
            if (!File.Exists(engineExe)) { ed.WriteMessage("\nНе найден движок: " + engineExe); return; }
            if (!File.Exists(configYaml)) { ed.WriteMessage("\nНе найден конфиг: " + configYaml); return; }

            // поля для подсказки в окне: атрибуты/параметры выбранных блоков + служебные.
            // Скрываем переменные деталировки (в спецификациях не участвуют).
            var HIDE = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "DOBL", "DOBR", "KLL", "KLR", "L", "R", "UGL", "UGR" };
            var fields = new List<string>();
            foreach (var f in fieldSet) if (!HIDE.Contains(f)) fields.Add(f);
            foreach (var extra in new[] { "Имя", "Слой", "Длина", "Ширина", "Высота" })
                if (!fields.Exists(z => string.Equals(z, extra, StringComparison.OrdinalIgnoreCase)))
                    fields.Add(extra);
            var layers = new List<string>(layerSet);

            // --- 4. окно-построитель отчёта ---
            var form = new ReportBuilderForm(layers, fields);
            if (AcApp.ShowModalDialog(form) != DialogResult.OK) { ed.WriteMessage("\nОтменено."); return; }

            // --- 5. payload: action=report + определение отчёта ---
            var payload = new Dictionary<string, object>
            {
                { "blocks", records },
                { "action", "report" },
                { "report", form.ReportDef }
            };

            // --- 6. вызов движка ---
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> result;
            try
            {
                result = ser.Deserialize<Dictionary<string, object>>(CallEngine(engineExe, configYaml, payload));
            }
            catch (System.Exception e)
            {
                ed.WriteMessage("\nОшибка движка (report): " + e.Message);
                return;
            }
            if (!Convert.ToBoolean(Get(result, "ok"))) { ed.WriteMessage("\nДвижок вернул ошибку."); return; }

            var rep = Get(result, "report") as Dictionary<string, object>;
            if (rep == null) { ed.WriteMessage("\nПустой отчёт."); return; }
            var header = ToStrList(Get(rep, "header"));
            var rows = Get(rep, "rows") as IList;
            string title = SafeStr(Get(rep, "title"));
            if (rows == null || rows.Count == 0) { ed.WriteMessage("\nВ отчёт не попало ни одной строки."); return; }
            if (header.Count == 0) // нет шапки -> ширина по первой строке
            {
                int wcols = ((IList)rows[0]).Count;
                for (int i = 0; i < wcols; i++) header.Add("");
            }

            // --- 7. точка вставки ---
            PromptPointResult pr = ed.GetPoint("\nТочка вставки таблицы: ");
            if (pr.Status != PromptStatus.OK) return;

            // --- 8. AcDbTable: title + header + rows (позиционно) + определение в таблице ---
            bool hideTitle = GetBoolFlag(form.ReportDef, "hide_title");
            bool hideHeader = GetBoolFlag(form.ReportDef, "hide_header");
            DrawTable(db, pr.Value, title, header, rows, ser.Serialize(form.ReportDef), hideTitle, hideHeader);
            ed.WriteMessage("\nГотово: \"" + title + "\", строк: " + rows.Count + ". Пересчёт — ATSPECUPDATE.");
        }

        private static void DrawTable(Database db, Point3d pos, string title, List<string> header, IList rows,
            string defJson, bool hideTitle, bool hideHeader)
        {
            int nCols = header.Count, nRows = rows.Count;
            int titleRow = hideTitle ? -1 : 0;
            int headerRow = hideHeader ? -1 : (hideTitle ? 0 : 1);
            int top = (hideTitle ? 0 : 1) + (hideHeader ? 0 : 1);   // зарезервировано строк сверху
            int want = top + nRows; if (want < 1) want = 1;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var tbl = new Table();
                tbl.TableStyle = db.Tablestyle;
                tbl.Position = pos;
                tbl.SetSize(want, nCols);

                if (titleRow >= 0)
                {
                    tbl.Cells[titleRow, 0].TextString = title ?? "";
                    try { tbl.MergeCells(CellRange.Create(tbl, titleRow, 0, titleRow, nCols - 1)); } catch { }
                }
                if (headerRow >= 0)
                    for (int c = 0; c < nCols; c++)
                        tbl.Cells[headerRow, c].TextString = SafeStr(header[c]);
                for (int r = 0; r < nRows; r++)
                {
                    var row = rows[r] as IList;
                    for (int c = 0; c < nCols; c++)
                        tbl.Cells[top + r, c].TextString = (row != null && c < row.Count) ? SafeStr(row[c]) : "";
                }
                // #7: убрать возможные хвостовые пустые строки (усечение SetSize иногда оставляет лишние)
                if (tbl.Rows.Count > want)
                    tbl.DeleteRows(want, tbl.Rows.Count - want);
                tbl.GenerateLayout();
                ms.AppendEntity(tbl);
                tr.AddNewlyCreatedDBObject(tbl, true);
                // сохранить определение отчёта в самой таблице — чтобы её можно было пересчитать (ATSPECUPDATE)
                try { ReportReactor.StoreDef(tr, tbl, defJson); } catch { }
                tr.Commit();
            }
        }

        // --- вызов движка через временные файлы (как в Commands.cs) ---
        private static string CallEngine(string engineExe, string configYaml, object payload)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string tmpIn = Path.Combine(Path.GetTempPath(), "atspec_rin_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(), "atspec_rout_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(tmpIn, ser.Serialize(payload), new UTF8Encoding(false));
                var psi = new ProcessStartInfo
                {
                    FileName = engineExe,
                    Arguments = "--json -c \"" + configYaml + "\" --in \"" + tmpIn + "\" --out-json \"" + tmpOut + "\"",
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0) throw new ApplicationException(err);
                }
                return File.ReadAllText(tmpOut, Encoding.UTF8);
            }
            finally { TryDelete(tmpIn); TryDelete(tmpOut); }
        }

        private static object Get(Dictionary<string, object> d, string key)
        { object v; return (d != null && d.TryGetValue(key, out v)) ? v : null; }

        private static bool GetBoolFlag(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null)
            { try { return Convert.ToBoolean(v); } catch { return false; } }
            return false;
        }

        private static List<string> ToStrList(object o)
        {
            var list = new List<string>(); var il = o as IList;
            if (il != null) foreach (var x in il) list.Add(SafeStr(x));
            return list;
        }

        private static string EffectiveName(Transaction tr, BlockReference br)
        {
            try
            {
                ObjectId id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                return btr != null ? btr.Name : br.Name;
            }
            catch { return br.Name; }
        }

        private static string SafeStr(object o) { return o == null ? "" : o.ToString(); }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
