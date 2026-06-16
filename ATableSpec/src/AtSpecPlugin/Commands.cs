// ATSPEC — читает выбранные блоки, спрашивает у пользователя ЧТО построить
// (пресет или произвольный запрос: источник / фильтр / группировка / меры),
// агрегирует во внешнем движке dxf_spec.exe и вставляет ведомость AcDbTable.
//
// ВНИМАНИЕ: компилируется на Windows (NuGet AutoCAD.NET 24.0.0 = AutoCAD 2021).
// В песочнице не собиралось — проверяется автосборкой GitHub Actions.
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013–2024).

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

            // --- 1. выбор блоков ---
            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var pso = new PromptSelectionOptions { MessageForAdding = "\nВыберите блоки фасада: " };
            PromptSelectionResult sel = ed.GetSelection(pso, filter);
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            // --- 2. собрать записи блоков ---
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
                        if (ar != null) attrs[ar.Tag] = ar.TextString;
                    }
                    records.Add(new Dictionary<string, object>
                    {
                        { "name", EffectiveName(tr, br) },
                        { "layer", br.Layer },
                        { "x", br.Position.X }, { "y", br.Position.Y },
                        { "rotation", br.Rotation * 180.0 / Math.PI },
                        { "xscale", br.ScaleFactors.X }, { "yscale", br.ScaleFactors.Y },
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

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

            // --- 4. метаданные для диалога (describe) ---
            Dictionary<string, object> meta;
            try
            {
                string descJson = CallEngine(engineExe, configYaml, new Dictionary<string, object>
                {
                    { "blocks", records }, { "action", "describe" }
                });
                meta = ser.Deserialize<Dictionary<string, object>>(descJson);
            }
            catch (System.Exception e)
            {
                ed.WriteMessage("\nОшибка движка (describe): " + e.Message);
                return;
            }

            var layers = ToStrList(Get(meta, "layers"));
            var types = ToStrList(Get(meta, "types"));
            var fields = ToStrList(Get(meta, "fields"));
            var values = new Dictionary<string, List<string>>();
            var vobj = Get(meta, "values") as Dictionary<string, object>;
            if (vobj != null)
                foreach (var kv in vobj) values[kv.Key] = ToStrList(kv.Value);
            var presets = new List<KeyValuePair<string, string>>();
            var pl = Get(meta, "presets") as IList;
            if (pl != null)
                foreach (var po in pl)
                {
                    var pd = po as Dictionary<string, object>;
                    if (pd == null) continue;
                    string nm = SafeStr(Get(pd, "name"));
                    string tt = pd.ContainsKey("title") ? SafeStr(pd["title"]) : nm;
                    presets.Add(new KeyValuePair<string, string>(nm, tt));
                }

            // --- 5. диалог-построитель ---
            var form = new QueryForm(types, layers, fields, values, presets);
            if (AcApp.ShowModalDialog(form) != DialogResult.OK) { ed.WriteMessage("\nОтменено."); return; }

            // --- 6. собрать payload запроса ---
            var payload = new Dictionary<string, object> { { "blocks", records }, { "action", "run" } };
            if (form.UsePreset) payload["report"] = form.PresetName;
            else payload["query"] = form.Query;

            Dictionary<string, object> result;
            try
            {
                result = ser.Deserialize<Dictionary<string, object>>(CallEngine(engineExe, configYaml, payload));
            }
            catch (System.Exception e)
            {
                ed.WriteMessage("\nОшибка движка (run): " + e.Message);
                return;
            }

            var reports = Get(result, "reports") as IList;
            if (reports == null || reports.Count == 0) { ed.WriteMessage("\nВедомость пустая."); return; }
            var rep = (Dictionary<string, object>)reports[0];
            var columns = (IList)rep["columns"];
            var rows = (IList)rep["rows"];
            string title = SafeStr(Get(rep, "title"));
            if (rows.Count == 0) { ed.WriteMessage("\nПод заданные условия не попало ни одной строки."); return; }

            // --- 7. точка вставки ---
            PromptPointResult pr = ed.GetPoint("\nТочка вставки таблицы: ");
            if (pr.Status != PromptStatus.OK) return;

            // --- 8. построить AcDbTable ---
            int nCols = columns.Count, nRows = rows.Count;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var tbl = new Table();
                tbl.TableStyle = db.Tablestyle;
                tbl.Position = pr.Value;
                tbl.SetSize(nRows + 2, nCols);

                tbl.Cells[0, 0].TextString = title;
                try { tbl.MergeCells(CellRange.Create(tbl, 0, 0, 0, nCols - 1)); } catch { }
                for (int c = 0; c < nCols; c++)
                    tbl.Cells[1, c].TextString = SafeStr(columns[c]);
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
            ed.WriteMessage("\nГотово: \"" + title + "\", строк: " + nRows + ".");
        }

        // --- вызов движка через временные файлы ---
        private static string CallEngine(string engineExe, string configYaml, object payload)
        {
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string tmpIn = Path.Combine(Path.GetTempPath(), "atspec_in_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(), "atspec_out_" + Guid.NewGuid().ToString("N") + ".json");
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
        {
            object v; return (d != null && d.TryGetValue(key, out v)) ? v : null;
        }

        private static List<string> ToStrList(object o)
        {
            var list = new List<string>();
            var il = o as IList;
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
