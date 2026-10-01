using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using FacadeSafety;
using AFacadesPlugin;
internal static class OrphanScopeProbe {
 static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength=int.MaxValue };
 static T Clone<T>(T v) { return Json.Deserialize<T>(Json.Serialize(v)); }
 static object Observe(string name, QuantityReport[] reports, string[] selected, string path) {
  string before=Json.Serialize(reports);
  var result=FacadeQuantitiesCore.BuildRows(reports,selected,true,true);
  if(!result.ok) throw new Exception(name+": source invalid "+Json.Serialize(result.issues));
  var table=ConnectionTableData.Build(result,reports,selected,null,true);
  var scope=new HashSet<string>(selected,StringComparer.Ordinal);
  var eligible=new Dictionary<string,QuantityElement>(StringComparer.Ordinal);
  var linked=new HashSet<string>(StringComparer.Ordinal);
  foreach(var r in reports) foreach(var p in r.connection_passports ?? new List<QuantityConnectionPassport>()) {
   if(p.schema!="aframe_connection_passport/1" || p.status!="inventory_only") continue;
   var pZones=new HashSet<string>(p.zone_ids,StringComparer.Ordinal); pZones.IntersectWith(scope);
   foreach(var m in p.members) if(pZones.Contains(m.zone_id)) foreach(var s in m.supports) foreach(var id in s.bracket_element_ids) linked.Add(id);
   foreach(var e in r.elements) if(e.role=="bracket" && e.origin=="generated:ATFRAME:brackets" &&
    e.element_id.StartsWith(p.source_run_id+":brackets:",StringComparison.Ordinal) &&
    e.zone_ids.Count==1 && pZones.Contains(e.zone_ids[0])) eligible[e.element_id]=e;
  }
  var orphans=eligible.Keys.Where(id=>!linked.Contains(id)).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
  string text=string.Join("\n",table.Rows("").Select(row=>string.Join(" | ",row.Select(v=>Convert.ToString(v)).ToArray())).ToArray());
  using(var file=new QuantityXlsxFile(path,"Соединения",QuantityTableView.FromConnections(table).Rows(""))) { file.Publish();file.Complete(); }
  if(before!=Json.Serialize(reports)) throw new Exception("source mutated");
  return new {name,core_ok=result.ok,selected,eligible_brackets=eligible.Count,linked_brackets=linked.Count,orphan_ids=orphans,
   orphan_ids_shown_in_table=orphans.Where(id=>text.Contains(id)).ToArray(),table_member_count=table.Members.Count,
   missing_passport_text=text.Contains("Паспорт не сформирован"),source_unchanged=true,
   physical_elements=reports.SelectMany(r=>r.elements).Select(e=>e.element_id).Distinct().Count(),
   table_messages=table.Messages.ToArray(),core_issue_codes=result.issues.Select(i=>i.code).Distinct().ToArray()};
 }
 static int Main(string[] args) {
  var report=Json.Deserialize<QuantityReport>(File.ReadAllText(args[0]));
  string dir=args[1];Directory.CreateDirectory(dir);var cases=new List<object>();
  cases.Add(Observe("all",new[]{report},report.zone_ids.ToArray(),Path.Combine(dir,"all.xlsx")));
  foreach(string z in report.zone_ids) cases.Add(Observe("selected_"+z,new[]{report},new[]{z},Path.Combine(dir,"selected_"+z+".xlsx")));
  var retained=Clone(report);retained.report_id=retained.run_id="retained-report";
  cases.Add(Observe("retained_distinct_report_run",new[]{retained},report.zone_ids.ToArray(),Path.Combine(dir,"retained.xlsx")));
  cases.Add(Observe("retained_duplicate_reports",new[]{report,retained,report},report.zone_ids.ToArray(),Path.Combine(dir,"duplicates.xlsx")));
  var removed=Clone(retained);var p=removed.connection_passports[0];
  var linked=new HashSet<string>(p.members.SelectMany(m=>m.supports).SelectMany(s=>s.bracket_element_ids));
  removed.elements.RemoveAll(e=>e.role=="bracket"&&!linked.Contains(e.element_id));
  cases.Add(Observe("retained_unlinked_elements_no_longer_present",new[]{removed},removed.zone_ids.ToArray(),Path.Combine(dir,"retained_removed.xlsx")));
  var unavailable=Clone(report);foreach(var pass in unavailable.connection_passports) {
   pass.status="unavailable";pass.reason="Synthetic unsupported legacy source";pass.members.Clear();pass.joints.Clear();pass.summary=new QuantityConnectionSummary();
  }
  cases.Add(Observe("unavailable_passport_not_zero_relation",new[]{unavailable},report.zone_ids.ToArray(),Path.Combine(dir,"unavailable.xlsx")));
  var legacy=Clone(report);legacy.connection_passports=null;
  cases.Add(Observe("legacy_absent_passport_not_zero_relation",new[]{legacy},report.zone_ids.ToArray(),Path.Combine(dir,"legacy.xlsx")));
  File.WriteAllText(Path.Combine(dir,"cases.json"),Json.Serialize(new {status="OBSERVED",cases,live_autocad_checked=false}));
  Console.WriteLine("Scope cases observed: "+cases.Count);return 0;
 }
}
