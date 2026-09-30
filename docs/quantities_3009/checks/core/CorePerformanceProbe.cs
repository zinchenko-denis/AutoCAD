using System; using System.Collections.Generic; using System.Diagnostics; using System.Text; using System.Web.Script.Serialization; using FacadeSafety;
class Perf {
 static void Main(string[] args) {
  int n=args.Length>0?int.Parse(args[0]):150000; bool rings=args.Length>1&&args[1]=="rings";
  var r=new QuantityReport{report_id=Guid.NewGuid().ToString("N"),run_id=Guid.NewGuid().ToString("N"),document_id=Guid.NewGuid().ToString("N"),algorithm="ATTILE/1",zone_ids=new List<string>{"Ф-1"},scope="Ф-1"};
  var sw=Stopwatch.StartNew();
  for(int i=0;i<n;i++){int variant=i%8; double w=600-variant*25, h=300; var ring=new QuantityRing{points=new[]{new[]{(i%1000)*650.0,(i/1000)*350.0},new[]{(i%1000)*650.0+w,(i/1000)*350.0},new[]{(i%1000)*650.0+w,(i/1000)*350.0+h},new[]{(i%1000)*650.0,(i/1000)*350.0+h}}}; QuantityShape s;string why;FacadeQuantitiesCore.TryShape(new[]{ring},out s,out why);
   r.elements.Add(new QuantityElement{element_id=r.run_id+":"+i,zone_ids=new List<string>{"Ф-1"},type="Керамогранит",material="Керамогранит",color="CAD ACI 7",orientation="front",origin="generated:ATTILE",piece_kind=variant==0?"full":"cut",width_mm=w,height_mm=h,area_mm2=w*h,shape_id=s.shape_id,rings=rings?new List<QuantityRing>{ring}:new List<QuantityRing>(),cad_entities=new List<QuantityCadEntity>{new QuantityCadEntity{handle=(12345+i).ToString("X"),role="primary",fingerprint=new string('a',64)}}});}
  double generated=sw.Elapsed.TotalSeconds;sw.Restart();var result=FacadeQuantitiesCore.BuildRows(new[]{r},null,true,false);double core=sw.Elapsed.TotalSeconds;long managed=GC.GetTotalMemory(true);sw.Restart();var json=new JavaScriptSerializer{MaxJsonLength=int.MaxValue}.Serialize(r);double serialize=sw.Elapsed.TotalSeconds;
  Console.WriteLine(new JavaScriptSerializer().Serialize(new{count=n,rings=rings,generated_s=generated,core_s=core,serialize_s=serialize,ok=result.ok,rows=result.rows.Count,issues=result.issues.Count,json_bytes=Encoding.UTF8.GetByteCount(json),managed_bytes=managed,peak_working_set=Process.GetCurrentProcess().PeakWorkingSet64}));
 }
}
