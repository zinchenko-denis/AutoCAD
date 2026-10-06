// Executable CAD doubles for the unmodified production commands, adapter and
// coordinator. No native dynamic graph is created or evaluated in this test.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using AFramePlugin;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace Autodesk.AutoCAD.Runtime
{
    [Flags] public enum CommandFlags { Modal=1, UsePickSet=2 }
    [AttributeUsage(AttributeTargets.Assembly,AllowMultiple=true)] public class CommandClassAttribute:Attribute { public CommandClassAttribute(Type t){} }
    public class CommandMethodAttribute:Attribute { public CommandMethodAttribute(string s,CommandFlags f){} }
}
namespace Autodesk.AutoCAD.Geometry
{
    public struct Point3d { public double X,Y,Z; public Point3d(double x,double y,double z){X=x;Y=y;Z=z;} public static Point3d Origin {get{return new Point3d();}} public Point3d TransformBy(Matrix3d m){double c=Math.Cos(m.R),s=Math.Sin(m.R);return new Point3d(m.DX+c*X*m.XScale-s*Y*m.YScale,m.DY+s*X*m.XScale+c*Y*m.YScale,m.DZ+Z*m.ZScale);} public double DistanceTo(Point3d p){return Math.Sqrt((X-p.X)*(X-p.X)+(Y-p.Y)*(Y-p.Y)+(Z-p.Z)*(Z-p.Z));}public static Vector3d operator -(Point3d a,Point3d b){return new Vector3d(a.X-b.X,a.Y-b.Y,a.Z-b.Z);} }
    public struct Vector3d { public double X,Y,Z; public Vector3d(double x,double y,double z){X=x;Y=y;Z=z;} }
    public struct Scale3d { public double X,Y,Z; public Scale3d(double x,double y,double z){X=x;Y=y;Z=z;} }
    public struct Matrix3d { public double DX,DY,DZ,R,SX,SY,SZ;public double XScale{get{return SX==0?1:SX;}}public double YScale{get{return SY==0?1:SY;}}public double ZScale{get{return SZ==0?1:SZ;}}public Matrix3d Inverse(){double c=Math.Cos(R),s=Math.Sin(R);return new Matrix3d{R=-R,SX=1/XScale,SY=1/YScale,SZ=1/ZScale,DX=(-c*DX-s*DY)/XScale,DY=(s*DX-c*DY)/YScale,DZ=-DZ/ZScale};} public double[] ToArray(){return new[]{DX,DY,DZ,R,SX,SY,SZ};} public static Matrix3d Displacement(Vector3d v){return new Matrix3d{DX=v.X,DY=v.Y,DZ=v.Z};} }
    public struct Extents3d { public Point3d MinPoint,MaxPoint; }
}
namespace Autodesk.AutoCAD.DatabaseServices
{
    public enum OpenMode { ForRead,ForWrite }
    public enum UnitsValue { Undefined,Millimeters,Inches }
    public enum FileOpenMode { OpenForReadAndReadShare }
    public enum DuplicateRecordCloning { Ignore,Replace,MangleName }
    public enum DynamicBlockReferencePropertyUnitsType { NoUnits,Distance,Angular }
    public struct Handle { public int Value;public override string ToString(){return Value.ToString();} }
    public struct ObjectId { internal int Value; public ObjectId(int i){Value=i;} public bool IsNull {get{return Value==0;}} public Handle Handle {get{return new Handle{Value=Value};}} public static ObjectId Null {get{return new ObjectId();}} public static bool operator ==(ObjectId a,ObjectId b){return a.Value==b.Value;}public static bool operator !=(ObjectId a,ObjectId b){return a.Value!=b.Value;}public override bool Equals(object b){return b is ObjectId && this==(ObjectId)b;}public override int GetHashCode(){return Value;} }
    public class ObjectIdCollection:List<ObjectId> { public ObjectIdCollection(ObjectId[] v):base(v){} }
    public struct IdPair { public bool IsCloned; public ObjectId Value; }
    public class IdMapping:Dictionary<ObjectId,IdPair>{}
    public class DBObject:IDisposable { public Database Database; public ObjectId ObjectId; public bool IsErased; public virtual DBObject Copy(){return (DBObject)MemberwiseClone();}public void Dispose(){} }
    public class Entity:DBObject{public string Layer="0",Linetype="Continuous";public int ColorIndex=256;public bool Visible=true;}
    public class DBObjectCollection:List<DBObject>,IDisposable {public void Dispose(){} }
    public class Line:Entity {public Point3d StartPoint,EndPoint;}
    public class Polyline:Entity {public bool Closed;public int NumberOfVertices;public Point3d GetPoint3dAt(int i){return Point3d.Origin;}public double GetBulgeAt(int i){return 0;}public double GetStartWidthAt(int i){return 0;}public double GetEndWidthAt(int i){return 0;}}
    public class Circle:Entity {public Point3d Center;public double Radius;}
    public class Dimension:Entity {public double Measurement,Dimlfac=1,Dimrnd;public int Dimlunit=2,Dimdec=2;public bool Dimtol,Dimlim;public string DimensionText="",Dimpost="";public Point3d TextPosition;}
    public class RotatedDimension:Dimension {public Point3d XLine1Point,XLine2Point,DimLinePoint;public double Rotation;}
    public class AlignedDimension:Dimension {public Point3d XLine1Point,XLine2Point,DimLinePoint;}
    public class TypedValue { public int TypeCode; public object Value; public TypedValue(int c,object v){TypeCode=c;Value=v;} }
    public class ResultBuffer:IDisposable { readonly TypedValue[] Values; public ResultBuffer(params TypedValue[] v){Values=v;} public TypedValue[] AsArray(){return Values;} public void Dispose(){} }
    public class Xrecord:DBObject { public ResultBuffer Data; }
    public class DBDictionary:DBObject { public Dictionary<string,ObjectId> Values=new Dictionary<string,ObjectId>(); public bool Contains(string s){return Values.ContainsKey(s);} public ObjectId GetAt(string s){return Values[s];} }
    public class BlockTable:DBObject,IEnumerable<ObjectId> { public List<ObjectId> Ids=new List<ObjectId>(); public IEnumerator<ObjectId> GetEnumerator(){return Ids.GetEnumerator();} IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();} public override DBObject Copy(){var x=(BlockTable)base.Copy();x.Ids=new List<ObjectId>(Ids);return x;} }
    public class BlockTableRecord:DBObject,IEnumerable<ObjectId> {
        public string Name="node"; public bool IsDynamicBlock=true,IsAnonymous,IsLayout,IsFromExternalReference,IsDependent;
        public ObjectId ExtensionDictionary; public List<ObjectId> Ids=new List<ObjectId>();
        public ObjectId AppendEntity(Entity e){Database.Add(e);Ids.Add(e.ObjectId);return e.ObjectId;}
        public IEnumerator<ObjectId> GetEnumerator(){return Ids.GetEnumerator();} IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
        public override DBObject Copy(){var x=(BlockTableRecord)base.Copy();x.Ids=new List<ObjectId>(Ids);return x;}
    }
    public class AttributeCollection:List<ObjectId> { internal BlockReference Owner; public void AppendAttribute(AttributeReference a){Owner.Database.Add(a);Add(a.ObjectId);} }
    public class MText:Entity { public string Contents;public Point3d Location;public double TextHeight,Width,Rotation; }
    public class DBText:Entity {public string TextString="unchanged"; public Point3d Position,AlignmentPoint;public double Rotation,Height=5,WidthFactor=1;public bool IsMirroredInX,IsMirroredInY,IsDefaultAlignment;public double Oblique;public Vector3d Normal=new Vector3d(0,0,1);public int HorizontalMode,VerticalMode;public ObjectId TextStyleId;}
    public class AttributeDefinition:DBText { public bool Constant;public string Tag="USER"; }
    public class AttributeReference:DBText {
        public string Tag="USER";public bool Invisible,IsMTextAttribute;
        public MText MTextAttribute {get{return new MText{Contents=TextString};}}
        public void SetAttributeFromBlock(AttributeDefinition d,Matrix3d m){TextString=d.TextString;Position=new Point3d().TransformBy(m);}
    }
    public class PropertyState { public string Name; public object Value; public bool ReadOnly,Reject,Throw;public DynamicBlockReferencePropertyUnitsType UnitsType=DynamicBlockReferencePropertyUnitsType.Distance; public object[] Allowed=new object[0]; public PropertyState Copy(){return (PropertyState)MemberwiseClone();} }
    public class DynamicBlockReferenceProperty {
        readonly BlockReference Block; readonly PropertyState State; readonly object Snapshot;
        public DynamicBlockReferenceProperty(BlockReference b,PropertyState s){Block=b;State=s;Snapshot=s.Value;}
        public string PropertyName {get{return State.Name;}} public bool ReadOnly {get{return State.ReadOnly;}}
        public DynamicBlockReferencePropertyUnitsType UnitsType {get{return State.UnitsType;}}
        public object[] GetAllowedValues(){return State.Allowed;}
        public object Value {get{return Snapshot;}set{
            Block.Database.SetterCalls++; if(State.Throw)throw new InvalidOperationException("setter throw");
            if(State.ReadOnly || State.Reject)return;State.Value=value;
            if(Block.Coupled && State.Name==FrameNodeLibraryContract.Cladding)Block.Properties[0].Value=(double)Block.Properties[0].Value+1;
            if(Block.MoveAttribute && State.Name==FrameNodeLibraryContract.Cladding)((AttributeReference)Block.Database.Items[Block.AttributeCollection[0]]).Position=new Point3d(99,99,0);
        }}
    }
    public class BlockReference:Entity {
        public bool IsDynamicBlock=true,Coupled,MoveAttribute; public ObjectId DynamicBlockTableRecord; public Point3d Position;
        public double Rotation;public Scale3d ScaleFactors=new Scale3d(1,1,1);public Vector3d Normal=new Vector3d(0,0,1);
        public AttributeCollection AttributeCollection=new AttributeCollection(); public List<PropertyState> Properties=new List<PropertyState>();
        public BlockReference(Point3d p,ObjectId d){Position=p;DynamicBlockTableRecord=d;AttributeCollection.Owner=this;
            Properties.Add(new PropertyState{Name=FrameNodeLibraryContract.Insulation,Value=130.0});
            Properties.Add(new PropertyState{Name=FrameNodeLibraryContract.Cladding,Value=230.0});
            Properties.Add(new PropertyState{Name=FrameNodeLibraryContract.Variant,Value=FrameNodeLibraryContract.VariantA,Allowed=new object[]{FrameNodeLibraryContract.VariantA,FrameNodeLibraryContract.VariantB}});
        }
        public Matrix3d BlockTransform {get{return new Matrix3d{DX=Position.X,DY=Position.Y,DZ=Position.Z,R=Rotation,SX=ScaleFactors.X,SY=ScaleFactors.Y,SZ=ScaleFactors.Z};}}
        public ObjectId BlockTableRecord {get{
            // A distinct evaluated body: the contract must still be read from
            // DynamicBlockTableRecord. No copied marker is provided here.
            var body=new BlockTableRecord{IsAnonymous=true,IsDynamicBlock=false};int id=10000+ObjectId.Value*10;Database.Add(body,id);
            for(int i=0;i<2;i++){double value=Database.HostStaleLocalDimensions?(i==0?130:230):(double)Properties[i].Value;var d=new AlignedDimension{Measurement=value,Dimlfac=Database.HostDimlfac??1,Dimrnd=Database.HostDimrnd,Dimpost=Database.HostDimpost,Dimlunit=Database.HostDimlunit,Dimdec=Database.HostPrecision,Dimtol=Database.HostTolerance,DimensionText=Database.HostDimText};Database.Add(d,id+i+1);body.Ids.Add(d.ObjectId);}
            return body.ObjectId;
        }}
        public Extents3d GeometricExtents {get{return new Extents3d{MinPoint=Position,MaxPoint=new Point3d(Position.X+400,Position.Y+400,Position.Z)};}}
        public IEnumerable<DynamicBlockReferenceProperty> DynamicBlockReferencePropertyCollection {get{Database.CollectionReads++;return Properties.Select(x=>new DynamicBlockReferenceProperty(this,x)).ToList();}}
        public void RecordGraphicsModified(bool yes){}
        public void Explode(DBObjectCollection result){
            // A controlled evaluator DOUBLE supplies independent native outputs.
            // Fault switches prove that the actual HostCheck rejects false evidence.
            double insulation=(double)Properties[0].Value,cladding=(double)Properties[1].Value,s=ScaleFactors.X;
            var m=BlockTransform;double geometry=Database.HostStaleGeometry?230:cladding;
            result.Add(new Line{StartPoint=new Point3d(0,0,0).TransformBy(m),EndPoint=new Point3d(geometry,20,0).TransformBy(m)});
            result.Add(new DBText{TextString=Database.HostStaleMarks?"unchanged":(string)Properties[2].Value,Position=new Point3d(10,30,0).TransformBy(m),Height=5*s,Rotation=Rotation});
            result.Add(new DBText{TextString="hidden "+(string)Properties[2].Value,Visible=false});
            if(Database.HostUnknown)result.Add(new Entity());
            double first=Database.HostStaleDimensions?130:insulation,second=Database.HostStaleDimensions?230:cladding;
            var d1=new RotatedDimension{XLine1Point=new Point3d(0,0,0).TransformBy(m),XLine2Point=new Point3d(first,0,0).TransformBy(m),DimLinePoint=new Point3d(0,-20,0).TransformBy(m),Rotation=Rotation};
            var d2=new AlignedDimension{XLine1Point=new Point3d(0,0,0).TransformBy(m),XLine2Point=new Point3d(second,0,0).TransformBy(m),DimLinePoint=new Point3d(0,-40,0).TransformBy(m)};
            d1.Measurement=first*s;d2.Measurement=second*s;
            foreach(var d in new Dimension[]{d1,d2}){d.Dimlfac=Database.HostExplodeFactor ?? Database.HostDimlfac ?? 1/s;d.Dimrnd=Database.HostDimrnd;d.Dimpost=Database.HostDimpost;d.Dimlunit=Database.HostDimlunit;d.Dimdec=Database.HostPrecision;d.Dimtol=Database.HostTolerance;d.DimensionText=Database.HostDimText;result.Add(d);}
        }
        public void TransformBy(Matrix3d m){Position=Position.TransformBy(m);foreach(var id in AttributeCollection){var a=(AttributeReference)Database.Items[id];a.Position=a.Position.TransformBy(m);a.AlignmentPoint=a.AlignmentPoint.TransformBy(m);}}
        public override DBObject Copy(){var x=(BlockReference)base.Copy();x.Properties=Properties.Select(p=>p.Copy()).ToList();x.AttributeCollection=new AttributeCollection{Owner=x};x.AttributeCollection.AddRange(AttributeCollection);return x;}
    }
    public class Transaction:IDisposable {
        readonly Database Db; readonly Dictionary<ObjectId,DBObject> Before;bool Done;
        public Transaction(Database db){Db=db;Before=db.CopyItems();}
        public DBObject GetObject(ObjectId id,OpenMode mode){if(mode==OpenMode.ForWrite && Db.Items[id] is BlockTableRecord && ((BlockTableRecord)Db.Items[id]).IsDynamicBlock)Db.DefinitionWrites++;return Db.Items[id];}
        public void AddNewlyCreatedDBObject(DBObject x,bool add){}
        public void Commit(){Done=true;Db.Commits++;}
        public void Dispose(){if(!Done){Db.Items=Before;Db.Aborts++;}}
    }
    public class TransactionManager { readonly Database Db;public TransactionManager(Database db){Db=db;}public Transaction StartTransaction(){return new Transaction(Db);} }
    public class Database:IDisposable {
        public static Dictionary<string,Database> Files=new Dictionary<string,Database>();public static int FileReads;public static Database Target;
        public Dictionary<ObjectId,DBObject> Items=new Dictionary<ObjectId,DBObject>();public UnitsValue Insunits=UnitsValue.Millimeters;
        public ObjectId BlockTableId=new ObjectId(1),CurrentSpaceId=new ObjectId(2);public int Next=100,SetterCalls,CollectionReads,Commits,Aborts,DefinitionWrites,DeepClones,WblockClones;
        public bool DropCopyAttribute,BreakImportedMarker,HostStaleGeometry,HostStaleMarks,HostStaleDimensions,HostStaleLocalDimensions,HostUnknown,HostTolerance;public double? HostDimlfac,HostExplodeFactor;public double HostDimrnd;public int HostDimlunit=2,HostPrecision=8;public string HostDimpost="",HostDimText="";public DuplicateRecordCloning LastPolicy;public TransactionManager TransactionManager {get{return new TransactionManager(this);}}
        public Database(bool a=true,bool b=false){Add(new BlockTable(),1);Add(new BlockTableRecord{IsDynamicBlock=false,IsLayout=true},2);((BlockTable)Items[BlockTableId]).Ids.Add(CurrentSpaceId);}
        public void Dispose(){} public void CloseInput(bool b){}
        public void Add(DBObject x,int id=0){if(id==0)id=Next++;x.ObjectId=new ObjectId(id);x.Database=this;Items[x.ObjectId]=x;}
        public Dictionary<ObjectId,DBObject> CopyItems(){return Items.ToDictionary(p=>p.Key,p=>p.Value.Copy());}
        public void ReadDwgFile(string path,FileOpenMode mode,bool a,object b){FileReads++;var file=Files[path];Items=file.CopyItems();Next=file.Next;Insunits=file.Insunits;foreach(var v in Items.Values)v.Database=this;}
        public void DeepCloneObjects(ObjectIdCollection ids,ObjectId owner,IdMapping map,bool defer){DeepClones++;foreach(var id in ids){var original=(BlockReference)Items[id];var copy=(BlockReference)original.Copy();copy.AttributeCollection.Clear();Add(copy);((BlockTableRecord)Items[owner]).Ids.Add(copy.ObjectId);if(!DropCopyAttribute)foreach(var attr in original.AttributeCollection){var a=(AttributeReference)Items[attr].Copy();Add(a);copy.AttributeCollection.Add(a.ObjectId);}map[id]=new IdPair{IsCloned=true,Value=copy.ObjectId};}}
        public void WblockCloneObjects(ObjectIdCollection ids,ObjectId owner,IdMapping map,DuplicateRecordCloning policy,bool defer){
            var target=Target;target.WblockClones++;target.LastPolicy=policy;
            foreach(var id in ids){var source=(BlockTableRecord)Items[id];var copy=(BlockTableRecord)source.Copy();copy.Name="$0$"+source.Name;target.Add(copy);
                var dict=(DBDictionary)Items[source.ExtensionDictionary];var old=Items[dict.GetAt(FrameNodeLibraryContract.Key)];var rec=old.Copy();target.Add(rec);var clonedDict=new DBDictionary();clonedDict.Values[FrameNodeLibraryContract.Key]=rec.ObjectId;target.Add(clonedDict);copy.ExtensionDictionary=clonedDict.ObjectId;
                if(target.BreakImportedMarker)copy.IsDynamicBlock=false;((BlockTable)target.Items[target.BlockTableId]).Ids.Add(copy.ObjectId);map[id]=new IdPair{IsCloned=true,Value=copy.ObjectId};}
        }
    }
}
namespace Autodesk.AutoCAD.EditorInput
{
    public enum PromptStatus { OK,Cancel }
    public class PromptResult { public PromptStatus Status;public string StringResult; }
    public class PromptDoubleResult:PromptResult { public double Value; }
    public class PromptPointResult:PromptResult { public Point3d Value; }
    public class PromptEntityResult:PromptResult { public ObjectId ObjectId; }
    public class SelectionSet { public ObjectId[] Ids;public ObjectId[] GetObjectIds(){return Ids;} }
    public class PromptSelectionResult:PromptResult { public SelectionSet Value; }
    public class Keywords { public void Add(string x){}public void Add(string a,string b,string c){} }
    public class PromptKeywordOptions { public Keywords Keywords=new Keywords();public bool AllowNone;public PromptKeywordOptions(string s){} }
    public class PromptSelectionOptions { public string MessageForAdding; }
    public class PromptOpenFileOptions { public string Filter;public PromptOpenFileOptions(string s){} }
    public class PromptDoubleOptions { public bool AllowNegative,AllowZero,AllowNone;public PromptDoubleOptions(string s){} }
    public class Editor {
        public Queue<object> Replies=new Queue<object>();public string Messages="",Calls="";public Matrix3d CurrentUserCoordinateSystem;public Action<string> ReplyHook;
        public void WriteMessage(string s){Messages+=s;}public void Regen(){}
        T Get<T>(string kind){Calls+=kind+";";if(ReplyHook!=null)ReplyHook(kind);return (T)Replies.Dequeue();}
        public PromptDoubleResult GetDouble(PromptDoubleOptions x){return Get<PromptDoubleResult>("double");}
        public PromptResult GetKeywords(PromptKeywordOptions x){return Get<PromptResult>("keyword");}
        public PromptSelectionResult GetSelection(PromptSelectionOptions x){return Get<PromptSelectionResult>("selection");}
        public PromptPointResult GetPoint(string x){return Get<PromptPointResult>("point");}
        public PromptEntityResult GetEntity(string x){return Get<PromptEntityResult>("entity");}
        public PromptResult GetFileNameForOpen(PromptOpenFileOptions x){return Get<PromptResult>("file");}
    }
}
namespace Autodesk.AutoCAD.ApplicationServices
{
    public class Document { public Editor Editor=new Editor();public Database Database;public IDisposable LockDocument(){return new Lock();}class Lock:IDisposable{public void Dispose(){}} }
    public class Documents { public Document MdiActiveDocument; }
    public static class Application { public static Documents DocumentManager=new Documents(); }
}

class NodeLibraryProbe
{
    static int Checks;
    static FrameNodeLibraryValues A=new FrameNodeLibraryValues(130,230,FrameNodeLibraryContract.VariantA);
    static FrameNodeLibraryValues B=new FrameNodeLibraryValues(180,330,FrameNodeLibraryContract.VariantB);
    static void Need(bool ok,string reason){Checks++;if(!ok)throw new Exception(reason);}
    static ObjectId O(int x){return new ObjectId(x);}
    static BlockReference Block(Database db,int id){return (BlockReference)db.Items[O(id)];}
    static Database New()
    {
        var db=new Database();
        var marker=new Xrecord{Data=new ResultBuffer(new TypedValue(1,FrameNodeLibraryContract.NodeId),new TypedValue(90,1),new TypedValue(1,FrameNodeLibraryContract.Insulation),new TypedValue(1,FrameNodeLibraryContract.Cladding),new TypedValue(1,FrameNodeLibraryContract.Variant),new TypedValue(1,FrameNodeLibraryContract.VariantA),new TypedValue(1,FrameNodeLibraryContract.VariantB))};db.Add(marker,3);
        var dict=new DBDictionary();dict.Values[FrameNodeLibraryContract.Key]=marker.ObjectId;db.Add(dict,4);
        var def=new BlockTableRecord{ExtensionDictionary=dict.ObjectId};db.Add(def,5);((BlockTable)db.Items[db.BlockTableId]).Ids.Add(def.ObjectId);
        for(int i=10;i<15;i++){
            var block=new BlockReference(new Point3d(i*1000,50,10),def.ObjectId);db.Add(block,i);
            var attr=new AttributeReference{TextString="project-"+i,Position=block.Position,AlignmentPoint=block.Position,Rotation=.4};db.Add(attr,i+10);block.AttributeCollection.Add(attr.ObjectId);
        }
        return db;
    }
    static string Digest(Database db,int id){using(var tr=new FrameNodeLibraryCad(db)){var s=tr.Read(O(id));return s.Definition+"|"+s.Placement+"|"+s.Attributes+"|"+s.Values.Insulation+"|"+s.Values.Cladding+"|"+s.Values.Variant;}}
    static int Apply(Database db,FrameNodeLibraryValues values,params int[] ids){using(var tr=new FrameNodeLibraryCad(db))return FrameNodeLibraryEdit.Apply(tr,ids.Select(x=>(object)O(x)),values);}
    static void Reject(Database db,Action corrupt,string label)
    {
        corrupt();var old=Block(db,10).Properties.Select(p=>p.Value).ToArray();bool failed=false;
        try{Apply(db,B,10,11,12);}catch(InvalidOperationException){failed=true;}
        Need(failed,label+" accepted");Need(old.SequenceEqual(Block(db,10).Properties.Select(p=>p.Value)),label+" left partial state");
    }
    static Autodesk.AutoCAD.ApplicationServices.Document Doc(Database db){var d=new Autodesk.AutoCAD.ApplicationServices.Document{Database=db};Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument=d;Database.Target=db;return d;}
    static void EditReplies(Editor ed,FrameNodeLibraryValues values,params int[] ids){ed.Replies.Enqueue(new PromptDoubleResult{Value=values.Insulation});ed.Replies.Enqueue(new PromptDoubleResult{Value=values.Cladding});ed.Replies.Enqueue(new PromptResult{StringResult=values.Variant==FrameNodeLibraryContract.VariantA?"A":"B"});ed.Replies.Enqueue(new PromptSelectionResult{Value=new SelectionSet{Ids=ids.Select(O).ToArray()}});}
    static bool HostCheck(Database db)
    {
        var before=Enumerable.Range(10,5).Select(id=>Digest(db,id)).ToArray();var doc=Doc(db);
        if(db.Insunits==UnitsValue.Undefined)doc.Editor.Replies.Enqueue(new PromptResult{StringResult="Yes"});
        doc.Editor.Replies.Enqueue(new PromptSelectionResult{Value=new SelectionSet{Ids=new[]{O(14),O(12),O(10),O(13),O(11)}}});
        doc.Editor.Replies.Enqueue(new PromptDoubleResult{Value=B.Insulation});doc.Editor.Replies.Enqueue(new PromptDoubleResult{Value=B.Cladding});
        new FrameNodeLibraryHostCheck().Test();
        Need(db.Commits==0 && Enumerable.Range(10,5).Select(id=>Digest(db,id)).SequenceEqual(before),"HostCheck committed or left changes");
        return doc.Editor.Messages.Contains("ATFNODETEST PASS:");
    }
    static void AdditionalCases()
    {
        var db=New();var def=(BlockTableRecord)db.Items[O(5)].Copy();db.Add(def,30);
        Block(db,11).DynamicBlockTableRecord=O(30);Block(db,13).DynamicBlockTableRecord=O(30);
        var before=Enumerable.Range(10,5).Select(id=>Digest(db,id)).ToArray();
        Need(Apply(db,B,10,11,12)==3,"compatible imports with different root definitions cannot be edited together");
        Need(Block(db,11).DynamicBlockTableRecord==O(30) && Digest(db,13)==before[3] && Digest(db,14)==before[4],"mixed definitions rebound/unselected modified");
        foreach(var damage in new Action<TypedValue[]>[]{v=>v[0].Value="native_rect_fixture_v1",v=>v[1].TypeCode=1070,v=>v[2].Value=FrameNodeLibraryContract.Cladding,v=>v[5].Value=null}){
            db=New();Reject(db,()=>damage(((Xrecord)db.Items[O(3)]).Data.AsArray()),"corrupted typed marker");Need(db.SetterCalls==0,"corrupted marker caused early writes");
        }
        db=New();db.Add(new Entity(),30);bool failed=false;try{Apply(db,B,10,30,12);}catch(InvalidOperationException){failed=true;}Need(failed && db.SetterCalls==0,"selection containing plain entity partially edited");
        foreach(string answer in new[]{"No","Cancel"}){
            db=New();db.Insunits=UnitsValue.Undefined;var d=Doc(db);d.Editor.Replies.Enqueue(new PromptResult{StringResult=answer,Status=answer=="Cancel"?PromptStatus.Cancel:PromptStatus.OK});new FrameNodeLibraryCommand().Edit();Need(db.SetterCalls==0 && db.Commits==0 && d.Editor.Calls=="keyword;","unitless decline/cancel continued");
        }
        db=New();var doc=Doc(db);EditReplies(doc.Editor,B,10,11,12);doc.Editor.ReplyHook=k=>{if(k=="selection")db.Insunits=UnitsValue.Inches;};new FrameNodeLibraryCommand().Edit();Need(db.SetterCalls==0 && db.Commits==0,"unit change during prompts accepted");
        db=New();doc=Doc(db);doc.Editor.Replies.Enqueue(new PromptEntityResult{ObjectId=O(10)});doc.Editor.Replies.Enqueue(new PromptPointResult{Status=PromptStatus.Cancel});new FrameNodeLibraryCommand().Demo();Need(db.DeepClones==0 && db.Commits==0,"demo cancellation created hidden copies");
        db=New();doc=Doc(db);Database.FileReads=0;doc.Editor.Replies.Enqueue(new PromptResult{Status=PromptStatus.Cancel});new FrameNodeLibraryCommand().Import();Need(Database.FileReads==0 && db.WblockClones==0,"file cancellation read/imported library");
        foreach(var units in new[]{UnitsValue.Inches,UnitsValue.Undefined}){
            Database.Files["library.dwg"]=New();Database.Files["library.dwg"].Insunits=units;db=New();doc=Doc(db);doc.Editor.Replies.Enqueue(new PromptResult{StringResult="library.dwg"});doc.Editor.Replies.Enqueue(new PromptPointResult());new FrameNodeLibraryCommand().Import();Need(db.WblockClones==0 && db.Commits==0,"non-mm library accepted");
        }
        Database.Files["library.dwg"]=New();db=New();doc=Doc(db);db.Insunits=UnitsValue.Undefined;doc.Editor.Replies.Enqueue(new PromptResult{StringResult="Yes"});doc.Editor.Replies.Enqueue(new PromptResult{StringResult="library.dwg"});doc.Editor.Replies.Enqueue(new PromptPointResult());new FrameNodeLibraryCommand().Import();Need(db.Commits==1 && db.WblockClones==1 && db.Insunits==UnitsValue.Undefined,"confirmed unitless import rejected/units mutated");
        db=New();doc=Doc(db);doc.Editor.Replies.Enqueue(new PromptResult{StringResult="library.dwg"});doc.Editor.Replies.Enqueue(new PromptPointResult());doc.Editor.ReplyHook=k=>{if(k=="point")doc.Editor.CurrentUserCoordinateSystem=new Matrix3d{DX=100};};new FrameNodeLibraryCommand().Import();Need(db.WblockClones==0 && db.Commits==0,"UCS change while picking point accepted");
        // COPY preserves fractional coordinates, rotation, scale and multiline
        // text. Transforms in this double now perform actual XY matrix algebra.
        db=New();Block(db,10).Position=new Point3d(10.25,-7.75,3.5);Block(db,10).Rotation=Math.PI/3;Block(db,10).ScaleFactors=new Scale3d(.5,.5,.5);((AttributeReference)db.Items[O(20)]).IsMTextAttribute=true;before=Enumerable.Range(10,5).Select(id=>Digest(db,id)).ToArray();doc=Doc(db);doc.Editor.Replies.Enqueue(new PromptEntityResult{ObjectId=O(10)});doc.Editor.Replies.Enqueue(new PromptPointResult{Value=new Point3d(200.125,500.875,4.25)});new FrameNodeLibraryCommand().Demo();Need(db.DeepClones==5 && db.Commits==1 && Enumerable.Range(10,5).Select(id=>Digest(db,id)).SequenceEqual(before),"transformed COPY failed or changed source");
        foreach(double scale in new[]{1.0,2.0,.5}){
            db=New();for(int id=10;id<15;id++){Block(db,id).Rotation=(id-9)*.17;Block(db,id).ScaleFactors=new Scale3d(scale,scale,scale);}
            Need(HostCheck(db),"ordinary transformed HostCheck rejected for scale "+scale);
        }
        foreach(var damage in new Action<Database>[] {d=>d.HostStaleGeometry=true,d=>d.HostStaleMarks=true,d=>d.HostStaleDimensions=true,d=>d.HostStaleLocalDimensions=true,d=>d.HostUnknown=true,d=>d.HostDimText="999"}){
            db=New();damage(db);Need(!HostCheck(db),"HostCheck falsely accepted stale/unknown output");
        }
        var falseAccepts=new List<string>();
        foreach(var name in new[]{"DIMRND","DIMPOST","DIMLUNIT","scaled DIMLFAC"}){
            db=New();if(name=="DIMRND")db.HostDimrnd=100;if(name=="DIMPOST")db.HostDimpost="999";if(name=="DIMLUNIT")db.HostDimlunit=4;
            if(name=="scaled DIMLFAC"){db.HostDimlfac=2;for(int id=10;id<15;id++)Block(db,id).ScaleFactors=new Scale3d(2,2,2);}
            if(HostCheck(db))falseAccepts.Add(name);
        }
        Need(falseAccepts.Count==0,"HostCheck falsely reports verified captions: "+String.Join(", ",falseAccepts));
        db=New();for(int id=10;id<15;id++){Block(db,id).Properties[0].Value=130.125;Block(db,id).Properties[1].Value=230.875;}
        Need(HostCheck(db),"decimal dimensions rejected with sufficient label precision");
        db.HostPrecision=2;Need(!HostCheck(db),"fractional value truncated in label accepted");
        db=New();db.HostTolerance=true;Need(!HostCheck(db),"unrequested tolerance notation accepted");
        db=New();db.Insunits=UnitsValue.Inches;Need(!HostCheck(db),"HostCheck labels an inch drawing as millimetres");
        db=New();db.Insunits=UnitsValue.Undefined;Need(HostCheck(db),"HostCheck rejects an explicitly confirmed unitless drawing");
        db=New();db.HostExplodeFactor=1;for(int id=10;id<15;id++)Block(db,id).ScaleFactors=new Scale3d(2,2,2);Need(HostCheck(db),"transient Explode override was mistaken for original local caption");
    }
    static int Main()
    {
        var db=New();Block(db,10).Rotation=.7;Block(db,10).ScaleFactors=new Scale3d(2,2,2);
        var before=Enumerable.Range(10,5).Select(id=>Digest(db,id)).ToArray();
        Need(Apply(db,B,10,12,14,14)==3,"3 of 5 selection or dedup failed");
        Need(Digest(db,11)==before[1] && Digest(db,13)==before[3],"unselected changed");
        Need(db.SetterCalls==9 && db.DefinitionWrites==0,"wrong setter count or source BTR rewritten");
        int calls=db.SetterCalls;Need(Apply(db,B,10,12,14)==0 && db.SetterCalls==calls,"repeat accumulated writes");
        Need(Apply(db,A,10,12,14)==3,"reverse transition failed");
        Need(Enumerable.Range(10,5).Select(id=>Digest(db,id)).SequenceEqual(before),"reverse drifted position, rotation, scale or attributes");
        Need(db.CollectionReads>30,"readback reused cached property wrappers");
        db=New();Reject(db,()=>Block(db,12).Properties[1].Reject=true,"silent rejection");Need(db.Commits==0,"silent rejection committed");
        db=New();Reject(db,()=>Block(db,11).Properties[1].Throw=true,"setter exception");
        db=New();Reject(db,()=>Block(db,12).Coupled=true,"coupled final pair");
        db=New();Reject(db,()=>Block(db,12).MoveAttribute=true,"attribute displacement");
        foreach(var mutate in new Action<BlockReference>[] {b=>b.ScaleFactors=new Scale3d(-1,1,1),b=>b.ScaleFactors=new Scale3d(1,2,1),b=>b.ScaleFactors=new Scale3d(1e-8,2e-8,1e-8),b=>b.Normal=new Vector3d(0,1,0),b=>b.Properties[0].ReadOnly=true,b=>b.Properties[1].Value=230,b=>b.Properties[0].UnitsType=DynamicBlockReferencePropertyUnitsType.Angular,b=>b.Properties[2].Allowed=new object[]{FrameNodeLibraryContract.VariantA},b=>b.Properties.Add(b.Properties[0].Copy())}){
            db=New();Reject(db,()=>mutate(Block(db,12)),"incompatible selection");Need(db.SetterCalls==0,"prevalidation wrote before finding invalid third block");
        }
        db=New();var foreign=new BlockTableRecord{Name="node"};db.Add(foreign,30);Reject(db,()=>Block(db,12).DynamicBlockTableRecord=O(30),"foreign same-name definition");Need(db.SetterCalls==0,"foreign prevalidation was late");
        db=New();Reject(db,()=>((BlockTableRecord)db.Items[O(5)]).IsAnonymous=true,"anonymous root");
        db=New();Reject(db,()=>((Xrecord)db.Items[O(3)]).Data.AsArray()[1].Value=2,"marker version");
        db=New();Reject(db,()=>Block(db,12).Properties[0].Allowed=new object[]{130.0},"disallowed target");Need(db.SetterCalls==0,"allowed set prevalidation was late");
        foreach(double invalid in new[]{double.NaN,double.PositiveInfinity,-1,0}){db=New();bool failed=false;try{Apply(db,new FrameNodeLibraryValues(invalid,330,B.Variant),10);}catch(InvalidOperationException){failed=true;}Need(failed && db.SetterCalls==0,"nonfinite/nonpositive accepted");}
        db=New();var doc=Doc(db);EditReplies(doc.Editor,B,10,11,12);new FrameNodeLibraryCommand().Edit();
        Need(db.Commits==1 && db.SetterCalls==9 && doc.Editor.Calls=="double;double;keyword;selection;","actual command path/order failed");
        db=New();doc=Doc(db);EditReplies(doc.Editor,B,10,11,12);doc.Editor.Replies.Dequeue();doc.Editor.Replies.Clear();doc.Editor.Replies.Enqueue(new PromptDoubleResult{Status=PromptStatus.Cancel});new FrameNodeLibraryCommand().Edit();Need(db.SetterCalls==0 && db.Commits==0,"parameter cancellation wrote");
        db=New();doc=Doc(db);EditReplies(doc.Editor,B,10,11,12);var replies=doc.Editor.Replies.ToArray();doc.Editor.Replies.Clear();foreach(var r in replies.Take(3))doc.Editor.Replies.Enqueue(r);doc.Editor.Replies.Enqueue(new PromptSelectionResult{Status=PromptStatus.Cancel});new FrameNodeLibraryCommand().Edit();Need(db.SetterCalls==0 && db.Commits==0,"selection cancellation wrote");
        db=New();doc=Doc(db);db.Insunits=UnitsValue.Undefined;doc.Editor.Replies.Enqueue(new PromptResult{StringResult="Yes"});EditReplies(doc.Editor,B,10,11,12);new FrameNodeLibraryCommand().Edit();Need(db.Commits==1 && db.Insunits==UnitsValue.Undefined,"unitless confirmation failed or changed units");
        db=New();doc=Doc(db);db.Insunits=UnitsValue.Inches;new FrameNodeLibraryCommand().Edit();Need(db.Commits==0 && doc.Editor.Calls=="","inches interpreted as mm");
        db=New();doc=Doc(db);before=Enumerable.Range(10,5).Select(id=>Digest(db,id)).ToArray();doc.Editor.Replies.Enqueue(new PromptEntityResult{ObjectId=O(10)});doc.Editor.Replies.Enqueue(new PromptPointResult{Value=new Point3d(0,5000,0)});new FrameNodeLibraryCommand().Demo();
        var copies=db.Items.Values.OfType<BlockReference>().Where(b=>b.ObjectId.Value>=100).Select(b=>b.ObjectId.Value).OrderBy(x=>x).ToArray();
        Need(copies.Length==5 && db.DeepClones==5,"actual demo did not native-clone five refs");Need(Enumerable.Range(10,5).Select(id=>Digest(db,id)).SequenceEqual(before),"demo mutated original(s)");
        var last=Digest(db,copies[4]);Need(Apply(db,B,copies.Take(3).ToArray())==3 && Digest(db,copies[4])==last && Digest(db,10)==before[0],"editing copied refs affected original/unselected");
        db=New();doc=Doc(db);db.DropCopyAttribute=true;doc.Editor.Replies.Enqueue(new PromptEntityResult{ObjectId=O(10)});doc.Editor.Replies.Enqueue(new PromptPointResult{Value=new Point3d(0,5000,0)});new FrameNodeLibraryCommand().Demo();Need(db.Items.Values.OfType<BlockReference>().Count()==5 && db.Commits==0,"copy attribute loss left partial demo");
        db=New();doc=Doc(db);Database.FileReads=0;doc.Editor.Replies.Enqueue(new PromptResult{StringResult="library.dwg"});doc.Editor.Replies.Enqueue(new PromptPointResult{Status=PromptStatus.Cancel});new FrameNodeLibraryCommand().Import();Need(Database.FileReads==0 && db.WblockClones==0,"cancelled import read/mutated source");
        Database.Files["library.dwg"]=New();db=New();doc=Doc(db);int oldCount=db.Items.Count;var oldDef=db.Items[O(5)];doc.Editor.Replies.Enqueue(new PromptResult{StringResult="library.dwg"});doc.Editor.Replies.Enqueue(new PromptPointResult{Value=new Point3d(0,0,0)});new FrameNodeLibraryCommand().Import();Need(db.Commits==1 && db.WblockClones==1 && db.LastPolicy==DuplicateRecordCloning.MangleName,"import clone policy/commit failed: "+doc.Editor.Messages);Need(db.Items.Count>oldCount && db.Items[O(5)]==oldDef,"import replaced existing definition");
        db=New();doc=Doc(db);oldCount=db.Items.Count;db.BreakImportedMarker=true;doc.Editor.Replies.Enqueue(new PromptResult{StringResult="library.dwg"});doc.Editor.Replies.Enqueue(new PromptPointResult());new FrameNodeLibraryCommand().Import();Need(db.Items.Count==oldCount && db.WblockClones==1 && db.Commits==0,"post-clone failure left imported definitions");
        Database.Files["library.dwg"]=New();((Xrecord)Database.Files["library.dwg"].Items[O(3)]).Data.AsArray()[1].Value=99;db=New();doc=Doc(db);oldCount=db.Items.Count;doc.Editor.Replies.Enqueue(new PromptResult{StringResult="library.dwg"});doc.Editor.Replies.Enqueue(new PromptPointResult());new FrameNodeLibraryCommand().Import();Need(db.Items.Count==oldCount && db.WblockClones==0 && db.Commits==0,"invalid source imported partially");
        AdditionalCases();
        Console.WriteLine("Node library: "+Checks+" PASS; actual commands/CAD adapter/coordinator/HostCheck with API doubles. Native geometry, grips, Undo and save/open require AutoCAD 2024.");return 0;
    }
}
