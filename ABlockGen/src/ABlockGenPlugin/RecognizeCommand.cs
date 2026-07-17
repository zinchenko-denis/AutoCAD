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

            // ── 1. образцы ОДНОЙ выборкой (просьба Алексея 17.07): «Укажите
            //    блоки конструкции» — категория берётся ПО СЛОЮ вставки:
            //    RF-стойки / RF-ригеля (обязательны), RF-заполнения /
            //    RF-створки (опционально), RF-двери — пока пропускаются
            //    (Алексей обдумывает). ПОВОРОТ и атрибут ШИРИНА — с образца
            //    (14.07/15.07c); bbox не меряется (обвес, 15.07b) ──
            var pso0 = new PromptSelectionOptions
            { MessageForAdding = "\nУкажите блоки конструкции (образцы: " +
                                 "стойка, ригель, заполнение, створка): " };
            var selFilter = new SelectionFilter(new[]
            { new TypedValue((int)DxfCode.Start, "INSERT") });
            var res0 = ed.GetSelection(pso0, selFilter);
            if (res0.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return; }

            string standName = null, standProf = null,
                   rigelName = null, rigelProf = null,
                   fillName = null, sashName = null,
                   fillMark = null, sashMark = null;
            double standRot = 0, rigelRot = 0, fillRot = 0, sashRot = 0,
                   standW = 0, rigelW = 0, foldW = 0, foldH = 0;
            bool doorsSeen = false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in res0.Value)
                {
                    var br = tr.GetObject(so.ObjectId, OpenMode.ForRead)
                             as BlockReference;
                    if (br == null) continue;
                    string lay = br.Layer ?? "";
                    string nm, pf; double rt, wd;
                    if (lay.Equals("RF-стойки", StringComparison.OrdinalIgnoreCase))
                    {
                        if (standName != null) continue;    // первый образец слоя
                        if (SampleFromBr(tr, br, out nm, out pf, out rt, out wd))
                        { standName = nm; standProf = pf; standRot = rt; standW = wd; }
                    }
                    else if (lay.Equals("RF-ригеля", StringComparison.OrdinalIgnoreCase))
                    {
                        if (rigelName != null) continue;
                        if (SampleFromBr(tr, br, out nm, out pf, out rt, out wd))
                        { rigelName = nm; rigelProf = pf; rigelRot = rt; rigelW = wd; }
                    }
                    else if (lay.Equals("RF-заполнения", StringComparison.OrdinalIgnoreCase))
                    {
                        if (fillName != null) continue;
                        if (SampleFromBr(tr, br, out nm, out pf, out rt, out wd))
                        {
                            fillName = nm; fillRot = rt;
                            // припуск и марка живут ДЕФОЛТАМИ ATTDEF блока
                            // («20Х20» в РАЗМЕР_ЗАП, «Ст» в МАРКИРОВКА —
                            // «Проба 2.1», 17.07b)
                            AttrDefaults(tr, br, out fillMark,
                                         out foldW, out foldH);
                        }
                    }
                    else if (lay.Equals("RF-створки", StringComparison.OrdinalIgnoreCase))
                    {
                        if (sashName != null) continue;
                        if (SampleFromBr(tr, br, out nm, out pf, out rt, out wd))
                        {
                            sashName = nm; sashRot = rt;
                            double fw, fh;
                            AttrDefaults(tr, br, out sashMark, out fw, out fh);
                        }
                    }
                    else if (lay.StartsWith("RF-двер", StringComparison.OrdinalIgnoreCase))
                        doorsSeen = true;
                }
                tr.Commit();
            }
            if (string.IsNullOrEmpty(standName) || string.IsNullOrEmpty(rigelName))
            {
                ed.WriteMessage("\nВ выборке нужны блоки на слоях RF-стойки И " +
                                "RF-ригеля (найдено: стойка " +
                                (standName ?? "нет") + ", ригель " +
                                (rigelName ?? "нет") + ").");
                return;
            }
            ed.WriteMessage("\nОбразцы: стойка «" + standName + "», ригель «" +
                rigelName + "»" +
                (fillName != null ? ", заполнение «" + fillName + "»" : "") +
                (sashName != null ? ", створка «" + sashName + "»" : "") +
                (doorsSeen ? "; блоки дверей пока пропускаются" : "") + ".");

            // ── 2. конструкция: АР-графика рамкой ──
            var pso = new PromptSelectionOptions
            { MessageForAdding = "\nУкажите конструкцию (АР-графику): " };
            var sel = ed.GetSelection(pso);
            if (sel.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено."); return; }

            // ── 3. сбор полос/панелей/ОТРЕЗКОВ (Э3-E): bbox по графике (AddGab);
            //       голые LINE и ломаные → segments, полосы из них собирает движок ──
            var strips = new List<Dictionary<string, object>>();
            var panels = new List<Dictionary<string, object>>();
            var segments = new List<Dictionary<string, object>>();
            var vees = new List<Dictionary<string, object>>();
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
                        continue;
                    }
                    var lin = ent as Line;
                    if (lin != null)
                    {
                        segments.Add(Seg(lin.StartPoint.X, lin.StartPoint.Y,
                                         lin.EndPoint.X, lin.EndPoint.Y));
                        continue;
                    }
                    var pl = ent as Polyline;
                    if (pl != null)
                    {
                        // «галочка» открывания (17.07b): незамкнутая
                        // 3-точечная ломаная, оба звена наклонные —
                        // отдельным каналом vees (направление створки)
                        if (!pl.Closed && pl.NumberOfVertices == 3)
                        {
                            var q0 = pl.GetPoint2dAt(0);
                            var q1 = pl.GetPoint2dAt(1);
                            var q2 = pl.GetPoint2dAt(2);
                            bool sl1 = Math.Abs(q1.X - q0.X) > 0.5 &&
                                       Math.Abs(q1.Y - q0.Y) > 0.5;
                            bool sl2 = Math.Abs(q2.X - q1.X) > 0.5 &&
                                       Math.Abs(q2.Y - q1.Y) > 0.5;
                            if (sl1 && sl2)
                            {
                                vees.Add(new Dictionary<string, object>
                                { { "p", new[] {
                                    new[] { q0.X, q0.Y },
                                    new[] { q1.X, q1.Y },
                                    new[] { q2.X, q2.Y } } } });
                                continue;
                            }
                        }
                        if (pl.Closed && pl.NumberOfVertices <= 5)
                        {
                            // простой замкнутый контур (≈прямоугольник) — полоса
                            try
                            {
                                var ex = pl.GeometricExtents;
                                strips.Add(Box(new[] { ex.MinPoint.X, ex.MinPoint.Y,
                                                       ex.MaxPoint.X, ex.MaxPoint.Y }));
                            }
                            catch { }
                        }
                        else
                        {
                            int n = pl.NumberOfVertices;
                            int segsN = pl.Closed ? n : n - 1;
                            for (int i = 0; i < segsN; i++)
                            {
                                if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9) continue;   // дуги мимо
                                var a = pl.GetPoint2dAt(i);
                                var b = pl.GetPoint2dAt((i + 1) % n);
                                segments.Add(Seg(a.X, a.Y, b.X, b.Y));
                            }
                        }
                        continue;
                    }
                    if (ent is Polyline2d || ent is Polyline3d)
                    {
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
            if (strips.Count == 0 && segments.Count == 0)
            { ed.WriteMessage("\nВ выборе нет пригодной АР-графики (вставки/полилинии/отрезки)."); return; }

            // ── 3b. ТЕРМОРАЗРЫВ (сценарий Алексея 15.07c): конструкция выше
            //    хлыста 6000 → две «резиновые» горизонтали (низ 1-го и 2-го
            //    терморазрывов) + размер зазора (Enter = 5 мм) ──
            double thL1 = 0, thL2 = 0, thGap = 5.0;
            bool thermal = false;
            {
                double yMin = double.MaxValue, yMax = double.MinValue,
                       xMin = double.MaxValue, xMax = double.MinValue;
                foreach (var s in strips) { AccBox(s, ref xMin, ref yMin, ref xMax, ref yMax); }
                foreach (var s in segments) { AccBox(s, ref xMin, ref yMin, ref xMax, ref yMax); }
                if (yMax - yMin > 6000.0)
                {
                    ed.WriteMessage("\nКонструкция выше 6000 (хлыст стойки) — задайте терморазрывы.");
                    if (!PickHLine(ed, xMin, xMax,
                        "\nУкажите нижнюю линию ПЕРВОГО терморазрыва от низа конструкции: ",
                        out thL1)) return;
                    while (true)
                    {
                        if (!PickHLine(ed, xMin, xMax,
                            "\nУкажите нижнюю линию ВТОРОГО терморазрыва (выше первого): ",
                            out thL2)) return;
                        if (thL2 > thL1 + 1.0) break;
                        ed.WriteMessage("\nВторая линия должна быть ВЫШЕ первой.");
                    }
                    var pdo = new PromptDoubleOptions("\nРазмер терморазрыва <5>: ")
                    { AllowNone = true, AllowNegative = false, AllowZero = false,
                      DefaultValue = 5.0, UseDefaultValue = true };
                    var dres = ed.GetDouble(pdo);
                    if (dres.Status == PromptStatus.OK || dres.Status == PromptStatus.None)
                        thGap = dres.Status == PromptStatus.OK ? dres.Value : 5.0;
                    else { ed.WriteMessage("\nОтменено."); return; }
                    thermal = true;
                }
            }

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

            var stand = new Dictionary<string, object> {
                { "name", standName }, { "prof", standProf }, { "rot", standRot } };
            if (standW > 0) stand["body_w"] = standW;   // атрибут ШИРИНА (15.07c)
            var rigel = new Dictionary<string, object> {
                { "name", rigelName }, { "prof", rigelProf }, { "rot", rigelRot } };
            if (rigelW > 0) rigel["body_w"] = rigelW;   // посадка обвязки ±ШИРИНА/2
            var blocks = new Dictionary<string, object>
            { { "stand", stand }, { "rigel", rigel } };
            if (fillName != null)                        // заполнения (17.07)
            {
                var fl = new Dictionary<string, object>
                { { "name", fillName }, { "rot", fillRot } };
                if (foldW > 0) fl["fold_w"] = foldW;     // «20Х20» из блока
                if (foldH > 0) fl["fold_h"] = foldH;
                if (!string.IsNullOrEmpty(fillMark)) fl["mark"] = fillMark;
                blocks["fill"] = fl;
            }
            if (sashName != null)                        // створки (17.07)
            {
                var sh = new Dictionary<string, object>
                { { "name", sashName }, { "rot", sashRot } };
                if (!string.IsNullOrEmpty(sashMark)) sh["mark"] = sashMark;
                blocks["sash"] = sh;
            }
            var payload = new Dictionary<string, object>
            {
                { "op", "recognize" },
                { "strips", strips },
                { "segments", segments },
                { "panels", panels },
                { "vees", vees },
                { "blocks", blocks },
            };
            if (thermal)
                payload["params"] = new Dictionary<string, object>
                { { "thermal", new Dictionary<string, object> {
                    { "l1", thL1 }, { "l2", thL2 }, { "gap", thGap } } } };
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

            // ── 7. вставка вхождений + размеры ──
            int inserted, skipped, dimsN;
            List<string> dynMiss, dynFb;
            try { VitrageCommand.InsertAll(db, plan, out inserted, out skipped, out dimsN, out dynMiss, out dynFb); }
            catch (System.Exception ex)
            { ed.WriteMessage("\nОшибка вставки: " + ex.Message); return; }

            var sum = VitrageCommand.Get(plan, "summary") as Dictionary<string, object>;
            ed.WriteMessage("\nКаркас построен: вхождений " + inserted +
                (skipped > 0 ? " (пропущено " + skipped + ")" : "") +
                (sum != null ? " — стойки " + VitrageCommand.SafeStr(VitrageCommand.Get(sum, "stands")) +
                               ", ригели " + VitrageCommand.SafeStr(VitrageCommand.Get(sum, "rigels")) : "") +
                (dimsN > 0 ? "; размеров " + dimsN : "") + ".");
            var notes = VitrageCommand.Get(plan, "notes") as System.Collections.IList;
            if (notes != null)
                foreach (var n in notes) ed.WriteMessage("\n  · " + VitrageCommand.SafeStr(n));
            if (dynMiss != null && dynMiss.Count > 0)
                ed.WriteMessage("\n  ! динсвойства не установлены (" +
                    dynMiss.Count + "): " + string.Join("; ",
                    dynMiss.GetRange(0, Math.Min(3, dynMiss.Count)).ToArray()) +
                    " — проверьте имя параметра/значения состояний блока.");
            if (dynFb != null && dynFb.Count > 0)
                ed.WriteMessage("\n  ! открывание распознано, но состояния нет " +
                    "в Visibility1 блока (" + dynFb.Count + "): " +
                    string.Join("; ", dynFb.ToArray()) +
                    " — добавьте состояние в блок и переключите вручную.");
        }

        // ── образец из вставки (в открытой транзакции): эффективное имя +
        //    ПРОФ + ПОВОРОТ (градусы) + ТЕЛО из атрибута «ШИРИНА» (15.07c:
        //    ATTRIB вставки ИЛИ константный ATTDEF определения — «Проба 2»).
        //    bbox НЕ меряется (обвес, урок 15.07b) ──
        private static bool SampleFromBr(Transaction tr, BlockReference br,
                                         out string name, out string prof,
                                         out double rotDeg, out double widthMm)
        {
            name = EffectiveName(tr, br); prof = null;
            rotDeg = NormDeg(br.Rotation); widthMm = 0;
            foreach (ObjectId aid in br.AttributeCollection)
            {
                var ar = tr.GetObject(aid, OpenMode.ForRead) as AttributeReference;
                if (ar == null) continue;
                if (string.Equals(ar.Tag, "ПРОФ", StringComparison.OrdinalIgnoreCase))
                    prof = ar.TextString;
                else if (string.Equals(ar.Tag, "ШИРИНА", StringComparison.OrdinalIgnoreCase))
                    widthMm = ParseMm(ar.TextString);
            }
            if (widthMm <= 0)
            {
                // константный атрибут: ATTDEF в определении (Проба 2)
                var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)
                          as BlockTableRecord;
                if (btr != null)
                    foreach (ObjectId eid in btr)
                    {
                        var ad = tr.GetObject(eid, OpenMode.ForRead)
                                 as AttributeDefinition;
                        if (ad != null && string.Equals(ad.Tag, "ШИРИНА",
                                StringComparison.OrdinalIgnoreCase))
                        { widthMm = ParseMm(ad.TextString); break; }
                    }
            }
            return !string.IsNullOrEmpty(name);
        }

        // аккумуляция bbox по словарю {x0,y0,x1,y1}
        private static void AccBox(Dictionary<string, object> s,
                                   ref double xMin, ref double yMin,
                                   ref double xMax, ref double yMax)
        {
            double x0 = Convert.ToDouble(s["x0"], CultureInfo.InvariantCulture);
            double y0 = Convert.ToDouble(s["y0"], CultureInfo.InvariantCulture);
            double x1 = Convert.ToDouble(s["x1"], CultureInfo.InvariantCulture);
            double y1 = Convert.ToDouble(s["y1"], CultureInfo.InvariantCulture);
            if (Math.Min(x0, x1) < xMin) xMin = Math.Min(x0, x1);
            if (Math.Min(y0, y1) < yMin) yMin = Math.Min(y0, y1);
            if (Math.Max(x0, x1) > xMax) xMax = Math.Max(x0, x1);
            if (Math.Max(y0, y1) > yMax) yMax = Math.Max(y0, y1);
        }

        // «резиновая» горизонталь через курсор поперёк АР (джиг) → Y (WCS)
        private static bool PickHLine(Editor ed, double x0, double x1,
                                      string msg, out double y)
        {
            y = 0;
            var jig = new HLineJig(msg, x0 - 500.0, x1 + 500.0);
            var res = ed.Drag(jig);
            if (res.Status != PromptStatus.OK)
            { ed.WriteMessage("\nОтменено."); return false; }
            y = jig.Y;
            return true;
        }

        private sealed class HLineJig : Autodesk.AutoCAD.EditorInput.DrawJig
        {
            private readonly string _msg;
            private readonly double _x0, _x1;
            private Point3d _pt = Point3d.Origin;
            public double Y { get { return _pt.Y; } }
            public HLineJig(string msg, double x0, double x1)
            { _msg = msg; _x0 = x0; _x1 = x1; }

            protected override SamplerStatus Sampler(JigPrompts prompts)
            {
                var opts = new JigPromptPointOptions(_msg)
                {
                    UserInputControls =
                        UserInputControls.Accept3dCoordinates |
                        UserInputControls.NullResponseAccepted
                };
                var res = prompts.AcquirePoint(opts);
                if (res.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                if (res.Value.DistanceTo(_pt) < 1e-6) return SamplerStatus.NoChange;
                _pt = res.Value;
                return SamplerStatus.OK;
            }

            protected override bool WorldDraw(
                Autodesk.AutoCAD.GraphicsInterface.WorldDraw draw)
            {
                draw.Geometry.WorldLine(new Point3d(_x0, _pt.Y, 0),
                                        new Point3d(_x1, _pt.Y, 0));
                return true;
            }
        }

        // ── дефолты ATTDEF образца заполнения/створки (17.07b, «Проба 2.1»):
        //    МАРКИРОВКА → марка-константа («Ст»/«С01»; пусто или с «#» —
        //    не используется, движок нумерует шаблоном); РАЗМЕР_ЗАП вида
        //    «20Х20» (Х кириллическая или латинская) → припуски fold_w/fold_h ──
        private static void AttrDefaults(Transaction tr, BlockReference br,
                                         out string mark,
                                         out double foldW, out double foldH)
        {
            mark = null; foldW = 0; foldH = 0;
            try
            {
                var btr = tr.GetObject(br.BlockTableRecord, OpenMode.ForRead)
                          as BlockTableRecord;
                if (btr == null) return;
                foreach (ObjectId eid in btr)
                {
                    var ad = tr.GetObject(eid, OpenMode.ForRead)
                             as AttributeDefinition;
                    if (ad == null) continue;
                    if (string.Equals(ad.Tag, "МАРКИРОВКА",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string t = (ad.TextString ?? "").Trim();
                        if (t.Length > 0 && t.IndexOf('#') < 0) mark = t;
                    }
                    else if (string.Equals(ad.Tag, "РАЗМЕР_ЗАП",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        string t = (ad.TextString ?? "").Trim()
                                   .Replace('Х', 'X').Replace('х', 'X')
                                   .Replace('x', 'X');
                        var parts = t.Split('X');
                        double a, b;
                        if (parts.Length == 2 &&
                            double.TryParse(parts[0], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out a) &&
                            double.TryParse(parts[1], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out b) &&
                            a > 0 && b > 0)
                        { foldW = a; foldH = b; }
                    }
                }
            }
            catch { }
        }

        // «53» / «52.5» / «52,5» → мм; мусор → 0
        private static double ParseMm(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            double v;
            if (double.TryParse(s.Trim().Replace(',', '.'),
                                System.Globalization.NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v) && v > 0)
                return v;
            return 0;
        }

        // радианы → градусы [0..360), округление 0.1
        internal static double NormDeg(double rad)
        {
            double d = Math.Round(rad * 180.0 / Math.PI, 1) % 360.0;
            return d < 0 ? d + 360.0 : d;
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

        private static Dictionary<string, object> Seg(double x0, double y0,
                                                      double x1, double y1)
        {
            return new Dictionary<string, object>
            { { "x0", x0 }, { "y0", y0 }, { "x1", x1 }, { "y1", y1 } };
        }

        // сдвиг плана: левый-нижний угол каркаса (min ось X, min Y стоек) → target;
        // размерные цепочки (dims) едут вместе с каркасом
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
            var dims = VitrageCommand.Get(plan, "dims") as System.Collections.IList;
            if (dims == null) return;
            foreach (var o in dims)
            {
                var dd = o as Dictionary<string, object>;
                if (dd == null) continue;
                bool vert = VitrageCommand.SafeStr(VitrageCommand.Get(dd, "dir")) == "v";
                double along = vert ? dy : dx;    // вдоль pts
                double across = vert ? dx : dy;   // ref/line
                dd["ref"] = Convert.ToDouble(VitrageCommand.Get(dd, "ref"), CultureInfo.InvariantCulture) + across;
                dd["line"] = Convert.ToDouble(VitrageCommand.Get(dd, "line"), CultureInfo.InvariantCulture) + across;
                var pts = VitrageCommand.Get(dd, "pts") as System.Collections.IList;
                if (pts == null) continue;
                for (int i = 0; i < pts.Count; i++)
                    pts[i] = Convert.ToDouble(pts[i], CultureInfo.InvariantCulture) + along;
            }
        }
    }
}
