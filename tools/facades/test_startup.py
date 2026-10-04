"""Run actual plugin startup and classic menu callbacks with CAD doubles.

Failures are injected at the UI boundary; this checks isolation/diagnostics,
not that a particular AutoCAD load error has been reproduced in the host.
"""
from pathlib import Path
import json
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools/quantities_3009"))
from probe_runtime import available, command, compile_probe

MODULES = (("Facades", "AFacades"), ("AClad", "AClad"), ("AFrame", "AFrame"))

STARTUP = r'''
using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;
namespace Autodesk.AutoCAD.Runtime {
 [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
 public class ExtensionApplicationAttribute : Attribute { public ExtensionApplicationAttribute(Type t) {} }
 public interface IExtensionApplication { void Initialize(); void Terminate(); }
}
namespace Autodesk.AutoCAD.ApplicationServices {
 public class Document { public Editor Editor = new Editor(); }
 public class Editor {
  public string History = ""; public bool Fail;
  public void WriteMessage(string message) { if (Fail) throw new InvalidOperationException("editor unavailable"); History += message; }
 }
 public class Documents { public Document MdiActiveDocument; }
 public static class Application {
  public static Documents DocumentManager = new Documents();
  public static event EventHandler Idle;
  public static int Subscribers { get { return Idle == null ? 0 : Idle.GetInvocationList().Length; } }
  public static void RaiseIdle() { if (Idle != null) Idle(null, EventArgs.Empty); }
 }
}
namespace FacadeSafety {
 internal static class FacadeBundleVersions {
  public static bool Fail;
  public static void Schedule() { if (Fail) throw new InvalidOperationException("versions unavailable"); }
  public static void Cancel() { }
 }
}
public static class Ui {
 public static bool BrokenRibbon = true, BrokenClassic;
 public static int RibbonCalls, ClassicCalls, Cleaned; public static bool BrokenCleanup;
 public static void RibbonCleanup() { Cleaned++; if (BrokenCleanup) throw new InvalidOperationException("ribbon cleanup failed"); }
 public static void Ribbon() { RibbonCalls++; if (BrokenRibbon) throw new TypeInitializationException("Ribbon", new Exception("bad image")); }
 public static void Classic() { ClassicCalls++; if (BrokenClassic) throw new InvalidOperationException("menu unavailable"); }
}
FAKE_UI
class Probe {
 static int checks;
 static void Expect(bool condition, string name) { checks++; if (!condition) throw new Exception(name); }
 static int Count(string text, string needle) { return (text.Length - text.Replace(needle, "").Length) / needle.Length; }
 static void Main() {
  IExtensionApplication[] plugins = { new AFacadesPlugin.Plugin(), new ACladPlugin.Plugin(), new AFramePlugin.Plugin() };
  string[] names = { "AFacades", "AClad", "AFrame" };
  for (int i=0; i<plugins.Length; i++) {
   plugins[i].Initialize();
   Expect(Ui.ClassicCalls == i+1, names[i] + " classic initializer survives broken ribbon initializer");
  }
  Expect(AcApp.Subscribers == 1, "diagnostics wait for document with a single Idle callback");
  AcApp.RaiseIdle(); Expect(AcApp.Subscribers == 1, "no active DWG: keep diagnostic queued");
  AcApp.DocumentManager.MdiActiveDocument = new Autodesk.AutoCAD.ApplicationServices.Document();
  var editor = AcApp.DocumentManager.MdiActiveDocument.Editor;
  editor.Fail = true; AcApp.RaiseIdle();
  Expect(AcApp.Subscribers == 1, "temporarily unavailable editor must not lose queued diagnostics");
  editor.Fail = false; AcApp.RaiseIdle();
  Expect(AcApp.Subscribers == 0, "successful F2 report detaches Idle callback");
  foreach (var name in names)
   Expect(editor.History.Contains(name + " (") && editor.History.Contains("сборка №108") &&
       editor.History.Contains("TypeInitializationException") && editor.History.Contains("Причина: Exception: bad image"), name + " diagnostic identifies module/build/type");
  Expect(Count(editor.History, "не удалось загрузить «лента»") == 3, "one ribbon failure per module");
  foreach (var plugin in plugins) plugin.Initialize();
  Expect(Ui.ClassicCalls == 6 && Count(editor.History, "не удалось загрузить «лента»") == 3,
      "repeated initializer retries other UI without duplicate warnings");
  Ui.BrokenRibbon = false; Ui.BrokenClassic = true;
  foreach (var plugin in plugins) plugin.Initialize();
  Expect(Ui.RibbonCalls == 9 && Count(editor.History, "не удалось загрузить «классическое меню»") == 3,
      "classic failure preserves ribbon and reports its own stage");
  FacadeSafety.FacadeBundleVersions.Fail = true; Ui.BrokenClassic = false;
  foreach (var plugin in plugins) plugin.Initialize();
  Expect(Ui.RibbonCalls == 12 && Ui.ClassicCalls == 12 && Count(editor.History, "не удалось загрузить «проверка версий»") == 3,
      "version probe failure cannot skip either interface");
  Ui.BrokenCleanup = true;
  foreach (var plugin in plugins) plugin.Terminate();
  Expect(Ui.Cleaned == 6 && AcApp.Subscribers == 0, "classic shutdown cleanup survives failed ribbon cleanup");
  Console.WriteLine("PASS: " + checks + " actual Plugin.Initialize isolation/diagnostic checks (CAD doubles)");
 }
}
'''

CLASSIC = r'''
using System;
using System.Collections.Generic;
namespace Autodesk.AutoCAD.ApplicationServices {
 public static class Application {
  public static dynamic AcadApplication = new FakeApplication();
  public static event EventHandler BeginQuit, QuitAborted;
  public static void Quit() { if (BeginQuit != null) BeginQuit(null, EventArgs.Empty); }
  public static void Abort() { if (QuitAborted != null) QuitAborted(null, EventArgs.Empty); }
 }
}
public class FakeApplication {
 public FakeGroups MenuGroups = new FakeGroups(); public FakeBar MenuBar = new FakeBar();
}
public class FakeBar { public int Count { get { return 4; } } }
public class FakeGroups { public FakeGroup Item(int i) { return Group; } public FakeGroup Group = new FakeGroup(); }
public class FakeGroup { public FakeMenus Menus = new FakeMenus(); }
public class FakeMenus {
 public List<FakeMenu> All = new List<FakeMenu>();
 public int Count { get { return All.Count; } }
 public FakeMenu Item(int i) { return All[i]; }
 public FakeMenu Add(string name) { var menu = new FakeMenu { Name = name }; All.Add(menu); return menu; }
}
public class FakeMenu {
 public string Name; public string NameNoMnemonic { get { return Name; } } public bool OnMenuBar, FailAdd;
 public List<FakeItem> Items = new List<FakeItem>();
 public int Count { get { return Items.Count; } }
 public FakeItem Item(int i) { return Items[i]; }
 public FakeItem AddMenuItem(int i, string label, string macro) {
  if (FailAdd) throw new InvalidOperationException("COM menu unavailable");
  var item = new FakeItem { Caption = label, Macro = macro, Menu = this }; Items.Insert(i, item); return item;
 }
 public void InsertInMenuBar(int i) { OnMenuBar = true; }
 public void RemoveFromMenuBar() { OnMenuBar = false; }
}
public class FakeItem { public string Caption, Macro; public FakeMenu Menu; public void Delete() { Menu.Items.Remove(this); } }
FAKE_PLUGINS
public class Probe {
 static int Checks; public static int Errors;
 static void Expect(bool condition, string label) { Checks++; if (!condition) throw new Exception(label); }
 static void Init() { AFacadesPlugin.FacadesClassic.Init(); ACladPlugin.FacadesClassic.Init(); AFramePlugin.FacadesClassic.Init(); }
 static void Main() {
  Init(); var app = (FakeApplication)Autodesk.AutoCAD.ApplicationServices.Application.AcadApplication;
  Expect(app.MenuGroups.Group.Menus.Count == 1, "one shared menu");
  var menu = app.MenuGroups.Group.Menus.Item(0);
  Expect(menu.Count == 11 && menu.OnMenuBar, "all commands and menubar");
  var expected = new HashSet<string> { "_ATFZONE ", "_ATFTABLE ", "_ATFZONEACCEPT ", "_ATFZONERESET ", "_ATTILE ", "_ATCLADDIM ", "_ATFRAME ", "_ATFRAMEDIM ", "_ATFPROJECT ", "_ATFZONEPARAMS ", "_ATFNODE " };
  foreach (var item in menu.Items) Expect(expected.Remove(item.Macro), "unique valid command macro " + item.Macro);
  Expect(expected.Count == 0, "all macros installed");
  Init(); Expect(menu.Count == 11, "idempotent reinitialization");
  Autodesk.AutoCAD.ApplicationServices.Application.Quit();
  Expect(menu.Count == 0 && !menu.OnMenuBar, "quit removes transient menu from persisted menubar");
  Autodesk.AutoCAD.ApplicationServices.Application.Abort();
  Expect(menu.Count == 11 && menu.OnMenuBar, "cancelled quit restores menu");
  Autodesk.AutoCAD.ApplicationServices.Application.Quit();
  Expect(menu.Count == 0 && !menu.OnMenuBar, "second quit cleans menu");
  Init(); Expect(menu.Count == 11 && menu.OnMenuBar, "later start restores items");
  Expect(Errors == 0, "valid lifecycle does not emit startup errors");
  menu.FailAdd = true; Init();
  Expect(Errors == 3, "actual classic Init reports swallowed COM failures");
  menu.FailAdd = false; Init();
  Expect(menu.Count == 11 && menu.OnMenuBar, "menu recovers after transient COM failure");
  Console.WriteLine("PASS: " + Checks + " actual COM menu lifecycle checks (CAD doubles)");
 }
}
'''


def run_probe(output, label, source, sources, references):
    probe = output / (label + ".cs")
    probe.write_text(source, encoding="utf-8")
    exe = output / (label + ".exe")
    compiled = compile_probe([probe, *sources], references, exe)
    if compiled.returncode:
        raise SystemExit(compiled.stdout + compiled.stderr)
    subprocess.run(command(exe), check=True)


def main():
    if not available():
        raise SystemExit("mono/mcs or Windows dotnet is required; checks were not run")
    with tempfile.TemporaryDirectory(prefix="facades_startup_") as directory:
        output = Path(directory)
        (output / "build-info.json").write_text(json.dumps({"build": "108", "sha": "a" * 40}), encoding="utf-8")
        ui = "\n".join('namespace ' + name + '''Plugin {
 internal static class FacadesRibbon { public static void Init() { Ui.Ribbon(); } public static void Cleanup() { Ui.RibbonCleanup(); } }
 internal static class FacadesClassic { public static void Init() { Ui.Classic(); } public static void Cleanup() { Ui.Cleaned++; } }
}''' for _, name in MODULES)
        plugins = [ROOT / folder / "src" / (name + "Plugin") / "Plugin.cs" for folder, name in MODULES]
        run_probe(output, "StartupProbe", STARTUP.replace("FAKE_UI", ui),
                  [*plugins, ROOT / "Common/FacadeStartupDiagnostics.cs"], ["System.Web.Extensions"])
        reporters = "\n".join('namespace ' + name + '''Plugin {
 internal static class Plugin { public static void ReportStartupFailure(string stage, Exception error) { Probe.Errors++; } }
}''' for _, name in MODULES)
        classics = [ROOT / folder / "src" / (name + "Plugin") / "FacadesClassic.cs" for folder, name in MODULES]
        run_probe(output, "ClassicProbe", CLASSIC.replace("FAKE_PLUGINS", reporters), classics, ["Microsoft.CSharp"])


if __name__ == "__main__":
    main()
