using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AFramePlugin;
using FacadeSafety;
using ProjectStore=FacadeSafety.FacadeProjectParameterStore;
using Forms=System.Windows.Forms;

internal static partial class LimitedPilotProbe {
 static Document NodeDoc;
 static Zone NodeZone;
 static BlockReference N1,N2;
 static string N1Original;
 static readonly List<string> NodeChecks=new List<string>();
 static readonly List<object> NodeCases=new List<object>();
 static void NodeNeed(bool ok,string reason){if(!ok)throw new InvalidOperationException(reason);NodeChecks.Add(reason);}
 static BlockTableRecord Model(){return (BlockTableRecord)Db.CurrentSpaceId.Item;}
 static List<BlockReference> Nodes(){return Model().Children.Select(id=>id.Item).OfType<BlockReference>().Where(n=>((BlockTableRecord)n.BlockTableRecord.Item).Name.StartsWith(FrameNodeRenderer.BlockPrefix,StringComparison.Ordinal)).ToList();}
 static object P(Point3d p){return new[]{p.X,p.Y,p.Z};}
 static string NodeState(BlockReference node){
  using(var tr=new Transaction(Db)){
   var saved=FrameNodeStore.Read(tr,node);var def=(BlockTableRecord)node.BlockTableRecord.Item;
   var ext=(DBDictionary)node.ExtensionDictionary.Item;var data=((Xrecord)ext.Items[FrameNodeStore.Key].Item).Data;
   var body=def.Children.Select(id=>{var e=(Entity)id.Item;var line=e as Line;var text=e as MText;return new{handle=e.Handle.ToString(),owner=e.OwnerId.Handle.ToString(),type=e.GetType().Name,layer=e.Layer,color=e.ColorIndex,line=line==null?null:new[]{P(line.StartPoint),P(line.EndPoint)},text=text==null?null:new{location=P(text.Location),text.Contents,text.TextHeight,text.Width,text.Rotation,style=text.TextStyleId.Handle.ToString()}};}).ToArray();
   NodeNeed(node.OwnerId==Db.CurrentSpaceId&&Model().Children.Count(id=>id==node.ObjectId)==1,"actual node reference belongs to ModelSpace exactly once");
   NodeNeed(def.Children.All(id=>id.Item.OwnerId==def.ObjectId),"definition primitives retain explicit definition ownership");
   NodeNeed(def.Children.All(id=>!Model().Children.Contains(id)),"definition primitives never enter ModelSpace");
   return Json.Serialize(new{owner=node.Handle.ToString(),owner_space=node.OwnerId.Handle.ToString(),position=P(node.Position),rotation=node.Rotation,scale=new[]{node.ScaleFactors.X,node.ScaleFactors.Y,node.ScaleFactors.Z},normal=new[]{node.Normal.X,node.Normal.Y,node.Normal.Z},definition=def.Handle.ToString(),definition_name=def.Name,zone=saved.ZoneHandle,saved.ZoneFingerprint,saved.RecordDigest,saved.CadContentDigest,saved.DrawingDigest,snapshot=saved.Snapshot.ToDict(),geometry=saved.Snapshot.Result.ToDict(),references=data.Where(v=>v.TypeCode==(int)DxfCode.SoftPointerId).Select(v=>((ObjectId)v.Value).Handle.ToString()).ToArray(),record=string.Concat(data.Where(v=>v.TypeCode==(int)DxfCode.Text).Select(v=>(string)v.Value)),body});
  }
 }
 static void PrepareNode(string mode,ObjectId selected){
  Application.DocumentManager.MdiActiveDocument=NodeDoc;NodeDoc.BeforeLock=null;Xrecord.OnRead=null;
  var ed=NodeDoc.Editor;ed.Messages="";ed.OnPoint=null;ed.Keywords.Clear();ed.Keywords.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult=mode});
  ed.Entity=new PromptEntityResult{Status=PromptStatus.OK,ObjectId=selected};ed.Point=new PromptPointResult{Status=PromptStatus.OK,Value=new Point3d(3,4,0)};
  ed.CurrentUserCoordinateSystem=new Matrix3d(new Point3d(100,200,0),new Scale3d(1),Math.PI/2,Point3d.Origin);
  Application.Dialog=form=>{((FrameNodeForm)form).Result=new FrameNodeGeometryInput{profile_near_face_x_mm=170,clearance_surface=new FrameNodeClearanceSurface{kind=FrameNodeClearanceSurface.Layers}};return Forms.DialogResult.OK;};
 }
 static Dictionary<string,object> RunNode(string name){
  CadCounters.Reset();var watch=System.Diagnostics.Stopwatch.StartNew();new FrameNodeCommand().Node();watch.Stop();var counters=CadCounters.Snapshot();
  NodeNeed(Convert.ToInt64(counters["modelspace_visits"])==0,"actual ATFNODE never scans ModelSpace: "+name);
  var entry=new Dictionary<string,object>{{"name",name},{"messages",NodeDoc.Editor.Messages},{"counters",counters},{"milliseconds",watch.Elapsed.TotalMilliseconds}};NodeCases.Add(entry);return entry;
 }
 static void ReadOnlyCheck(BlockReference node,string name,bool fresh){
  string before=NodeState(node);PrepareNode("Check",node.ObjectId);var entry=RunNode(name);var counters=(Dictionary<string,object>)entry["counters"];
  NodeNeed(NodeDoc.Editor.Messages.Contains(fresh?"Снимок и тело схемы соответствуют текущим источникам":"E_NODE_SOURCE_STALE"),"actual Check reports expected freshness: "+name+" "+NodeDoc.Editor.Messages);
  NodeNeed(Convert.ToInt64(counters["objects_created"])==0&&Convert.ToInt64(counters["xrecord_data_set"])==0&&Convert.ToInt64(counters["dictionary_set"])==0,"actual Check is read-only: "+name);
  NodeNeed(NodeState(node)==before,"actual Check preserves full node body and passport: "+name);entry["fresh"]=fresh;entry["node"]=D(Json.DeserializeObject(before));
 }
 static BlockReference CreateNode(string name){
  var old=Nodes();PrepareNode("Create",NodeZone.Mark.ObjectId);var entry=RunNode(name);var created=Nodes().Except(old).ToArray();
  NodeNeed(created.Length==1&&NodeDoc.Editor.Messages.Contains("Размерная схема создана"),"actual command creates exactly one reference through canonical mark: "+NodeDoc.Editor.Messages);
  var node=created[0];NodeNeed(Math.Abs(node.Position.X-96)<1e-10&&Math.Abs(node.Position.Y-203)<1e-10,"actual node command converts controlled UCS point to WCS");
  entry["node"]=D(Json.DeserializeObject(NodeState(node)));entry["position"]=P(node.Position);
  using(var tr=new Transaction(Db)){
   var stored=FrameNodeStore.Read(tr,node);var mounting=FrameMountingAssessment.Evaluate(stored.Snapshot.Selection,stored.Snapshot.Result);
   NodeNeed(stored.ZoneId==NodeZone.Hatch.ObjectId&&stored.Snapshot.Context.zone.owner_handle==NodeZone.Hatch.Handle.ToString(),"actual store resolves selected mark to same canonical hatch");
   entry["mounting_status"]=mounting.MountingStatus;entry["automatic_bracket_selection_allowed"]=mounting.AutomaticBracketSelectionAllowed;
   NodeNeed(stored.Snapshot.Result.GapMm==20&&mounting.MountingStatus=="not_confirmed"&&!mounting.AutomaticBracketSelectionAllowed,"node geometry keeps engineering admission closed");
  }
  return node;
 }
 static void FailedN2(string mode){
  string before=NodeState(N1);int count=Nodes().Count;PrepareNode("Create",NodeZone.Hatch.ObjectId);
  if(mode=="cancel_dialog")Application.Dialog=form=>Forms.DialogResult.Cancel;
  else if(mode=="cancel_point")NodeDoc.Editor.Point.Status=PromptStatus.Cancel;
  else NodeDoc.Editor.OnPoint=()=>NodeDoc.Editor.CurrentUserCoordinateSystem=Matrix3d.Identity;
  var entry=RunNode(mode);var counters=(Dictionary<string,object>)entry["counters"];
  NodeNeed(Nodes().Count==count&&NodeState(N1)==before&&before==N1Original,"unsuccessful N2 leaves exact N1 owner/definition/raw passport/body untouched: "+mode);
  NodeNeed(Convert.ToInt64(counters["objects_created"])==0&&Convert.ToInt64(counters["xrecord_data_set"])==0&&Convert.ToInt64(counters["dictionary_set"])==0,"unsuccessful N2 refuses before writes, no rollback claim: "+mode);
  if(mode=="refuse_ucs")NodeNeed(NodeDoc.Editor.Messages.Contains("E_NODE_UCS"),"late UCS refusal uses actual environment guard");
  using(var tr=new Transaction(Db))NodeNeed(N(ProjectStore.ReadProject(tr,Db).Payload["revision"])==2,"unsuccessful N2 preserves externally committed project revision 2");
  entry["n1_unchanged"]=true;entry["node_count"]=count;ReadOnlyCheck(N1,mode+"_n1_stale",false);
 }
 static void ExcludeNodeFromQuantities(string stage){
  using(var tr=new Transaction(Db)){
   var nodes=Nodes();var cache=new ManualQuantityGeometry.Cache();var observations=nodes.Select(n=>ManualQuantityGeometry.Extract(tr,n,cache)).ToArray();
   for(int i=0;i<nodes.Count;i++){
    var rule=new ManualQuantityRule{rule_id="node-exclusion",revision="1",kind="frame",role="bracket",adapter="symbol",one_physical_part=true,sample_key=observations[i].sample_key,fields=new Dictionary<string,ManualQuantityField>{{"mark",new ManualQuantityField{source="literal",value="schematic"}}}};
    var preview=FacadeQuantityStore.PreviewImport(tr,Db,NodeZone.Mark.ObjectId,rule,new[]{nodes[i].ObjectId});
    NodeNeed(preview.Preview.accepted_count==0&&preview.Preview.items.Single().observation.generated_conflict,"actual manual import refuses AFRAME_NODE_GEOMETRY owner: "+stage);
    NodeNeed(!FacadeQuantityStore.HasManual(tr,nodes[i]),"node is never assigned a manual physical passport");
   }
   var ids=Zones.Select(z=>z.Hatch.ObjectId).Concat(nodes.Select(n=>n.ObjectId)).ToArray();
   var frame=FacadeQuantityStore.ReadFrame(tr,Db,ids);var cladding=FacadeQuantityStore.ReadCladding(tr,Db,ids);
   NodeNeed(frame.Ok&&cladding.Ok,"mixed zone/node selection retains valid physical reports: "+stage);
   NodeNeed(frame.Unaccounted.Count==nodes.Count&&cladding.Unaccounted.Count==nodes.Count,"each explicitly selected node is visibly unaccounted, never silently included: "+stage);
   var quantities=FacadeQuantitiesCore.BuildRows(frame.Reports,frame.SelectedZoneIds,true,true);
   NodeNeed(frame.Reports.SelectMany(r=>r.elements).Count(e=>e.role=="rail")==2&&frame.Reports.SelectMany(r=>r.elements).Count(e=>e.role=="bracket")==6&&cladding.Reports.SelectMany(r=>r.elements).Count()==12,"nodes preserve physical totals 12 tiles / 2 rails / 6 brackets: "+stage);
   Save("node_command/quantities_"+stage,new{nodes=nodes.Select(n=>n.Handle.ToString()).ToArray(),frame_unaccounted=frame.Unaccounted,cladding_unaccounted=cladding.Unaccounted,frame=quantities});
  }
 }
 static partial void NodeCheckpoint(string stage){
  if(stage=="initial"){
   NodeDoc=new Document(Db);NodeZone=Zones.Single(z=>z.Outer.GetPoint2dAt(0).X==0);N1=CreateNode("create_n1");N1Original=NodeState(N1);ReadOnlyCheck(N1,"n1_initial_fresh",true);ExcludeNodeFromQuantities("initial");
  }
  if(stage=="project_changed"){
   ReadOnlyCheck(N1,"n1_after_project_change",false);FailedN2("cancel_dialog");FailedN2("cancel_point");FailedN2("refuse_ucs");N2=CreateNode("create_n2");
   NodeNeed(N1.ObjectId!=N2.ObjectId&&N1.BlockTableRecord!=N2.BlockTableRecord,"successful N2 owns a distinct reference and definition");
   NodeNeed(NodeState(N1)==N1Original,"successful N2 does not rewrite N1");ReadOnlyCheck(N2,"n2_fresh_at_issue",true);ReadOnlyCheck(N1,"n1_stale_after_n2",false);
  }
  if(stage=="project_reissued")ExcludeNodeFromQuantities(stage);
  if(stage=="final"){
   NodeNeed(NodeState(N1)==N1Original,"unrelated local zone change and table updates preserve N1 bytes");ReadOnlyCheck(N2,"n2_fresh_before_table_late_mutation",true);ExcludeNodeFromQuantities(stage);
  }
  if(stage=="after_table_final"){
   ReadOnlyCheck(N1,"n1_after_table_project_revision3",false);ReadOnlyCheck(N2,"n2_after_table_project_revision3",false);
   using(var tr=new Transaction(Db))NodeNeed(N(ProjectStore.ReadProject(tr,Db).Payload["revision"])==3,"combined run retains table test external project revision 3");
   NodeNeed(NodeState(N1)==N1Original,"final N1 source stamp/body remain exact after later table mutation");
   Save("node_command/result",new{status="PASS",checks=NodeChecks.Count,assertions=NodeChecks,cases=NodeCases,final_project_revision=3,final_n1_fresh=false,final_n2_fresh=false,
    scope="actual FrameNodeCommand.Node/Create/Check, FrameNodeRenderer, FrameNodeStore and canonical guards in SAME Database as actual TableCommand and engines; scripted host/form, no native rendering",
    rollback="only refusal/cancellation before writes; Commit/Dispose no-op; native rollback/Undo/COPY/SaveAs/save/open NOT_RUN"});
  }
 }
}
