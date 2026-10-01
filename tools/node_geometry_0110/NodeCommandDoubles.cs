// Prompt, host and verified-zone seams only. Product command/store/renderer execute.
using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AFramePlugin;
namespace Autodesk.AutoCAD.Runtime {
 [Flags] public enum CommandFlags { Modal=1,UsePickSet=2 }
 [AttributeUsage(AttributeTargets.Assembly)] public sealed class CommandClassAttribute:Attribute {public CommandClassAttribute(Type type){} }
 [AttributeUsage(AttributeTargets.Method)] public sealed class CommandMethodAttribute:Attribute {public CommandMethodAttribute(string name,CommandFlags flags){} }
}
namespace Autodesk.AutoCAD.EditorInput {
 public enum PromptStatus {OK,Cancel,Error,None}
 public sealed class KeywordCollection {public string Default; public void Add(string global){ }public void Add(string global,string local,string display){ }public void Add(string global,string local,string display,bool visible,bool enabled){ }}
 public sealed class PromptKeywordOptions {public string Message;public bool AllowNone;public readonly KeywordCollection Keywords=new KeywordCollection();public PromptKeywordOptions(string message){Message=message;}public PromptKeywordOptions(string message,string keywords){Message=message;}}
 public class PromptResult {public PromptStatus Status;public string StringResult;}
 public sealed class PromptEntityResult:PromptResult {public ObjectId ObjectId;}
 public sealed class PromptPointResult:PromptResult {public Point3d Value;}
 public sealed class PromptEntityOptions {public string Message;public PromptEntityOptions(string message){Message=message;}public void AddAllowedClass(Type type,bool exact){}public void SetRejectMessage(string text){} }
 public sealed class PromptPointOptions {public string Message;public bool AllowNone;public PromptPointOptions(string message){Message=message;}}
 public sealed class Editor {
  public string Messages="";public Matrix3d CurrentUserCoordinateSystem=Matrix3d.Identity;
  public readonly Queue<PromptResult> Keywords=new Queue<PromptResult>();
  public PromptEntityResult Entity=new PromptEntityResult{Status=PromptStatus.Cancel};
  public PromptPointResult Point=new PromptPointResult{Status=PromptStatus.Cancel};
  public Action OnPoint,OnEntity,OnKeywords;
  public int KeywordCalls,EntityCalls,PointCalls;
  public PromptResult GetKeywords(PromptKeywordOptions options){KeywordCalls++;if(OnKeywords!=null)OnKeywords();return Keywords.Count>0?Keywords.Dequeue():new PromptResult{Status=PromptStatus.Cancel};}
  public PromptEntityResult GetEntity(PromptEntityOptions options){EntityCalls++;if(OnEntity!=null)OnEntity();return Entity;}
  public PromptPointResult GetPoint(PromptPointOptions options){PointCalls++;if(OnPoint!=null)OnPoint();return Point;}
  public PromptPointResult GetPoint(string message){return GetPoint(new PromptPointOptions(message));}
  public void WriteMessage(string text){Messages+=text;}
 }
}
namespace Autodesk.AutoCAD.ApplicationServices {
 public sealed class Document {
  public readonly Database Database=new Database();public readonly Autodesk.AutoCAD.EditorInput.Editor Editor=new Autodesk.AutoCAD.EditorInput.Editor();public int LockCalls;
  public Action BeforeLock;
  public IDisposable LockDocument(){LockCalls++;if(BeforeLock!=null)BeforeLock();return new EmptyDisposable();}
 }
 internal sealed class EmptyDisposable:IDisposable {public void Dispose(){}}
 public sealed class Documents {public Document MdiActiveDocument;}
 public static class Application {
  public static readonly Documents DocumentManager=new Documents();public static int DialogCalls;
  public static Func<object,System.Windows.Forms.DialogResult> Dialog;
  public static System.Windows.Forms.DialogResult ShowModalDialog(object form){DialogCalls++;return Dialog(form);}
 }
}
namespace FacadeSafety {
 public sealed class ZoneGeometryResult {public bool Ok;public string Reason,ZoneId,Fingerprint;public ObjectId HatchId;}
 internal static class ZoneGeometryGuard {
  internal static readonly Dictionary<ObjectId,ZoneGeometryResult> Zones=new Dictionary<ObjectId,ZoneGeometryResult>();internal static int Calls;
  internal static ZoneGeometryResult Verify(Transaction tr,Database db,Entity entity,object sidecar){Calls++;ZoneGeometryResult result;return entity!=null&&Zones.TryGetValue(entity.ObjectId,out result)?result:new ZoneGeometryResult{Ok=false,Reason="fixture: carrier is not a verified ATFZONE"};}
 }
}
namespace AFramePlugin {
 public sealed class FrameNodeForm:IDisposable {
  public readonly FrameSolutionSelection Selection;public readonly FrameParameterContext Context;public FrameNodeGeometryInput Result;public FrameNodeGeometryResult Geometry;
  public FrameNodeForm(FrameSolutionSelection selection,FrameParameterContext parameterContext,FrameNodeGeometryInput initialInput=null){Selection=selection;Context=parameterContext;Result=initialInput;}
  public void Dispose(){}
 }
}
