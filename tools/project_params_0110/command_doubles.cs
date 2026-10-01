// UI/host seams only. Actual FrameProjectCommand and actual persistence execute.
// No AutoCAD geometry, document locking or native modal behavior is claimed.
using System;
using System.Collections;
using System.Collections.Generic;
using AFramePlugin;
using Autodesk.AutoCAD.DatabaseServices;

namespace Autodesk.AutoCAD.Runtime {
 [Flags] public enum CommandFlags { Modal=1, UsePickSet=2 }
 [AttributeUsage(AttributeTargets.Assembly)] public sealed class CommandClassAttribute:Attribute { public CommandClassAttribute(Type t){} }
 [AttributeUsage(AttributeTargets.Method)] public sealed class CommandMethodAttribute:Attribute { public CommandMethodAttribute(string n,CommandFlags f){} }
}
namespace Autodesk.AutoCAD.DatabaseServices {
 public sealed class TransactionManager { readonly Database db; public TransactionManager(Database db){this.db=db;} public Transaction StartTransaction(){return new Transaction(db);} }
}
namespace Autodesk.AutoCAD.EditorInput {
 public enum PromptStatus { OK, Cancel, Error }
 public sealed class PromptSelectionOptions { public string MessageForAdding; }
 public sealed class SelectionFilter { public SelectionFilter(TypedValue[] values){} }
 public sealed class SelectedObject { public ObjectId ObjectId; }
 public sealed class SelectionSet:IEnumerable<SelectedObject> {
  public readonly List<SelectedObject> Items=new List<SelectedObject>();
  public IEnumerator<SelectedObject> GetEnumerator(){return Items.GetEnumerator();} IEnumerator IEnumerable.GetEnumerator(){return GetEnumerator();}
 }
 public sealed class PromptSelectionResult { public PromptStatus Status; public SelectionSet Value; }
 public sealed class Editor {
  public PromptSelectionResult Selection=new PromptSelectionResult { Status=PromptStatus.Cancel };
  public string Messages="";
  public PromptSelectionResult GetSelection(PromptSelectionOptions options,SelectionFilter filter){return Selection;}
  public void WriteMessage(string text){Messages+=text;}
 }
}
namespace Autodesk.AutoCAD.ApplicationServices {
 public sealed class Document {
  public readonly Database Database=new Database();
  public readonly Autodesk.AutoCAD.EditorInput.Editor Editor=new Autodesk.AutoCAD.EditorInput.Editor();
  public int LockCalls;
  public IDisposable LockDocument(){LockCalls++;return new EmptyDisposable();}
 }
 internal sealed class EmptyDisposable:IDisposable { public void Dispose(){} }
 public sealed class Documents { public Document MdiActiveDocument; }
 public static class Application {
  public static readonly Documents DocumentManager=new Documents();
  public static Func<object,System.Windows.Forms.DialogResult> Dialog;
  public static int DialogCalls;
  public static System.Windows.Forms.DialogResult ShowModalDialog(object form){DialogCalls++;return Dialog(form);}
 }
}
namespace FacadeSafety {
 public sealed class ZoneGeometryResult { public bool Ok; public string Reason,ZoneId,Fingerprint; public ObjectId HatchId; }
 internal static class ZoneGeometryGuard {
  internal static readonly Dictionary<ObjectId,ZoneGeometryResult> Zones=new Dictionary<ObjectId,ZoneGeometryResult>();
  internal static int Calls;
  internal static ZoneGeometryResult Verify(Transaction tr,Database db,Entity entity,object ignored){Calls++; ZoneGeometryResult result;return entity!=null&&Zones.TryGetValue(entity.ObjectId,out result)?result:new ZoneGeometryResult { Ok=false,Reason="fixture: carrier not a verified ATFZONE" };}
 }
}
namespace AFramePlugin {
 public sealed class FrameProjectForm:IDisposable {
  public readonly FrameProjectParameters Previous; public FrameProjectParameters Result;
  public FrameProjectForm(FrameProjectParameters previous){Previous=previous;} public void Dispose(){}
 }
 public sealed class FrameZoneParametersForm:IDisposable {
  public readonly FrameProjectParameters Project; public readonly FrameZoneParameters Previous; public readonly string Scope;
  public FrameZoneParameters Result;
  public FrameZoneParametersForm(FrameProjectParameters project,FrameZoneParameters previous,string scope,int zoneCount=1,bool generationChanged=false){Project=project;Previous=previous;Scope=scope;}
  public void Dispose(){}
 }
}
