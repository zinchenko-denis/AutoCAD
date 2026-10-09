"""Exercise actual startup version checks with three tiny local bundle fixtures.

CAD doubles only deliver Idle and record the warning; no AutoCAD UI is claimed.
"""
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command, compile_probe

PROBE = r'''
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using FacadeSafety;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Autodesk.AutoCAD.ApplicationServices
{
    public class Document { public Editor Editor = new Editor(); }
    public class Editor {
        public string History = ""; public bool Fail;
        public void WriteMessage(string value) {
            if (Fail) throw new InvalidOperationException("editor unavailable");
            History += value;
        }
    }
    public class Documents { public Document MdiActiveDocument; }
    public static class Application
    {
        public static Documents DocumentManager = new Documents();
        public static event EventHandler Idle;
        public static int Alerts;
        public static bool FailAlert;
        public static string Warning;
        public static void RaiseIdle() { if (Idle != null) Idle(null, EventArgs.Empty); }
        public static int Subscribers { get { return Idle == null ? 0 : Idle.GetInvocationList().Length; } }
        public static void ShowAlertDialog(string warning) {
            if (FailAlert) throw new InvalidOperationException("alert unavailable");
            Alerts++; Warning = warning;
        }
    }
}

class Probe
{
    static int checks;
    static void Expect(bool value, string name)
    { checks++; if (!value) throw new Exception(name); }
    static readonly string[] Modules = { "AFacades", "AClad", "AFrame" };
    static Dictionary<string, List<string>> Files = new Dictionary<string, List<string>>();
    static string Metadata(string module) { return Path.Combine(Path.GetDirectoryName(Files[module][0]), "build-info.json"); }
    static void Write(string module, int build = 105, char sha = 'a', string label = null)
    {
        File.WriteAllText(Metadata(module), new JavaScriptSerializer().Serialize(new {
            bundle = label ?? module, build = build.ToString(), sha = new string(sha, 40) }));
    }
    static string Check() { return FacadeBundleVersions.FindProblem(Files); }
    static void Main(string[] args)
    {
        foreach (string module in Modules)
        {
            string dir = Path.Combine(args[0], module); Directory.CreateDirectory(dir);
            Files[module] = new List<string> { Path.Combine(dir, module + "Plugin.dll") }; Write(module);
        }
        Expect(Check() == null, "matched facade release must not warn");
        Files["ATableSpec"] = new List<string> { "unrelated missing DLL" };
        Files["ABlockGen"] = new List<string> { "unrelated missing DLL" };
        Expect(Check() == null, "unrelated modules must not affect facade compatibility");
        Write("AClad", 104);
        string warning = Check();
        Expect(warning != null && warning.Contains("№104") && warning.Contains("№105") && warning.Contains("AClad"), "build mismatch must identify old module");
        Write("AClad", 105, 'b');
        Expect(Check() != null, "same build with different commit must warn");
        Write("AClad"); File.Delete(Metadata("AFacades"));
        Expect(Check().Contains("нет build-info.json"), "old AFacades with no metadata must warn");
        File.WriteAllText(Metadata("AFacades"), "{broken");
        Expect(Check().Contains("не читается"), "damaged metadata must warn without crashing startup");
        Write("AFacades", label: "AClad");
        Expect(Check().Contains("не соответствует"), "metadata from another module must warn");
        Write("AFacades", 0);
        Expect(Check().Contains("номер сборки"), "invalid build must warn");
        Write("AFacades", 105, 'x');
        Expect(Check().Contains("коммит сборки повреждён"), "invalid SHA must warn");
        Write("AFacades"); var frame = Files["AFrame"]; Files.Remove("AFrame");
        Expect(Check().Contains("AFrame — не загружен"), "missing module must be identified");
        Files["AFrame"] = frame; frame.Add(frame[0]);
        Expect(Check().Contains("несколько копий"), "duplicate loaded module must warn");
        frame.RemoveAt(1); Expect(Check() == null, "complete matching set must recover");
        FacadeBundleVersions.Schedule(); FacadeBundleVersions.Schedule();
        Expect(AcApp.Subscribers == 1 && AcApp.Alerts == 0, "initialization must coalesce until Idle");
        AcApp.RaiseIdle();
        Expect(AcApp.Alerts == 0 && AcApp.Subscribers == 1, "no document: defer warning");
        AcApp.DocumentManager.MdiActiveDocument = new Autodesk.AutoCAD.ApplicationServices.Document();
        AcApp.RaiseIdle();
        Expect(AcApp.Alerts == 1 && AcApp.Subscribers == 0, "exactly one startup warning and detach Idle");
        Expect(AcApp.Warning.Contains("AFacades") && AcApp.Warning.Contains("AClad") && AcApp.Warning.Contains("AFrame") &&
            AcApp.Warning.Contains("Закройте AutoCAD") && AcApp.DocumentManager.MdiActiveDocument.Editor.History.Contains(AcApp.Warning), "actionable alert must also be in F2 history");
        FacadeBundleVersions.Schedule(); AcApp.RaiseIdle();
        Expect(AcApp.Alerts == 1 && AcApp.Subscribers == 0, "no repeated warning in the same AppDomain");
        // Common/*.cs is compiled separately into each plugin. Load three
        // independent copies to verify the AppDomain gate, not only one static.
        AppDomain.CurrentDomain.SetData("AutoCAD.Facades.BundleVersions.Checked", null);
        var applications = new List<Type>(); var checkers = new HashSet<Type>();
        for (int i = 0; i < 3; i++)
        {
            string dir = Path.Combine(args[0], "copy" + i); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "VersionProbe.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, path);
            var assembly = Assembly.LoadFile(path);
            var checker = assembly.GetType("FacadeSafety.FacadeBundleVersions"); checkers.Add(checker);
            var application = assembly.GetType("Autodesk.AutoCAD.ApplicationServices.Application");
            var documents = application.GetField("DocumentManager").GetValue(null);
            documents.GetType().GetField("MdiActiveDocument").SetValue(documents,
                Activator.CreateInstance(assembly.GetType("Autodesk.AutoCAD.ApplicationServices.Document")));
            checker.GetMethod("Schedule", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            applications.Add(application);
        }
        Expect(checkers.Count == 3 && !checkers.Contains(typeof(FacadeBundleVersions)), "three independent linked helper copies");
        int alerts = 0, subscriptions = 0;
        foreach (var application in applications)
        {
            application.GetMethod("RaiseIdle").Invoke(null, null);
            alerts += (int)application.GetField("Alerts").GetValue(null);
            subscriptions += (int)application.GetProperty("Subscribers").GetValue(null, null);
        }
        Expect(alerts == 1 && subscriptions == 0, "three assemblies must show one warning and detach all callbacks");
        // A startup diagnostic must not throw out of Idle and skip other plugins.
        AppDomain.CurrentDomain.SetData("AutoCAD.Facades.BundleVersions.Checked", null);
        var editor = AcApp.DocumentManager.MdiActiveDocument.Editor;
        editor.Fail = true;
        int previousAlerts = AcApp.Alerts;
        bool escaped = false, nextCallback = false;
        EventHandler observer = delegate { nextCallback = true; };
        FacadeBundleVersions.Schedule(); AcApp.Idle += observer;
        try { AcApp.RaiseIdle(); } catch (Exception) { escaped = true; }
        finally { AcApp.Idle -= observer; }
        Expect(!escaped && nextCallback, "unavailable Editor must not abort later Idle callbacks");
        Expect(AcApp.Alerts == previousAlerts + 1 && AcApp.Subscribers == 0,
            "unavailable Editor preserves the version alert and detaches the completed check");
        editor.Fail = false;
        AppDomain.CurrentDomain.SetData("AutoCAD.Facades.BundleVersions.Checked", null);
        AcApp.FailAlert = true;
        string previousHistory = editor.History;
        FacadeBundleVersions.Schedule(); AcApp.RaiseIdle();
        string addedHistory = editor.History.Substring(previousHistory.Length);
        Expect(addedHistory.Contains("Фасадные модули установлены") &&
            !addedHistory.Contains("Не удалось проверить версии"),
            "unavailable alert preserves the real F2 result without claiming the version check failed");
        Expect(AcApp.Subscribers == 0, "unavailable alert does not retain an Idle callback");
        // Both output channels may fail, but this remains a contained diagnostic failure.
        AppDomain.CurrentDomain.SetData("AutoCAD.Facades.BundleVersions.Checked", null);
        editor.Fail = true; escaped = false; nextCallback = false;
        FacadeBundleVersions.Schedule(); AcApp.Idle += observer;
        try { AcApp.RaiseIdle(); } catch (Exception) { escaped = true; }
        finally { AcApp.Idle -= observer; }
        Expect(!escaped && nextCallback && AcApp.Subscribers == 0,
            "failure of both diagnostic channels cannot abort startup callbacks");
        editor.Fail = false; AcApp.FailAlert = false;
        Console.WriteLine("PASS: " + checks + " bundle compatibility/startup checks");
    }
}
'''


def main():
    if not available():
        raise SystemExit("mono/mcs or Windows dotnet is required; checks were not run")
    with tempfile.TemporaryDirectory(prefix="facade_versions_") as temp:
        output = Path(temp)
        probe = output / "VersionProbe.cs"
        probe.write_text(PROBE, encoding="utf-8")
        executable = output / "VersionProbe.exe"
        result = compile_probe([ROOT / "Common/FacadeBundleVersions.cs", probe], ["System.Web.Extensions"], executable)
        if result.returncode:
            raise SystemExit(result.stdout + result.stderr)
        subprocess.run(command(executable) + [temp], check=True)


if __name__ == "__main__":
    main()
