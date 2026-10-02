"""Run the actual C# producer, saved DTO, quantities and Excel warning path.

CAD links are doubles; this does not claim native AutoCAD drawing or UI testing.
No large fixtures or generated logs are kept in the repository.
"""
import argparse
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'tools/quantities_3009'))
from probe_runtime import available, command, compile_probe

PROBE = r'''
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;
using AFramePlugin;
using AFacadesPlugin;
class Probe {
    static int count;
    static JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    static Dictionary<string, object> D(params object[] pairs) {
        var d = new Dictionary<string, object>(); for (int i=0;i<pairs.Length;i+=2) d[(string)pairs[i]]=pairs[i+1]; return d;
    }
    static int Main(string[] args) {
        var issue = D("kind","rail","member_index",0,"zone_id","P1","x",100.0,"y0",0.0,"y1",500.0,
            "status","not_verified","reason","insufficient_supports","support_count",1,
            "message","Один кронштейн посередине; расчёт схемы требует проверки инженером.");
        var response = D("ok",true,"summary",D(),"calculation_status","not_requested",
            "per_zone",new object[]{D("zone_id","P1")},
            "rails",new object[]{D("zone","P1","x",100.0,"y0",0.0,"y1",500.0,"len",500.0),
                D("zone","P1","x",1000.0,"y0",0.0,"y1",3000.0,"len",3000.0)},
            "hrails",new object[0],"brackets",new object[]{D("zone","P1","kind","рядовой")},
            "clamps",new object[0],"fittings",new object[0],"local_issues",new object[]{issue});
        var mapping = new Dictionary<string,string>{{"P1","Зона-А"}};
        var report = FrameQuantities.BuildReport(response,D("system","Вектор-1"),mapping);
        Check(report.elements.Count==3,"Review marker became physical material or lost a member");
        Check(report.engineering_coverage=="geometry_with_unverified_members","Manual issue was promoted to verified calculation");
        var warning=report.issues.Single(x=>x.code=="Q_FRAME_MEMBER_REVIEW");
        Check(warning.element_id==report.elements[0].element_id,"Issue mapped to wrong physical member");
        Check(warning.message.Contains("Зона-А") && warning.message.Contains("X=100") && warning.message.Contains("Y=0…500") &&
            warning.message.Contains("расчёт не подтверждён"),"Coordinate, root zone or status lost from user list");
        report.document_id="test-dwg";
        foreach(var e in report.elements) e.cad_entities.Add(new QuantityCadEntity {
            handle=e.element_id,fingerprint="cad-double",role="outer" });
        // Same serializer as storage DTO: reopening may not erase or improve an issue.
        report=json.Deserialize<QuantityReport>(json.Serialize(report));
        Check(report.issues.Single(x=>x.code=="Q_FRAME_MEMBER_REVIEW").message==warning.message,"Reopen lost member issue");
        var bill=FacadeQuantitiesCore.BuildRows(new[]{report},null,true,false);
        Check(bill.ok && bill.completeness=="partial" && bill.rows.Sum(x=>x.quantity)==3,"Warning prevents useful quantities or becomes success");
        var table=FrameTableData.Build(bill,new[]{report},new[]{"Зона-А"},new string[0],true);
        Check(!table.Complete && table.Warnings.Contains(warning.message),"Table hides persisted warning");
        Check(table.Information.Any(x=>x.Contains("красный цвет служит пометкой")),"Red mark mistaken for product colour");
        var rows=table.Rows("");
        using (var file = new QuantityXlsxFile(Path.Combine(args[0],"local_issues.xlsx"),"Подсистема",rows)) { file.Publish(); file.Complete(); }
        // The real retention helper is used by Store for an unchanged rail during clamps-only.
        var next=new QuantityReport();
        FrameQuantities.AppendRetainedIssues(report,new HashSet<string>{report.elements[0].element_id},next.issues);
        var again=new List<QuantityIssue>();
        FrameQuantities.AppendRetainedIssues(json.Deserialize<QuantityReport>(json.Serialize(next)),
            new HashSet<string>{report.elements[0].element_id},again);
        Check(next.issues.Count==1 && again.Count==1 && again[0].message==warning.message,"Repeated clamps update loses/duplicates issue");
        var deleted=new List<QuantityIssue>();
        FrameQuantities.AppendRetainedIssues(report,new HashSet<string>(),deleted);
        Check(deleted.Count==0,"Removed rail leaves stale warning");
        issue["status"]="failed";issue["message"]="Превышена несущая способность анкера.";
        response["calc_report"]=D();response["calculation_status"]="partial";
        var partial=FrameQuantities.BuildReport(response,D("system","Вектор-1"),mapping);
        Check(partial.elements.Count==3 && partial.issues.Any(x=>x.code=="Q_FRAME_MEMBER_REVIEW" && x.message.Contains("расчёт не пройден")),
            "Failed calculated member hides geometry or failure status");
        foreach(var mutation in new Action[]{()=>issue["member_index"]=100,()=>{issue["member_index"]=0;issue["zone_id"]="P2";},
            ()=>{issue["zone_id"]="P1";issue["y1"]=600.0;}}) {
            mutation();bool rejected=false;try {FrameQuantities.BuildReport(response,D(),mapping);} catch(InvalidOperationException){rejected=true;}
            Check(rejected,"Corrupt issue could colour/list the wrong member");
        }
        foreach(var mode in new[]{"missing","null","empty"}) {
            if(mode=="missing") response.Remove("local_issues");
            else response["local_issues"]=mode=="null"?null:new object[0];
            bool rejected=false;
            try { FrameQuantities.BuildReport(response,D(),mapping); }
            catch(InvalidOperationException ex) { rejected=ex.Message.Contains("не передал список проблемных участков"); }
            Check(rejected,"Partial calculation without issue list was promoted: "+mode);
        }
        response.Remove("calculation_status");response.Remove("local_issues");
        Check(FrameQuantities.BuildReport(response,D(),mapping).elements.Count==3,"Legacy successful response no longer readable");
        Console.WriteLine("Local member issue producer/table checks: "+count+" PASS; CAD links are doubles");return 0;
    }
}
'''


def method(source, name):
    start = source.rfind('        private static ', 0, source.index(name))
    brace = source.index('{', start)
    depth, end = 1, brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


def test_drawing_and_retention(out):
    # Execute the actual small marker writer with CAD doubles. It intentionally
    # receives no quantity producer: annotation cannot become a physical part.
    source = (ROOT / 'AFrame/src/AFramePlugin/FrameCommand.cs').read_text(encoding='utf-8')
    marker_start = source.index('        private static void MarkRailForReview(')
    methods = method(source[marker_start:], 'MarkRailForReview') + '\n' + method(source[source.index('        private static void Remember('):], 'Remember') + '\n' + method(source[source.index('        private static void PrintCalcReport('):], 'PrintCalcReport')
    probe = r"""
using System;
using System.Collections.Generic;
using System.Text;
class Entity { public short ColorIndex; public string Layer,Handle; }
class BlockReference:Entity {} class Polyline:Entity {}
class Point3d { public double X,Y,Z; public Point3d(double x,double y,double z){X=x;Y=y;Z=z;} }
class Vector3d { public static Vector3d ZAxis=new Vector3d(); }
class Circle:Entity { public Point3d Center; public double Radius; public Circle(Point3d c,Vector3d z,double r){Center=c;Radius=r;} }
class Transaction { public int Written; public void AddNewlyCreatedDBObject(Entity e,bool add){Written++;} }
class BlockTableRecord { public List<Entity> Items=new List<Entity>(); public void AppendEntity(Entity e){e.Handle=(Items.Count+1).ToString();Items.Add(e);} }
class Editor { public string Output=""; public void WriteMessage(string s){Output+=s;} }
class Drawing {
    static object Get(Dictionary<string,object> d,string key){return d!=null && d.ContainsKey(key)?d[key]:null;}
    static string SafeStr(object value){return Convert.ToString(value);}
    const string LayerReview="_01_ПС_ПРОВЕРИТЬ";
    __METHODS__
    static void Need(bool ok,string reason){if(!ok)throw new Exception(reason);}
    static int Main(){
        var tr=new Transaction();var ms=new BlockTableRecord();var handles=new Dictionary<string,List<string>>();
        var roots=new Dictionary<string,string>{{"P1","Z"}};
        var poly=new Polyline();MarkRailForReview(tr,ms,poly,100,0,500,40,handles,roots,"P1");
        Need(poly.ColorIndex==1 && ms.Items.Count==0,"Polyline lost red or annotation counted twice");
        var block=new BlockReference();MarkRailForReview(tr,ms,block,100,0,500,40,handles,roots,"P1");
        var ring=ms.Items[0] as Circle;
        Need(block.ColorIndex==1 && ring!=null && ring.ColorIndex==1,"Block fixed child colours can hide review mark");
        Need(ring.Layer==LayerReview && ring.Layer!="_01_ПС_НАПРАВЛЯЮЩИЕ","Marker enters existing-rail selection");
        Need(ring.Center.X==100 && ring.Center.Y==250 && ring.Radius==60,"Marker placed on wrong member");
        Need(tr.Written==1 && handles["Z"].Count==1 && handles["Z"][0]==ring.Handle,"Marker orphaned outside replacement/cleanup handles");
        var report=new Dictionary<string,object>{{"row",new Dictionary<string,object>{{"w_p",32},{"checks",new object[]{
            new Dictionary<string,object>{{"name","fixture"},{"value",1},{"limit",2}}}}}}};
        var ed=new Editor();PrintCalcReport(ed,report,true);
        Need(!ed.Output.Contains("условия данной арифметической цепочки выполнены") && ed.Output.Contains("отдельные участки не прошли"),
            "Partial multi-zone issue displayed as successful calculation");
        ed=new Editor();PrintCalcReport(ed,report,false);
        Need(ed.Output.Contains("условия данной арифметической цепочки выполнены"),"Successful chain status lost");
        Console.WriteLine("Actual review marker and calculation text: 7 PASS; CAD doubles, not AutoCAD");return 0;
    }
}
"""
    driver = out / 'Drawing.cs'; driver.write_text(probe.replace('__METHODS__', methods), encoding='utf-8')
    exe = out / 'Drawing.exe'
    compiled = compile_probe([driver], [], exe)
    assert compiled.returncode == 0, compiled.stdout + compiled.stderr
    subprocess.run(command(exe), check=True)

    # Reuse the existing actual constructor/Store extraction with one added
    # regression. Capture persistence, then round-trip and update clamps again.
    sys.path.insert(0, str(ROOT / 'tools/catalog_connections_0110'))
    import test_clamps_manual_boundary as boundary
    addition = r"""
        Check("member warning survives two actual Store updates", delegate {
            var old=Old("review",false);
            old.engineering_coverage="geometry_with_unverified_members";
            old.issues.Add(new QuantityIssue { code="Q_FRAME_MEMBER_REVIEW", element_id="review:rails:0",
                message="X=100, Y=0…500: расчёт не подтверждён", severity="warning" });
            Store(old);
            var saved=FacadeQuantityStore.Saved;
            Need(saved!=null && FacadeQuantityStore.Unavailable==null,"Clamps update refused useful frame");
            Need(saved.issues.Count(x=>x.code=="Q_FRAME_MEMBER_REVIEW")==1 && saved.engineering_coverage=="geometry_with_unverified_members",
                "First actual Store lost warning/coverage");
            Store(Json.Deserialize<QuantityReport>(Json.Serialize(saved)));
            saved=FacadeQuantityStore.Saved;
            Need(saved!=null && saved.issues.Count(x=>x.code=="Q_FRAME_MEMBER_REVIEW")==1 &&
                saved.issues.Single(x=>x.code=="Q_FRAME_MEMBER_REVIEW").message.Contains("Y=0…500"),"Second actual Store lost/duplicated warning");
            Need(saved.engineering_coverage=="geometry_with_unverified_members","Clamps operation promoted unverified member");
        });
"""
    marker = '        Console.WriteLine(Json.Serialize(new { status = "PASS", checks = Results.Count, cases = Results }));'
    assert boundary.PROBE.count(marker) == 1
    boundary.PROBE = boundary.PROBE.replace(marker, addition + marker)
    saved_args = sys.argv
    try:
        sys.argv = [boundary.__file__, '--out', str(out / 'clamps_store')]
        assert boundary.main() == 0
    finally:
        sys.argv = saved_args


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--out', type=Path)
    args = parser.parse_args()
    if not available():
        print('BLOCKED: native .NET test compiler/runtime unavailable')
        return 2
    out = (args.out or Path(tempfile.mkdtemp(prefix='local_issues_'))).resolve()
    out.mkdir(parents=True, exist_ok=True)
    producer = ROOT / 'AFrame/src/AFramePlugin/FrameQuantities.cs'
    source = producer.read_text(encoding='utf-8')
    marker = '        internal static QuantityReport BuildReport('
    assert source.count(marker) == 1, 'Actual producer extraction requires review'
    categories = re.findall(r'(?m)^\s*private static readonly string\[\] Categories = .*?;', source)
    assert len(categories) == 1
    prefix = '\n'.join(line for line in source.splitlines() if line.startswith('using ') and 'Autodesk.' not in line)
    extracted = out / 'Producer.cs'
    extracted.write_text(prefix+'\nnamespace AFramePlugin { internal sealed class FrameQuantities {\n'+categories[0]+'\n'+source[source.index(marker):], encoding='utf-8')
    driver = out / 'Probe.cs'
    driver.write_text(PROBE, encoding='utf-8')
    sources = [driver, extracted, ROOT / 'Common/FacadeQuantities.cs']
    sources += [ROOT / 'Facades/src/AFacadesPlugin' / p for p in ('FrameTableData.cs','QuantityXlsxFile.cs','XlsxWriter.cs')]
    exe = out / 'Probe.exe'
    compiled = compile_probe(sources, ['System.Web.Extensions','System.Xml','System.IO.Compression','System.IO.Compression.FileSystem'], exe)
    if compiled.returncode:
        print(compiled.stdout+compiled.stderr)
        return 1
    subprocess.run(command(exe)+[str(out)],check=True)
    from openpyxl import load_workbook
    rows = list(load_workbook(out/'local_issues.xlsx')['Подсистема'].values)
    assert any('Зона-А' in str(row[0]) and 'Y=0…500' in str(row[0]) and 'расчёт не подтверждён' in str(row[0]) for row in rows), 'Excel lost coordinate/status warning'
    assert any('красный цвет служит пометкой' in str(row[0]) for row in rows), 'Excel lost diagnostic-colour note'
    print('Local member issue Excel checks: 2 PASS; live AutoCAD NOT_RUN')
    test_drawing_and_retention(out)
    return 0

if __name__ == '__main__':
    raise SystemExit(main())
