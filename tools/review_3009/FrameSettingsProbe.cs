// Independent review 30.09: changes of frame type must preserve material mass.
// Build with the production FrameSettings.cs (no AutoCAD dependency):
// mcs -r:System.Web.Extensions.dll -out:/tmp/frame-settings-probe.exe tools/review_3009/FrameSettingsProbe.cs AFrame/src/AFramePlugin/FrameSettings.cs
using System;
using AFramePlugin;

static class FrameSettingsProbe
{
    static int Main()
    {
        int failures = 0;
        foreach (string material in new[] { "porcelain", "composite", "concrete", "clinker" })
        {
            var settings = new FrameSettings { Cladding = material, QClad = 25 };
            settings.SetSubType("interfloor");
            double sentMass = (double)settings.CalcDict()["q_clad"];
            bool valid = settings.Validate(true) == null;
            Console.WriteLine("{0}: explicitly entered 25 kg/m2 -> {1} kg/m2, validation={2}",
                              material, sentMass, valid ? "accepted" : "rejected");
            if (sentMass != 25) failures++;
        }
        Console.WriteLine("Unexpected material-mass changes: {0}", failures);
        return failures == 0 ? 0 : 1;
    }
}
