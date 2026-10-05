using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AFramePlugin
{
    internal sealed class FrameNodeLibraryCad : IFrameNodeLibraryTransaction
    {
        internal readonly Transaction Transaction;
        internal FrameNodeLibraryCad(Database database) { Transaction = database.TransactionManager.StartTransaction(); }
        public void Dispose() { Transaction.Dispose(); }
        public void Commit() { Transaction.Commit(); }

        internal static bool HasMarker(Transaction tr, BlockTableRecord definition)
        {
            if (definition.ExtensionDictionary.IsNull) return false;
            var dictionary = tr.GetObject(definition.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            return dictionary != null && dictionary.Contains(FrameNodeLibraryContract.Key);
        }
        internal static void ValidateDefinition(Transaction tr, BlockTableRecord definition)
        {
            if (definition == null || definition.IsErased || !definition.IsDynamicBlock || definition.IsAnonymous ||
                definition.IsLayout || definition.IsFromExternalReference || definition.IsDependent || !HasMarker(tr, definition))
                FrameNodeLibraryContract.Fail("Нужно исходное динамическое определение библиотечного узла с маркером AF_NODE_LIBRARY.");
            var dictionary = (DBDictionary)tr.GetObject(definition.ExtensionDictionary, OpenMode.ForRead);
            var record = tr.GetObject(dictionary.GetAt(FrameNodeLibraryContract.Key), OpenMode.ForRead) as Xrecord;
            using (var data = record == null ? null : record.Data)
            {
                var entries = data == null ? null : data.AsArray();
                object[] expected = { FrameNodeLibraryContract.NodeId, FrameNodeLibraryContract.Version,
                    FrameNodeLibraryContract.Insulation, FrameNodeLibraryContract.Cladding, FrameNodeLibraryContract.Variant,
                    FrameNodeLibraryContract.VariantA, FrameNodeLibraryContract.VariantB };
                if (entries == null || entries.Length != expected.Length)
                    FrameNodeLibraryContract.Fail("Повреждён или несовместим маркер библиотечного определения.");
                for (int i = 0; i < expected.Length; i++)
                    if (entries[i].TypeCode != (i == 1 ? 90 : 1) || !expected[i].Equals(entries[i].Value))
                        FrameNodeLibraryContract.Fail("Несовместимая версия или привязка свойств библиотечного определения.");
            }
        }
        internal BlockReference Reference(object id, OpenMode mode = OpenMode.ForRead)
        {
            var block = Transaction.GetObject((ObjectId)id, mode) as BlockReference;
            if (block == null || block.IsErased || !block.IsDynamicBlock || block.DynamicBlockTableRecord.IsNull)
                FrameNodeLibraryContract.Fail("Выбор содержит объект, который не является динамическим библиотечным узлом.");
            // Anonymous evaluation BTR and the visible block name are never
            // used as compatibility evidence, even after COPY or visibility changes.
            var definition = Transaction.GetObject(block.DynamicBlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            ValidateDefinition(Transaction, definition);
            ValidatePlacement(block);
            return block;
        }
        internal static void ValidatePlacement(BlockReference block)
        {
            var scale = block.ScaleFactors; var normal = block.Normal; var p = block.Position;
            if (!FrameNodeLibraryContract.Finite(p.X) || !FrameNodeLibraryContract.Finite(p.Y) || !FrameNodeLibraryContract.Finite(p.Z) ||
                !FrameNodeLibraryContract.Finite(block.Rotation) || !FrameNodeLibraryContract.Finite(scale.X) ||
                scale.X <= 0 || scale.Y <= 0 || scale.Z <= 0 ||
                !SameScale(scale.X, scale.Y) || !SameScale(scale.X, scale.Z) ||
                !FrameNodeLibraryContract.Equal(normal.X, 0) || !FrameNodeLibraryContract.Equal(normal.Y, 0) ||
                !FrameNodeLibraryContract.Equal(normal.Z, 1))
                FrameNodeLibraryContract.Fail("Поддерживаются перенос, поворот в XY и положительный равномерный масштаб. Зеркало, наклон и неравномерный масштаб не поддерживаются.");
        }
        private static bool SameScale(double a, double b)
        {
            return FrameNodeLibraryContract.Finite(a) && FrameNodeLibraryContract.Finite(b) &&
                Math.Abs(a - b) <= Math.Max(Math.Abs(a), Math.Abs(b)) * 1e-9;
        }
        private static Dictionary<string, DynamicBlockReferenceProperty> Properties(BlockReference block)
        {
            var result = new Dictionary<string, DynamicBlockReferenceProperty>(StringComparer.Ordinal);
            foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
            {
                string name = property.PropertyName;
                if (name != FrameNodeLibraryContract.Insulation && name != FrameNodeLibraryContract.Cladding &&
                    name != FrameNodeLibraryContract.Variant) continue;
                if (result.ContainsKey(name)) FrameNodeLibraryContract.Fail("Повторяется обязательное динамическое свойство: " + name);
                result.Add(name, property);
            }
            if (result.Count != 3) FrameNodeLibraryContract.Fail("В определении отсутствуют обязательные динамические свойства AFN_INSULATION / AFN_CLADDING_X / AFN_VARIANT.");
            foreach (var entry in result)
            {
                bool number = entry.Key != FrameNodeLibraryContract.Variant;
                if (entry.Value.ReadOnly || (number ? !(entry.Value.Value is double) : !(entry.Value.Value is string)) ||
                    (number && entry.Value.UnitsType != DynamicBlockReferencePropertyUnitsType.Distance))
                    FrameNodeLibraryContract.Fail("Динамическое свойство имеет неверный тип или недоступно для записи: " + entry.Key);
            }
            var variants = result[FrameNodeLibraryContract.Variant].GetAllowedValues();
            if (variants == null || variants.Length != 2 ||
                !Contains(variants, FrameNodeLibraryContract.VariantA) || !Contains(variants, FrameNodeLibraryContract.VariantB))
                FrameNodeLibraryContract.Fail("Штатное свойство видимости не содержит два точных варианта пилота.");
            return result;
        }
        private static bool Contains(object[] allowed, object value)
        {
            foreach (var item in allowed)
                if (value is double && item is double ? FrameNodeLibraryContract.Equal((double)value, (double)item) : value.Equals(item)) return true;
            return false;
        }
        private static void Allowed(DynamicBlockReferenceProperty property, object value)
        {
            var allowed = property.GetAllowedValues();
            if (allowed != null && allowed.Length != 0 && !Contains(allowed, value))
                FrameNodeLibraryContract.Fail("Значение не входит в набор нативного свойства " + property.PropertyName + ".");
        }
        public FrameNodeLibrarySnapshot Read(object id)
        {
            var block = Reference(id); var properties = Properties(block);
            var values = new FrameNodeLibraryValues((double)properties[FrameNodeLibraryContract.Insulation].Value,
                (double)properties[FrameNodeLibraryContract.Cladding].Value, (string)properties[FrameNodeLibraryContract.Variant].Value);
            values.Validate();
            var placement = new StringBuilder();
            foreach (double n in block.BlockTransform.ToArray()) Number(placement, n);
            var attributes = new StringBuilder();
            foreach (ObjectId attributeId in block.AttributeCollection)
            {
                var attribute = Transaction.GetObject(attributeId, OpenMode.ForRead) as AttributeReference;
                if (attribute == null) FrameNodeLibraryContract.Fail("Повреждён атрибут вставки.");
                Text(attributes, attributeId.Handle.ToString()); Text(attributes, attribute.Tag); Text(attributes, attribute.TextString);
                Number(attributes, attribute.Position.X); Number(attributes, attribute.Position.Y); Number(attributes, attribute.Position.Z);
                Number(attributes, attribute.AlignmentPoint.X); Number(attributes, attribute.AlignmentPoint.Y); Number(attributes, attribute.AlignmentPoint.Z);
                Number(attributes, attribute.Rotation); Number(attributes, attribute.Height); Number(attributes, attribute.WidthFactor);
                Number(attributes, attribute.Normal.X); Number(attributes, attribute.Normal.Y); Number(attributes, attribute.Normal.Z);
                Number(attributes, attribute.Oblique); Text(attributes, attribute.IsMirroredInX.ToString()); Text(attributes, attribute.IsMirroredInY.ToString());
                Text(attributes, attribute.HorizontalMode.ToString()); Text(attributes, attribute.VerticalMode.ToString()); Text(attributes, attribute.TextStyleId.Handle.ToString());
                Text(attributes, attribute.Invisible.ToString()); Text(attributes, attribute.IsMTextAttribute.ToString());
                if (attribute.IsMTextAttribute) using (var mtext = attribute.MTextAttribute) Text(attributes, mtext.Contents);
            }
            return new FrameNodeLibrarySnapshot { Definition = block.DynamicBlockTableRecord.Handle.ToString(),
                Placement = placement.ToString(), Attributes = attributes.ToString(), Values = values };
        }
        private static void Number(StringBuilder text, double value) { Text(text, value.ToString("R", CultureInfo.InvariantCulture)); }
        private static void Text(StringBuilder text, string value) { value = value ?? ""; text.Append(value.Length).Append(':').Append(value); }
        public void ValidateTarget(object id, FrameNodeLibraryValues values)
        {
            values.Validate(); var properties = Properties(Reference(id));
            Allowed(properties[FrameNodeLibraryContract.Insulation], values.Insulation);
            Allowed(properties[FrameNodeLibraryContract.Cladding], values.Cladding);
            Allowed(properties[FrameNodeLibraryContract.Variant], values.Variant);
        }
        public void Write(object id, FrameNodeLibraryValues values)
        {
            var block = Reference(id, OpenMode.ForWrite);
            // Visibility first, then both linear coordinates. Reacquire wrappers
            // between setters because visibility may rebuild the property collection.
            Properties(block)[FrameNodeLibraryContract.Variant].Value = values.Variant;
            Properties(block)[FrameNodeLibraryContract.Insulation].Value = values.Insulation;
            Properties(block)[FrameNodeLibraryContract.Cladding].Value = values.Cladding;
            block.RecordGraphicsModified(true);
        }
        internal void VerifyCopy(BlockReference source, BlockReference copy, Point3d destination)
        {
            var a = source.ScaleFactors; var b = copy.ScaleFactors;
            if (!FrameNodeLibraryContract.Equal(source.Rotation, copy.Rotation) ||
                !SameScale(a.X, b.X) || !SameScale(a.Y, b.Y) ||
                !SameScale(a.Z, b.Z) || !Same(copy.Position, destination) ||
                source.AttributeCollection.Count != copy.AttributeCollection.Count)
                FrameNodeLibraryContract.Fail("Нативная копия не сохранила преобразование или число атрибутов.");
            var originals = new List<ObjectId>(); var copies = new List<ObjectId>();
            foreach (ObjectId id in source.AttributeCollection) originals.Add(id);
            foreach (ObjectId id in copy.AttributeCollection) copies.Add(id);
            var displacement = Matrix3d.Displacement(destination - source.Position);
            for (int i = 0; i < originals.Count; i++)
            {
                var x = (AttributeReference)Transaction.GetObject(originals[i], OpenMode.ForRead);
                var y = (AttributeReference)Transaction.GetObject(copies[i], OpenMode.ForRead);
                if (originals[i] == copies[i] || x.Tag != y.Tag || x.TextString != y.TextString ||
                    x.Invisible != y.Invisible || x.IsMTextAttribute != y.IsMTextAttribute ||
                    !Same(x.Position.TransformBy(displacement), y.Position) ||
                    (!x.IsDefaultAlignment && !Same(x.AlignmentPoint.TransformBy(displacement), y.AlignmentPoint)) ||
                    !FrameNodeLibraryContract.Equal(x.Rotation, y.Rotation) || !FrameNodeLibraryContract.Equal(x.Height, y.Height) ||
                    !FrameNodeLibraryContract.Equal(x.WidthFactor, y.WidthFactor) || !FrameNodeLibraryContract.Equal(x.Oblique, y.Oblique) ||
                    !FrameNodeLibraryContract.Equal(x.Normal.X, y.Normal.X) || !FrameNodeLibraryContract.Equal(x.Normal.Y, y.Normal.Y) ||
                    !FrameNodeLibraryContract.Equal(x.Normal.Z, y.Normal.Z) || x.IsMirroredInX != y.IsMirroredInX || x.IsMirroredInY != y.IsMirroredInY ||
                    x.HorizontalMode != y.HorizontalMode || x.VerticalMode != y.VerticalMode || x.TextStyleId != y.TextStyleId)
                    FrameNodeLibraryContract.Fail("Нативная копия не сохранила значения или положение пользовательских атрибутов.");
                if (x.IsMTextAttribute)
                    using (var xm = x.MTextAttribute) using (var ym = y.MTextAttribute)
                        if (xm.Contents != ym.Contents) FrameNodeLibraryContract.Fail("Нативная копия изменила многострочный атрибут.");
            }
        }
        private static bool Same(Point3d a, Point3d b)
        {
            return FrameNodeLibraryContract.Equal(a.X, b.X) && FrameNodeLibraryContract.Equal(a.Y, b.Y) && FrameNodeLibraryContract.Equal(a.Z, b.Z);
        }
        internal static void AddAttributes(Transaction tr, BlockTableRecord definition, BlockReference block)
        {
            foreach (ObjectId id in definition)
            {
                var source = tr.GetObject(id, OpenMode.ForRead) as AttributeDefinition;
                if (source == null || source.Constant) continue;
                var attribute = new AttributeReference();
                attribute.SetAttributeFromBlock(source, block.BlockTransform);
                attribute.TextString = source.TextString;
                block.AttributeCollection.AppendAttribute(attribute); tr.AddNewlyCreatedDBObject(attribute, true);
            }
        }
    }
}
