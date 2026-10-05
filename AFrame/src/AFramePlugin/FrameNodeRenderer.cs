using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AFramePlugin
{
    internal static class FrameNodeRenderer
    {
        internal const string LayerName = "_01_УЗЛЫ_СХЕМЫ", BlockPrefix = "AFNODE_SCHEMA_", Revision = "aframe_node_renderer/2", LegacyRevision = "aframe_node_renderer/1";
        internal const int MaxPrimitives = FrameNodeDrawingBuilder.MaxPrimitives;
        internal static BlockReference Create(Transaction tr, Database db, FrameNodeDrawing drawing, Point3d positionWcs)
        {
            if (drawing == null || drawing.Primitives.Count == 0 || drawing.Primitives.Count > MaxPrimitives)
                Fail("E_NODE_CAPACITY", "Недопустимое число примитивов размерной схемы.");
            Point(positionWcs);
            var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            ObjectId layerId;
            if (layers.Has(LayerName))
            {
                layerId = layers[LayerName]; var existing = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForRead);
                if (existing.IsLocked || existing.IsOff || existing.IsFrozen) Fail("E_NODE_LAYER", "Слой схем выключен, заморожен или заблокирован. Включите его явно.");
            }
            else
            {
                layers.UpgradeOpen(); var layer = new LayerTableRecord { Name = LayerName, Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7) };
                layerId = layers.Add(layer); tr.AddNewlyCreatedDBObject(layer, true);
            }
            // A dedicated style prevents the user's current vertical, fixed
            // height or expanded style from changing this dimensioned layout.
            var textStyles = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead); textStyles.UpgradeOpen();
            var nodeStyle = new TextStyleTableRecord { Name = "AFNODE_TEXT_" + Guid.NewGuid().ToString("N"), TextSize = 0,
                XScale = 1, ObliquingAngle = 0, IsVertical = false,
                Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Arial", false, false, 0, 0) };
            ObjectId nodeStyleId = textStyles.Add(nodeStyle); tr.AddNewlyCreatedDBObject(nodeStyle, true);
            var blocks = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead); blocks.UpgradeOpen();
            var definition = new BlockTableRecord { Name = BlockPrefix + Guid.NewGuid().ToString("N"), Origin = Point3d.Origin, Units = UnitsValue.Millimeters };
            ObjectId definitionId = blocks.Add(definition); tr.AddNewlyCreatedDBObject(definition, true);
            foreach (var primitive in drawing.Primitives)
            {
                Entity entity;
                if (primitive.Kind == "line") entity = new Line(new Point3d(primitive.X1, primitive.Y1, 0), new Point3d(primitive.X2, primitive.Y2, 0));
                else if (primitive.Kind == "text") entity = new MText { Location = new Point3d(primitive.X1, primitive.Y1, 0),
                    Contents = Escape(primitive.Text), TextHeight = primitive.TextHeight, Width = primitive.TextWidth,
                    Rotation = 0, Normal = Vector3d.ZAxis, Attachment = AttachmentPoint.TopLeft, TextStyleId = nodeStyleId };
                else { Fail("E_NODE_PRIMITIVE", "Неизвестный тип примитива схемы."); return null; }
                entity.Layer = "0"; entity.ColorIndex = primitive.Role == "insulation" ? 3 : primitive.Role == "profile" ? 1 : primitive.Role == "cladding" ? 4 : 7;
                entity.Linetype = "Continuous"; entity.LineWeight = LineWeight.LineWeight025; entity.Visible = true;
                definition.AppendEntity(entity); tr.AddNewlyCreatedDBObject(entity, true);
            }
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            var reference = new BlockReference(positionWcs, definitionId) { LayerId = layerId, ScaleFactors = new Scale3d(1),
                Normal = Vector3d.ZAxis, Rotation = 0, ColorIndex = 7, Linetype = "Continuous", LineWeight = LineWeight.LineWeight025 };
            space.AppendEntity(reference); tr.AddNewlyCreatedDBObject(reference, true); return reference;
        }
        private static string Escape(string text)
        { return (text ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}").Replace("\r", "").Replace("\n", "\\P"); }
        internal static string CadContentDigest(Transaction tr, BlockReference owner, string rendererRevision = Revision)
        {
            ValidateTransform(owner);
            CheckExtensions(tr, owner, true);
            if (owner.IsDynamicBlock || owner.AttributeCollection.Count != 0)
                Fail("E_NODE_BODY_CHANGED", "Динамический блок или добавленные атрибуты не входят в размерную схему.");
            var definition = tr.GetObject(owner.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (definition == null || definition.IsErased || !definition.Name.StartsWith(BlockPrefix, StringComparison.Ordinal) ||
                definition.IsFromExternalReference || definition.Units != UnitsValue.Millimeters || definition.Origin != Point3d.Origin)
                Fail("E_NODE_DEFINITION", "Определение размерной схемы или его единицы изменены.");
            CheckExtensions(tr, definition, false);
            var body = new List<object>(); var styles = new Dictionary<ObjectId, object>();
            foreach (ObjectId id in definition)
            {
                if (body.Count >= MaxPrimitives) Fail("E_NODE_CAPACITY", "Определение превышает техническую ёмкость 4096 примитивов.");
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null || entity.IsErased) Fail("E_NODE_BODY_CHANGED", "Примитив схемы отсутствует.");
                CheckExtensions(tr, entity, false);
                var record = Style(entity, rendererRevision); record["handle"] = entity.Handle.ToString();
                var line = entity as Line; var text = entity as MText;
                if (line != null)
                {
                    record["type"] = "Line"; record["start"] = Point(line.StartPoint); record["end"] = Point(line.EndPoint);
                    record["normal"] = Vector(line.Normal); record["thickness"] = Finite(line.Thickness);
                }
                else if (text != null)
                {
                    record["type"] = "MText"; record["location"] = Point(text.Location); record["contents"] = text.Contents;
                    record["height"] = Finite(text.TextHeight); record["width"] = Finite(text.Width); record["rotation"] = Finite(text.Rotation);
                    record["normal"] = Vector(text.Normal); record["attachment"] = (int)text.Attachment;
                    record["spacing"] = Finite(text.LineSpacingFactor); record["spacing_style"] = (int)text.LineSpacingStyle;
                    record["background"] = text.BackgroundFill;
                    // Optional native getters fail when this MText has no
                    // background/column data. Doubles used to return values
                    // unconditionally and hid that failure on freshly drawn text.
                    if (text.BackgroundFill || rendererRevision == LegacyRevision)
                    {
                        var background = text.BackgroundFillColor;
                        record["background_color"] = background.ColorValue.ToArgb();
                        record["background_method"] = background.ColorMethod.ToString(); record["background_scale"] = Finite(text.BackgroundScaleFactor);
                    }
                    record["background_use_drawing"] = text.UseBackgroundColor; record["borders"] = text.ShowBorders;
                    int columns = (int)text.ColumnType; record["columns"] = columns;
                    if (columns != 0 || rendererRevision == LegacyRevision) // 0 = NoColumns
                    {
                        record["column_count"] = text.ColumnCount;
                        record["column_width"] = Finite(text.ColumnWidth); record["column_gutter"] = Finite(text.ColumnGutterWidth);
                    }
                    record["style"] = text.TextStyleId.Handle.ToString();
                    object style;
                    if (!styles.TryGetValue(text.TextStyleId, out style))
                    {
                        var ts = (TextStyleTableRecord)tr.GetObject(text.TextStyleId, OpenMode.ForRead);
                        style = new Dictionary<string, object> { { "name", ts.Name }, { "file", ts.FileName }, { "big_font", ts.BigFontFileName },
                            { "size", Finite(ts.TextSize) }, { "width", Finite(ts.XScale) }, { "oblique", Finite(ts.ObliquingAngle) },
                            { "vertical", ts.IsVertical }, { "typeface", ts.Font.TypeFace }, { "bold", ts.Font.Bold }, { "italic", ts.Font.Italic },
                            { "charset", ts.Font.CharacterSet }, { "pitch", ts.Font.PitchAndFamily } };
                        styles.Add(text.TextStyleId, style);
                    }
                    record["text_style"] = style;
                }
                else Fail("E_NODE_BODY_CHANGED", "В схему добавлен чужой тип объекта; проверка прекращена.");
                body.Add(record);
            }
            if (body.Count == 0) Fail("E_NODE_BODY_CHANGED", "Определение схемы пусто.");
            var layer = (LayerTableRecord)tr.GetObject(owner.LayerId, OpenMode.ForRead);
            return FrameParameterJson.Hash(new Dictionary<string, object> { { "renderer", rendererRevision }, { "definition", definition.Handle.ToString() },
                { "name", definition.Name }, { "units", (int)definition.Units }, { "origin", Point(definition.Origin) },
                { "reference_style", Style(owner, rendererRevision) }, { "layer_state", new object[] { layer.Name, layer.IsOff, layer.IsFrozen, layer.IsLocked,
                    layer.Color.ColorMethod.ToString(), layer.Color.ColorValue.ToArgb(), (int)layer.LineWeight, layer.LinetypeObjectId.Handle.ToString(), Alpha(layer.Transparency, rendererRevision), layer.Transparency.IsByLayer, layer.Transparency.IsByBlock } }, { "body", body } });
        }
        private static void CheckExtensions(Transaction tr, DBObject owner, bool nodeOwner)
        {
            if (owner.ExtensionDictionary.IsNull) return;
            var ext = tr.GetObject(owner.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (ext == null) Fail("E_NODE_BODY_CHANGED", "Словарь оформления схемы повреждён.");
            foreach (DBDictionaryEntry entry in ext)
                if (!nodeOwner || entry.Key != FrameNodeStore.Key)
                    Fail("E_NODE_BODY_CHANGED", "Добавлены неподдержанные данные оформления или отсечения схемы. Создайте новый экземпляр.");
        }
        internal static void ValidateTransform(BlockReference owner)
        {
            if (owner == null || owner.IsErased) Fail("E_NODE_NOT_OWNED", "Выберите существующий блок размерной схемы.");
            Point(owner.Position); Finite(owner.Rotation);
            if (owner.ScaleFactors.X != 1 || owner.ScaleFactors.Y != 1 || owner.ScaleFactors.Z != 1 || owner.Normal != Vector3d.ZAxis)
                Fail("E_NODE_TRANSFORM", "Масштаб, зеркало или наклон схемы изменены. Разрешены перенос и поворот в плоскости XY.");
            var axes = owner.BlockTransform.CoordinateSystem3d;
            // This tolerance validates floating matrix orthonormality only. It
            // never participates in the exact source clearance calculation.
            if (Math.Abs(axes.Xaxis.Length - 1) > 1e-12 || Math.Abs(axes.Yaxis.Length - 1) > 1e-12 ||
                Math.Abs(axes.Xaxis.DotProduct(axes.Yaxis)) > 1e-12 ||
                Math.Abs(axes.Xaxis.Z) > 1e-12 || Math.Abs(axes.Yaxis.Z) > 1e-12 ||
                axes.Xaxis.CrossProduct(axes.Yaxis).DotProduct(Vector3d.ZAxis) < 1 - 1e-12)
                Fail("E_NODE_TRANSFORM", "Преобразование схемы не является переносом и поворотом XY без масштаба.");
        }
        private static Dictionary<string, object> Style(Entity e, string rendererRevision)
        { return new Dictionary<string, object> { { "layer", e.Layer }, { "color_method", e.Color.ColorMethod.ToString() },
            { "color", e.Color.ColorValue.ToArgb() }, { "color_index", e.ColorIndex }, { "linetype", e.Linetype },
            { "linetype_scale", Finite(e.LinetypeScale) }, { "lineweight", (int)e.LineWeight }, { "visible", e.Visible },
            { "transparency", Alpha(e.Transparency, rendererRevision) }, { "transparency_by_layer", e.Transparency.IsByLayer }, { "transparency_by_block", e.Transparency.IsByBlock } }; }
        private static object Alpha(Autodesk.AutoCAD.Colors.Transparency value, string rendererRevision)
        {
            // AutoCAD throws eInvalidKey for Alpha on inherited transparency.
            // The two method flags remain in the digest, so ByLayer/ByBlock
            // cannot be silently changed into an explicit opacity or each other.
            return value.IsByAlpha || rendererRevision == LegacyRevision ? (object)value.Alpha : null;
        }
        private static double Finite(double value) { if (double.IsNaN(value) || double.IsInfinity(value)) Fail("E_NODE_BODY_CHANGED", "Неконечная координата или параметр схемы."); return value; }
        private static double[] Point(Point3d p) { return new[] { Finite(p.X), Finite(p.Y), Finite(p.Z) }; }
        private static double[] Vector(Vector3d v) { return new[] { Finite(v.X), Finite(v.Y), Finite(v.Z) }; }
        private static void Fail(string code, string message) { throw new FrameNodeGeometryException(code, message); }
    }
}
