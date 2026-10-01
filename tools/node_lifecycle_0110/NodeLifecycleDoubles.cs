// Only members read/written by actual node command/store/renderer. No native
// rendering, remapping, rollback, save/open or AutoCAD transaction semantics.
using System;
using System.Collections;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Autodesk.AutoCAD.Geometry {
 public partial struct Point3d {
  public static bool operator ==(Point3d a,Point3d b){return a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;} public static bool operator !=(Point3d a,Point3d b){return !(a==b);}
  public override bool Equals(object o){return o is Point3d&&this==(Point3d)o;} public override int GetHashCode(){return X.GetHashCode()^Y.GetHashCode()^Z.GetHashCode();}
 }
 public partial struct Vector3d {
  public double DotProduct(Vector3d v){return X*v.X+Y*v.Y+Z*v.Z;} public Vector3d CrossProduct(Vector3d v){return new Vector3d(Y*v.Z-Z*v.Y,Z*v.X-X*v.Z,X*v.Y-Y*v.X);}
  public static bool operator ==(Vector3d a,Vector3d b){return a.X==b.X&&a.Y==b.Y&&a.Z==b.Z;} public static bool operator !=(Vector3d a,Vector3d b){return !(a==b);}
  public override bool Equals(object o){return o is Vector3d&&this==(Vector3d)o;} public override int GetHashCode(){return X.GetHashCode()^Y.GetHashCode()^Z.GetHashCode();}
 }
 public struct CoordinateSystem3d {public Point3d Origin;public Vector3d Xaxis,Yaxis,Zaxis;}
 public partial struct Matrix3d {
  public CoordinateSystem3d CoordinateSystem3d {get{var a=ToArray();return new CoordinateSystem3d{Origin=new Point3d(a[3],a[7],a[11]),Xaxis=new Vector3d(a[0],a[4],a[8]),Yaxis=new Vector3d(a[1],a[5],a[9]),Zaxis=new Vector3d(a[2],a[6],a[10])};}}
 }
}
namespace Autodesk.AutoCAD.Colors {
 public sealed partial class Color {
  public System.Drawing.Color ColorValue {get{return System.Drawing.Color.FromArgb(255,Red,Green,Blue);}}
  public static Color FromColorIndex(ColorMethod method,short index){return new Color{ColorMethod=method,ColorIndex=index};}
 }
 public struct Transparency {public byte Alpha;public bool IsByLayer,IsByBlock;public Transparency(byte alpha){Alpha=alpha;IsByLayer=IsByBlock=false;}}
}
namespace Autodesk.AutoCAD.GraphicsInterface {
 public struct FontDescriptor {public string TypeFace;public bool Bold,Italic;public int CharacterSet,PitchAndFamily;public FontDescriptor(string face,bool bold,bool italic,int charset,int pitch){TypeFace=face;Bold=bold;Italic=italic;CharacterSet=charset;PitchAndFamily=pitch;}}
}
namespace Autodesk.AutoCAD.DatabaseServices {
 public enum UnitsValue {Undefined=0,Millimeters=4}
 public enum LineWeight {ByLayer=-1,LineWeight025=25}
 public enum AttachmentPoint {TopLeft=1}
 public enum LineSpacingStyle {AtLeast=1}
 public partial class Database {
  public UnitsValue Insunits=UnitsValue.Millimeters; private ObjectId layers,styles;
  public ObjectId CurrentSpaceId {get{return ((BlockTable)BlockTableId.Item)[BlockTableRecord.ModelSpace];}}
  public ObjectId LayerTableId {get{if(layers.IsNull)layers=Add(new LayerTable()).ObjectId;return layers;}}
  public ObjectId TextStyleTableId {get{if(styles.IsNull)styles=Add(new TextStyleTable()).ObjectId;return styles;}}
 }
 public partial class Entity {
  public string Linetype="ByLayer"; public double LinetypeScale=1; public LineWeight LineWeight=LineWeight.ByLayer;
  public Autodesk.AutoCAD.Colors.Transparency Transparency=new Autodesk.AutoCAD.Colors.Transparency(0);
 }
 public struct DBDictionaryEntry {public string Key;public ObjectId Value;}
 public partial class DBDictionary:IEnumerable<DBDictionaryEntry> {
  public IEnumerator<DBDictionaryEntry> GetEnumerator(){foreach(var pair in Items)yield return new DBDictionaryEntry{Key=pair.Key,Value=pair.Value};} IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
 }
 public partial class LayerTableRecord {
  public string Name;public bool IsLocked,IsOff,IsFrozen;public LineWeight LineWeight=LineWeight.ByLayer;public ObjectId LinetypeObjectId;
  public Autodesk.AutoCAD.Colors.Transparency Transparency=new Autodesk.AutoCAD.Colors.Transparency(0);
 }
 public sealed class LayerTable:DBObject {
  public readonly Dictionary<string,ObjectId> Items=new Dictionary<string,ObjectId>(); public bool Has(string name){return Items.ContainsKey(name);}public ObjectId this[string name]{get{return Items[name];}}
  public ObjectId Add(LayerTableRecord value){return Items[value.Name]=Database.Add(value).ObjectId;}
 }
 public sealed class TextStyleTable:DBObject {public readonly Dictionary<string,ObjectId> Items=new Dictionary<string,ObjectId>();public ObjectId Add(TextStyleTableRecord value){return Items[value.Name]=Database.Add(value).ObjectId;}}
 public sealed class TextStyleTableRecord:DBObject {public string Name,FileName,BigFontFileName="";public double TextSize,XScale=1,ObliquingAngle;public bool IsVertical;public Autodesk.AutoCAD.GraphicsInterface.FontDescriptor Font;}
 public partial class BlockTable {public ObjectId Add(BlockTableRecord value){return Items[value.Name]=Database.Add(value).ObjectId;}}
 public partial class BlockTableRecord {public UnitsValue Units;public bool IsFromExternalReference;}
 public partial class BlockReference {public BlockReference(){}public BlockReference(Point3d position,ObjectId definition){Position=position;BlockTableRecord=definition;}}
 public partial class Line {public Line(){}public Line(Point3d start,Point3d end){StartPoint=start;EndPoint=end;}}
 public partial class MText {
  public double LineSpacingFactor=1,BackgroundScaleFactor=1.5,ColumnWidth,ColumnGutterWidth;public int ColumnType,ColumnCount;
  public bool BackgroundFill,UseBackgroundColor,ShowBorders;public AttachmentPoint Attachment=AttachmentPoint.TopLeft;public ObjectId TextStyleId;public LineSpacingStyle LineSpacingStyle=LineSpacingStyle.AtLeast;
  public Autodesk.AutoCAD.Colors.Color BackgroundFillColor=Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci,7);
 }
}
namespace Autodesk.AutoCAD.Runtime {
 [Flags]public enum CommandFlags {Modal=1}
 [AttributeUsage(AttributeTargets.Assembly)]public sealed class CommandClassAttribute:Attribute {public CommandClassAttribute(Type type){}}
 [AttributeUsage(AttributeTargets.Method)]public sealed class CommandMethodAttribute:Attribute {public CommandMethodAttribute(string name,CommandFlags flags){}}
}
namespace Autodesk.AutoCAD.EditorInput {
 public sealed class KeywordCollection {public void Add(string global,string local,string display){}}
 public sealed partial class PromptKeywordOptions {public bool AllowNone;public readonly KeywordCollection Keywords=new KeywordCollection();public PromptKeywordOptions(string message){}}
 public sealed class PromptPointOptions {public PromptPointOptions(string message){}}
 public sealed partial class Editor {public PromptPointResult GetPoint(PromptPointOptions options){return GetPoint("node insertion");}}
}
namespace Autodesk.AutoCAD.ApplicationServices {
 public sealed class Documents {public Document MdiActiveDocument;}
 public static partial class Application {public static readonly Documents DocumentManager=new Documents();}
}
namespace AFramePlugin {
 public sealed class FrameNodeForm:IDisposable {public FrameNodeGeometryInput Result;public FrameNodeForm(FrameSolutionSelection selection,FrameParameterContext context){ }public void Dispose(){}}
}
