using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(AFramePlugin.FrameNodeLibraryHostCheck))]

namespace AFramePlugin
{
    // This command needs a real AutoCAD evaluator and a real authored library.
    // Compiling it or testing the edit coordinator with doubles is not a host run.
    public sealed class FrameNodeLibraryHostCheck
    {
        // Exercise the production coordinator without committing its transaction.
        // All native mutations, including anonymous evaluation states, are aborted.
        private sealed class RollbackGroup : IFrameNodeLibraryTransaction
        {
            private readonly FrameNodeLibraryCad cad;
            internal RollbackGroup(FrameNodeLibraryCad value) { cad = value; }
            public FrameNodeLibrarySnapshot Read(object id) { return cad.Read(id); }
            public void ValidateTarget(object id, FrameNodeLibraryValues values) { cad.ValidateTarget(id, values); }
            public void Write(object id, FrameNodeLibraryValues values) { cad.Write(id, values); }
            public void Commit() { }
            public void Dispose() { }
        }

        private sealed class View
        {
            internal FrameNodeLibrarySnapshot State;
            internal readonly List<string> Geometry = new List<string>();
            internal readonly List<string> Text = new List<string>();
            internal readonly List<string> Marks = new List<string>();
            internal readonly List<string> Dimensions = new List<string>();
            internal readonly List<double> Measures = new List<double>();
            internal readonly List<double> CaptionMeasures = new List<double>();
            internal string GeometryKey, TextKey, MarksKey, DimensionKey;
            internal void Finish()
            {
                GeometryKey = Join(Geometry); TextKey = Join(Text);
                MarksKey = Join(Marks); DimensionKey = Join(Dimensions);
                if (Geometry.Count == 0 || Marks.Count == 0 || Dimensions.Count < 2)
                    FrameNodeLibraryContract.Fail("Проверка не выполнена: нужны геометрия, марки DBText/MText и два нативных линейных размера.");
                CheckMeasure(Measures, State.Values.Insulation); CheckMeasure(Measures, State.Values.Cladding);
                CheckMeasure(CaptionMeasures, State.Values.Insulation); CheckMeasure(CaptionMeasures, State.Values.Cladding);
            }
            private void CheckMeasure(List<double> measures, double expected)
            {
                foreach (double actual in measures) if (Math.Abs(actual - expected) <= 1e-6) return;
                FrameNodeLibraryContract.Fail("Нативные размеры не подтверждают значение " + N(expected) +
                    " мм. Чтение динамического свойства само по себе не доказывает работу узла.");
            }
            internal void Same(View after, string stage)
            {
                State.VerifyPreserved(after.State);
                if (!State.Values.Same(after.State.Values) || GeometryKey != after.GeometryKey ||
                    TextKey != after.TextKey || MarksKey != after.MarksKey || DimensionKey != after.DimensionKey)
                    FrameNodeLibraryContract.Fail("Нарушена неизменность узла: " + stage + ".");
            }
        }

        [CommandMethod("ATFNODETEST", CommandFlags.Modal)]
        public void Test()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var ed = doc.Editor; var db = doc.Database;
            try
            {
                UnitsValue units;
                if (!FrameNodeLibraryCommand.Millimeters(ed, db, out units)) return;
                ed.WriteMessage("\nНативная проверка 3 из 5: A→B→B→A→A. Изменения будут отменены; save/open и пользовательский UNDO проверяются отдельно.");
                var result = ed.GetSelection(new PromptSelectionOptions {
                    MessageForAdding = "\nВыберите ровно пять контрольных узлов с одинаковыми параметрами A: " });
                if (result.Status != PromptStatus.OK) return;
                var unique = new HashSet<ObjectId>(result.Value.GetObjectIds());
                if (unique.Count != 5) FrameNodeLibraryContract.Fail("Нужно ровно пять различных библиотечных вставок.");
                var ids = new List<ObjectId>(unique);
                // A frame selection has no useful pick order. Sort by handles
                // and report exactly which references will be changed.
                ids.Sort((a, b) => a.Handle.Value.CompareTo(b.Handle.Value));
                var insulation = ed.GetDouble(new PromptDoubleOptions("\nДругой размер утеплителя B, мм: ") {
                    AllowNone = false, AllowNegative = false, AllowZero = false });
                if (insulation.Status != PromptStatus.OK) return;
                var cladding = ed.GetDouble(new PromptDoubleOptions("\nДругое положение плоскости облицовки B, мм: ") {
                    AllowNone = false, AllowNegative = false, AllowZero = false });
                if (cladding.Status != PromptStatus.OK) return;
                View[] original;
                using (doc.LockDocument())
                {
                    FrameNodeLibraryCommand.CheckUnits(db, units);
                    using (var cad = new FrameNodeLibraryCad(db))
                    {
                        original = Capture(cad, ids);
                        var a = original[0].State.Values;
                        for (int i = 1; i < 5; i++)
                            if (!a.Same(original[i].State.Values) || original[i].State.Definition != original[0].State.Definition)
                                FrameNodeLibraryContract.Fail("Пять вставок должны использовать одно исходное определение и одинаковый набор A.");
                        var b = new FrameNodeLibraryValues(insulation.Value, cladding.Value,
                            a.Variant == FrameNodeLibraryContract.VariantA ? FrameNodeLibraryContract.VariantB : FrameNodeLibraryContract.VariantA);
                        b.Validate();
                        if (FrameNodeLibraryContract.Equal(a.Insulation, b.Insulation) || FrameNodeLibraryContract.Equal(a.Cladding, b.Cladding))
                            FrameNodeLibraryContract.Fail("Для проверки оба линейных значения B должны отличаться от A.");
                        var selected = new object[] { ids[0], ids[1], ids[2] };
                        ed.WriteMessage("\nИзменяем handles " + ids[0].Handle + ", " + ids[1].Handle + ", " + ids[2].Handle +
                            "; сохраняем " + ids[3].Handle + ", " + ids[4].Handle + ".");
                        var group = new RollbackGroup(cad);
                        if (FrameNodeLibraryEdit.Apply(group, selected, b) != 3)
                            FrameNodeLibraryContract.Fail("Первый переход должен изменить ровно три вставки.");
                        var changed = Capture(cad, ids);
                        for (int i = 0; i < 3; i++)
                        {
                            original[i].State.VerifyPreserved(changed[i].State);
                            if (original[i].GeometryKey == changed[i].GeometryKey || original[i].MarksKey == changed[i].MarksKey ||
                                original[i].DimensionKey == changed[i].DimensionKey)
                                FrameNodeLibraryContract.Fail("Вставка " + ids[i].Handle +
                                    ": свойства изменились, но изменение геометрии, марок и размеров нативным Explode не подтверждено. Проверка не пройдена.");
                        }
                        for (int i = 3; i < 5; i++) original[i].Same(changed[i], "невыбранный после B");
                        if (FrameNodeLibraryEdit.Apply(group, selected, b) != 0)
                            FrameNodeLibraryContract.Fail("Повтор B не должен повторно менять вставки.");
                        SameAll(changed, Capture(cad, ids), "повтор B");
                        if (FrameNodeLibraryEdit.Apply(group, selected, a) != 3)
                            FrameNodeLibraryContract.Fail("Обратный переход должен изменить ровно три вставки.");
                        SameAll(original, Capture(cad, ids), "возврат A");
                        if (FrameNodeLibraryEdit.Apply(group, selected, a) != 0)
                            FrameNodeLibraryContract.Fail("Повтор A не должен повторно менять вставки.");
                        SameAll(original, Capture(cad, ids), "повтор A");
                        // No Commit: disposing the only real transaction aborts.
                    }
                    using (var verify = new FrameNodeLibraryCad(db))
                        SameAll(original, Capture(verify, ids), "после rollback");
                }
                ed.Regen();
                ed.WriteMessage("\nATFNODETEST PASS: реальные геометрия, марки и размеры трёх вставок изменились и вернулись; две остальные, базы и атрибуты сохранены. Повторы без сдвига; rollback проверен. COPY, UNDO и save/open этим тестом не подтверждаются.");
            }
            catch (System.Exception exception)
            {
                ed.WriteMessage("\nATFNODETEST не пройден: " + exception.Message +
                    "\nПроверка не фиксирует изменения. F2:\n" + exception);
            }
        }

        private static View[] Capture(FrameNodeLibraryCad cad, List<ObjectId> ids)
        {
            var result = new View[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                var block = cad.Reference(ids[i]);
                var view = new View { State = cad.Read(ids[i]) };
                CaptureCaptions(cad, block, view);
                var objects = new DBObjectCollection();
                try
                {
                    block.Explode(objects);
                    var inverse = block.BlockTransform.Inverse();
                    foreach (DBObject obj in objects)
                    {
                        var entity = obj as Entity;
                        if (entity == null) FrameNodeLibraryContract.Fail("Проверка не выполнена: Explode вернул не Entity.");
                        Describe(view, entity, inverse, block.ScaleFactors.X);
                    }
                }
                finally { foreach (DBObject obj in objects) obj.Dispose(); objects.Dispose(); }
                view.Finish(); result[i] = view;
            }
            return result;
        }
        private static void SameAll(View[] before, View[] after, string stage)
        { for (int i = 0; i < before.Length; i++) before[i].Same(after[i], stage); }

        private static void CaptureCaptions(FrameNodeLibraryCad cad, BlockReference block, View view)
        {
            // This is the evaluated body, used only to inspect displayed
            // dimension settings in local coordinates. Compatibility still
            // comes exclusively from the original DynamicBlockTableRecord.
            // Explode may transform dimension overrides along with a scaled
            // insert; those transient overrides are not the source caption.
            var body = (BlockTableRecord)cad.Transaction.GetObject(block.BlockTableRecord, OpenMode.ForRead);
            foreach (ObjectId id in body)
            {
                var dimension = cad.Transaction.GetObject(id, OpenMode.ForRead) as Dimension;
                if (dimension == null || !dimension.Visible) continue;
                if (!(dimension is AlignedDimension) && !(dimension is RotatedDimension))
                    FrameNodeLibraryContract.Fail("Проверка не выполнена: неподдержанный размер вычисленного тела узла.");
                double measured = dimension.Measurement, displayed = measured * dimension.Dimlfac;
                if (!FrameNodeLibraryContract.Finite(measured) || !FrameNodeLibraryContract.Finite(displayed) ||
                    dimension.Dimlfac <= 0 || Math.Abs(displayed - measured) > 1e-6)
                    FrameNodeLibraryContract.Fail("Проверка не выполнена: DIMLFAC меняет подпись локального размера узла в мм.");
                if ((!String.IsNullOrEmpty(dimension.DimensionText) && dimension.DimensionText != "<>") ||
                    dimension.Dimrnd != 0 || dimension.Dimlunit != 2 || dimension.Dimtol || dimension.Dimlim ||
                    (!String.IsNullOrEmpty(dimension.Dimpost) && dimension.Dimpost != "<>"))
                    FrameNodeLibraryContract.Fail("Проверка не выполнена: текст, округление, единицы, допуск или префикс размера отличаются от десятичных миллиметров пилота.");
                if (dimension.Dimdec < 0 || dimension.Dimdec > 8 || Math.Abs(Math.Round(displayed, dimension.Dimdec) - measured) > 1e-6)
                    FrameNodeLibraryContract.Fail("Проверка не выполнена: точность подписи скрывает введённую дробную часть размера.");
                view.CaptionMeasures.Add(measured);
                view.Dimensions.Add("LOCAL_CAPTION|" + N(measured) + "|" + N(dimension.Dimlfac) + "|" +
                    dimension.DimensionText + "|" + dimension.Dimpost + "|" + dimension.Dimdec);
            }
        }

        // Only the small set generated by native_author.lsp is supported.
        // Unknown types are a visible incomplete check, never a silent PASS.
        private static void Describe(View view, Entity entity, Matrix3d inverse, double scale)
        {
            if (!entity.Visible)
            {
                // Hidden primitives cannot prove movement of the displayed
                // node. Attached attributes are still checked by cad.Read.
                if (entity is Line || entity is Polyline || entity is Circle || entity is DBText ||
                    entity is MText || entity is RotatedDimension || entity is AlignedDimension) return;
                FrameNodeLibraryContract.Fail("Проверка не выполнена: неподдержанный скрытый результат Explode " + entity.GetType().Name + ".");
            }
            string appearance = entity.Layer + "|" + entity.ColorIndex + "|" + entity.Visible + "|" + entity.Linetype;
            var line = entity as Line;
            if (line != null)
            { view.Geometry.Add("L|" + P(line.StartPoint, inverse) + P(line.EndPoint, inverse) + appearance); return; }
            var polyline = entity as Polyline;
            if (polyline != null)
            {
                var text = new StringBuilder("P|" + polyline.Closed + "|");
                for (int i = 0; i < polyline.NumberOfVertices; i++) text.Append(P(polyline.GetPoint3dAt(i), inverse))
                    .Append(N(polyline.GetBulgeAt(i))).Append('|').Append(N(polyline.GetStartWidthAt(i) / scale))
                    .Append('|').Append(N(polyline.GetEndWidthAt(i) / scale)).Append('|');
                view.Geometry.Add(text + appearance); return;
            }
            var circle = entity as Circle;
            if (circle != null)
            { view.Geometry.Add("C|" + P(circle.Center, inverse) + N(circle.Radius / scale) + "|" + appearance); return; }
            var attribute = entity as AttributeDefinition;
            if (attribute != null)
            {
                // Explode returns definitions, not per-reference attribute values.
                // Those values and positions are checked by the production Read.
                view.Text.Add("ATTDEF|" + attribute.Tag + "|" + attribute.TextString + "|" +
                    P(attribute.Position, inverse) + N(attribute.Height / scale) + "|" + appearance); return;
            }
            var textEntity = entity as DBText;
            if (textEntity != null)
            {
                // Some host versions may return invisible entities from an
                // evaluated definition. Only visible labels prove a mark change.
                if (textEntity.Visible) view.Marks.Add(textEntity.TextString);
                view.Text.Add("T|" + textEntity.TextString + "|" + P(textEntity.Position, inverse) +
                    P(textEntity.AlignmentPoint, inverse) + N(textEntity.Height / scale) + "|" +
                    N(textEntity.Rotation) + "|" + N(textEntity.WidthFactor) + "|" + appearance); return;
            }
            var mtext = entity as MText;
            if (mtext != null)
            {
                if (mtext.Visible) view.Marks.Add(mtext.Contents);
                view.Text.Add("MT|" + mtext.Contents + "|" + P(mtext.Location, inverse) +
                    N(mtext.TextHeight / scale) + "|" + N(mtext.Width / scale) + "|" + N(mtext.Rotation) + "|" + appearance); return;
            }
            var dimension = entity as Dimension;
            if (dimension != null)
            {
                Point3d p1, p2, dimLine;
                var rotated = dimension as RotatedDimension;
                var aligned = dimension as AlignedDimension;
                double geometric;
                if (rotated != null)
                {
                    p1 = rotated.XLine1Point; p2 = rotated.XLine2Point; dimLine = rotated.DimLinePoint;
                    geometric = Math.Abs((p2.X - p1.X) * Math.Cos(rotated.Rotation) +
                        (p2.Y - p1.Y) * Math.Sin(rotated.Rotation)) / scale;
                }
                else if (aligned != null)
                { p1 = aligned.XLine1Point; p2 = aligned.XLine2Point; dimLine = aligned.DimLinePoint; geometric = p1.DistanceTo(p2) / scale; }
                else
                {
                    FrameNodeLibraryContract.Fail("Проверка не выполнена: неподдержанный тип размера " + dimension.GetType().Name);
                    return;
                }
                // RecomputeDimensionBlock is deliberately not called: it can
                // write a dimension's referenced BTR. Inspect the evaluated
                // native measurement and its extension points independently.
                double measured = dimension.Measurement / scale;
                if (!FrameNodeLibraryContract.Finite(measured) || Math.Abs(measured - geometric) > 1e-6)
                    FrameNodeLibraryContract.Fail("Нативное значение размера не совпадает с геометрией выносных точек; вычисление размера не подтверждено.");
                view.Measures.Add(measured);
                view.Dimensions.Add(dimension.GetType().Name + "|" + N(measured) + "|" + P(p1, inverse) + P(p2, inverse) +
                    P(dimLine, inverse) + P(dimension.TextPosition, inverse) + dimension.DimensionText + "|" +
                    N(dimension.Dimlfac) + "|" + dimension.Dimpost + "|" + dimension.Dimdec + "|" + appearance); return;
            }
            FrameNodeLibraryContract.Fail("Проверка не выполнена: неподдержанный результат Explode " + entity.GetType().Name + ".");
        }

        private static string N(double value)
        {
            if (!FrameNodeLibraryContract.Finite(value)) FrameNodeLibraryContract.Fail("Неконечная координата в нативной геометрии.");
            return Math.Round(value, 6).ToString("0.######", CultureInfo.InvariantCulture);
        }
        private static string P(Point3d point, Matrix3d inverse)
        { point = point.TransformBy(inverse); return N(point.X) + "," + N(point.Y) + "," + N(point.Z) + "|"; }
        private static string Join(List<string> values)
        {
            values.Sort(StringComparer.Ordinal); var text = new StringBuilder();
            foreach (string value in values) text.Append(value.Length).Append(':').Append(value);
            return text.ToString();
        }
    }
}
