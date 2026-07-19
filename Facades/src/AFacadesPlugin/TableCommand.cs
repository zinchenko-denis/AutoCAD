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
                    string json = ZoneCommand.ReadZoneData(tr, ent);
                    if (json == null) { noData++; continue; }
                    var z = ser.DeserializeObject(json)
                            as Dictionary<string, object>;
                    if (z == null) { noData++; continue; }
                    string id = ZoneCommand.SafeStr(
                        ZoneCommand.Get(z, "zone_id"));
                    if (id.Length == 0 || seen.Contains(id)) continue;

                    // сверка с фактом: изменилась ли штриховка после обсчёта
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
                            if (Math.Abs(fact - stored) > 0.001)
                            {
                                stale.Add(id);
                                z["stale"] = true;
                            }
                        }
                        catch { }
                    }
                    seen.Add(id);
                    zones.Add(z);
                }
                tr.Commit();
            }
            if (zones.Count == 0)
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

            var pdo = new PromptDoubleOptions("\nВысота текста таблицы, мм: ")
            { DefaultValue = 250.0, AllowNegative = false, AllowZero = false };
            var hres = ed.GetDouble(pdo);
            double textH = hres.Status == PromptStatus.OK ? hres.Value : 250.0;

            var ppr = ed.GetPoint("\nТочка вставки таблицы: ");
            if (ppr.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db),
                    OpenMode.ForWrite);
                // пометить изменённые зоны звёздочкой у марки
                foreach (var z in zones)
                    if (z.ContainsKey("stale"))
                        z["zone_id"] = ZoneCommand.SafeStr(
                            ZoneCommand.Get(z, "zone_id")) + " *";
                ZoneCommand.InsertTable(tr, db, btr, ppr.Value, zones, textH);
                tr.Commit();
            }

            ed.WriteMessage("\nATFTABLE: строк " + zones.Count + "." +
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
            var m = Regex.Match(s ?? "", @"(\d+)\s*$");
            int n;
            if (m.Success && int.TryParse(m.Groups[1].Value, out n)) return n;
            return int.MaxValue;
        }
    }
}
