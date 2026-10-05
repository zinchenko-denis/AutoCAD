// Node-specific CAD/storage doubles; only actual node commands/store/renderer run.
// Geometry/transform values are explicit model state, not native AutoCAD behavior.
// Rollback below is an explicit in-memory model. It does not verify AutoCAD
// Undo, COPY remapping, document locking, persistence or Windows host behavior.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

internal static class ProjectCadCounters
{
    internal static int ObjectReads, ObjectWrites, NodReads, DictionaryReads,
        DictionaryWrites, DataReads, DataWrites, BufferDisposals, ModelSpaceReads;
    internal static int FailAfterMutations = -1;
    internal static int ModelSpaceEnumerations, DefinitionEnumerations, PrimitiveVisits, Created, ProjectRecordReads, ZoneRecordReads, Mutations;
    internal static void Reset()
    {
        ObjectReads = ObjectWrites = NodReads = DictionaryReads = DictionaryWrites = 0;
        DataReads = DataWrites = BufferDisposals = ModelSpaceReads = 0;
        FailAfterMutations = -1; ModelSpaceEnumerations = DefinitionEnumerations = PrimitiveVisits = Created = ProjectRecordReads = ZoneRecordReads = Mutations = 0;
    }
    internal static void Mutation()
    {
        Mutations++;
        if (FailAfterMutations == 0) throw new InvalidOperationException("Injected storage write failure");
        if (FailAfterMutations > 0) FailAfterMutations--;
    }
    internal static Dictionary<string, object> Snapshot()
    {
        return new Dictionary<string, object> {
            { "object_reads", ObjectReads }, { "object_writes", ObjectWrites },
            { "nod_reads", NodReads }, { "dictionary_reads", DictionaryReads },
            { "dictionary_writes", DictionaryWrites }, { "xrecord_data_reads", DataReads },
            { "xrecord_data_writes", DataWrites }, { "buffer_disposals", BufferDisposals },
            { "modelspace_reads", ModelSpaceReads }
        };
    }
}

namespace Autodesk.AutoCAD.DatabaseServices
{
    public enum OpenMode { ForRead, ForWrite }
    public enum DxfCode { Text = 1, SoftPointerId = 330, HardPointerId = 340, Int16 = 70, Operator=-4, Start=0 }
    public enum UnitsValue { Undefined=0, Inches=1, Feet=2, Millimeters=4, Centimeters=5, Meters=6 }
    public enum AttachmentPoint { TopLeft=1, TopCenter=2, TopRight=3, MiddleLeft=4, MiddleCenter=5, BottomLeft=7 }
    public enum LineWeight { ByLayer=-1, ByBlock=-2, LineWeight000=0, LineWeight025=25 }
    public enum LineSpacingStyle { AtLeast=1, Exactly=2 }
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
        public bool IsValid { get { return Item != null && !Item.Removed; } }
        public Handle Handle { get { return Item == null ? new Handle(0) : Item.Handle; } }
        public Database Database { get { return Item == null ? null : Item.Database; } }
        public static ObjectId Null { get { return new ObjectId(); } }
        public bool Equals(ObjectId other) { return Object.ReferenceEquals(Item, other.Item); }
        public override bool Equals(object other) { return other is ObjectId && Equals((ObjectId)other); }
        public override int GetHashCode() { return Item == null ? 0 : Item.GetHashCode(); }
        public static bool operator ==(ObjectId first, ObjectId second) { return first.Equals(second); }
        public static bool operator !=(ObjectId first, ObjectId second) { return !first.Equals(second); }
    }
    public class DBObject : IDisposable
    {
        public ObjectId ObjectId;
        public ObjectId ExtensionDictionary;
        public void CreateExtensionDictionary(){ProjectCadCounters.Mutation();ExtensionDictionary=Database.Add(new DBDictionary()).ObjectId;}
        public Handle Handle;
        public Database Database;
        public bool IsErased, IsWriteEnabled;
        internal bool Removed;
        public void UpgradeOpen() { IsWriteEnabled = true; ProjectCadCounters.ObjectWrites++; }
        public void Erase() { ProjectCadCounters.Mutation(); IsErased = true; }
        public void Erase(bool value) { ProjectCadCounters.Mutation(); IsErased = value; }
        public void Dispose() { }
    }
    public class Entity : DBObject
    {
        private string layer="0"; public string Layer { get { return !LayerId.IsNull ? ((LayerTableRecord)LayerId.Item).Name : layer; } set { layer=value; } } public string Linetype="ByLayer";
        public int ColorIndex=256;
        public Autodesk.AutoCAD.Colors.Color Color=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByLayer,256);
        public bool Visible=true; public double LinetypeScale=1; public LineWeight LineWeight=LineWeight.ByLayer;
        public ObjectId LayerId;
        public static Autodesk.AutoCAD.Colors.Transparency DefaultTransparency=new Autodesk.AutoCAD.Colors.Transparency(255);
        public Autodesk.AutoCAD.Colors.Transparency Transparency=DefaultTransparency;
        public void SetDatabaseDefaults(){} public void SetDatabaseDefaults(Database db){}

    }
    public sealed class Hatch : Entity { }
    public sealed class MText : Entity {
        // Native optional getters are not unrestricted fields. In particular,
        // getBackgroundFillColor/getBackgroundScaleFactor return eNotApplicable
        // when no background has been defined (Autodesk ObjectARX reference).
        internal static bool StrictOptionalProperties;
        public Autodesk.AutoCAD.Geometry.Point3d Location;
        public Autodesk.AutoCAD.Geometry.Vector3d Normal=Autodesk.AutoCAD.Geometry.Vector3d.ZAxis;
        public double TextHeight=2.5, Width, Rotation, LineSpacingFactor=1;
        public string Contents=""; public AttachmentPoint Attachment=AttachmentPoint.TopLeft;
        public ObjectId TextStyleId; public bool BackgroundFill, UseBackgroundColor, ShowBorders;
        private double backgroundScaleFactor=1.5,columnWidth,columnGutterWidth; private int columnCount;
        private Autodesk.AutoCAD.Colors.Color backgroundFillColor=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,7);
        public int ColumnType;
        public double BackgroundScaleFactor {get {BackgroundRequired();return backgroundScaleFactor;} set {backgroundScaleFactor=value;}}
        public Autodesk.AutoCAD.Colors.Color BackgroundFillColor {get {BackgroundRequired();return backgroundFillColor;} set {backgroundFillColor=value;}}
        public int ColumnCount {get {ColumnsRequired();return columnCount;} set {columnCount=value;}}
        public double ColumnWidth {get {ColumnsRequired();return columnWidth;} set {columnWidth=value;}}
        public double ColumnGutterWidth {get {ColumnsRequired();return columnGutterWidth;} set {columnGutterWidth=value;}}
        private void BackgroundRequired(){if(StrictOptionalProperties&&!BackgroundFill)throw new InvalidOperationException("eNotApplicable: background fill is not defined");}
        private void ColumnsRequired(){if(StrictOptionalProperties&&ColumnType==0)throw new InvalidOperationException("Column data is not defined for NoColumns");}
        public LineSpacingStyle LineSpacingStyle=LineSpacingStyle.AtLeast;
    }
    public sealed class Polyline : Entity { }
    public sealed class Database
    {
        internal readonly Dictionary<long, DBObject> Objects = new Dictionary<long, DBObject>();
        internal long Next;
        private readonly ObjectId nod; private readonly ObjectId blocks,layers,current,textStyles;
        public UnitsValue Insunits=UnitsValue.Millimeters; public ObjectId Textstyle;
        public Guid FingerprintGuid = Guid.NewGuid();
        public Database() { nod = Add(new DBDictionary()).ObjectId;
            var table=Add(new BlockTable());blocks=table.ObjectId;
            var model=Add(new BlockTableRecord { Name=BlockTableRecord.ModelSpace }); current=model.ObjectId;table.Items[model.Name]=current;
            var lt=Add(new LayerTable());layers=lt.ObjectId;lt.Add(new LayerTableRecord{Name="0",Color=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,7)});
            var styles=Add(new TextStyleTable());textStyles=styles.ObjectId;Textstyle=styles.Add(new TextStyleTableRecord{Name="Standard",FileName="fixture.shx"});
        }
        public TransactionManager TransactionManager { get { return new TransactionManager(this); } }
        public ObjectId CurrentSpaceId { get { return current; } }
        public ObjectId LayerTableId { get { return layers; } }
        public ObjectId TextStyleTableId { get { return textStyles; } }
        public ObjectId NamedObjectsDictionaryId { get { ProjectCadCounters.NodReads++; return nod; } }
        public ObjectId BlockTableId { get { return blocks; } }
        public T Add<T>(T value) where T : DBObject
        {
            if (!value.ObjectId.IsNull) return value;
            ProjectCadCounters.Created++; value.Database = this; value.Handle = new Handle(++Next);
            value.ObjectId = new ObjectId { Item = value }; Objects.Add(Next, value);
            return value;
        }
        public bool TryGetObjectId(Handle handle, out ObjectId id)
        {
            DBObject value;
            if (Objects.TryGetValue(handle.Value, out value) && !value.Removed)
            { id = value.ObjectId; return true; }
            id = ObjectId.Null; return false;
        }
        public ObjectId GetObjectId(bool createIfNotFound, Handle handle, int xrefId)
        {
            ObjectId id; if (TryGetObjectId(handle, out id)) return id;
            throw new InvalidOperationException("Unknown object handle");
        }
    }
    public sealed class Transaction : IDisposable
    {
        private sealed class Saved
        {
            internal DBObject Value;
            internal bool Erased;
            internal ObjectId Extension;
            internal Dictionary<string, ObjectId> Items;
            internal TypedValue[] Values; internal string RecordName;
            internal bool Xlate;
            internal Dictionary<string,ObjectId> SymbolItems; internal List<ObjectId> Children;
        }
        private readonly Database db;
        private readonly long next;
        private readonly List<Saved> saved = new List<Saved>();
        private bool committed, disposed;
        public Transaction(Database database)
        {
            db = database; next = db.Next;
            foreach (var item in db.Objects.Values) {
                var entity = item as Entity; var dictionary = item as DBDictionary; var record = item as Xrecord;
                saved.Add(new Saved { Value = item, Erased = item.IsErased,
                    Extension = item.ExtensionDictionary,
                    Items = dictionary == null ? null : new Dictionary<string, ObjectId>(dictionary.Items),
                    Values = record == null || record.Values == null ? null : record.Values.ToArray(),
                    Xlate = record != null && record.XlateReferences,
                    SymbolItems = item is SymbolTable ? new Dictionary<string,ObjectId>(((SymbolTable)item).Items) : null,
                    Children=item is BlockTableRecord ? new List<ObjectId>(((BlockTableRecord)item).Children) : null });
            }
        }
        public DBObject GetObject(ObjectId id, OpenMode mode) { return GetObject(id, mode, false); }
        public DBObject GetObject(ObjectId id, OpenMode mode, bool openErased)
        {
            if (mode == OpenMode.ForRead) ProjectCadCounters.ObjectReads++; else ProjectCadCounters.ObjectWrites++;
            if (id.IsNull || !id.IsValid || (!openErased && id.IsErased)) throw new InvalidOperationException("Absent or erased object");
            if (id.Item.Database != db) throw new InvalidOperationException("Foreign database object");
            if (mode == OpenMode.ForWrite) id.Item.IsWriteEnabled = true;
            return id.Item;
        }
        public void AddNewlyCreatedDBObject(DBObject value, bool add) { if (add) db.Add(value); }
        public void Commit() { committed = true; }
        public void Abort() { Rollback(); disposed = true; }
        private void Rollback()
        {
            foreach (var key in db.Objects.Keys.Where(key => key > next).ToArray()) {
                db.Objects[key].Removed = true; db.Objects.Remove(key);
            }
            db.Next = next;
            foreach (var item in saved) {
                item.Value.IsErased = item.Erased; item.Value.IsWriteEnabled = false;
                item.Value.ExtensionDictionary = item.Extension;
                var dictionary = item.Value as DBDictionary;
                if (dictionary != null) { dictionary.Items.Clear(); foreach (var pair in item.Items) dictionary.Items.Add(pair.Key, pair.Value); }
                var symbols=item.Value as SymbolTable;
                if(symbols!=null){symbols.Items.Clear();foreach(var pair in item.SymbolItems)symbols.Items.Add(pair.Key,pair.Value);}
                var block=item.Value as BlockTableRecord;if(block!=null){block.Children.Clear();block.Children.AddRange(item.Children);}
                var record = item.Value as Xrecord;
                if (record != null) { record.Values = item.Values == null ? null : item.Values.ToArray(); record.XlateReferences = item.Xlate; }
            }
        }
        public void Dispose() { if (disposed) return; if (!committed) Rollback(); disposed = true; }
    }
    public struct DBDictionaryEntry {public string Key;public ObjectId Value;}
    public sealed class DBDictionary : DBObject, IEnumerable<DBDictionaryEntry>
    {
        public readonly Dictionary<string, ObjectId> Items = new Dictionary<string, ObjectId>(StringComparer.Ordinal);
        public IEnumerator<DBDictionaryEntry> GetEnumerator(){foreach(var pair in Items)yield return new DBDictionaryEntry{Key=pair.Key,Value=pair.Value};} IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
        public bool Contains(string key) { ProjectCadCounters.DictionaryReads++; return Items.ContainsKey(key); }
        public ObjectId GetAt(string key) { ProjectCadCounters.DictionaryReads++; return Items[key]; }
        public ObjectId SetAt(string key, DBObject value)
        {
            ProjectCadCounters.Mutation(); ProjectCadCounters.DictionaryWrites++;
            if(value is Xrecord)((Xrecord)value).RecordName=key; Items[key] = Database.Add(value).ObjectId; return value.ObjectId;
        }
        public ObjectId Remove(string key)
        {
            ProjectCadCounters.Mutation(); ProjectCadCounters.DictionaryWrites++;
            var previous = Items[key]; Items.Remove(key); return previous;
        }
    }
    public sealed class TypedValue
    {
        public int TypeCode; public object Value;
        public TypedValue(int code, object value) { TypeCode = code; Value = value; }
    }
    public sealed class ResultBuffer : IEnumerable<TypedValue>, IDisposable
    {
        private readonly List<TypedValue> values;
        internal bool Disposed;
        public ResultBuffer(params TypedValue[] initial) { values = new List<TypedValue>(initial); }
        public void Add(TypedValue value) { if (Disposed) throw new ObjectDisposedException("ResultBuffer"); values.Add(value); }
        public TypedValue[] AsArray() { if (Disposed) throw new ObjectDisposedException("ResultBuffer"); return values.ToArray(); }
        public IEnumerator<TypedValue> GetEnumerator() { if (Disposed) throw new ObjectDisposedException("ResultBuffer"); return values.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }
        public void Dispose() { if (!Disposed) ProjectCadCounters.BufferDisposals++; Disposed = true; }
    }
    public sealed class Xrecord : DBObject
    {
        internal TypedValue[] Values; internal string RecordName;
        public bool XlateReferences;
        public ResultBuffer Data {
            get { ProjectCadCounters.DataReads++;
                if (RecordName=="PARAMETERS") ProjectCadCounters.ProjectRecordReads++;
                if (RecordName=="AFACADE_ZONE_PARAMETERS") ProjectCadCounters.ZoneRecordReads++;
                return Values == null ? null : new ResultBuffer(Values); }
            set { ProjectCadCounters.Mutation(); ProjectCadCounters.DataWrites++; Values = value == null ? null : value.AsArray(); }
        }
    }
}

namespace Autodesk.AutoCAD.Geometry {
 public struct Point3d {
  public readonly double X,Y,Z; public Point3d(double x,double y,double z){X=x;Y=y;Z=z;}
  public static bool operator ==(Point3d a,Point3d b){return a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;}public static bool operator !=(Point3d a,Point3d b){return !(a==b);}public override bool Equals(object o){return o is Point3d&&this==(Point3d)o;}public override int GetHashCode(){return X.GetHashCode()^Y.GetHashCode()^Z.GetHashCode();}
  public static Point3d Origin { get {return new Point3d(0,0,0);} }
  public Point3d TransformBy(Matrix3d m){return m.Transform(this);}
  public double DistanceTo(Point3d p){return Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z));}
 }
 public struct Vector3d {
  public readonly double X,Y,Z; public Vector3d(double x,double y,double z){X=x;Y=y;Z=z;}
  public double DotProduct(Vector3d v){return X*v.X+Y*v.Y+Z*v.Z;}public Vector3d CrossProduct(Vector3d v){return new Vector3d(Y*v.Z-Z*v.Y,Z*v.X-X*v.Z,X*v.Y-Y*v.X);}
  public static bool operator ==(Vector3d a,Vector3d b){return a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;}public static bool operator !=(Vector3d a,Vector3d b){return !(a==b);}public override bool Equals(object o){return o is Vector3d&&this==(Vector3d)o;}public override int GetHashCode(){return X.GetHashCode()^Y.GetHashCode()^Z.GetHashCode();}
  public static Vector3d ZAxis {get{return new Vector3d(0,0,1);}}
  public static Vector3d XAxis {get{return new Vector3d(1,0,0);}}
  public static Vector3d YAxis {get{return new Vector3d(0,1,0);}}
  public double Length {get{return Math.Sqrt(X*X+Y*Y+Z*Z);}}
  public static Vector3d operator -(Vector3d a,Vector3d b){return new Vector3d(a.X-b.X,a.Y-b.Y,a.Z-b.Z);}
 }
 public struct CoordinateSystem3d {public Point3d Origin;public Vector3d Xaxis,Yaxis,Zaxis;}
 public struct Scale3d { public readonly double X,Y,Z;public Scale3d(double s){X=Y=Z=s;}public Scale3d(double x,double y,double z){X=x;Y=y;Z=z;} }
 public struct Matrix3d {
  private readonly double[] entries;
  public Matrix3d(double[] v){if(v.Length!=16)throw new ArgumentException("Matrix fixture needs 16 values");entries=(double[])v.Clone();}
  public static Matrix3d Identity {get{return new Matrix3d(new double[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1});}}
  public double[] ToArray(){return entries==null?Identity.ToArray():(double[])entries.Clone();}
  public CoordinateSystem3d CoordinateSystem3d {get{var a=ToArray();return new CoordinateSystem3d{Origin=new Point3d(a[3],a[7],a[11]),Xaxis=new Vector3d(a[0],a[4],a[8]),Yaxis=new Vector3d(a[1],a[5],a[9]),Zaxis=new Vector3d(a[2],a[6],a[10])};}}
  public double this[int row,int col]{get{return ToArray()[row*4+col];}}
  internal Point3d Transform(Point3d p){var a=ToArray();return new Point3d(a[0]*p.X+a[1]*p.Y+a[2]*p.Z+a[3],a[4]*p.X+a[5]*p.Y+a[6]*p.Z+a[7],a[8]*p.X+a[9]*p.Y+a[10]*p.Z+a[11]);}
  public static Matrix3d Placement(Point3d p,Scale3d s,double rotation){double c=Math.Cos(rotation),n=Math.Sin(rotation);return new Matrix3d(new[]{c*s.X,-n*s.Y,0,p.X,n*s.X,c*s.Y,0,p.Y,0,0,s.Z,p.Z,0,0,0,1.0});}
 }
}
namespace Autodesk.AutoCAD.Colors {
 public enum ColorMethod { ByLayer=192,ByBlock=193,ByColor=194,ByAci=195 }
 public sealed class Color {
  public ColorMethod ColorMethod;public short ColorIndex;public byte Red,Green,Blue;public System.Drawing.Color ColorValue {get{return System.Drawing.Color.FromArgb(255,Red,Green,Blue);}}
  public static Color FromColorIndex(ColorMethod method,short index){return new Color{ColorMethod=method,ColorIndex=index};}
  public bool IsByLayer {get{return ColorMethod==ColorMethod.ByLayer;}}
  public bool IsByBlock {get{return ColorMethod==ColorMethod.ByBlock;}}
  public bool IsByAci {get{return ColorMethod==ColorMethod.ByAci;}}
  public bool IsByColor {get{return ColorMethod==ColorMethod.ByColor;}}
 }
 public struct Transparency {
  private byte alpha;public static bool StrictAlpha;public bool ByLayerFlag,ByBlockFlag,InvalidFlag;
  public Transparency(byte value){alpha=value;ByLayerFlag=ByBlockFlag=InvalidFlag=false;}
  public byte Alpha {get{if(StrictAlpha&&!IsByAlpha)throw new InvalidOperationException("eInvalidKey");return alpha;}set{alpha=value;}}
  public bool IsByLayer {get{return ByLayerFlag;}}public bool IsByBlock {get{return ByBlockFlag;}}public bool IsByAlpha {get{return !ByLayerFlag&&!ByBlockFlag&&!InvalidFlag;}}
 }
}
namespace Autodesk.AutoCAD.DatabaseServices {
 using Autodesk.AutoCAD.Geometry;
 public sealed class TransactionManager {readonly Database db;public TransactionManager(Database d){db=d;}public Transaction StartTransaction(){return new Transaction(db);} }
 public abstract class SymbolTable:DBObject {
  public readonly Dictionary<string,ObjectId> Items=new Dictionary<string,ObjectId>();
  public bool Has(string key){return Items.ContainsKey(key);}public ObjectId this[string key]{get{return Items[key];}}
 }
 public sealed class BlockTable:SymbolTable {
  public ObjectId Add(BlockTableRecord b){ProjectCadCounters.Mutation();return Items[b.Name]=Database.Add(b).ObjectId;}
 }
 public sealed class TextStyleTable:SymbolTable { public ObjectId Add(TextStyleTableRecord style){ProjectCadCounters.Mutation();return Items[style.Name]=Database.Add(style).ObjectId;} }
 public sealed class LayerTable:SymbolTable { public ObjectId Add(LayerTableRecord b){ProjectCadCounters.Mutation();return Items[b.Name]=Database.Add(b).ObjectId;} }
 public sealed class LayerTableRecord:DBObject {public string Name;public Autodesk.AutoCAD.Colors.Color Color;public bool IsOff,IsFrozen,IsLocked;public LineWeight LineWeight=LineWeight.ByLayer;public ObjectId LinetypeObjectId;public Autodesk.AutoCAD.Colors.Transparency Transparency=new Autodesk.AutoCAD.Colors.Transparency(0);}
 public sealed class TextStyleTableRecord:DBObject {public string Name,FileName,BigFontFileName="";public double TextSize, XScale=1,ObliquingAngle;public bool IsVertical;public Autodesk.AutoCAD.GraphicsInterface.FontDescriptor Font=new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Fixture",false,false,0,0);}
 public sealed class NodeFont {public string TypeFace="Fixture";public bool Bold,Italic;public int CharacterSet,PitchAndFamily;}
 public sealed class BlockTableRecord:DBObject,IEnumerable<ObjectId> {
  public const string ModelSpace="*Model_Space";public string Name;public Point3d Origin=Point3d.Origin;public UnitsValue Units=UnitsValue.Undefined;
  public readonly List<ObjectId> Children=new List<ObjectId>();public bool IsAnonymous,IsLayout,IsFromExternalReference;
  public ObjectId AppendEntity(Entity e){ProjectCadCounters.Mutation();var id=Database.Add(e).ObjectId;Children.Add(id);return id;}
  public IEnumerator<ObjectId> GetEnumerator(){if(Name==ModelSpace){ProjectCadCounters.ModelSpaceEnumerations++;throw new InvalidOperationException("ModelSpace enumeration forbidden");}ProjectCadCounters.DefinitionEnumerations++;foreach(var id in Children){ProjectCadCounters.PrimitiveVisits++;yield return id;}}
  IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
 }
 public sealed class BlockReference:Entity {
  public Point3d Position;public ObjectId BlockTableRecord;public Vector3d Normal=Vector3d.ZAxis;public Scale3d ScaleFactors=new Scale3d(1);public double Rotation;
  public bool IsDynamicBlock;public readonly List<ObjectId> AttributeCollection=new List<ObjectId>();
  public BlockReference(Point3d p,ObjectId b){Position=p;BlockTableRecord=b;}
  public Matrix3d BlockTransform {get{return Matrix3d.Placement(Position,ScaleFactors,Rotation);}}
 }
 public sealed class Line:Entity {public Point3d StartPoint,EndPoint;public Vector3d Normal=Vector3d.ZAxis;public double Thickness;public Line(Point3d start,Point3d end){StartPoint=start;EndPoint=end;} }
 public sealed class Circle:Entity {public Point3d Center;public double Radius;}
 public sealed class AttributeReference:Entity { }
}

namespace Autodesk.AutoCAD.GraphicsInterface {public struct FontDescriptor {public string TypeFace;public bool Bold,Italic;public int CharacterSet,PitchAndFamily;public FontDescriptor(string face,bool bold,bool italic,int charset,int pitch){TypeFace=face;Bold=bold;Italic=italic;CharacterSet=charset;PitchAndFamily=pitch;}}}
