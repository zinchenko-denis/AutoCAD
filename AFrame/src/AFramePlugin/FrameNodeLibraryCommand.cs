using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AFramePlugin.FrameNodeLibraryCommand))]

namespace AFramePlugin
{
    /// <summary>Native dynamic node library, independent of ATFNODE layer diagrams.</summary>
    public sealed class FrameNodeLibraryCommand
    {
        [CommandMethod("ATFNODEEDIT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void Edit()
        {
            Run("ATFNODEEDIT", (doc, ed, db) =>
            {
                UnitsValue units;
                if (!Millimeters(ed, db, out units)) return;
                ed.WriteMessage("\nУчебный пилот библиотеки. Размеры от стены в локальной системе узла, мм. Они задают графику; монтажный подбор не выполняется.");
                var insulation = Positive(ed, "\nТолщина утеплителя, мм: "); if (insulation.Status != PromptStatus.OK) return;
                var cladding = Positive(ed, "\nПоложение наружной плоскости облицовки от стены, мм: "); if (cladding.Status != PromptStatus.OK) return;
                ed.WriteMessage("\nA — КР2 70×200, ГП 40×40×1,2; B — КР2 70×250, ГП 60×40×1,2. ПП и УК остаются по исходному узлу.");
                var options = new PromptKeywordOptions("\nНабор деталей [A/B]: ");
                options.Keywords.Add("A"); options.Keywords.Add("B"); options.AllowNone = false;
                var variant = ed.GetKeywords(options); if (variant.Status != PromptStatus.OK) return;
                var values = new FrameNodeLibraryValues(insulation.Value, cladding.Value,
                    variant.StringResult == "A" ? FrameNodeLibraryContract.VariantA : FrameNodeLibraryContract.VariantB);
                values.Validate();
                var select = new PromptSelectionOptions { MessageForAdding = "\nВыберите рамкой или по одному нужные библиотечные узлы: " };
                // Deliberately no INSERT-only filter: an accidental foreign
                // selection must be reported, not silently ignored as success.
                var result = ed.GetSelection(select); if (result.Status != PromptStatus.OK) return;
                var ids = new List<object>(); foreach (var id in result.Value.GetObjectIds()) ids.Add(id);
                int changed;
                using (doc.LockDocument())
                using (var transaction = new FrameNodeLibraryCad(db))
                {
                    CheckUnits(db, units);
                    changed = FrameNodeLibraryEdit.Apply(transaction, ids, values);
                }
                ed.WriteMessage("\nВыбрано узлов: " + ids.Count + "; изменено: " + changed + ". Изменения всей группы записаны одной операцией; отмена — UNDO.");
            });
        }

        [CommandMethod("ATFNODEIMPORT", CommandFlags.Modal)]
        public void Import()
        {
            Run("ATFNODEIMPORT", (doc, ed, db) =>
            {
                UnitsValue units;
                if (!Millimeters(ed, db, out units)) return;
                var options = new PromptOpenFileOptions("\nВыберите DWG учебного пилота библиотеки одного динамического узла:")
                { Filter = "Чертежи AutoCAD (*.dwg)|*.dwg" };
                var file = ed.GetFileNameForOpen(options); if (file.Status != PromptStatus.OK) return;
                var ucs = ed.CurrentUserCoordinateSystem;
                var point = ed.GetPoint("\nТочка вставки узла: "); if (point.Status != PromptStatus.OK) return;
                // All cancelable prompts precede file loading and DB writes.
                var position = point.Value.TransformBy(ucs);
                CheckEnvironment(ed, db, units, ucs);
                using (var source = new Database(false, true))
                {
                    source.ReadDwgFile(file.StringResult, FileOpenMode.OpenForReadAndReadShare, false, null);
                    source.CloseInput(true);
                    if (source.Insunits != UnitsValue.Millimeters)
                        FrameNodeLibraryContract.Fail("Библиотечный DWG должен явно использовать миллиметры (INSUNITS=4).");
                    ObjectId definitionId = ValidateSource(source);
                    using (doc.LockDocument())
                    using (var transaction = new FrameNodeLibraryCad(db))
                    {
                        CheckEnvironment(ed, db, units, ucs);
                        var map = new IdMapping();
                        // Mangle all duplicate symbol names, including nested
                        // dependencies. Never reuse/replace a foreign definition.
                        source.WblockCloneObjects(new ObjectIdCollection(new[] { definitionId }), db.BlockTableId,
                            map, DuplicateRecordCloning.MangleName, false);
                        var imported = map[definitionId];
                        if (!imported.IsCloned || imported.Value.IsNull)
                            FrameNodeLibraryContract.Fail("Определение библиотеки не было импортировано как новое.");
                        var definition = (BlockTableRecord)transaction.Transaction.GetObject(imported.Value, OpenMode.ForRead);
                        FrameNodeLibraryCad.ValidateDefinition(transaction.Transaction, definition);
                        var block = new BlockReference(position, definition.ObjectId);
                        var space = (BlockTableRecord)transaction.Transaction.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                        space.AppendEntity(block); transaction.Transaction.AddNewlyCreatedDBObject(block, true);
                        FrameNodeLibraryCad.AddAttributes(transaction.Transaction, definition, block);
                        transaction.Read(block.ObjectId);
                        transaction.Commit();
                    }
                }
                ed.WriteMessage("\nДинамический узел учебного пилота импортирован. Условные детали указаны на чертеже. Изменение выбранных экземпляров — ATFNODEEDIT; пять копий для проверки — ATFNODEDEMO.");
            });
        }

        [CommandMethod("ATFNODEDEMO", CommandFlags.Modal)]
        public void Demo()
        {
            Run("ATFNODEDEMO", (doc, ed, db) =>
            {
                UnitsValue units;
                if (!Millimeters(ed, db, out units)) return;
                var result = ed.GetEntity("\nВыберите библиотечный узел для пяти контрольных копий: ");
                if (result.Status != PromptStatus.OK) return;
                var ucs = ed.CurrentUserCoordinateSystem;
                var point = ed.GetPoint("\nТочка первой из пяти копий, ряд вправо по X МСК: "); if (point.Status != PromptStatus.OK) return;
                var position = point.Value.TransformBy(ucs);
                using (doc.LockDocument())
                using (var transaction = new FrameNodeLibraryCad(db))
                {
                    CheckEnvironment(ed, db, units, ucs);
                    var before = transaction.Read(result.ObjectId);
                    var original = transaction.Reference(result.ObjectId);
                    var extents = original.GeometricExtents;
                    double width = extents.MaxPoint.X - extents.MinPoint.X;
                    if (!FrameNodeLibraryContract.Finite(width) || width <= 0)
                        FrameNodeLibraryContract.Fail("Не удалось определить ширину исходного узла для размещения ряда.");
                    // Spacing is a sheet layout convenience, not an engineering dimension.
                    double step = width * 1.3;
                    for (int i = 0; i < 5; i++)
                    {
                        var map = new IdMapping();
                        db.DeepCloneObjects(new ObjectIdCollection(new[] { result.ObjectId }), db.CurrentSpaceId, map, false);
                        var clone = map[result.ObjectId];
                        if (!clone.IsCloned) FrameNodeLibraryContract.Fail("AutoCAD не создал контрольную копию.");
                        var block = transaction.Reference(clone.Value, OpenMode.ForWrite);
                        var next = new Point3d(position.X + i * step, position.Y, position.Z);
                        block.TransformBy(Matrix3d.Displacement(next - block.Position));
                        transaction.VerifyCopy(original, block, next);
                        var copied = transaction.Read(clone.Value);
                        if (!before.Values.Same(copied.Values) || before.Definition != copied.Definition)
                            FrameNodeLibraryContract.Fail("Параметры нативной копии отличаются от исходного узла.");
                    }
                    var after = transaction.Read(result.ObjectId);
                    before.VerifyPreserved(after);
                    if (!before.Values.Same(after.Values)) FrameNodeLibraryContract.Fail("Исходный узел изменился при копировании.");
                    transaction.Commit();
                }
                ed.WriteMessage("\nСозданы пять отдельных нативных копий; исходный узел сохранён. ATFNODEEDIT: измените первые три, проверьте два остальных, обратный переход, UNDO и повторное открытие DWG.");
            });
        }

        private static ObjectId ValidateSource(Database source)
        {
            using (var transaction = new FrameNodeLibraryCad(source))
            {
                var tr = transaction.Transaction;
                var table = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead);
                ObjectId found = ObjectId.Null;
                foreach (ObjectId id in table)
                {
                    var definition = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    // Evaluation BTRs may inherit the extension dictionary. They
                    // are never a separate library source or an import candidate.
                    if (definition.IsAnonymous || !FrameNodeLibraryCad.HasMarker(tr, definition)) continue;
                    FrameNodeLibraryCad.ValidateDefinition(tr, definition);
                    if (!found.IsNull) FrameNodeLibraryContract.Fail("Для первого пилота DWG должен содержать ровно одно помеченное исходное определение узла.");
                    found = id;
                }
                if (found.IsNull) FrameNodeLibraryContract.Fail("В DWG не найден нативный узел пилота с маркером AF_NODE_LIBRARY.");
                var original = (BlockTableRecord)tr.GetObject(found, OpenMode.ForRead);
                var space = (BlockTableRecord)tr.GetObject(source.CurrentSpaceId, OpenMode.ForWrite);
                var probe = new BlockReference(Point3d.Origin, found);
                space.AppendEntity(probe); tr.AddNewlyCreatedDBObject(probe, true);
                FrameNodeLibraryCad.AddAttributes(tr, original, probe);
                transaction.Read(probe.ObjectId);
                // Dispose aborts this temporary reference; the library file is never saved.
                return found;
            }
        }
        private static PromptDoubleResult Positive(Editor ed, string message)
        {
            return ed.GetDouble(new PromptDoubleOptions(message) { AllowNegative = false, AllowZero = false, AllowNone = false });
        }
        internal static bool Millimeters(Editor ed, Database db, out UnitsValue units)
        {
            units = db.Insunits;
            if (units == UnitsValue.Millimeters) return true;
            if (units != UnitsValue.Undefined)
                FrameNodeLibraryContract.Fail("Команда узлов работает с размерами в миллиметрах. Единицы этого DWG отличаются; автоматического пересчёта нет.");
            var options = new PromptKeywordOptions("\nВ DWG не заданы единицы. Принять 1 единицу чертежа за 1 мм для узлов [Да/Нет]: ");
            options.Keywords.Add("Yes", "Да", "Да"); options.Keywords.Add("No", "Нет", "Нет"); options.AllowNone = false;
            var result = ed.GetKeywords(options);
            return result.Status == PromptStatus.OK && (result.StringResult == "Yes" || result.StringResult == "Да");
        }
        internal static void CheckUnits(Database db, UnitsValue expected)
        {
            if (db.Insunits != expected) FrameNodeLibraryContract.Fail("Единицы DWG изменились во время команды. Повторите команду.");
        }
        private static void CheckEnvironment(Editor ed, Database db, UnitsValue units, Matrix3d ucs)
        {
            CheckUnits(db, units);
            var before = ucs.ToArray(); var after = ed.CurrentUserCoordinateSystem.ToArray();
            for (int i = 0; i < before.Length; i++)
                if (before[i] != after[i]) FrameNodeLibraryContract.Fail("ПСК изменилась во время команды. Повторите выбор точки.");
        }
        private static void Run(string command, Action<Document, Editor, Database> action)
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try { action(doc, doc.Editor, doc.Database); }
            catch (System.Exception exception)
            {
                doc.Editor.WriteMessage("\n" + command + ": ошибка команды. " + exception.Message +
                    "\nПодробности (F2):\n" + exception);
            }
        }
    }
}
