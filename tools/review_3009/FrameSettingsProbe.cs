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
                    if (sentMass != mass || gamma != (material == "composite" ? 1.2 : 1.1)
                        || settings.Validate(true) != null) failures++;
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
        Console.WriteLine("Domain settings failures: {0}", failures);
        return failures == 0 ? 0 : 1;
    }
}
