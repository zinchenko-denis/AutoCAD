using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(ABlockGenPlugin.RecognizeCommand))]

namespace ABlockGenPlugin
{
    /// <summary>
    /// ATVITRAGEAR (Э3): «Укажите блок стоек» → «Укажите блок ригелей» →
    /// «Укажите конструкцию» (АР-графика рамкой) → движок распознаёт полосы
    /// импостов (оси/цепочки/отметки, тело профиля — из ширины полос, двери —
    /// пропуск порога) → каркас вставками указанных блоков-образцов.
    /// Workflow Алексея, файл-эталон «Проба 3.dxf».
    /// </summary>
    public class RecognizeCommand
    {
        [CommandMethod("ATVITRAGEAR", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            // ── 1. образцы: блок стоек, блок ригелей ──
            string standName, standProf, rigelName, rigelProf;
            if (!PickSample(ed, db, "\nУкажите блок стоек (образец): ",
                            out standName, out standProf)) return;
            if (!PickSample(ed, db, "\nУкажите блок ригелей (образец): ",
                            out rigelName, out rigelProf)) return;

            // ── 2. конструкция: АР-графика рамкой ──
            var pso = new PromptSelectionOptions
            { MessageForAdding = "\nУкажите конструкцию (АР-графику): " };
            var sel = ed.GetSelection(pso);
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            // ── 3. сбор полос/панелей: bbox по ГРАФИКЕ (паттерн AddGab) ──
            var strips = new List<Dictionary<string, object>>();
            var panels = new List<Dictionary<string, object>>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in sel.Value)
                {
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    var br = ent as BlockReference;
                    if (br != null)
                    {
                        // готовые КМД-блоки в выборе — не еда распознавания
                        if (ent.Layer.StartsWith("RF-", StringComparison.OrdinalIgnoreCase))
                            continue;
                        double[] bb = GraphicsBox(tr, br);
                        if (bb == null) continue;
                        strips.Add(Box(bb));
                        var p = Box(bb);
                        p["name"] = EffectiveName(tr, br);
                        panels.Add(p);
                    }
                    else if (ent is Polyline || ent is Polyline2d || ent is Polyline3d)
                    {
                        // на будущее: АР голыми контурами; прямоугольник = полоса
                        try
                        {
                            var ex = ent.GeometricExtents;
                            strips.Add(Box(new[] { ex.MinPoint.X, ex.MinPoint.Y,
                                                   ex.MaxPoint.X, ex.MaxPoint.Y }));
                        }
                        catch { }
                    }
                }
                tr.Commit();
            }
            if (strips.Count == 0)
            { ed.WriteMessage("\nВ выборе нет пригодной АР-графики (вставки/полилинии)."); return; }

            // ── 4. точка вставки результата (Enter — на месте АР) ──
            var ppo = new PromptPointOptions(
                "\nТочка вставки конструкции <Enter — на месте АР>: ")
            { AllowNone = true };
            var pres = ed.GetPoint(ppo);
            bool shift = pres.Status == PromptStatus.OK;
            Point3d target = shift ? pres.Value.TransformBy(ed.CurrentUserCoordinateSystem)
                                   : Point3d.Origin;

            // ── 5. движок ──
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string engineExe = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "vitrage_engine.exe"));
            if (!File.Exists(engineExe))
            { ed.WriteMessage("\nНе найден движок: " + engineExe); return; }

            var payload = new Dictionary<string, object>
            {
                { "op", "recognize" },
                { "strips", strips },
                { "panels", panels },
                { "blocks", new Dictionary<string, object> {
                    { "stand", new Dictionary<string, object> {
                        { "name", standName }, { "prof", standProf } } },
                    { "rigel", new Dictionary<string, object> {
                        { "name", rigelName }, { "prof", rigelProf } } } } },
            };
            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> plan;
            try
            {
                plan = ser.DeserializeObject(VitrageCommand.CallEngine(engineExe, ser.Serialize(payload)))
                       as Dictionary<string, object>;
            }
            catch (System.Exception ex)
            { ed.WriteMessage("\nОшибка движка: " + ex.Message); return; }
            if (plan == null || !VitrageCommand.GetBool(plan, "ok"))
            {
                ed.WriteMessage("\nКаркас не распознан: " +
                    VitrageCommand.SafeStr(VitrageCommand.Get(plan, "error")));
                return;
            }

            // ── 6. сдвиг в точку вставки (опора — левый низ каркаса) ──
            if (shift) ShiftPlan(plan, target);

            // ── 7. вставка ──
            int inserted, skipped;
            try { VitrageCommand.InsertAll(db, plan, out inserted, out skipped); }
            catch (System.Exception ex)
            { ed.WriteMessage("\nОшибка вставки: " + ex.Message); return; }

            var sum = VitrageCommand.Get(plan, "summary") as Dictionary<string, object>;
            ed.WriteMessage("\nКаркас построен: вхождений " + inserted +
                (skipped > 0 ? " (пропущено " + skipped + ")" : "") +
                (sum != null ? " — стойки " + VitrageCommand.SafeStr(VitrageCommand.Get(sum, "stands")) +
                               ", ригели " + VitrageCommand.SafeStr(VitrageCommand.Get(sum, "rigels")) : "") + ".");
            var notes = VitrageCommand.Get(plan, "notes") as System.Collections.IList;
            if (notes != null)
                foreach (var n in notes) ed.WriteMessage("\n  · " + VitrageCommand.SafeStr(n));
        }

        // ── образец: BlockReference → эффективное имя + ПРОФ ──
        private static bool PickSample(Editor ed, Database db, string msg,
                                       out string name, out string prof)
        {
            name = null; prof = null;
            var peo = new PromptEntityOptions(msg);
            peo.SetRejectMessage("\nНужен блок (вхождение).");
            peo.AddAllowedClass(typeof(BlockReference), false);
            var res = ed.GetEntity(peo);
            if (res.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return false; }
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var br = tr.GetObject(res.ObjectId, OpenMode.ForRead) as BlockReference;
                if (br == null) { ed.WriteMessage("\nЭто не блок."); return false; }
                name = EffectiveName(tr, br);
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    var ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                    if (ar != null && string.Equals(ar.Tag, "ПРОФ", StringComparison.OrdinalIgnoreCase))
                    { prof = ar.TextString; break; }
                }
                tr.Commit();
            }
            if (string.IsNullOrEmpty(name)) { ed.WriteMessage("\nНе удалось прочитать имя блока."); return false; }
            return true;
        }

        // эффективное имя динблока (исходное определение, не *U…)
        internal static string EffectiveName(Transaction tr, BlockReference br)
        {
            try
            {
                var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
                var btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                return btr != null ? btr.Name : null;
            }
            catch { return null; }
        }

        // bbox по графике определения (без текстов/размеров/штриховок) — AddGab-паттерн
        private static double[] GraphicsBox(Transaction tr, BlockReference br)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue,
                   x1 = double.MinValue, y1 = double.MinValue;
            bool any = false;
            try
            {
                var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
                if (btr != null)
                {
                    Matrix3d m = br.BlockTransform;
                    foreach (ObjectId eid in btr)
                    {
                        var ent = tr.GetObject(eid, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;
                        if (ent is DBText || ent is MText || ent is AttributeDefinition ||
                            ent is Dimension || ent is Hatch) continue;
                        try
                        {
                            Extents3d ex = ent.GeometricExtents;
                            ex.TransformBy(m);
                            if (ex.MinPoint.X < x0) x0 = ex.MinPoint.X;
                            if (ex.MinPoint.Y < y0) y0 = ex.MinPoint.Y;
                            if (ex.MaxPoint.X > x1) x1 = ex.MaxPoint.X;
                            if (ex.MaxPoint.Y > y1) y1 = ex.MaxPoint.Y;
                            any = true;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            if (!any)
            {
                try
                {
                    Extents3d ex2 = br.GeometricExtents;
                    x0 = ex2.MinPoint.X; y0 = ex2.MinPoint.Y;
                    x1 = ex2.MaxPoint.X; y1 = ex2.MaxPoint.Y;
                    any = true;
                }
                catch { }
            }
            return any ? new[] { x0, y0, x1, y1 } : null;
        }

        private static Dictionary<string, object> Box(double[] bb)
        {
            return new Dictionary<string, object>
            { { "x0", bb[0] }, { "y0", bb[1] }, { "x1", bb[2] }, { "y1", bb[3] } };
        }

        // сдвиг плана: левый-нижний угол каркаса (min ось X, min Y стоек) → target
        private static void ShiftPlan(Dictionary<string, object> plan, Point3d target)
        {
            var items = VitrageCommand.Get(plan, "inserts") as System.Collections.IList;
            if (items == null || items.Count == 0) return;
            double minX = double.MaxValue, minY = double.MaxValue;
            foreach (var o in items)
            {
                var it = o as Dictionary<string, object>;
                if (it == null) continue;
                double x = Convert.ToDouble(VitrageCommand.Get(it, "x"), CultureInfo.InvariantCulture);
                double y = Convert.ToDouble(VitrageCommand.Get(it, "y"), CultureInfo.InvariantCulture);
                if (x < minX) minX = x;
                if (y < minY) minY = y;
            }
            double dx = target.X - minX, dy = target.Y - minY;
            foreach (var o in items)
            {
                var it = o as Dictionary<string, object>;
                if (it == null) continue;
                it["x"] = Convert.ToDouble(VitrageCommand.Get(it, "x"), CultureInfo.InvariantCulture) + dx;
                it["y"] = Convert.ToDouble(VitrageCommand.Get(it, "y"), CultureInfo.InvariantCulture) + dy;
            }
        }
    }
}
