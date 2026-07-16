using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(ABlockGenPlugin.VitrageCommand))]

namespace ABlockGenPlugin
{
    /// <summary>
    /// ATVITRAGE (Э1): 2 точки прямоугольника проёма + диалог параметров →
    /// JSON-запрос → движок vitrage_engine.exe (op:plan) → JSON-план → вставка вхождений
    /// СУЩЕСТВУЮЩИХ определений блоков чертежа (стойки/ригели/заполнения).
    /// Свои определения блоков не создаются — только вхождения (как Алексей руками).
    /// </summary>
    public class VitrageCommand
    {
        [CommandMethod("ATVITRAGE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Run()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor;
            var db = doc.Database;

            // ── 1. прямоугольник проёма (2 точки, резинка) ──
            var p1o = new PromptPointOptions("\nПервый угол проёма витража: ");
            var r1 = ed.GetPoint(p1o);
            if (r1.Status != PromptStatus.OK) return;
            var p2o = new PromptCornerOptions("\nПротивоположный угол: ", r1.Value)
            { UseDashedLine = true };
            var r2 = ed.GetCorner(p2o);
            if (r2.Status != PromptStatus.OK) return;

            // Точки приходят в UCS → переводим в WCS. Э1 строит витраж в мировых
            // осях X/Y (вид фасада); повёрнутый UCS в v1 не поддержан — предупреждаем.
            var ucs = ed.CurrentUserCoordinateSystem;
            var w1 = r1.Value.TransformBy(ucs);
            var w2 = r2.Value.TransformBy(ucs);
            if (!ucs.IsEqualTo(Matrix3d.Identity, new Tolerance(1e-9, 1e-9)))
                ed.WriteMessage("\nВнимание: активен ненулевой UCS — витраж строится в мировых осях X/Y (v1).");
            double x0 = Math.Min(w1.X, w2.X), x1 = Math.Max(w1.X, w2.X);
            double y0 = Math.Min(w1.Y, w2.Y), y1 = Math.Max(w1.Y, w2.Y);
            if (x1 - x0 < 1 || y1 - y0 < 1)
            { ed.WriteMessage("\nВырожденный прямоугольник — отмена."); return; }

            // ── 2. определения блоков чертежа (для выбора в диалоге) ──
            var defs = new List<string>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId id in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (btr.IsAnonymous || btr.IsLayout || btr.IsFromExternalReference ||
                        btr.IsFromOverlayReference || btr.IsDependent)
                        continue;
                    defs.Add(btr.Name);
                }
                tr.Commit();
            }
            defs.Sort(StringComparer.OrdinalIgnoreCase);
            if (defs.Count == 0)
            { ed.WriteMessage("\nВ чертеже нет определений блоков — витраж строить не из чего."); return; }

            // ── 3. диалог параметров ──
            var form = new VitrageForm(defs, x1 - x0, y1 - y0);
            if (AcApp.ShowModalDialog(form) != DialogResult.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            // блоки, которых нет в чертеже, не пропускаем (свои определения не выдумываем)
            foreach (var need in new[] { form.StandBlock, form.RigelBlock, form.FillBlock })
                if (need.Length > 0 && !defs.Contains(need))
                { ed.WriteMessage("\nБлок «" + need + "» не найден в чертеже — отмена."); return; }

            // ── 4. запрос движку ──
            var grid = new Dictionary<string, object>
            {
                { "rail_y", form.Rails },
                { "tier_gap", form.TierGap }
            };
            if (form.UseCols) grid["n_cols"] = form.NCols; else grid["step_x"] = form.StepX;
            if (form.Tiers.Count > 0) grid["tiers"] = form.Tiers;
            // поворот вставки — «как принято в этом чертеже»: мода поворотов
            // существующих вхождений выбранных определений (фикс 14.07)
            double standRot = ModeRotation(db, form.StandBlock);
            double rigelRot = form.RigelBlock.Length > 0 ? ModeRotation(db, form.RigelBlock) : 0;
            var blocks = new Dictionary<string, object>
            {
                { "stand", new Dictionary<string, object> {
                    { "name", form.StandBlock }, { "body_w", form.BodyStand },
                    { "rot", standRot } } },
                { "rigel", new Dictionary<string, object> {
                    { "name", form.RigelBlock.Length > 0 ? (object)form.RigelBlock : null },
                    { "body_w", form.BodyRigel }, { "rot", rigelRot } } },
                { "fill", new Dictionary<string, object> {
                    { "name", form.FillBlock.Length > 0 ? (object)form.FillBlock : null },
                    { "fold", form.Fold } } }
            };
            var payload = new Dictionary<string, object>
            {
                { "op", "plan" },
                { "opening", new Dictionary<string, object> {
                    { "x0", x0 }, { "y0", y0 }, { "x1", x1 }, { "y1", y1 } } },
                { "grid", grid },
                { "blocks", blocks },
                { "marks", new Dictionary<string, object> {
                    { "stand", form.MarkStand }, { "rigel", form.MarkRigel },
                    { "fill", form.MarkFill } } }
            };

            // ── 5. вызов движка ──
            string baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string engineExe = Path.GetFullPath(Path.Combine(baseDir, "..", "engine", "vitrage_engine.exe"));
            if (!File.Exists(engineExe))
            { ed.WriteMessage("\nНе найден движок: " + engineExe); return; }

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            Dictionary<string, object> plan;
            try
            {
                plan = ser.DeserializeObject(CallEngine(engineExe, ser.Serialize(payload)))
                       as Dictionary<string, object>;
            }
            catch (System.Exception ex)
            { ed.WriteMessage("\nОшибка движка: " + ex.Message); return; }
            if (plan == null || !GetBool(plan, "ok"))
            { ed.WriteMessage("\nПлан не построен: " + SafeStr(Get(plan, "error"))); return; }

            // ── 6. вставка вхождений + размеры ──
            int inserted, skipped, dimsN;
            try { InsertAll(db, plan, out inserted, out skipped, out dimsN); }
            catch (System.Exception ex)
            { ed.WriteMessage("\nОшибка вставки: " + ex.Message); return; }

            var sum = Get(plan, "summary") as Dictionary<string, object>;
            ed.WriteMessage("\nВитраж построен: вхождений " + inserted +
                            (skipped > 0 ? " (пропущено " + skipped + ")" : "") +
                            (sum != null ? " — стойки " + SafeStr(Get(sum, "stands")) +
                                           ", ригели " + SafeStr(Get(sum, "rigels")) +
                                           ", заполнения " + SafeStr(Get(sum, "fills")) : "") +
                            (dimsN > 0 ? "; размеров " + dimsN : "") + ".");
            var notes = Get(plan, "notes") as IList;
            if (notes != null)
                foreach (var n in notes) ed.WriteMessage("\n  · " + SafeStr(n));
        }

        // ── вставка плана: одна транзакция = один undo (блоки + размеры) ──
        internal static void InsertAll(Database db, Dictionary<string, object> plan,
                                      out int inserted, out int skipped, out int dimsN)
        {
            inserted = 0; skipped = 0; dimsN = 0;
            var items = Get(plan, "inserts") as IList;
            if (items == null) return;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (var itObj in items)
                {
                    var it = itObj as Dictionary<string, object>;
                    if (it == null) { skipped++; continue; }
                    string bname = SafeStr(Get(it, "block"));
                    if (bname.Length == 0 || !bt.Has(bname)) { skipped++; continue; }
                    double x = ToD(Get(it, "x")), y = ToD(Get(it, "y"));
                    double rot = ToD(Get(it, "rot"));
                    string layer = SafeStr(Get(it, "layer"));

                    var br = new BlockReference(new Point3d(x, y, 0), bt[bname])
                    { Rotation = rot * Math.PI / 180.0 };
                    if (layer.Length > 0)
                    {
                        EnsureLayer(db, tr, layer);
                        br.Layer = layer;
                    }
                    ms.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    // 1) динсвойства ДО атрибутов: растяжка меняет геометрию, атрибуты
                    //    потом сядут по обновлённому представлению динблока.
                    var dyn = Get(it, "dyn") as Dictionary<string, object>;
                    if (dyn != null && br.IsDynamicBlock)
                        foreach (DynamicBlockReferenceProperty pr in
                                 br.DynamicBlockReferencePropertyCollection)
                        {
                            if (pr.ReadOnly) continue;
                            object v;
                            if (!dyn.TryGetValue(pr.PropertyName, out v) || v == null) continue;
                            try
                            { pr.Value = Convert.ChangeType(v, pr.Value.GetType(),
                                                            CultureInfo.InvariantCulture); }
                            catch { }
                        }

                    // 2) атрибуты — из ТЕКУЩЕГО представления (после динрастяжки)
                    var attrs = Get(it, "attrs") as Dictionary<string, object>;
                    var repId = br.BlockTableRecord;   // анонимное представление динблока
                    var rbtr = (BlockTableRecord)tr.GetObject(repId, OpenMode.ForRead);
                    if (rbtr.HasAttributeDefinitions)
                        foreach (ObjectId aid in rbtr)
                        {
                            var ad = tr.GetObject(aid, OpenMode.ForRead) as AttributeDefinition;
                            if (ad == null || ad.Constant) continue;
                            var ar = new AttributeReference();
                            ar.SetAttributeFromBlock(ad, br.BlockTransform);
                            object v;
                            if (attrs != null && attrs.TryGetValue(ad.Tag, out v) && v != null)
                                ar.TextString = SafeStr(v);
                            br.AttributeCollection.AppendAttribute(ar);
                            tr.AddNewlyCreatedDBObject(ar, true);
                        }
                    inserted++;
                }

                // ── размеры (фидбэк Алексея 14.07): габаритные + межосевые
                //    цепочки по стойкам и ригелям, RotatedDimension на слое
                //    «Размеры», стиль — текущий стиль чертежа.
                //    Глобальный масштаб (DIMSCALE) — ВСЕГДА 40 объектным
                //    override (фидбэк Алексея 16.07: «умное» правило
                //    «чертёжный ≠1 → берём его» на чертежах с DIMSCALE=100
                //    давало 100 — убрано; иной масштаб правится вручную) ──
                var dims = Get(plan, "dims") as IList;
                if (dims != null && dims.Count > 0)
                {
                    const double dimScale = 40.0;
                    EnsureLayer(db, tr, "Размеры");
                    foreach (var dObj in dims)
                    {
                        var dd = dObj as Dictionary<string, object>;
                        if (dd == null) continue;
                        bool vert = SafeStr(Get(dd, "dir")) == "v";
                        double refc = ToD(Get(dd, "ref"));
                        double line = ToD(Get(dd, "line"));
                        var pts = Get(dd, "pts") as IList;
                        if (pts == null || pts.Count < 2) continue;
                        for (int i = 0; i + 1 < pts.Count; i++)
                        {
                            double a = Convert.ToDouble(pts[i], CultureInfo.InvariantCulture);
                            double b = Convert.ToDouble(pts[i + 1], CultureInfo.InvariantCulture);
                            RotatedDimension rd = vert
                                ? new RotatedDimension(Math.PI / 2.0,
                                    new Point3d(refc, a, 0), new Point3d(refc, b, 0),
                                    new Point3d(line, (a + b) / 2.0, 0), null, db.Dimstyle)
                                : new RotatedDimension(0.0,
                                    new Point3d(a, refc, 0), new Point3d(b, refc, 0),
                                    new Point3d((a + b) / 2.0, line, 0), null, db.Dimstyle);
                            rd.SetDatabaseDefaults();
                            rd.Layer = "Размеры";
                            try { rd.Dimscale = dimScale; } catch { }
                            ms.AppendEntity(rd);
                            tr.AddNewlyCreatedDBObject(rd, true);
                            dimsN++;
                        }
                    }
                }
                tr.Commit();
            }
        }

        // мода поворота существующих вхождений определения в модели (Э1:
        // образца нет — берём «как принято в этом чертеже»; нет вхождений → 0)
        internal static double ModeRotation(Database db, string effName)
        {
            var counts = new Dictionary<double, int>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(
                    bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var br = tr.GetObject(id, OpenMode.ForRead) as BlockReference;
                    if (br == null) continue;
                    if (!string.Equals(RecognizeCommand.EffectiveName(tr, br), effName,
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    double deg = RecognizeCommand.NormDeg(br.Rotation);
                    int c;
                    counts[deg] = counts.TryGetValue(deg, out c) ? c + 1 : 1;
                }
                tr.Commit();
            }
            double best = 0; int bestC = 0;
            foreach (var kv in counts)
                if (kv.Value > bestC) { bestC = kv.Value; best = kv.Key; }
            return best;
        }

        private static void EnsureLayer(Database db, Transaction tr, string name)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return;
            lt.UpgradeOpen();
            var rec = new LayerTableRecord { Name = name };
            lt.Add(rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }

        // ── вызов движка через временные файлы (паттерн ATableSpec) ──
        internal static string CallEngine(string engineExe, string reqJson)
        {
            string tmpIn = Path.Combine(Path.GetTempPath(),
                "vitrage_in_" + Guid.NewGuid().ToString("N") + ".json");
            string tmpOut = Path.Combine(Path.GetTempPath(),
                "vitrage_out_" + Guid.NewGuid().ToString("N") + ".json");
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
                    if (p.ExitCode != 0 && !File.Exists(tmpOut))
                        throw new ApplicationException(
                            err.Length > 0 ? err : "движок вернул код " + p.ExitCode);
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

        internal static string SafeStr(object o)
        { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }
    }
}
