using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using AFramePlugin;
class MountBaseline {
  static JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
  static int Main(string[] args) {
    var sample = FrameSolutionSelection.FromDict(json.DeserializeObject(File.ReadAllText(args[0])));
    sample.geometry.cladding_front_offset_mm = 230;
    sample.geometry.insulation_layers_mm = new List<double>{100,50};
    var local = new FrameNodeGeometryInput { profile_near_face_x_mm=170,
      clearance_surface=new FrameNodeClearanceSurface{kind=FrameNodeClearanceSurface.Layers} };
    var rows=new List<object>();
    foreach(var id in new[]{"declared_reference","short_bracket","long_bracket","different_nominal_widths","mixed_executions"}) {
      var s=FrameNodeGeometry.CloneSelection(sample);
      if(id=="short_bracket") {s.bracket.L_mm=50; s.extender.L_mm=100;}
      if(id=="long_bracket") {s.bracket.L_mm=350; s.extender.L_mm=150;}
      if(id=="different_nominal_widths") {s.bracket.nominal_width_mm=50; s.extender.nominal_width_mm=85;}
      if(id=="mixed_executions") s.extender.execution="corrosion_resistant";
      var result=FrameNodeGeometry.Evaluate(s,local);
      rows.Add(new {case_id=id, selection=s.ToDict(), input=local.ToDict(), result=result.ToDict(), review_text=result.ReviewText()});
      if(!result.CanInsert || result.ClearanceStatus!="pass" || result.GapText!="20") return 1;
    }
    File.WriteAllText(args[1],json.Serialize(new {status="BASELINE_REPRODUCED",scope="Existing local-plane check only; no assessment that these assemblies fit or fail",cases=rows}));
    return 0;
  }
}
