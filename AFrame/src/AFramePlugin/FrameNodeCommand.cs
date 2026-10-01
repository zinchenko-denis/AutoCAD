using System;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using FacadeSafety;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
using WinForms = System.Windows.Forms;

[assembly: CommandClass(typeof(AFramePlugin.FrameNodeCommand))]

namespace AFramePlugin
{
    // No reactor, drawing-wide search or mutation during review. All source
    // owners are obtained from the selected, verified canonical zone.
    public sealed class FrameNodeCommand
    {
        private sealed class Source
        {
            internal ObjectId ZoneId;
            internal string ZoneHandle, ZoneIdText, Fingerprint;
            internal FacadeProjectParameterStore.Record Project, Binding;
            internal FrameParameterResolution Resolution;
        }
        [CommandMethod("ATFNODE", CommandFlags.Modal)]
        public void Node()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var ed = doc.Editor; var db = doc.Database;
            try
            {
                var options = new PromptKeywordOptions("\nРазмерная схема [Создать/Проверить]: ");
                options.Keywords.Add("Create", "Создать", "Создать"); options.Keywords.Add("Check", "Проверить", "Проверить");
                options.AllowNone = false;
                var mode = ed.GetKeywords(options); if (mode.Status != PromptStatus.OK) return;
                if (mode.StringResult == "Create" || mode.StringResult == "Создать") Create(doc, ed, db);
                else if (mode.StringResult == "Check" || mode.StringResult == "Проверить") Check(ed, db);
            }
            catch (FrameNodeGeometryException ex) { ed.WriteMessage("\nATFNODE: " + ex.Message + " [" + ex.Code + "]"); }
            catch (System.Exception ex) { ed.WriteMessage("\nATFNODE: операция не выполнена. " + ex.Message); }
        }
        private static void Create(Autodesk.AutoCAD.ApplicationServices.Document doc, Editor ed, Database db)
        {
            var selected = ed.GetEntity(new PromptEntityOptions("\nВыберите штриховку или марку зоны, привязанной через ATFZONEPARAMS: "));
            if (selected.Status != PromptStatus.OK) return;
            Source source;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var entity = tr.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
                if (!(entity is Hatch) && !(entity is MText)) Fail("E_NODE_ZONE", "Выберите проверенную штриховку или марку фасадной зоны.");
                source = ReadSource(tr, db, entity); tr.Commit();
            }
            UnitsValue units = db.Insunits; string unitDeclaration;
            if (units == UnitsValue.Millimeters) unitDeclaration = "database_mm";
            else if (units == UnitsValue.Undefined)
            {
                var options = new PromptKeywordOptions("\nВ DWG не заданы единицы. Для этой схемы подтвердить 1 единица = 1 мм [Да/Нет]: ");
                options.Keywords.Add("Yes", "Да", "Да"); options.Keywords.Add("No", "Нет", "Нет"); options.AllowNone = false;
                var answer = ed.GetKeywords(options);
                if (answer.Status != PromptStatus.OK || (answer.StringResult != "Yes" && answer.StringResult != "Да")) return;
                unitDeclaration = "unitless_explicit_mm";
            }
            else { Fail("E_NODE_UNITS", "Для схемы требуются миллиметры либо DWG без единиц с явным подтверждением 1 единица = 1 мм. Единицы чертежа автоматически не меняются."); return; }
            FrameNodeGeometryInput input;
            using (var form = new FrameNodeForm(source.Resolution.Selection, source.Resolution.Context))
            {
                if (AcApp.ShowModalDialog(form) != WinForms.DialogResult.OK) return;
                input = form.Result;
            }
            if (input == null) Fail("E_NODE_INPUT", "После просмотра не получены параметры схемы.");
            var snapshot = FrameNodeSnapshot.Create(source.Resolution.Selection, source.Resolution.Context, input,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            var drawing = FrameNodeDrawingBuilder.Build(snapshot.Result, snapshot.CaptionLines());
            Matrix3d ucs = ed.CurrentUserCoordinateSystem;
            var point = ed.GetPoint(new PromptPointOptions("\nТочка вставки схемы (размеры в мм, направление наружу — +X МСК, схема в XY МСК): "));
            if (point.Status != PromptStatus.OK) return;
            VerifyEnvironment(ed, db, ucs, units);
            // Autodesk's managed UCS example converts GetPoint.Value from the
            // captured current UCS to WCS. BlockReference.Position is WCS.
            Point3d wcs = point.Value.TransformBy(ucs);
            using (doc.LockDocument())
            using (var tr = db.TransactionManager.StartTransaction())
            {
                VerifyEnvironment(ed, db, ucs, units);
                VerifyFresh(tr, db, source);
                // Environment is checked again after fresh source reads; no
                // DWG write (even an empty block/layer) precedes this boundary.
                VerifyEnvironment(ed, db, ucs, units);
                var reference = FrameNodeRenderer.Create(tr, db, drawing, wcs);
                var hatch = (Hatch)tr.GetObject(source.ZoneId, OpenMode.ForRead);
                FrameNodeStore.Write(tr, reference, hatch, source.Fingerprint, snapshot, unitDeclaration, units, drawing.Digest);
                tr.Commit();
            }
            ed.WriteMessage("\nРазмерная схема создана. " + (snapshot.Result.ClearanceStatus == "pass" ?
                "Локальный просвет " + snapshot.Result.GapText + " мм проверен только по заявленным плоскостям." : "Проверка локального просвета не выполнена; данные неполны.") +
                " Это не рабочий узел и не подтверждение совместимости. Актуальность: ATFNODE → Проверить.");
        }
        private static void Check(Editor ed, Database db)
        {
            var selected = ed.GetEntity(new PromptEntityOptions("\nВыберите блок размерной схемы ATFNODE: ")); if (selected.Status != PromptStatus.OK) return;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var owner = tr.GetObject(selected.ObjectId, OpenMode.ForRead) as BlockReference;
                var saved = FrameNodeStore.Read(tr, owner);
                if (db.Insunits != saved.DrawingUnits) Fail("E_NODE_UNITS", "Единицы DWG изменились после вставки схемы.");
                var hatch = tr.GetObject(saved.ZoneId, OpenMode.ForRead) as Hatch;
                var current = ReadSource(tr, db, hatch);
                if (current.ZoneId != saved.ZoneId || current.ZoneHandle != saved.ZoneHandle || current.Fingerprint != saved.ZoneFingerprint ||
                    !FrameParameterContext.Same(current.Resolution.Context, saved.Snapshot.Context) ||
                    !FrameSolutionSelection.Same(current.Resolution.Selection, saved.Snapshot.Selection))
                    Fail("E_NODE_SOURCE_STALE", "Источники схемы изменились. Старый снимок сохранён; создайте новую схему ATFNODE.");
                tr.Commit();
                ed.WriteMessage("\nСнимок и тело схемы соответствуют текущим источникам.\n" + saved.Snapshot.Result.ReviewText() +
                    "Перенос и поворот XY допустимы. Проверка не подтверждает посадки, регулировку, прочность или спецификацию.");
            }
        }
        private static Source ReadSource(Transaction tr, Database db, Entity carrier)
        {
            if (carrier == null || carrier.IsErased) Fail("E_NODE_SOURCE_STALE", "Исходная зона отсутствует.");
            var geometry = ZoneGeometryGuard.Verify(tr, db, carrier, null);
            if (!geometry.Ok || geometry.HatchId.IsNull) Fail("E_NODE_ZONE", "Геометрия зоны не подтверждена: " + geometry.Reason);
            var hatch = tr.GetObject(geometry.HatchId, OpenMode.ForRead) as Hatch;
            if (hatch == null || hatch.IsErased) Fail("E_NODE_ZONE", "Не найден канонический владелец зоны.");
            var reads = new FacadeProjectParameterStore.ReadContext();
            var binding = FacadeProjectParameterStore.ReadZone(tr, hatch, geometry.Fingerprint, reads);
            if (!binding.Present) Fail("E_NODE_UNBOUND", "Зона не привязана к параметрам проекта. Выполните ATFPROJECT и ATFZONEPARAMS; прежнее локальное решение автоматически не наследуется.");
            var project = FacadeProjectParameterStore.ReadProject(tr, db, reads);
            if (!project.Present) Fail("E_NODE_SOURCE_STALE", "Проект связанной зоны отсутствует.");
            var resolution = FrameParameterResolver.Resolve(FrameProjectParameters.FromDict(project.Payload), project.RecordDigest,
                FrameZoneParameters.FromDict(binding.Payload), binding.RecordDigest, hatch.Handle.ToString(), geometry.ZoneId);
            FrameNodeGeometry.ValidateBoundSelection(resolution.Selection, resolution.Context, project.Payload, binding.Payload);
            return new Source { ZoneId = hatch.ObjectId, ZoneHandle = hatch.Handle.ToString(), ZoneIdText = geometry.ZoneId,
                Fingerprint = geometry.Fingerprint, Binding = binding, Project = project, Resolution = resolution };
        }
        private static void VerifyFresh(Transaction tr, Database db, Source expected)
        {
            if (expected.ZoneId.IsErased) Fail("E_NODE_SOURCE_STALE", "Зона удалена после открытия окна.");
            var current = ReadSource(tr, db, tr.GetObject(expected.ZoneId, OpenMode.ForRead) as Hatch);
            FacadeProjectParameterStore.Compare(expected.Project, current.Project, "Параметры проекта изменились после просмотра. Повторите ATFNODE.");
            FacadeProjectParameterStore.Compare(expected.Binding, current.Binding, "Параметры зоны изменились после просмотра. Повторите ATFNODE.");
            if (current.ZoneId != expected.ZoneId || current.ZoneHandle != expected.ZoneHandle || current.Fingerprint != expected.Fingerprint ||
                !FrameParameterContext.Same(current.Resolution.Context, expected.Resolution.Context))
                Fail("E_NODE_SOURCE_STALE", "Геометрия или происхождение зоны изменились после просмотра. Повторите ATFNODE.");
        }
        private static void VerifyEnvironment(Editor ed, Database db, Matrix3d expectedUcs, UnitsValue expectedUnits)
        {
            if (db.Insunits != expectedUnits) Fail("E_NODE_UNITS", "Единицы DWG изменились во время команды. Повторите ATFNODE.");
            double[] before = expectedUcs.ToArray(), after = ed.CurrentUserCoordinateSystem.ToArray();
            if (before.Length != after.Length) Fail("E_NODE_UCS", "ПСК изменилась во время выбора точки. Повторите ATFNODE.");
            for (int i = 0; i < before.Length; i++) if (before[i] != after[i]) Fail("E_NODE_UCS", "ПСК изменилась во время выбора точки. Повторите ATFNODE.");
        }
        private static void Fail(string code, string message) { throw new FrameNodeGeometryException(code, message); }
    }
}
