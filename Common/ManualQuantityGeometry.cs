using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FacadeSafety
{
    // Read-only, operation-scoped extraction. Evaluated block geometry is read
    // once per definition; no dynamic property is set and no block is evaluated.
    internal static class ManualQuantityGeometry
    {
        internal sealed class Cache
        {
            internal Transaction Transaction;
            internal readonly Dictionary<ObjectId, Definition> Definitions = new Dictionary<ObjectId, Definition>();
            internal readonly Dictionary<ObjectId, string> Names = new Dictionary<ObjectId, string>();
            internal readonly Dictionary<ObjectId, string> Colors = new Dictionary<ObjectId, string>();
            internal int Operations;
            internal void Check(Transaction tr, Action cancel)
            {
                if (Transaction == null) Transaction = tr;
                if (!ReferenceEquals(Transaction, tr))
                    throw new InvalidOperationException("Кеш ручных объектов нельзя переносить в другую операцию чтения.");
                if ((Operations++ & 127) == 0 && cancel != null) cancel();
            }
        }

        internal sealed class Definition
        {
            internal Point3d[] Boundary;
            internal Point3d[] Line;
            internal string BoundaryReason, LinearReason, SymbolReason, ExtraFingerprint;
            internal readonly Dictionary<string, string> Constants = new Dictionary<string, string>(StringComparer.Ordinal);
            internal string AttributesReason;
        }

        internal static ManualQuantityObservation Extract(Transaction tr, Entity entity, Cache cache, Action cancel = null)
        {
            if (tr == null || cache == null) throw new ArgumentNullException(tr == null ? "tr" : "cache");
            cache.Check(tr, cancel);
            var observation = new ManualQuantityObservation();
            if (entity == null || entity.ObjectId.IsNull || entity.IsErased)
            { observation.extraction_reason = "Ручной объект удалён или отсутствует."; return observation; }
            observation.handle = entity.Handle.ToString();
            observation.entity_type = entity.GetType().Name;
            observation.layer = entity.Layer;
            observation.sample_key = "entity:" + entity.GetType().FullName;
            observation.is_block = entity is BlockReference;
            try
            {
                if (!entity.Visible) throw new InvalidOperationException("Невидимый объект не учитывается как видимая физическая деталь.");
                observation.color = Color(tr, entity, cache);
                string extra = ExtractGeometry(tr, entity, observation, cache, cancel, true);
                observation.cad_fingerprint = FacadeQuantityStore.CaptureEntity(tr, entity, "manual").fingerprint;
                var signature = new StringBuilder("manual_geometry/1;");
                Token(signature, observation.cad_fingerprint); Token(signature, extra);
                Token(signature, observation.sample_key); Token(signature, observation.effective_block_name);
                AddGeometry(signature, observation);
                foreach (var key in Sorted(observation.attributes.Keys))
                { Token(signature, key); Token(signature, observation.attributes[key]); }
                foreach (var key in Sorted(observation.dynamic_units.Keys))
                { Token(signature, key); Token(signature, observation.dynamic_units[key]); }
                observation.fingerprint = Hash(signature.ToString());
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            { observation.extraction_reason = "Не удалось подтвердить ручной объект: " + ex.Message; }
            return observation;
        }

        // The generated ledger has already validated this entity. Only the
        // geometric descriptor needed for a duplicate warning is read here;
        // no fingerprint, attributes, dynamic values or full report is rebuilt.
        internal static string GeneratedDuplicateKey(Transaction tr, Entity entity, QuantityElement element,
            string kind, Cache cache, Action cancel = null)
        {
            if (tr == null || cache == null) throw new ArgumentNullException(tr == null ? "tr" : "cache");
            cache.Check(tr, cancel);
            if (entity == null || element == null || entity.IsErased || !entity.Visible) return null;
            try
            {
                var observation = new ManualQuantityObservation { is_block = entity is BlockReference };
                bool linear = element.role == "rail" || element.role == "hrail" || element.role == "shina";
                if (kind == "frame" && !linear)
                {
                    var symbol = entity as BlockReference;
                    if (symbol == null) return null;
                    BlockIdentity(tr, symbol, observation, cache);
                    observation.position = Point(symbol.Position); observation.rotation = symbol.Rotation;
                    observation.scale_x = symbol.ScaleFactors.X; observation.scale_y = symbol.ScaleFactors.Y; observation.scale_z = symbol.ScaleFactors.Z;
                    return ManualQuantitiesCore.DuplicateKey(kind, element.role, "symbol", null, observation);
                }
                ExtractGeometry(tr, entity, observation, cache, cancel, false);
                if (kind == "cladding")
                    return observation.boundary_points == null || observation.boundary_reason != null ? null :
                        ManualQuantitiesCore.DuplicateKey(kind, element.role, "boundary", null, observation);
                if (!linear || !element.length_mm.HasValue) return null;
                double length = element.length_mm.Value;
                if (observation.line_start != null && LengthMatches(observation.line_start, observation.line_end, length))
                    return ManualQuantitiesCore.DuplicateKey(kind, element.role, "linear", null, observation);
                bool x = LengthMatches(observation.axis_x_start, observation.axis_x_end, length);
                bool y = LengthMatches(observation.axis_y_start, observation.axis_y_end, length);
                if (x == y) return null; // Missing or ambiguous direction is not a duplicate proof.
                // Generated rails may be stand-alone closed rectangles, whereas
                // imported stand-alone linear objects must be real line segments.
                if (!observation.is_block)
                {
                    observation.line_start = x ? observation.axis_x_start : observation.axis_y_start;
                    observation.line_end = x ? observation.axis_x_end : observation.axis_y_end;
                }
                return ManualQuantitiesCore.DuplicateKey(kind, element.role, "linear", x ? "X" : "Y", observation);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; } // Unsupported descriptors never manufacture a match.
        }

        private static bool LengthMatches(double[] start, double[] end, double length)
        {
            if (start == null || end == null || start.Length != 3 || end.Length != 3 || !Finite(length) || length <= 0) return false;
            double x = end[0] - start[0], y = end[1] - start[1], z = end[2] - start[2];
            return Math.Abs(Math.Sqrt(x * x + y * y + z * z) - length) <= 0.5;
        }

        private static void BlockIdentity(Transaction tr, BlockReference block, ManualQuantityObservation observation, Cache cache)
        {
            ObjectId baseId = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
            string name;
            if (!cache.Names.TryGetValue(baseId, out name))
            {
                var definition = tr.GetObject(baseId, OpenMode.ForRead) as BlockTableRecord;
                if (definition == null) throw new InvalidOperationException("Нет определения образца блока.");
                cache.Names[baseId] = name = definition.Name;
            }
            observation.effective_block_name = name;
            observation.sample_key = "block:" + baseId.Handle.ToString() + ":" + name;
            observation.symbol_key = observation.sample_key + "|state:" + block.BlockTableRecord.Handle.ToString();
        }

        private static string ExtractGeometry(Transaction tr, Entity entity, ManualQuantityObservation observation,
            Cache cache, Action cancel, bool properties)
        {
            var extra = new StringBuilder(); Token(extra, entity.Visible ? "visible" : "hidden");
            var block = entity as BlockReference;
            if (block != null)
            {
                observation.position = Point(block.Position); observation.rotation = block.Rotation;
                observation.scale_x = block.ScaleFactors.X; observation.scale_y = block.ScaleFactors.Y; observation.scale_z = block.ScaleFactors.Z;
                if (!Finite(block.Rotation) || !Nonzero(block.ScaleFactors.X) || !Nonzero(block.ScaleFactors.Y) ||
                    !Nonzero(block.ScaleFactors.Z) || !PlanarNormal(block.Normal))
                    throw new InvalidOperationException("Блок имеет неплоский, нулевой или неконечный масштаб/поворот.");
                BlockIdentity(tr, block, observation, cache);
                Definition shape = ReadDefinition(tr, block.BlockTableRecord, cache, cancel);
                Token(extra, shape.ExtraFingerprint);
                observation.boundary_reason = shape.BoundaryReason;
                observation.linear_reason = shape.LinearReason;
                observation.symbol_reason = shape.SymbolReason;
                if (shape.SymbolReason != null)
                    observation.boundary_reason = observation.linear_reason = shape.SymbolReason;
                else
                {
                    if (shape.Boundary != null)
                    {
                        Point3d[] world = Transform(shape.Boundary, block.BlockTransform);
                        try
                        {
                            observation.boundary_points = Boundary(world);
                            Point3d[] axes = RectangleAxes(shape.Boundary);
                            if (axes != null) SetAxes(observation, Transform(axes, block.BlockTransform));
                            else observation.linear_reason = "Для линейного блока нужен один прямоугольник вдоль его локальных осей X/Y или один прямой отрезок.";
                        }
                        catch (InvalidOperationException ex)
                        {
                            // A flat symbol may use crossing decorative lines;
                            // that does not make them a measurable panel boundary.
                            observation.boundary_reason = observation.linear_reason = ex.Message;
                        }
                    }
                    if (shape.Line != null)
                    {
                        var world = Transform(shape.Line, block.BlockTransform);
                        ValidateLine(world);
                        SetLineAxis(observation, shape.Line, world);
                    }
                }
                if (properties)
                {
                    if (shape.AttributesReason != null) throw new InvalidOperationException(shape.AttributesReason);
                    var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pair in shape.Constants) AddAttribute(observation.attributes, attributeNames, pair.Key, pair.Value);
                    foreach (ObjectId id in block.AttributeCollection)
                    {
                        cache.Check(tr, cancel);
                        var attribute = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                        if (attribute != null) AddAttribute(observation.attributes, attributeNames, attribute.Tag, attribute.TextString);
                    }
                    if (block.IsDynamicBlock)
                    {
                        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                        {
                            cache.Check(tr, cancel);
                            string key = property.PropertyName;
                            if (string.IsNullOrWhiteSpace(key) || !propertyNames.Add(key))
                                throw new InvalidOperationException("Имена динамических свойств пусты или повторяются; сопоставление неоднозначно.");
                            object value = property.Value;
                            if (value != null && !(value is string) && !(value is bool) && !(value is byte) && !(value is short) &&
                                !(value is int) && !(value is long) && !(value is float) && !(value is double) && !(value is decimal))
                                throw new InvalidOperationException("Неподдержанный тип динамического свойства «" + key + "».");
                            observation.dynamic_properties.Add(key, value);
                            observation.dynamic_units.Add(key, property.UnitsType.ToString());
                        }
                    }
                }
            }
            else
            {
                observation.symbol_reason = "Штучный символ должен быть одним явно классифицированным блоком.";
                var polyline = entity as Polyline;
                var line = entity as Line;
                if (polyline != null)
                {
                    Token(extra, Number(polyline.Thickness));
                    Point3d[] points = PolylinePoints(polyline);
                    if (polyline.Closed)
                    {
                        observation.boundary_points = Boundary(points);
                        observation.linear_reason = "Линейный ручной объект должен быть отрезком или открытой полилинией из двух вершин.";
                        Point3d[] axes = RectangleAxes(points); if (axes != null) SetAxes(observation, axes);
                    }
                    else
                    {
                        observation.boundary_reason = "Для облицовки нужна замкнутая полилиния без дуг.";
                        ValidateLine(points); observation.line_start = Point(points[0]); observation.line_end = Point(points[1]);
                    }
                }
                else if (line != null)
                {
                    Token(extra, Number(line.Thickness));
                    if (Math.Abs(line.Thickness) > 1e-9) throw new InvalidOperationException("Отрезок с объёмной толщиной не поддерживается.");
                    var points = new[] { line.StartPoint, line.EndPoint }; ValidateLine(points);
                    observation.line_start = Point(points[0]); observation.line_end = Point(points[1]);
                    observation.boundary_reason = "Для облицовки нужна замкнутая полилиния без дуг.";
                }
                else throw new InvalidOperationException("Поддерживаются LINE, плоская POLYLINE и явно классифицированный BLOCK.");
            }
            return extra.ToString();
        }

        private static Definition ReadDefinition(Transaction tr, ObjectId id, Cache cache, Action cancel)
        {
            Definition result;
            if (cache.Definitions.TryGetValue(id, out result)) return result;
            result = new Definition();
            var record = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
            if (record == null) throw new InvalidOperationException("Нет текущего вычисленного определения блока.");
            var extra = new StringBuilder(); int primitives = 0; Point3d[] boundary = null, line = null;
            string primitiveReason = null; double? elevation = null;
            var attributeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId childId in record)
            {
                cache.Check(tr, cancel);
                if (childId.IsNull || childId.IsErased) continue;
                var entity = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                if (entity == null || entity.IsErased) continue;
                Token(extra, childId.Handle.ToString()); Token(extra, entity.Visible ? "visible" : "hidden");
                if (entity is BlockReference)
                    result.SymbolReason = "Вложенный блок может быть сборкой; его нельзя автоматически считать одной физической деталью.";
                var attribute = entity as AttributeDefinition;
                if (attribute != null && attribute.Constant)
                {
                    try { AddAttribute(result.Constants, attributeNames, attribute.Tag, attribute.TextString); }
                    catch (InvalidOperationException ex) { result.AttributesReason = ex.Message; }
                }
                if (entity is Polyline) Token(extra, Number(((Polyline)entity).Thickness));
                if (entity is Line) Token(extra, Number(((Line)entity).Thickness));
                if (entity is Circle) Token(extra, Number(((Circle)entity).Thickness));
                if (entity is Arc) Token(extra, Number(((Arc)entity).Thickness));
                if (!entity.Visible || entity is Hatch || entity is DBText || entity is MText) continue;
                primitives++;
                try
                {
                    Point3d[] points = null;
                    var polyline = entity as Polyline; var segment = entity as Line;
                    if (polyline != null)
                    {
                        points = PolylinePoints(polyline, false);
                        bool curved = false;
                        for (int i = 0; i < polyline.NumberOfVertices; i++) if (polyline.GetBulgeAt(i) != 0) curved = true;
                        if (curved) primitiveReason = "Дуговые сегменты не используются для измерения границ и линейных деталей.";
                        else if (polyline.Closed) boundary = points;
                        else if (points.Length == 2) { ValidateLine(points); line = points; }
                        else primitiveReason = "Для линейного измерения нужна полилиния ровно из двух вершин.";
                    }
                    else if (segment != null)
                    {
                        if (Math.Abs(segment.Thickness) > 1e-9) throw new InvalidOperationException("Отрезок блока имеет объёмную толщину.");
                        points = new[] { segment.StartPoint, segment.EndPoint }; ValidateLine(points); line = points;
                    }
                    else if (entity is Circle)
                    {
                        var circle = (Circle)entity;
                        if (!PlanarNormal(circle.Normal) || Math.Abs(circle.Thickness) > 1e-9) throw new InvalidOperationException("Окружность блока не лежит в плоскости XY.");
                        points = new[] { circle.Center };
                    }
                    else if (entity is Arc)
                    {
                        var arc = (Arc)entity;
                        if (!PlanarNormal(arc.Normal) || Math.Abs(arc.Thickness) > 1e-9) throw new InvalidOperationException("Дуга блока не лежит в плоскости XY.");
                        points = new[] { arc.Center };
                    }
                    else if (entity is DBPoint) points = new[] { ((DBPoint)entity).Position };
                    else if (entity is Solid)
                    {
                        var solid = (Solid)entity; points = new Point3d[4];
                        for (short i = 0; i < 4; i++) points[i] = solid.GetPointAt(i);
                    }
                    else if (!(entity is BlockReference))
                        result.SymbolReason = "Геометрия образца «" + entity.GetType().Name + "» не поддерживается ручным адаптером.";
                    if (points != null)
                        foreach (Point3d point in points)
                        {
                            Point(point);
                            if (elevation.HasValue && Math.Abs(point.Z - elevation.Value) > 1e-6)
                                throw new InvalidOperationException("Геометрия блока не лежит в одной плоскости XY.");
                            elevation = point.Z;
                        }
                }
                catch (InvalidOperationException ex) { primitiveReason = ex.Message; result.SymbolReason = ex.Message; }
            }
            result.ExtraFingerprint = Hash(extra.ToString());
            if (primitives == 1 && primitiveReason == null)
            { result.Boundary = boundary; result.Line = line; }
            string reason = primitiveReason ?? (primitives != 1
                ? "Для измерения нужен ровно один видимый контур или отрезок; несколько примитивов неоднозначны."
                : "Для измерения поддерживается замкнутая полилиния без дуг или один прямой отрезок.");
            if (result.Boundary == null) result.BoundaryReason = reason;
            if (result.Boundary == null && result.Line == null) result.LinearReason = reason;
            cache.Definitions[id] = result;
            return result;
        }

        private static Point3d[] PolylinePoints(Polyline polyline, bool rejectArcs = true)
        {
            if (!PlanarNormal(polyline.Normal) || !Finite(polyline.Thickness) || Math.Abs(polyline.Thickness) > 1e-9)
                throw new InvalidOperationException("Полилиния должна быть плоской в XY и без объёмной толщины.");
            var points = new Point3d[polyline.NumberOfVertices];
            for (int i = 0; i < points.Length; i++)
            {
                if (rejectArcs && polyline.GetBulgeAt(i) != 0) throw new InvalidOperationException("Дуговые сегменты полилинии не поддерживаются ручным адаптером.");
                points[i] = polyline.GetPoint3dAt(i); Point(points[i]);
            }
            return points;
        }

        private static double[][] Boundary(Point3d[] points)
        {
            if (points == null || points.Length < 3) throw new InvalidOperationException("Контур облицовки содержит менее трёх вершин.");
            var xy = new double[points.Length][];
            for (int i = 0; i < points.Length; i++)
            {
                Point(points[i]);
                if (Math.Abs(points[i].Z - points[0].Z) > 1e-6) throw new InvalidOperationException("Контур облицовки не лежит в плоскости XY.");
                xy[i] = new[] { points[i].X, points[i].Y };
            }
            QuantityShape shape; string reason;
            if (!FacadeQuantitiesCore.TryShape(new[] { new QuantityRing { role = "outer", points = xy } }, out shape, out reason))
                throw new InvalidOperationException("Неподтверждённый контур облицовки: " + reason + ".");
            return xy;
        }

        private static void ValidateLine(Point3d[] points)
        {
            if (points == null || points.Length != 2) throw new InvalidOperationException("Нужен один прямой отрезок с двумя вершинами.");
            Point(points[0]); Point(points[1]);
            if (Math.Abs(points[0].Z - points[1].Z) > 1e-6 || points[0].DistanceTo(points[1]) <= 1e-9)
                throw new InvalidOperationException("Отрезок должен иметь положительную длину и лежать в плоскости XY.");
        }

        private static Point3d[] RectangleAxes(Point3d[] points)
        {
            int count = points.Length;
            if (count == 5 && points[0].DistanceTo(points[4]) <= 1e-8) count--;
            if (count != 4) return null;
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < count; i++)
            { minX = Math.Min(minX, points[i].X); minY = Math.Min(minY, points[i].Y); maxX = Math.Max(maxX, points[i].X); maxY = Math.Max(maxY, points[i].Y); }
            if (maxX - minX <= 1e-9 || maxY - minY <= 1e-9) return null;
            var corners = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                bool left = Math.Abs(points[i].X - minX) <= 1e-8, right = Math.Abs(points[i].X - maxX) <= 1e-8;
                bool bottom = Math.Abs(points[i].Y - minY) <= 1e-8, top = Math.Abs(points[i].Y - maxY) <= 1e-8;
                if ((!left && !right) || (!bottom && !top) || !corners.Add((right ? 1 : 0) + (top ? 2 : 0))) return null;
                Point3d next = points[(i + 1) % count];
                if (Math.Abs(points[i].X - next.X) > 1e-8 && Math.Abs(points[i].Y - next.Y) > 1e-8) return null;
            }
            double z = points[0].Z;
            return new[] { new Point3d(minX, (minY + maxY) / 2, z), new Point3d(maxX, (minY + maxY) / 2, z),
                new Point3d((minX + maxX) / 2, minY, z), new Point3d((minX + maxX) / 2, maxY, z) };
        }
        private static void SetAxes(ManualQuantityObservation observation, Point3d[] points)
        { observation.axis_x_start = Point(points[0]); observation.axis_x_end = Point(points[1]); observation.axis_y_start = Point(points[2]); observation.axis_y_end = Point(points[3]); }
        private static void SetLineAxis(ManualQuantityObservation observation, Point3d[] local, Point3d[] world)
        {
            if (Math.Abs(local[0].Y - local[1].Y) <= 1e-8)
            { observation.axis_x_start = Point(world[0]); observation.axis_x_end = Point(world[1]); }
            else if (Math.Abs(local[0].X - local[1].X) <= 1e-8)
            { observation.axis_y_start = Point(world[0]); observation.axis_y_end = Point(world[1]); }
            else observation.linear_reason = "Отрезок внутри блока не совпадает с локальной осью X или Y.";
        }
        private static Point3d[] Transform(Point3d[] points, Matrix3d transform)
        {
            var result = new Point3d[points.Length];
            for (int i = 0; i < points.Length; i++) { result[i] = points[i].TransformBy(transform); Point(result[i]); }
            return result;
        }
        private static void AddAttribute(Dictionary<string, string> values, HashSet<string> names, string tag, string value)
        {
            if (string.IsNullOrWhiteSpace(tag) || !names.Add(tag))
                throw new InvalidOperationException("Теги атрибутов пусты или повторяются; сопоставление неоднозначно.");
            values.Add(tag, value);
        }
        private static string Color(Transaction tr, Entity entity, Cache cache)
        {
            var color = entity.Color;
            if (color.IsByLayer)
            {
                string value;
                if (cache.Colors.TryGetValue(entity.LayerId, out value)) return value;
                var layer = entity.LayerId.IsNull ? null : tr.GetObject(entity.LayerId, OpenMode.ForRead) as LayerTableRecord;
                if (layer == null) return null;
                color = layer.Color;
                value = color.IsByAci ? "CAD ACI " + color.ColorIndex : color.IsByColor ? "CAD RGB " + color.Red + "," + color.Green + "," + color.Blue : null;
                cache.Colors[entity.LayerId] = value; return value;
            }
            return color.IsByAci ? "CAD ACI " + color.ColorIndex : color.IsByColor ? "CAD RGB " + color.Red + "," + color.Green + "," + color.Blue : null;
        }
        private static void AddGeometry(StringBuilder text, ManualQuantityObservation observation)
        {
            if (observation.boundary_points != null) foreach (var point in observation.boundary_points) AddPoint(text, point);
            AddPoint(text, observation.line_start); AddPoint(text, observation.line_end);
            AddPoint(text, observation.axis_x_start); AddPoint(text, observation.axis_x_end);
            AddPoint(text, observation.axis_y_start); AddPoint(text, observation.axis_y_end);
            AddPoint(text, observation.position);
            Token(text, observation.boundary_reason); Token(text, observation.linear_reason); Token(text, observation.symbol_reason);
        }
        private static void AddPoint(StringBuilder text, double[] point)
        { Token(text, point == null ? "null" : point.Length.ToString(CultureInfo.InvariantCulture)); if (point != null) foreach (double value in point) Token(text, Number(value)); }
        private static double[] Point(Point3d point)
        {
            if (!Finite(point.X) || !Finite(point.Y) || !Finite(point.Z)) throw new InvalidOperationException("Объект содержит неконечные координаты.");
            return new[] { point.X, point.Y, point.Z };
        }
        private static bool PlanarNormal(Vector3d normal)
        { return Finite(normal.X) && Finite(normal.Y) && Finite(normal.Z) && Math.Abs(normal.X) <= 1e-9 && Math.Abs(normal.Y) <= 1e-9 && Math.Abs(Math.Abs(normal.Z) - 1) <= 1e-9; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool Nonzero(double value) { return Finite(value) && Math.Abs(value) > 1e-12; }
        private static string Number(double value)
        { if (!Finite(value)) throw new InvalidOperationException("Объект содержит неконечный размер."); return (value == 0 ? 0 : value).ToString("R", CultureInfo.InvariantCulture); }
        private static List<string> Sorted(IEnumerable<string> values)
        { var list = new List<string>(values); list.Sort(StringComparer.Ordinal); return list; }
        private static void Token(StringBuilder text, string value)
        { if (value == null) text.Append("-1:"); else text.Append(value.Length).Append(':').Append(value); }
        private static string Hash(string value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", ""); }
    }
}
