// ATSPEC — чтение выбранных блоков из чертежа, агрегация во внешнем
// Python-движке (dxf_spec.exe) и вставка результата таблицей AcDbTable.
//
// ВНИМАНИЕ: компилируется на Windows против сборок AutoCAD 2021
// (accoremgd/acdbmgd/acmgd). В песочнице сборки нет — код не прогонялся
// внутри AutoCAD; первый компилирующий проход делается у Дениса (build.ps1).
//
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013–2024).
// JSON разбираем штатным JavaScriptSerializer (System.Web.Extensions),
// чтобы не тащить внешних зависимостей. Для будущей сборки под .NET 8
// (AutoCAD 2025+) тот же код переведётся на System.Text.Json.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AtSpecPlugin.Commands))]

namespace AtSpecPlugin
{
    public class Commands
    {
        [CommandMethod("ATSPEC")]
        public void AtSpec()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            // --- 1. выбор блоков (только INSERT) ---
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var pso = new PromptSelectionOptions { MessageForAdding = "\nВыберите блоки фасада: " };
            PromptSelectionResult sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            // --- 2. собрать записи блоков в формат контракта ---
            var records = new List<Dictionary<string, object>>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    if (so == null) continue;
                    var br = tr.GetObject(so.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;

                    var attrs = new Dictionary<string, object>();
                    foreach (ObjectId arId in br.AttributeCollection)
                    {
                        var ar = tr.GetObject(arId, OpenMode.ForRead) as AttributeReference;
                        if (ar == null) continue;
                        attrs[ar.Tag] = ar.TextString;   // тег -> значение
                    }

                    records.Add(new Dictionary<string, object>
                    {
                        { "name",     EffectiveName(tr, br) },
                        { "layer",    br.Layer },
                        { "x",        br.Position.X },
                        { "y",        br.Position.Y },
                        { "rotation", br.Rotation * 180.0 / Math.PI },  // рад -> град
                        { "xscale",   br.ScaleFactors.X },
                        { "yscale",   br.ScaleFactors.Y },
                        { "attributes", attrs }
                    });
                }
                tr.Commit();
            }
            if (records.Count == 0) { ed.WriteMessage("\nСреди выбранного нет блоков."); return; }

            // --- 3. найти движок и конфиг рядом с DLL ---
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string engineExe = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "dxf_spec.exe"));
            string configYaml = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "mapping.yaml"));
            if (!File.Exists(engineExe)) { ed.WriteMessage("\nНе найден движок: " + engineExe); return; }
            if (!File.Exists(configYaml)) { ed.WriteMessage("\nНе найден конфиг: " + configYaml); return; }

            // --- 4. обмен через временные файлы (надёжнее пайпов) ---
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string tmpIn = Path.Combine(Path.GetTempPath(), "atspec_in_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(), "atspec_out_" + Guid.NewGuid().ToString("N") + ".json");
            string outJson;
            try
            {
                File.WriteAllText(tmpIn, ser.Serialize(records), new UTF8Encoding(false));

                var psi = new ProcessStartInfo
                {
                    FileName = engineExe,
                    Arguments = "--json -c \"" + configYaml + "\" --in \"" + tmpIn + "\" --out-json \"" + tmpOut + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };
                using (Process p = Process.Start(psi))
                {
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        ed.WriteMessage("\nДвижок завершился с ошибкой:\n" + err);
                        return;
                    }
                }
                outJson = File.ReadAllText(tmpOut, Encoding.UTF8);
            }
            finally
            {
                TryDelete(tmpIn);
                TryDelete(tmpOut);
            }

            // --- 5. разобрать ответ движка ---
            var result = ser.Deserialize<Dictionary<string, object>>(outJson);
            var reports = result["reports"] as IList;   // ArrayList или object[]
            if (reports == null || reports.Count == 0) { ed.WriteMessage("\nНет ведомостей."); return; }

            var names = new List<string>();
            foreach (var r in reports) names.Add((string)((Dictionary<string, object>)r)["name"]);

            // выбор ведомости по номеру (без проблем с кириллицей в keyword'ах)
            int pick = 1;
            if (names.Count > 1)
            {
                ed.WriteMessage("\nДоступные ведомости:");
                for (int i = 0; i < names.Count; i++)
                    ed.WriteMessage("\n  " + (i + 1) + ". " + names[i]);
                var pio = new PromptIntegerOptions("\nНомер ведомости")
                { LowerLimit = 1, UpperLimit = names.Count, DefaultValue = 1, AllowNone = false };
                PromptIntegerResult ir = ed.GetInteger(pio);
                if (ir.Status != PromptStatus.OK) return;
                pick = ir.Value;
            }
            var rep = (Dictionary<string, object>)reports[pick - 1];

            // --- 6. точка вставки ---
            PromptPointResult pr = ed.GetPoint("\nТочка вставки таблицы: ");
            if (pr.Status != PromptStatus.OK) return;

            // --- 7. построить AcDbTable ---
            var columns = (IList)rep["columns"];
            var rows = (IList)rep["rows"];
            string title = (string)rep["title"];
            int nCols = columns.Count;
            int nRows = rows.Count;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var tbl = new Table();
                tbl.TableStyle = db.Tablestyle;
                tbl.Position = pr.Value;
                tbl.SetSize(nRows + 2, nCols);              // заголовок + шапка + данные

                // строка 0 — заголовок на всю ширину
                tbl.Cells[0, 0].TextString = title;
                try { tbl.MergeCells(CellRange.Create(tbl, 0, 0, 0, nCols - 1)); } catch { }

                // строка 1 — шапка колонок
                for (int c = 0; c < nCols; c++)
                    tbl.Cells[1, c].TextString = SafeStr(columns[c]);

                // строки данных
                for (int r = 0; r < nRows; r++)
                {
                    var row = (Dictionary<string, object>)rows[r];
                    for (int c = 0; c < nCols; c++)
                    {
                        object v;
                        row.TryGetValue(SafeStr(columns[c]), out v);
                        tbl.Cells[r + 2, c].TextString = SafeStr(v);
                    }
                }

                tbl.GenerateLayout();
                ms.AppendEntity(tbl);
                tr.AddNewlyCreatedDBObject(tbl, true);
                tr.Commit();
            }

            ed.WriteMessage("\nГотово: ведомость \"" + names[pick - 1] + "\", строк: " + nRows + ".");
        }

        // имя блока с учётом динамических/анонимных (*U…) определений
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

        private static string SafeStr(object o)
        {
            return o == null ? "" : o.ToString();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
