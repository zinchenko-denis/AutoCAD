using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using FacadeSafety;
using AFacadesPlugin;
internal static class OrphanTableProbe {
 static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength=int.MaxValue };
 static int checks;
 static bool ExpectFixed;
 static void Check(bool value,string reason) { checks++;if(!value)throw new Exception(reason); }
 static T Clone<T>(T v) { return Json.Deserialize<T>(Json.Serialize(v)); }
 static object Observe(string name, QuantityReport[] reports, string[] selected, string path) {
  string before=Json.Serialize(reports);
  var result=FacadeQuantitiesCore.BuildRows(reports,selected,true,true);
  if(!result.ok) throw new Exception(name+": source invalid "+Json.Serialize(result.issues));
  var diagnostics=new ConnectionTableDiagnostics();
  var table=ConnectionTableData.Build(result,reports,selected,null,true,diagnostics);
  var scope=new HashSet<string>(selected,StringComparer.Ordinal);
  var eligible=new Dictionary<string,QuantityElement>(StringComparer.Ordinal);
  var linked=new HashSet<string>(StringComparer.Ordinal);
  foreach(var r in reports) foreach(var p in r.connection_passports ?? new List<QuantityConnectionPassport>()) {
   if(p.schema!="aframe_connection_passport/1" || p.status!="inventory_only") continue;
   var pZones=new HashSet<string>(p.zone_ids,StringComparer.Ordinal); pZones.IntersectWith(scope);
   foreach(var m in p.members) if(pZones.Contains(m.zone_id)) foreach(var s in m.supports) foreach(var id in s.bracket_element_ids) linked.Add(id);
   foreach(var e in r.elements) if(e.role=="bracket" && e.origin=="generated:ATFRAME:brackets" &&
    e.element_id.StartsWith(p.source_run_id+":brackets:",StringComparison.Ordinal) &&
    Zones(e).Count==1 && pZones.Contains(Zones(e).Single())) eligible[e.element_id]=e;
  }
  var orphans=eligible.Keys.Where(id=>!linked.Contains(id)).OrderBy(id=>id,StringComparer.Ordinal).ToArray();
  string text=string.Join("\n",table.Rows("").Select(row=>string.Join(" | ",row.Select(v=>Convert.ToString(v)).ToArray())).ToArray());
  var warnings=table.Messages.Where(m=>m.Contains("ID есть в проверенном составе, но отсутствует среди ссылок кандидатов опор этого паспорта.")).ToArray();
  Check(warnings.Length==(ExpectFixed?orphans.Length:0),name+": missing, duplicate or false addressed orphan warning");
  var continued=new Dictionary<string,string>(StringComparer.Ordinal);
  foreach(string warning in warnings.Where(m=>m.StartsWith("Монтажная принадлежность не определена: Запись ",StringComparison.Ordinal))) {
   string label=warning.Substring("Монтажная принадлежность не определена: ".Length).Split(new[]{". "},2,StringSplitOptions.None)[0];
   string recoveredId=Recover(table.Messages,label,"ID кронштейна");
   string recoveredZone=Recover(table.Messages,label,"зона");
   Check(!continued.ContainsKey(recoveredId),name+": duplicate continued physical identity");continued.Add(recoveredId,recoveredZone);
  }
  foreach(string id in orphans) {
   var matches=warnings.Where(m=>m.Contains("кронштейн "+id+"; зона «")).ToArray();
   Check(matches.Length+(continued.ContainsKey(id)?1:0)==(ExpectFixed?1:0),name+": physical orphan ID absent or repeated");
   if(ExpectFixed)Check(continued.ContainsKey(id)?continued[id]==Zones(eligible[id]).Single():matches[0].Contains("зона «"+Zones(eligible[id]).Single()+"»"),name+": orphan scope lost");
  }
  Check(warnings.All(m=>m.Length<=8000)&&table.Messages.Where(m=>m.StartsWith("Запись ",StringComparison.Ordinal)).All(m=>m.Length<=8000),name+": orphan warning cell unbounded");
  Check(diagnostics.UnlinkedBrackets==(ExpectFixed?orphans.Length:0),name+": diagnostic counter mismatch");
  Check(table.Members.Count(row=>Convert.ToString(row[10]).StartsWith("Элемент: ",StringComparison.Ordinal))==reports.SelectMany(r=>r.connection_passports??new List<QuantityConnectionPassport>())
   .Where(p=>p.status=="inventory_only").SelectMany(p=>p.members).Where(m=>scope.Contains(m.zone_id))
   .Select(m=>m.rail_element_id).Distinct().Count() || table.Members.All(row=>Convert.ToString(row[1])=="Паспорт не сформирован"),name+": physical member rows changed");
  if(path!=null)using(var file=new QuantityXlsxFile(path,"Соединения",QuantityTableView.FromConnections(table).Rows(""))) { file.Publish();file.Complete(); }

  if(before!=Json.Serialize(reports)) throw new Exception("source mutated");
  return new {name,core_ok=result.ok,selected,eligible_brackets=eligible.Count,linked_brackets=linked.Count,orphan_ids=orphans,
   orphan_ids_shown_in_table=orphans.Where(id=>text.Contains(id)).ToArray(),table_member_count=table.Members.Count,
   missing_passport_text=text.Contains("Паспорт не сформирован"),source_unchanged=true,
   physical_elements=reports.SelectMany(r=>r.elements).Select(e=>e.element_id).Distinct().Count(),
   counters=new {diagnostics.PhysicalElementsVisited,diagnostics.BracketZoneLinksVisited,diagnostics.PassportScopesVisited,
    diagnostics.EligibleBracketsVisited,diagnostics.MembersIndexed,diagnostics.SupportLinksIndexed,diagnostics.UnlinkedBrackets,diagnostics.CandidateLinksForAbsenceVisited},
   table_messages=table.Messages.ToArray(),core_issue_codes=result.issues.Select(i=>i.code).Distinct().ToArray()};
 }
 static string Recover(List<string> messages,string label,string field) {
  string prefix=label+" — "+field+", часть ";int next=1;var values=new List<string>();
  foreach(string message in messages.Where(m=>m.StartsWith(prefix,StringComparison.Ordinal))) {
   string tail=message.Substring(prefix.Length);int colon=tail.IndexOf(": ",StringComparison.Ordinal);
   Check(colon>0&&tail.Substring(0,colon)==next.ToString(System.Globalization.CultureInfo.InvariantCulture),"Long identity chunk order/gap/repetition");
   values.Add(tail.Substring(colon+2));next++;
  }
  Check(values.Count>0,"Long identity field missing");return string.Join("",values.ToArray());
 }
 static HashSet<string> Zones(QuantityElement e) {
  var zones=new HashSet<string>((e.zone_ids??new List<string>()).Where(z=>!string.IsNullOrWhiteSpace(z)),StringComparer.Ordinal);
  if(zones.Count==0&&!string.IsNullOrWhiteSpace(e.zone_id))zones.Add(e.zone_id);return zones;
 }
 static QuantityReport Scale(QuantityReport source,int count) {
  var report=Clone(source);report.elements.Clear();report.connection_passports.Clear();report.estimates.Clear();report.cutting.Clear();
  string original=source.connection_passports[0].source_run_id;
  for(int i=0;i<count;i++) {
   string run="synthetic"+i;var pass=Clone(source.connection_passports[0]);pass.source_run_id=run;pass.passport_id=run+":connections";
   foreach(var m in pass.members) {
    m.rail_element_id=m.rail_element_id.Replace(original+":",run+":");
    foreach(var s in m.supports)s.bracket_element_ids=s.bracket_element_ids.Select(id=>id.Replace(original+":",run+":")).ToList();
   }
   foreach(var j in pass.joints) {j.first_rail_element_id=j.first_rail_element_id.Replace(original+":",run+":");j.second_rail_element_id=j.second_rail_element_id.Replace(original+":",run+":");}
   report.connection_passports.Add(pass);
   foreach(var initial in source.elements) {
    var e=Clone(initial);e.element_id=e.element_id.Replace(original+":",run+":");
    foreach(var cad in e.cad_entities)cad.handle="S"+i+"_"+cad.handle;
    report.elements.Add(e);
   }
  }
  return report;
 }
 static QuantityReport LongIdentity(QuantityReport source,bool unicode) {
  var report=Clone(source);string old=report.connection_passports[0].source_run_id;
  string run=new string('R',13000),zone=unicode?string.Concat(Enumerable.Repeat("\U0001F600",10000)):new string('Z',20000);
  report.run_id=report.report_id=run;report.zone_ids=new List<string>{zone};report.estimates.Clear();report.cutting.Clear();
  foreach(var p in report.connection_passports) {
   p.source_run_id=run;p.passport_id=run+":connections";p.zone_ids=new List<string>{zone};
   foreach(var m in p.members) {m.rail_element_id=m.rail_element_id.Replace(old+":",run+":");m.zone_id=zone;
    foreach(var s in m.supports)s.bracket_element_ids=s.bracket_element_ids.Select(id=>id.Replace(old+":",run+":")).ToList();}
   foreach(var j in p.joints) {j.first_rail_element_id=j.first_rail_element_id.Replace(old+":",run+":");j.second_rail_element_id=j.second_rail_element_id.Replace(old+":",run+":");}
  }
  foreach(var e in report.elements) {e.element_id=e.element_id.Replace(old+":",run+":");e.zone_id=zone;e.zone_ids=new List<string>{zone};}
  return report;
 }
 static int Main(string[] args) {
  ExpectFixed=args.Length<3||args[2]!="baseline";
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
  var manual=Clone(report);manual.connection_passports=null;manual.run_id=manual.report_id="manual-run";
  manual.scope="manual_zone_contribution";manual.algorithm="manual-import/1";manual.estimates.Clear();manual.cutting.Clear();
  manual.elements.RemoveAll(e=>e.role!="bracket");
  for(int i=0;i<manual.elements.Count;i++) {manual.elements[i].origin="manual:fixture";manual.elements[i].element_id="manual:fixture:"+i;}
  cases.Add(Observe("manual_registration_without_passport",new[]{manual},manual.zone_ids.ToArray(),Path.Combine(dir,"manual_without_passport.xlsx")));
  if(args.Length<4||args[3]!="controls")foreach(string mutation in new[]{"foreign_source_run","manual_origin","duplicate_zone_ids","empty_zone_ids_fallback","display_zone_disagrees","unselected_zone","multiple_canonical_zones"}) {
   var r=Clone(report);var bracket=r.elements.First(e=>e.role=="bracket");
   if(mutation=="foreign_source_run")bracket.element_id="foreign:brackets:0";
   if(mutation=="manual_origin") {bracket.origin="manual:fixture";bracket.element_id="manual:fixture";}
   if(mutation=="duplicate_zone_ids")bracket.zone_ids=new List<string>{bracket.zone_id,bracket.zone_id," "};
   if(mutation=="empty_zone_ids_fallback")bracket.zone_ids.Clear();
   if(mutation=="display_zone_disagrees")bracket.zone_id="display-only-not-scope";
   if(mutation=="unselected_zone") {r.zone_ids.Add("unselected");bracket.zone_id="unselected";bracket.zone_ids=new List<string>{"unselected"};}
   if(mutation=="multiple_canonical_zones") {r.zone_ids.Add("other-zone");bracket.zone_ids.Add("other-zone");}
   cases.Add(Observe(mutation,new[]{r},(mutation=="multiple_canonical_zones"?r.zone_ids:report.zone_ids).ToArray(),Path.Combine(dir,mutation+".xlsx")));
  }
  if(args.Length<4||args[3]!="controls") {
   // Independent trust-boundary shape: the common core accepts distinct passport
   // aliases of one source run. Negative evidence must consume all their links.
   var noLinks=Clone(report);var linkedAlias=Clone(report);
   linkedAlias.report_id=linkedAlias.run_id="alias-report";
   var alias=linkedAlias.connection_passports[0];alias.passport_id+="-alias";
   foreach(var member in alias.members) {
    var rail=linkedAlias.elements.Single(e=>e.element_id==member.rail_element_id);
    string bracket=linkedAlias.elements.First(e=>e.role=="bracket"&&Zones(e).Contains(member.zone_id)).element_id;
    member.support_count=1;member.span_count=0;member.intervals_mm.Clear();
    member.bottom_free_mm=member.top_free_mm=rail.length_mm.Value/2;
    member.supports=new List<QuantityConnectionSupport>{new QuantityConnectionSupport{offset_mm=rail.length_mm.Value/2,bracket_element_ids=new List<string>{bracket}}};
   }
   alias.summary.support_positions=alias.members.Count;alias.summary.support_links=alias.members.Count;
   File.WriteAllText(Path.Combine(dir,"alias_no_links_report.json"),Json.Serialize(noLinks));
   File.WriteAllText(Path.Combine(dir,"alias_with_links_report.json"),Json.Serialize(linkedAlias));
   cases.Add(Observe("distinct_passport_alias_unlinked_first",new[]{noLinks,linkedAlias},report.zone_ids.ToArray(),Path.Combine(dir,"alias_unlinked_first.xlsx")));
   cases.Add(Observe("distinct_passport_alias_linked_first",new[]{linkedAlias,noLinks},report.zone_ids.ToArray(),Path.Combine(dir,"alias_linked_first.xlsx")));
  }
  var mismatch=Clone(report);mismatch.connection_passports[0].source_run_id="foreign";
  var refused=FacadeQuantitiesCore.BuildRows(new[]{mismatch},null,true,true);
  Check(!refused.ok&&refused.issues.Any(i=>i.code=="Q_CONNECTION_INVALID"),"Mismatched source run must fail common validation before table");
  if(args.Length>3&&args[3]=="scale")foreach(int count in new[]{1,100,1000}) {
   var scaled=Scale(report,count);var raw=Observe("scale_"+count,new[]{scaled},scaled.zone_ids.ToArray(),Path.Combine(dir,"scale_"+count+".xlsx"));
   var obj=(Dictionary<string,object>)Json.DeserializeObject(Json.Serialize(raw));
   var counters=(Dictionary<string,object>)obj["counters"];
   Check(Convert.ToInt32(counters["PhysicalElementsVisited"])==3*count&&Convert.ToInt32(counters["PassportScopesVisited"])==count&&
    Convert.ToInt32(counters["EligibleBracketsVisited"])==count&&Convert.ToInt32(counters["UnlinkedBrackets"])==count,
    "Orphan lookup is not one pass over elements/passport scopes/eligible IDs");
   cases.Add(raw);
  }
  if(args.Length>3&&args[3]=="scale")foreach(bool unicode in new[]{false,true}) {
   var longReport=LongIdentity(report,unicode);string name=unicode?"long_unicode_identity":"long_identity";
   File.WriteAllText(Path.Combine(dir,name+"_report.json"),Json.Serialize(longReport));
   cases.Add(Observe(name,new[]{longReport},longReport.zone_ids.ToArray(),Path.Combine(dir,name+".xlsx")));
  }
  File.WriteAllText(Path.Combine(dir,"cases.json"),Json.Serialize(new {status=ExpectFixed?"PASS":"REPRODUCED",checks,cases,live_autocad_checked=false}));
  Console.WriteLine("Scope cases observed: "+cases.Count);return 0;
 }
}
