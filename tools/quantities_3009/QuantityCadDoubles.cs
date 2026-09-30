// Deliberately small in-memory adapters, NOT the Autodesk runtime.
// No transaction isolation, automatic associativity, clone remapping, reactors,
// Hatch evaluation, or Undo is simulated. Tests set boundary state explicitly.
// Unknown/erased/foreign ObjectIds fail; entity geometry is explicit. This file
// deliberately implements only members needed to execute production guards.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

internal static class CadCounters
{
    public static long ObjectReads, ObjectWrites, DictionaryContains, DictionaryGet, DictionarySet,
        XrecordReads, XrecordWrites, BufferEnumerations, BufferValues, BufferTextChars,
        PointsRead, BulgesRead, HatchLoopsRead, TypedValuesAllocated, ObjectsCreated, ModelSpaceVisits;
    public static void Reset() {
        ObjectReads=ObjectWrites=DictionaryContains=DictionaryGet=DictionarySet=XrecordReads=XrecordWrites=0;
        BufferEnumerations=BufferValues=BufferTextChars=PointsRead=BulgesRead=HatchLoopsRead=TypedValuesAllocated=ObjectsCreated=ModelSpaceVisits=0;
    }
    public static Dictionary<string,object> Snapshot() {
        return new Dictionary<string,object> {
            {"get_object_read",ObjectReads},{"get_object_write",ObjectWrites},
            {"dictionary_contains",DictionaryContains},{"dictionary_get",DictionaryGet},{"dictionary_set",DictionarySet},
            {"xrecord_data_get",XrecordReads},{"xrecord_data_set",XrecordWrites},
            {"buffer_enumerations",BufferEnumerations},{"buffer_values",BufferValues},{"buffer_text_chars",BufferTextChars},
            {"points_read",PointsRead},{"bulges_read",BulgesRead},{"hatch_loops_read",HatchLoopsRead},
            {"typed_values_allocated",TypedValuesAllocated},{"objects_created",ObjectsCreated},{"modelspace_visits",ModelSpaceVisits}
        };
    }
}
namespace Autodesk.AutoCAD.Geometry
{
    public struct Point2d
    {
        public double X, Y;
        public Point2d(double x, double y) { X = x; Y = y; }
        public double GetDistanceTo(Point2d p) { return Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)); }
    }
    public struct Point3d
    {
        public double X, Y, Z;
        public Point3d(double x, double y, double z) { X=x; Y=y; Z=z; }
        public double DistanceTo(Point3d p) { return Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z)); }
    }
    public struct Vector3d
    {
        public double X, Y, Z;
        public Vector3d(double x, double y, double z) { X=x; Y=y; Z=z; }
        public static Vector3d ZAxis { get { return new Vector3d(0,0,1); } }
        public double Length { get { return Math.Sqrt(X*X+Y*Y+Z*Z); } }
        public static Vector3d operator -(Vector3d a, Vector3d b) { return new Vector3d(a.X-b.X,a.Y-b.Y,a.Z-b.Z); }
    }
    public struct Scale3d
    {
        public double X, Y, Z;
        public Scale3d(double scale) { X = scale; Y = scale; Z = scale; }
        public Scale3d(double x, double y, double z) { X = x; Y = y; Z = z; }
    }
    public abstract class Curve2d { public Point2d StartPoint; public Point2d EndPoint; }
    public sealed class LineSegment2d : Curve2d
    {
        public LineSegment2d(Point2d start, Point2d end) { StartPoint=start; EndPoint=end; }
    }
    public sealed class CircularArc2d : Curve2d
    {
        public double StartAngle, EndAngle;
        public bool IsClockWise;
    }
    public sealed class UnsupportedCurve2d : Curve2d { }
    public class Curve2dCollection : List<Curve2d> { }
}

namespace Autodesk.AutoCAD.Colors
{
    public enum ColorMethod { ByLayer = 192, ByBlock = 193, ByColor = 194, ByAci = 195 }
    public sealed class Color
    {
        public ColorMethod ColorMethod = ColorMethod.ByAci;
        public bool IsByLayer;
        public bool IsByAci = true;
        public short ColorIndex = 7;
        public byte Red, Green, Blue;
    }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
    using Autodesk.AutoCAD.Geometry;
    public enum OpenMode { ForRead, ForWrite }
    public enum DxfCode { Text = 1, SoftPointerId = 330, HardPointerId = 340 }
    public enum HatchStyle { Normal = 0, Outer = 1, Ignore = 2 }
    [Flags] public enum HatchLoopTypes { Default = 0, External = 1, Polyline = 2, Derived = 4, Outermost = 16 }
    public struct Handle
    {
        public long Value;
        public Handle(long value) { Value = value; }
        public override string ToString() { return Value.ToString("X"); }
    }
    public struct ObjectId : IEquatable<ObjectId>
    {
        internal DBObject Item;
        public bool IsNull { get { return Item == null; } }
        public bool IsErased { get { return Item != null && Item.IsErased; } }
        public bool IsValid { get { return Item != null; } }
        public Handle Handle { get { return Item == null ? new Handle(0) : Item.Handle; } }
        public static ObjectId Null { get { return new ObjectId(); } }
        public bool Equals(ObjectId other) { return Object.ReferenceEquals(Item, other.Item); }
        public override bool Equals(object other) { return other is ObjectId && Equals((ObjectId)other); }
        public override int GetHashCode() { return Item == null ? 0 : Item.GetHashCode(); }
        public static bool operator ==(ObjectId a, ObjectId b) { return a.Equals(b); }
        public static bool operator !=(ObjectId a, ObjectId b) { return !a.Equals(b); }
    }
    public class ObjectIdCollection : List<ObjectId>
    {
        public ObjectIdCollection() { }
        public ObjectIdCollection(ObjectId[] ids) : base(ids) { }
    }
    public class DBObject : IDisposable
    {
        public ObjectId ObjectId;
        public Handle Handle;
        public Database Database;
        public bool IsErased;
        public bool IsWriteEnabled = true;
        public void Dispose() { }
        public void UpgradeOpen() { }
        public void Erase() { IsErased = true; }
    }
    public class Entity : DBObject
    {
        public ObjectId ExtensionDictionary;
        public string Layer = "0";
        public ObjectId LayerId;
        public short ColorIndex = 7;
        public Autodesk.AutoCAD.Colors.Color Color = new Autodesk.AutoCAD.Colors.Color();
        public void CreateExtensionDictionary() { ExtensionDictionary = Database.Add(new DBDictionary()).ObjectId; }
    }
    public class Database
    {
        public Guid FingerprintGuid = Guid.NewGuid();
        private ObjectId namedObjectsDictionaryId, blockTableId;
        private BlockTableRecord modelSpace;
        public ObjectId NamedObjectsDictionaryId { get {
            if (namedObjectsDictionaryId.IsNull) namedObjectsDictionaryId = Add(new DBDictionary()).ObjectId;
            return namedObjectsDictionaryId;
        } }
        public ObjectId BlockTableId { get {
            if (blockTableId.IsNull) {
                var table = Add(new BlockTable()); blockTableId = table.ObjectId;
                modelSpace = Add(new BlockTableRecord { Name = BlockTableRecord.ModelSpace });
                table.Items.Add(BlockTableRecord.ModelSpace, modelSpace.ObjectId);
                modelSpace.Children.AddRange(objects.Values.OfType<Entity>().Select(x => x.ObjectId));
            }
            return blockTableId;
        } }
        private long next;
        private readonly Dictionary<long, DBObject> objects = new Dictionary<long, DBObject>();
        public T Add<T>(T obj) where T : DBObject
        {
            if (!obj.ObjectId.IsNull) return obj;
            obj.Database = this; obj.Handle = new Handle(++next); obj.ObjectId = new ObjectId { Item = obj };
            objects.Add(obj.Handle.Value, obj); CadCounters.ObjectsCreated++;
            if (modelSpace != null && obj is Entity) modelSpace.Children.Add(obj.ObjectId);
            return obj;
        }
        public ObjectId GetObjectId(bool createIfNotFound, Handle handle, int xrefId)
        {
            DBObject value;
            if (!objects.TryGetValue(handle.Value, out value)) throw new InvalidOperationException("Missing handle");
            return value.ObjectId;
        }
        public bool TryGetObjectId(Handle handle, out ObjectId id)
        {
            DBObject value;
            if (objects.TryGetValue(handle.Value, out value)) { id = value.ObjectId; return true; }
            id = ObjectId.Null; return false;
        }
    }
    public class Transaction : IDisposable
    {
        private readonly Database database;
        public Transaction(Database db) { database = db; }
        public DBObject GetObject(ObjectId id, OpenMode mode)
        {
            if (mode == OpenMode.ForRead) CadCounters.ObjectReads++; else CadCounters.ObjectWrites++;
            if (id.IsNull || id.IsErased) throw new InvalidOperationException("Missing/erased object");
            if (id.Item.Database != database) throw new InvalidOperationException("Foreign database object");
            return id.Item;
        }
        public void AddNewlyCreatedDBObject(DBObject obj, bool add) { if (add) database.Add(obj); }
        public void Dispose() { }
        public void Commit() { }
    }
    public class DBDictionary : DBObject
    {
        public readonly Dictionary<string, ObjectId> Items = new Dictionary<string, ObjectId>();
        public bool Contains(string key) { CadCounters.DictionaryContains++; return Items.ContainsKey(key); }
        public ObjectId GetAt(string key) { CadCounters.DictionaryGet++; return Items[key]; }
        public ObjectId SetAt(string key, DBObject value) { CadCounters.DictionarySet++; Items[key] = Database.Add(value).ObjectId; return value.ObjectId; }
        public ObjectId Remove(string key) { var old = Items[key]; Items.Remove(key); return old; }
    }
    public class TypedValue
    {
        public int TypeCode; public object Value;
        public TypedValue(int typeCode, object value) { CadCounters.TypedValuesAllocated++; TypeCode=typeCode; Value=value; }
    }
    public class ResultBuffer : IEnumerable<TypedValue>, IDisposable
    {
        private readonly List<TypedValue> values;
        public ResultBuffer(params TypedValue[] initial) { values = new List<TypedValue>(initial); }
        public void Add(TypedValue value) { values.Add(value); }
        public TypedValue[] AsArray() { return values.ToArray(); }
        public IEnumerator<TypedValue> GetEnumerator() {
            CadCounters.BufferEnumerations++;
            foreach (var item in values) {
                CadCounters.BufferValues++;
                if (item.TypeCode == (int)DxfCode.Text && item.Value is string) CadCounters.BufferTextChars+=((string)item.Value).Length;
                yield return item;
            }
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        public void Dispose() { }
    }
    public class Xrecord : DBObject { private ResultBuffer data;
        public ResultBuffer Data { get { CadCounters.XrecordReads++; return data; } set { CadCounters.XrecordWrites++; data=value; } }
        public bool XlateReferences; }
    public class LayerTableRecord : DBObject { public Autodesk.AutoCAD.Colors.Color Color = new Autodesk.AutoCAD.Colors.Color(); }
    public class Table : Entity { }
    public class Line : Entity { public Point3d StartPoint, EndPoint; public Vector3d Normal = Vector3d.ZAxis; }
    public class Circle : Entity { public Point3d Center; public double Radius; public Vector3d Normal = Vector3d.ZAxis; }
    public class Arc : Entity { public Point3d Center; public double Radius, StartAngle, EndAngle; public Vector3d Normal = Vector3d.ZAxis; }
    public class Ellipse : Entity { public Point3d Center; public Vector3d MajorAxis, MinorAxis; public double StartAngle, EndAngle; }
    public class DBText : Entity {
        public Point3d Position, AlignmentPoint; public Vector3d Normal = Vector3d.ZAxis;
        public double Height, Rotation, WidthFactor = 1; public string TextString;
    }
    public class AttributeDefinition : DBText { public string Tag; public bool Constant; }
    public class AttributeReference : DBText { public string Tag; }
    public class DBPoint : Entity { public Point3d Position; }
    public class Solid : Entity { public readonly Point3d[] Points = new Point3d[4]; public Point3d GetPointAt(int i) { CadCounters.PointsRead++; return Points[i]; } }
    public class BlockTable : DBObject {
        public readonly Dictionary<string,ObjectId> Items = new Dictionary<string,ObjectId>();
        public ObjectId this[string name] { get { return Items[name]; } }
    }
    public class BlockTableRecord : DBObject, IEnumerable<ObjectId>
    {
        public const string ModelSpace = "*Model_Space";
        public string Name;
        public Point3d Origin;
        public readonly List<ObjectId> Children = new List<ObjectId>();
        public ObjectId AppendEntity(Entity entity) { Database.Add(entity); Children.Add(entity.ObjectId); return entity.ObjectId; }
        public IEnumerator<ObjectId> GetEnumerator() {
            foreach(var child in Children) { if (Name == ModelSpace) CadCounters.ModelSpaceVisits++; yield return child; }
        }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
    }
    public class BlockReference : Entity
    {
        public Point3d Position;
        public Scale3d ScaleFactors = new Scale3d(1);
        public double Rotation;
        public Vector3d Normal = Vector3d.ZAxis;
        public ObjectId BlockTableRecord;
        public ObjectId DynamicBlockTableRecord;
        public bool IsDynamicBlock;
        public readonly List<DynamicBlockReferenceProperty> DynamicBlockReferencePropertyCollection = new List<DynamicBlockReferenceProperty>();
        public readonly List<ObjectId> AttributeCollection = new List<ObjectId>();
    }
    public class DynamicBlockReferenceProperty { public string PropertyName; public object Value; }
    public class Polyline : Entity
    {
        public readonly List<Point2d> Points = new List<Point2d>();
        public readonly List<double> Bulges = new List<double>();
        public bool Closed = true;
        public double Elevation;
        public double ConstantWidth;
        public readonly List<double> StartWidths = new List<double>();
        public readonly List<double> EndWidths = new List<double>();
        public Vector3d Normal = Vector3d.ZAxis;
        public int NumberOfVertices { get { return Points.Count; } }
        public Point2d GetPoint2dAt(int i) { CadCounters.PointsRead++; return Points[i]; }
        public Point3d GetPoint3dAt(int i) { CadCounters.PointsRead++; return new Point3d(Points[i].X, Points[i].Y, Elevation); }
        public double GetBulgeAt(int i) { CadCounters.BulgesRead++; return Bulges[i]; }
        public double GetStartWidthAt(int i) { return StartWidths[i]; }
        public double GetEndWidthAt(int i) { return EndWidths[i]; }
        public void AddVertexAt(int i, Point2d p, double bulge, double startWidth, double endWidth) {
            Points.Insert(i,p); Bulges.Insert(i,bulge); StartWidths.Insert(i,startWidth); EndWidths.Insert(i,endWidth);
        }
    }
    public struct BulgeVertex
    {
        public Point2d Vertex; public double Bulge;
        public BulgeVertex(Point2d vertex, double bulge) { Vertex=vertex; Bulge=bulge; }
    }
    public class BulgeVertexCollection : List<BulgeVertex> { }
    public class HatchLoop
    {
        public bool IsPolyline = true;
        public HatchLoopTypes LoopType;
        public BulgeVertexCollection Polyline = new BulgeVertexCollection();
        public Curve2dCollection Curves = new Curve2dCollection();
    }
    public class Hatch : Entity
    {
        public string PatternName = "SOLID";
        public double PatternScale = 1, PatternAngle;
        public readonly List<HatchLoop> Loops = new List<HatchLoop>();
        public readonly List<ObjectIdCollection> AssociatedIds = new List<ObjectIdCollection>();
        public bool Associative = true;
        public HatchStyle HatchStyle = HatchStyle.Normal;
        public double Elevation;
        public Vector3d Normal = Vector3d.ZAxis;
        public double Area;
        public int NumberOfLoops { get { return Loops.Count; } }
        public HatchLoop GetLoopAt(int i) { CadCounters.HatchLoopsRead++; return Loops[i]; }
        public ObjectIdCollection GetAssociatedObjectIdsAt(int i) { return AssociatedIds[i]; }
        public ObjectIdCollection GetAssociatedObjectIds() { return new ObjectIdCollection(AssociatedIds.SelectMany(ids=>ids).ToArray()); }
    }
    public class MText : Entity {
        public Point3d Location; public string Contents; public Vector3d Normal = Vector3d.ZAxis;
        public double Rotation, Width, TextHeight;
    }
}
