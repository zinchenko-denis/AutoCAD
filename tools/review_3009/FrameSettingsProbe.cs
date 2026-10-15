// Independent review 30.09: changes of frame type must preserve material mass.
// Build with the production FrameSettings.cs (no AutoCAD dependency):
// mcs -r:System.Web.Extensions.dll -out:/tmp/frame-settings-probe.exe tools/review_3009/FrameSettingsProbe.cs AFrame/src/AFramePlugin/FrameSettings.cs
using System;
using System.Collections.Generic;
using AFramePlugin;

static class FrameSettingsProbe
{
    static int Main()
    {
        int failures = 0;
        foreach (string material in new[] { "porcelain", "composite", "concrete", "clinker" })
        {
            foreach (double mass in new[] { 8.0, 25.0, 42.0 })
            {
                var settings = new FrameSettings { Cladding = material, QClad = mass, Mode = "frame" };
                foreach (string sub in new[] { "interfloor", "ortho", "vertical" })
                {
                    settings.SetSubType(sub);
                    double sentMass = (double)settings.CalcDict()["q_clad"];
                    double gamma = (double)settings.CalcDict()["gamma_clad"];
                    string validation = settings.Validate(true);
                    bool expectedValidation = sub == "interfloor"
                        ? validation != null && validation.Contains("Автоматический расчёт межэтажной")
                          && validation.Contains("неподвижные/подвижные")
                        : validation == null;
                    if (sentMass != mass || gamma != (material == "composite" ? 1.2 : 1.1)
                        || !expectedValidation) failures++;
                }
            }
            Console.WriteLine("{0}: mass and material factor checked across 3 masses x 3 frame types", material);
        }
        var legacy = FrameSettings.FromDict(new Dictionary<string, object> { { "sub_type", "interfloor" } });
        if (legacy.QClad != 25) failures++;
        var explicitOld = FrameSettings.FromDict(new Dictionary<string, object>
                            { { "sub_type", "interfloor" }, { "q_clad", 8.0 } });
        if (explicitOld.QClad != 8) failures++;
        foreach (string mode in new[] { "all", "clamps" })
            if (new FrameSettings { Cladding = "composite", Mode = mode }.Validate(true) == null) failures++;
        var nsp2 = new FrameSettings { SubType = "interfloor", Profile = "НСП-2", Mode = "frame" };
        if (nsp2.Validate(true) == null) failures++;
        nsp2.Steps = "manual";
        if (nsp2.Validate(true) != null || !nsp2.Describe(true).Contains("не проверяется")) failures++;
        if (new FrameSettings { QClad = double.NaN }.Validate(true) == null) failures++;
        var inter = new FrameSettings { Mode = "frame" };
        inter.SetSubType("interfloor");
        if (inter.Validate(true) == null || !inter.Describe(true).Contains("не подтверждены")) failures++;
        inter.Height = 0;
        if (inter.Validate(true) == null || !inter.Validate(true).Contains("Высота")) failures++;
        inter.Height = 30;
        inter.Steps = "manual";
        if (inter.Validate(true) != null || inter.EngineParams(1).ContainsKey("calc")) failures++;
        inter.Steps = "calc";
        inter.Mode = "clamps";
        if (inter.Validate(true) != null || inter.EngineParams(1).ContainsKey("calc")) failures++;
        var gp60 = new FrameSettings { Profile = "ГП-60-40", Mode = "frame" };
        if (gp60.Validate(true) == null || !gp60.Describe(true).Contains("масса")) failures++;
        gp60.Steps = "manual";
        if (gp60.Validate(true) != null || !gp60.Describe(true).Contains("не проверяется")) failures++;
        gp60.Steps = "calc";
        gp60.Mode = "clamps";
        if (gp60.Validate(true) != null) failures++;
        // External-corner geometry must use a separate, explicitly supplied L.
        var unsetOffset = FrameSettings.FromDict(new Dictionary<string, object> { { "offset", 777.0 } });
        if (unsetOffset.EffectiveCladdingOffset.HasValue || unsetOffset.EngineParams(0).ContainsKey("cladding_offset")) failures++;
        foreach (string sub in new[] { "vertical", "ortho", "interfloor" })
            foreach (string mode in new[] { "manual", "calc" })
            {
                var corners = new FrameSettings { SubType = sub, Steps = mode, CladdingOffset = 345.75, Offset = 123.0 };
                var parameters = corners.EngineParams(0);
                if ((double)parameters["cladding_offset"] != 345.75 || corners.Clone().CladdingOffset != 345.75) failures++;
                if (mode == "calc" && (double)((Dictionary<string, object>)parameters["calc"])["offset"] != 123.0) failures++;
                if (mode == "manual" && parameters.ContainsKey("calc")) failures++;
                bool expectedChannel = sub == "vertical";
                if (parameters.ContainsKey("corner_channel_outer_overhang") != expectedChannel ||
                    parameters.ContainsKey("corner_channel_y_offset") != expectedChannel) failures++;
                if (expectedChannel && ((double)parameters["corner_channel_outer_overhang"] != 50.0 ||
                    (double)parameters["corner_channel_y_offset"] != 0.0 ||
                    (double)corners.Clone().EngineParams(0)["corner_channel_outer_overhang"] != 50.0 ||
                    !corners.Describe(true).Contains("без фактических монтажных смещений"))) failures++;
                corners.AskCorners = false;
                if (corners.EngineParams(0).ContainsKey("corner_channel_outer_overhang") ||
                    corners.EngineParams(0).ContainsKey("corner_channel_y_offset")) failures++;
                corners.AskCorners = true;
                corners.Mode = "clamps";
                if (corners.EngineParams(0).ContainsKey("cladding_offset") ||
                    corners.EngineParams(0).ContainsKey("corner_channel_outer_overhang") ||
                    corners.EngineParams(0).ContainsKey("corner_channel_y_offset")) failures++;
            }
        if (unsetOffset.EngineParams(0).ContainsKey("corner_channel_outer_overhang") ||
            unsetOffset.EngineParams(0).ContainsKey("corner_channel_y_offset")) failures++;
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -1.0 })
            if (new FrameSettings { CladdingOffset = invalid, Steps = "manual" }.Validate(true) == null) failures++;
        if (new FrameSettings { CladdingOffset = 0, Steps = "manual" }.Validate(true) != null) failures++;
        var explicitProjectOffset = new FrameSettings { CladdingOffset = 456.0, Offset = 123.0,
            SolutionSelection = new FrameSolutionSelection { geometry = new FrameSolutionGeometry { cladding_front_offset_mm = 321.25 } } };
        if (explicitProjectOffset.EffectiveCladdingOffset != 321.25 || explicitProjectOffset.CladdingOffset != 456.0 ||
            explicitProjectOffset.Offset != 123.0) failures++;
        explicitProjectOffset.SolutionSelection = null;
        if (explicitProjectOffset.EffectiveCladdingOffset != 456.0) failures++;
        if (FrameSettings.HasUsableAxisInput(false, "vertical", 0, 0) ||
            !FrameSettings.HasUsableAxisInput(false, "vertical", 0, 1) ||
            !FrameSettings.HasUsableAxisInput(false, "vertical", 1, 0) ||
            !FrameSettings.HasUsableAxisInput(true, "vertical", 0, 0) ||
            FrameSettings.HasUsableAxisInput(false, "ortho", 0, 1) ||
            FrameSettings.HasUsableAxisInput(false, "interfloor", 0, 1)) failures++;
        Console.WriteLine("Separate external-corner offset: manual/calc, all 3 schemes, serialization, missing/zero/invalid values checked");
        Console.WriteLine("Domain settings failures: {0}", failures);
        return failures == 0 ? 0 : 1;
    }
}
