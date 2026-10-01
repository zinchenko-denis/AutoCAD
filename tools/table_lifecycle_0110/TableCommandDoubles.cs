// Restricted command/table/host seams. Production command, guards, stores,
// producers and table models execute. No native UI, DWG render or CAD rollback.
using System;
using System.Collections;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace Autodesk.AutoCAD.Geometry {
 public partial struct Point3d { public static Point3d Origin {get{return new Point3d(0,0,0);}} }
 public partial struct Matrix3d {
  public static Matrix3d Identity {get{return new Matrix3d(Point3d.Origin,new Scale3d(1),0,Point3d.Origin);}}
  public double[] ToArray(){var zero=Transform(Point3d.Origin);var x=Transform(new Point3d(1,0,0));var y=Transform(new Point3d(0,1,0));var z=Transform(new Point3d(0,0,1));return new[]{x.X-zero.X,y.X-zero.X,z.X-zero.X,zero.X,x.Y-zero.Y,y.Y-zero.Y,z.Y-zero.Y,zero.Y,x.Z-zero.Z,y.Z-zero.Z,z.Z-zero.Z,zero.Z,0,0,0,1.0};}
 }
}
namespace Autodesk.AutoCAD.DatabaseServices {
 public sealed class TransactionManager {readonly Database db;public TransactionManager(Database value){db=value;} public Transaction StartTransaction(){return new Transaction(db);} }
 public partial class Database {
  public TransactionManager TransactionManager {get{return new TransactionManager(this);}}
  public string Filename="limited-pilot.dwg"; public ObjectId Tablestyle;
 }
 public static class SymbolUtilityServices {public static ObjectId GetBlockModelSpaceId(Database db){return ((BlockTable)db.BlockTableId.Item)[BlockTableRecord.ModelSpace];}}
 public partial class Xrecord {public static Action OnRead;partial void AfterRead(){if(OnRead!=null)OnRead();}}
 public sealed class CellRange {public int TopRow,LeftColumn,BottomRow,RightColumn;public static CellRange Create(Table table,int a,int b,int c,int d){return new CellRange{TopRow=a,LeftColumn=b,BottomRow=c,RightColumn=d};}}
 public sealed class TableCell {public string TextString="";public double TextHeight;}
 public sealed class TableRow {public double Height;}
 public sealed class TableColumn {public double Width;}
 public sealed class TableCells {internal TableCell[,] Values=new TableCell[0,0];public TableCell this[int row,int column]{get{return Values[row,column];}}}
 public partial class Table {
  public static readonly List<Table> Instances=new List<Table>();
  public ObjectId TableStyle;public Point3d Position;public readonly List<TableRow> Rows=new List<TableRow>();public readonly List<TableColumn> Columns=new List<TableColumn>();public readonly TableCells Cells=new TableCells();
  public readonly List<CellRange> Merges=new List<CellRange>();public int LayoutCalls;
  public Table(){Instances.Add(this);}
  public void SetSize(int rows,int columns){Rows.Clear();Columns.Clear();for(int r=0;r<rows;r++)Rows.Add(new TableRow());for(int c=0;c<columns;c++)Columns.Add(new TableColumn());Cells.Values=new TableCell[rows,columns];for(int r=0;r<rows;r++)for(int c=0;c<columns;c++)Cells.Values[r,c]=new TableCell();}
  public void MergeCells(CellRange range){Merges.Add(range);}public void UnmergeCells(CellRange range){Merges.Clear();}public void GenerateLayout(){LayoutCalls++;}
 }
 public sealed class HostApplicationServices {public static readonly HostApplicationServices Current=new HostApplicationServices();public Func<bool> Break;public bool UserBreak(){return Break!=null&&Break();}}
}
namespace Autodesk.AutoCAD.EditorInput {
 public enum PromptStatus {OK,Cancel,Error,None}
 public class PromptResult {public PromptStatus Status;public string StringResult;}
 public sealed partial class PromptKeywordOptions {public PromptKeywordOptions(string message,string keywords){}}
 public sealed class PromptSelectionOptions {public string MessageForAdding;}
 public sealed class SelectedObject {public ObjectId ObjectId;}
 public sealed class SelectionSet:List<SelectedObject> {}
 public sealed class PromptSelectionResult:PromptResult {public SelectionSet Value;}
 public sealed class PromptEntityOptions {public PromptEntityOptions(string message){}public void SetRejectMessage(string text){}public void AddAllowedClass(Type type,bool exact){}}
 public sealed class PromptEntityResult:PromptResult {public ObjectId ObjectId;}
 public sealed class PromptDoubleOptions {public double DefaultValue;public bool AllowNegative,AllowZero;public PromptDoubleOptions(string text){}}
 public sealed class PromptDoubleResult:PromptResult {public double Value;}
 public sealed class PromptPointResult:PromptResult {public Point3d Value;}
 public sealed partial class Editor {
  private Matrix3d ucs=Matrix3d.Identity;public int UcsReads;public Matrix3d CurrentUserCoordinateSystem {get{UcsReads++;return ucs;}set{ucs=value;}}
  public readonly Queue<PromptResult> Keywords=new Queue<PromptResult>();
  public PromptSelectionResult Selection;public PromptEntityResult Entity;public PromptDoubleResult Height;public PromptPointResult Point;
  public Action OnPoint,OnEntity,OnDouble;public int PointCalls;public string Messages="";
  public PromptResult GetKeywords(PromptKeywordOptions options){if(Keywords.Count==0)throw new InvalidOperationException("Unscripted keyword prompt");return Keywords.Dequeue();}
  public PromptSelectionResult GetSelection(PromptSelectionOptions options){return Selection;}
  public PromptEntityResult GetEntity(PromptEntityOptions options){if(OnEntity!=null)OnEntity();return Entity;}
  public PromptDoubleResult GetDouble(PromptDoubleOptions options){if(OnDouble!=null)OnDouble();return Height;}
  public PromptPointResult GetPoint(string message){PointCalls++;if(OnPoint!=null)OnPoint();return Point;}
  public void WriteMessage(string text){Messages+=text;}
 }
}
namespace Autodesk.AutoCAD.ApplicationServices {
 public sealed class Document {
  public readonly Database Database;public readonly Autodesk.AutoCAD.EditorInput.Editor Editor=new Autodesk.AutoCAD.EditorInput.Editor();public Action BeforeLock;
  public Document(Database db){Database=db;}public IDisposable LockDocument(){if(BeforeLock!=null)BeforeLock();return new Empty();}
  sealed class Empty:IDisposable {public void Dispose(){}}
 }
 public static partial class Application {
  public static Func<object,System.Windows.Forms.DialogResult> Dialog;public static int DialogCalls;
  public static System.Windows.Forms.DialogResult ShowModalDialog(object form){DialogCalls++;return Dialog(form);}
 }
}
namespace System.Windows.Forms {
 public enum DialogResult {None,OK,Cancel}
 public sealed class SaveFileDialog:IDisposable {
  public string Filter,FileName,InitialDirectory;public bool OverwritePrompt;public static Func<SaveFileDialog,DialogResult> Dialog;
  public DialogResult ShowDialog(){return Dialog(this);}public void Dispose(){}
 }
}
namespace AFacadesPlugin {
 internal sealed class QuantityTablePreview:IDisposable {
  internal readonly QuantityTableView Data;internal string UserNote;internal readonly bool AllowContinue;
  internal QuantityTablePreview(QuantityTableView data,string note,bool allowContinue=true){Data=data;UserNote=note;AllowContinue=allowContinue;}public void Dispose(){}
 }
 internal sealed class LayerPickForm:IDisposable {
  internal List<string> Selected=new List<string>();
  internal LayerPickForm(IDictionary<string,int> layers,ICollection<string> selected,string purpose,string instruction,string count,IDictionary<string,int> other=null){throw new InvalidOperationException("Layer-selection UI is outside this object-selection fixture");}public void Dispose(){}
 }
}
