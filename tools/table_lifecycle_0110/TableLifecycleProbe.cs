using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.ApplicationServices;
using AFacadesPlugin;
using Forms=System.Windows.Forms;

// Optional extension: absent in the original 1275-check runner. It observes and
// commands the same actual stored zones/reports; no QuantityReport fixture stub.
internal static partial class LimitedPilotProbe {
 static Document TableDoc;
 const string IssueNote="Приёмочный пилот {A}\\путь 50%\nМонтаж не подтверждён.";
 static Dictionary<string,object> LastCommandCounters;static double LastCommandMilliseconds;static int LastUcsReads;
 static readonly Dictionary<string,Table> IssuedTables=new Dictionary<string,Table>();
 static readonly List<object> TableCases=new List<object>();
 static readonly List<string> TableChecks=new List<string>();
 static List<object[]> PreviewRows;static string Expect;static string ActiveTag;
 static void TableNeed(bool value,string message){if(!value)throw new InvalidOperationException(message);TableChecks.Add(message);}
 static Matrix3d FixtureUcs(){return new Matrix3d(new Point3d(100,200,0),new Scale3d(1),Math.PI/2,Point3d.Origin);}
 static int TableCount(){return Table.Instances.Count(t=>!t.ObjectId.IsNull&&!t.IsErased);}
 static Dictionary<string,object> Metadata(Table table){using(var tr=new Transaction(Db))return D(Json.DeserializeObject(ZoneCommand.ReadZoneData(tr,table,"ATFQUANTITY_TABLE")));}
 static string Body(Table table){var rows=new List<object>();for(int r=0;r<table.Rows.Count;r++)rows.Add(Enumerable.Range(0,table.Columns.Count).Select(c=>table.Cells[r,c].TextString).ToArray());return Json.Serialize(new{position=new[]{table.Position.X,table.Position.Y,table.Position.Z},rows,metadata=Metadata(table)});}
 static string AllBodies(){return string.Join("\n",IssuedTables.OrderBy(p=>p.Key).Select(p=>p.Key+":"+Body(p.Value)).ToArray());}
 static void Setup(string key,string tag,bool update=false,string output="Оба") {
  ActiveTag=tag;TableDoc.Editor.Messages="";TableDoc.Editor.Keywords.Clear();TableDoc.BeforeLock=null;
  var ed=TableDoc.Editor;ed.OnPoint=null;ed.OnDouble=null;ed.OnEntity=null;ed.PointCalls=0;Xrecord.OnRead=null;
  ed.CurrentUserCoordinateSystem=FixtureUcs();
  if(key!="cladding")ed.Keywords.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult=key=="connections"?"Соединения":"Элементы"});
  foreach(var answer in new[]{"Объекты","ПоЗонам"})ed.Keywords.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult=answer});
  if(key=="cladding")ed.Keywords.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult="Детали"});
  ed.Keywords.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult=output});
  if(output!="Ексель")ed.Keywords.Enqueue(new PromptResult{Status=PromptStatus.OK,StringResult=update?"Обновить":"Новая"});
  ed.Selection=new PromptSelectionResult{Status=PromptStatus.OK,Value=new SelectionSet()};foreach(var z in Zones)ed.Selection.Value.Add(new SelectedObject{ObjectId=z.Hatch.ObjectId});
  ed.Entity=new PromptEntityResult{Status=PromptStatus.OK,ObjectId=update?IssuedTables[key].ObjectId:ObjectId.Null};
  ed.Height=new PromptDoubleResult{Status=PromptStatus.OK,Value=2.5};ed.Point=new PromptPointResult{Status=PromptStatus.OK,Value=new Point3d(3,4,0)};
  PreviewRows=null;Application.DialogCalls=0;
  Application.Dialog=o=>{var preview=(QuantityTablePreview)o;if(!update)preview.UserNote=IssueNote;PreviewRows=preview.Data.Rows(preview.UserNote);return Forms.DialogResult.OK;};
  Forms.SaveFileDialog.Dialog=d=>{d.FileName=Path.Combine(Output,"table_command",tag+".xlsx");return Forms.DialogResult.OK;};
  Directory.CreateDirectory(Path.Combine(Output,"table_command"));
 }
 static void ExecuteTable(string key){CadCounters.Reset();TableDoc.Editor.UcsReads=0;var watch=System.Diagnostics.Stopwatch.StartNew();FacadeQuantityTableCommand.Run(TableDoc,key=="cladding"?"cladding":"frame");LastCommandMilliseconds=watch.Elapsed.TotalMilliseconds;LastCommandCounters=CadCounters.Snapshot();LastUcsReads=TableDoc.Editor.UcsReads;}
 static void Record(string name,string key,Table table=null) {
  Dictionary<string,object> metadata=table==null?null:Metadata(table);
  var observed=CadCounters.Snapshot();var inspection=observed.ToDictionary(p=>p.Key,p=>(object)(Convert.ToInt64(p.Value)-Convert.ToInt64(LastCommandCounters[p.Key])));
  TableCases.Add(new{name,key,owner=table==null?null:table.Handle.ToString(),messages=TableDoc.Editor.Messages,command_counters=LastCommandCounters,command_milliseconds=LastCommandMilliseconds,editor_ucs_reads=LastUcsReads,inspection_counters=inspection,injected_external_mutation=name=="late_target"||name=="late_project",metadata});
  if(table!=null){Save("table_command/"+name+"_table",new{owner=table.Handle.ToString(),position=new[]{table.Position.X,table.Position.Y,table.Position.Z},metadata,row_count=table.Rows.Count,column_count=table.Columns.Count,merges=table.Merges.Select(m=>new[]{m.TopRow,m.LeftColumn,m.BottomRow,m.RightColumn}).ToArray(),body=Body(table)});}
 }
 static void Create(string key) {
  Setup(key,"initial_"+key);int before=TableCount();CadCounters.Reset();ExecuteTable(key);
  TableNeed(TableCount()==before+1,"actual command creates one "+key+" table from persistent engine stores: "+TableDoc.Editor.Messages);
  var table=Table.Instances.Last(t=>!t.ObjectId.IsNull);IssuedTables.Add(key,table);
  var actual=new[]{table.Position.X,table.Position.Y};var wanted=Expect=="baseline"?new[]{3.0,4.0}:new[]{96.0,203.0};
  TableNeed(Math.Abs(actual[0]-wanted[0])<1e-10&&Math.Abs(actual[1]-wanted[1])<1e-10,"actual new table UCS placement matches expected "+Expect);
  TableNeed(LastUcsReads==(Expect=="baseline"?0:3),"new table reads UCS only capture and two guards: "+key);
  using(var tr=new Transaction(Db)){var model=(BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db),OpenMode.ForRead);TableNeed(model.Count(id=>id==table.ObjectId)==1,"new command table has one ModelSpace membership");}
  var meta=Metadata(table);TableNeed((string)meta["owner"]==table.Handle.ToString(),"actual table metadata binds real owner");
  TableNeed(Json.Serialize(meta["rows"])==Json.Serialize(PreviewRows),"actual command stores exactly previewed quantity rows");
  TableNeed(TableDoc.Editor.Messages.Contains("проверка источников, проходов 2:"),"actual issue reads sources twice without inspection reads");
  TableNeed(TableDoc.Editor.Messages.Contains("сформирована"),"actual command reports successful issue");
  TableCases.Add(new{name="ucs_reproduction_"+key,picked_ucs=new[]{3,4,0},origin_wcs=new[]{100,200,0},rotation_degrees=90,expected_wcs=new[]{96,203,0},actual_position=actual,defect_reproduced=Expect=="baseline"});
  Save("table_command/initial_"+key+"_rows",PreviewRows);Record("initial_"+key,key,table);
 }
 static void Update(string key,string stage) {
  var old=IssuedTables[key];var position=old.Position;string owner=old.Handle.ToString(),priorStamp=(string)Metadata(old)["source_snapshot"];
  Setup(key,stage+"_"+key,true);TableDoc.Editor.CurrentUserCoordinateSystem=new Matrix3d(new Point3d(-700,900,0),new Scale3d(1),-.3,Point3d.Origin);
  TableDoc.BeforeLock=()=>TableDoc.Editor.CurrentUserCoordinateSystem=Matrix3d.Identity;
  CadCounters.Reset();ExecuteTable(key);
  TableNeed(LastUcsReads==0,"update adds no UCS reads");
  TableNeed(TableDoc.Editor.PointCalls==0,"update does not request a new point or acquire UCS dependency");
  TableNeed(old.Handle.ToString()==owner&&old.Position.DistanceTo(position)==0,"update preserves actual owner and existing WCS position: "+key);
  TableNeed(TableDoc.Editor.Messages.Contains("проверка источников, проходов 2:"),"actual update reads sources twice without inspection reads");
  TableNeed(TableDoc.Editor.Messages.Contains("обновлена"),"actual command updates after actual reissue: "+key);
  var meta=Metadata(old);TableNeed(Json.Serialize(meta["rows"])==Json.Serialize(PreviewRows),"updated table rows equal second preview: "+key);
  TableNeed((string)meta["user_note"]==IssueNote,"update restores stored user note before second preview");
  if(key!="cladding")TableNeed((string)meta["source_snapshot"]!=priorStamp,"updated frame table carries new actual source stamp");
  Save("table_command/"+stage+"_"+key+"_rows",PreviewRows);Record(stage+"_"+key,key,old);
 }
 static void RefuseStale(string stage) {
  Setup("frame",stage+"_refuse",true);var before=AllBodies();int count=TableCount();CadCounters.Reset();ExecuteTable("frame");
  TableNeed(AllBodies()==before&&TableCount()==count&&TableDoc.Editor.Messages.Contains("Ведомость не создана"),"actual stale report refuses before table output "+stage);
  TableNeed(CadCounters.DictionarySet==0&&CadCounters.XrecordWrites==0,"stale table command writes no records "+stage);Record(stage+"_refuse","frame");
 }
 static void EarlyCancels() {
  foreach(string step in new[]{"preview","update_second_preview","point","save_dialog"}) {
   Setup("frame","cancel_"+step,true);if(step=="point")Setup("frame","cancel_"+step,false);
   if(step=="preview")Application.Dialog=o=>Forms.DialogResult.Cancel;
   if(step=="update_second_preview"){int calls=0;Application.Dialog=o=>++calls==2?Forms.DialogResult.Cancel:Forms.DialogResult.OK;}
   if(step=="point")TableDoc.Editor.Point.Status=PromptStatus.Cancel;
   if(step=="save_dialog")Forms.SaveFileDialog.Dialog=d=>Forms.DialogResult.Cancel;
   string path=Path.Combine(Output,"table_command","cancel_"+step+".xlsx");File.Copy(Path.Combine(Output,"table_command","initial_frame.xlsx"),path,true);byte[] original=File.ReadAllBytes(path);
   string before=AllBodies();int count=TableCount();CadCounters.Reset();ExecuteTable("frame");
   TableNeed(original.SequenceEqual(File.ReadAllBytes(path)),"early cancel preserves original XLSX: "+step);
   TableNeed(before==AllBodies()&&count==TableCount()&&CadCounters.DictionarySet==0&&CadCounters.XrecordWrites==0,"cancel before table writes preserves prior output: "+step);Record("cancel_"+step,"frame");
  }
 }
 static void RefuseLateUcs() {
  foreach(string boundary in new[]{"point","save_dialog","lock","fresh_read"}) {
   Setup("frame","late_ucs_"+boundary);string path=Path.Combine(Output,"table_command","late_ucs_"+boundary+".xlsx");
   File.Copy(Path.Combine(Output,"table_command","initial_frame.xlsx"),path,true);byte[] original=File.ReadAllBytes(path);
   Action change=()=>TableDoc.Editor.CurrentUserCoordinateSystem=Matrix3d.Identity;
   if(boundary=="point")TableDoc.Editor.OnPoint=change;
   else if(boundary=="save_dialog")Forms.SaveFileDialog.Dialog=d=>{d.FileName=path;change();return Forms.DialogResult.OK;};
   else if(boundary=="lock")TableDoc.BeforeLock=change;
   else TableDoc.BeforeLock=()=>Xrecord.OnRead=()=>{Xrecord.OnRead=null;change();};
   string before=AllBodies();int count=TableCount();CadCounters.Reset();ExecuteTable("frame");Xrecord.OnRead=null;
   TableNeed(LastUcsReads==(boundary=="point"?2:3),"changed UCS has bounded environment reads: "+boundary);
   TableNeed(before==AllBodies()&&count==TableCount()&&CadCounters.DictionarySet==0&&CadCounters.XrecordWrites==0,"changed UCS refused before table write: "+boundary);
   TableNeed(original.SequenceEqual(File.ReadAllBytes(path)),"changed UCS preserves original XLSX: "+boundary);
   TableNeed(TableDoc.Editor.Messages.Contains("ПСК"),"UCS refusal reports exact environment cause: "+boundary);Record("late_ucs_"+boundary,"frame");
  }
 }
 static void PositionControl(string name,Point3d origin,double angle,double x,double y) {
  Setup("frame","position_"+name,false,"Чертеж");TableDoc.Editor.CurrentUserCoordinateSystem=new Matrix3d(origin,new Scale3d(1),angle,Point3d.Origin);
  int count=TableCount();ExecuteTable("frame");var table=Table.Instances.Last(t=>!t.ObjectId.IsNull);
  double expectedX=Expect=="baseline"?3:x,expectedY=Expect=="baseline"?4:y;
  TableNeed(TableCount()==count+1&&Math.Abs(table.Position.X-expectedX)<1e-10&&Math.Abs(table.Position.Y-expectedY)<1e-10,"actual full command position control "+name);
  TableCases.Add(new{name="position_"+name,origin=new[]{origin.X,origin.Y,origin.Z},rotation_degrees=angle*180/Math.PI,picked_ucs=new[]{3,4,0},expected_wcs=new[]{x,y,0},actual_position=new[]{table.Position.X,table.Position.Y,table.Position.Z}});
 }
 static void WrongTargets() {
  foreach(string key in new[]{"cladding","connections"}) {
   Setup(key,"wrong_target_"+key,true);TableDoc.Editor.Entity.ObjectId=IssuedTables["frame"].ObjectId;
   string before=AllBodies();int count=TableCount();CadCounters.Reset();ExecuteTable(key);
   TableNeed(before==AllBodies()&&count==TableCount()&&CadCounters.DictionarySet==0&&CadCounters.XrecordWrites==0,"wrong view or kind refuses without writes: "+key);
   TableNeed(TableDoc.Editor.Messages.Contains("не является ведомостью"),"wrong target diagnosis identifies kind/view boundary: "+key);Record("wrong_target_"+key,key);
  }
 }
 static void ExcelOnly() {
  Setup("frame","excel_only",false,"Ексель");TableDoc.BeforeLock=()=>TableDoc.Editor.CurrentUserCoordinateSystem=Matrix3d.Identity;
  string before=AllBodies();int count=TableCount();ExecuteTable("frame");
  TableNeed(LastUcsReads==0,"Excel-only adds no UCS reads");
  TableNeed(before==AllBodies()&&count==TableCount()&&TableDoc.Editor.PointCalls==0,"Excel-only does not create table or request point despite UCS change");
  TableNeed(File.Exists(Path.Combine(Output,"table_command","excel_only.xlsx")),"actual Excel-only command publishes output");Save("table_command/excel_only_rows",PreviewRows);Record("excel_only","frame");
 }
 static void RefuseChangedTarget() {
  Setup("frame","late_target",true);string path=Path.Combine(Output,"table_command","late_target.xlsx");File.Copy(Path.Combine(Output,"table_command","initial_frame.xlsx"),path,true);byte[] original=File.ReadAllBytes(path);var table=IssuedTables["frame"];string externalBody=null;
  TableDoc.BeforeLock=()=>{using(var tr=new Transaction(Db)){var metadata=Metadata(table);metadata["user_note"]="Внешняя законченная правка";ZoneCommand.StoreZoneData(tr,table,Json.Serialize(metadata),"ATFQUANTITY_TABLE",new[]{Zones[0].Hatch.ObjectId});tr.Commit();}externalBody=Body(table);};
  ExecuteTable("frame");TableNeed(original.SequenceEqual(File.ReadAllBytes(path)),"late target refusal preserves original XLSX");TableNeed(externalBody==Body(table),"late target refusal preserves external finished edit");
  TableNeed(TableDoc.Editor.Messages.Contains("таблица изменилась после предпросмотра"),"actual command refuses replaced target metadata");Record("late_target","frame",table);
 }
 static void RefuseLateProject() {
  Setup("frame","late_project",true);string path=Path.Combine(Output,"table_command","late_project.xlsx");File.Copy(Path.Combine(Output,"table_command","initial_frame.xlsx"),path,true);byte[] original=File.ReadAllBytes(path);var before=AllBodies();int count=TableCount();
  TableDoc.BeforeLock=()=>ChangeProject(270);ExecuteTable("frame");
  TableNeed(original.SequenceEqual(File.ReadAllBytes(path)),"late project refusal preserves original XLSX");
  TableNeed(before==AllBodies()&&count==TableCount(),"late project change preserves existing tables");
  TableNeed(TableDoc.Editor.Messages.Contains("Параметры проекта изменились"),"actual second source read refuses exact late project change");
  using(var tr=new Transaction(Db))TableNeed(N(FacadeSafety.FacadeProjectParameterStore.ReadProject(tr,Db).Payload["revision"])==3,"external project mutation remains committed; harness does not restore it");
  Record("late_project","frame");
 }
 static partial void CommandCheckpoint(string stage) {
  if(stage=="initial") {
   Expect=(string)D(Json.DeserializeObject(File.ReadAllText(Path.Combine(Output,"table_expect.json"))))["expect"];TableDoc=new Document(Db);
   foreach(var key in new[]{"cladding","frame","connections"})Create(key);
   PositionControl("identity",Point3d.Origin,0,3,4);PositionControl("translated",new Point3d(100,200,0),0,103,204);EarlyCancels();WrongTargets();
  }
  if(stage=="project_changed"||stage=="zone_changed")RefuseStale(stage);
  if(stage=="project_reissued")foreach(var key in new[]{"cladding","frame","connections"})Update(key,stage);
  if(stage=="final") {
   foreach(var key in new[]{"cladding","frame","connections"})Update(key,"zone_reissued");
   if(Expect=="fixed")RefuseLateUcs();ExcelOnly();RefuseChangedTarget();RefuseLateProject();
   TableNeed(!Directory.GetFiles(Path.Combine(Output,"table_command"),"*.tmp").Any()&&!Directory.GetFiles(Path.Combine(Output,"table_command"),"*.bak").Any(),"command success and early refusals leave no staged or backup files");
   Save("table_command/result",new{status="PASS",expect=Expect,checks=TableChecks.Count,assertions=TableChecks,cases=TableCases,
    scope="actual FacadeQuantityTableCommand.Run branch in same persistent engine/store DB; external ATFTABLE dispatcher and native UI NOT_RUN",
    rollback="mid-Fill cancellation and CAD transaction rollback NOT_RUN; QuantityCadDoubles transactions remain no-ops"});
  }
 }
}
