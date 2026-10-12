"""Check actual trace delegates and production conversion; NOT AutoCAD execution."""
import argparse
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "AClad/tests"))
from test_dynamic_block_size import method
from probe_runtime import available, command, compile_probe

DRIVER = r'''
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ACladPlugin;
class SetterTraceCheck
{
__ACTUAL_METHODS__
    static int checks, failures;
    static bool Production(Func<object> read, Action<object> write, double value)
    {
        return (bool)typeof(LayoutSafety).GetMethod("SetNumberChecked", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { read, write, value, Type.Missing });
    }
    static List<object> Events(Dictionary<string, object> r) { return (List<object>)r["events"]; }
    static string[] Kinds(Dictionary<string, object> r)
    { return Events(r).Select(v => (string)((object[])v)[0]).ToArray(); }
    static void Check(string name, Func<bool> test)
    {
        checks++;
        try { if (test()) return; }
        catch (Exception ex) { Console.WriteLine("ERROR " + name + ": " + ex); }
        failures++; Console.WriteLine("FAIL " + name);
    }
    static int Main()
    {
        Check("exact Double input and ordered read/write events", () => {
            object state = 600.0;
            var r = TraceAssignment(Production, () => state, v => state = v, 563.55);
            var boxed = (Dictionary<string, object>)((object[])Events(r)[1])[1];
            return (bool)r["production_returned"] && (double)state == 563.55 &&
                (int)r["setter_invocations"] == 1 && (string)boxed["clr_type"] == "System.Double" &&
                (string)boxed["value"] == "563.55" && Kinds(r).SequenceEqual(new[] { "read", "setter_input", "setter_returned", "read" });
        });
        Check("silent rounding is observed with production false", () => {
            object state = 600.0;
            var r = TraceAssignment(Production, () => state, v => state = Math.Round((double)v), 588.25);
            var last = (Dictionary<string, object>)((object[])Events(r).Last())[1];
            return !(bool)r["production_returned"] && (double)state == 588 && (string)last["value"] == "588";
        });
        Check("silent rejection remains distinct from rounding", () => {
            object state = 564.0;
            var r = TraceAssignment(Production, () => state, v => { }, 563.55);
            return !(bool)r["production_returned"] && (double)state == 564 &&
                Kinds(r).SequenceEqual(new[] { "read", "setter_input", "setter_returned", "read" });
        });
        Check("original setter exception survives production catch", () => {
            var original = new InvalidOperationException("raw-native-setter-failure");
            var r = TraceAssignment(Production, () => 600.0, v => { throw original; }, 563.55);
            return !(bool)r["production_returned"] && !r.ContainsKey("production_exception") &&
                Kinds(r).SequenceEqual(new[] { "read", "setter_input", "setter_exception" }) &&
                ((string)((object[])Events(r).Last())[1]).Contains("raw-native-setter-failure");
        });
        Check("mutation then throw is not hidden or retried", () => {
            object state = 600.0;
            var r = TraceAssignment(Production, () => state, v => { state = 588.0; throw new InvalidOperationException("after-mutation"); }, 588.25);
            return !(bool)r["production_returned"] && (double)state == 588 &&
                (int)r["setter_invocations"] == 1 && Kinds(r).Last() == "setter_exception";
        });
        Check("lossy integer conversion never invokes native setter", () => {
            var r = TraceAssignment(Production, () => (object)600, v => { throw new Exception("must-not-write"); }, 563.55);
            return !(bool)r["production_returned"] && (int)r["setter_invocations"] == 0 &&
                Kinds(r).SequenceEqual(new[] { "read" });
        });
        Check("getter exception retained with production false", () => {
            var r = TraceAssignment(Production, () => { throw new InvalidOperationException("raw-getter-failure"); }, v => { }, 563.55);
            return !(bool)r["production_returned"] && (int)r["setter_invocations"] == 0 &&
                Kinds(r).SequenceEqual(new[] { "read_exception" }) &&
                ((string)((object[])Events(r)[0])[1]).Contains("raw-getter-failure");
        });
        Check("already exact property is not needlessly rewritten", () => {
            var r = TraceAssignment(Production, () => 563.55, v => { throw new Exception("must-not-write"); }, 563.55);
            return (bool)r["production_returned"] && (int)r["setter_invocations"] == 0 &&
                Kinds(r).SequenceEqual(new[] { "read" });
        });
        Check("round-trip diagnostic independent of Russian culture", () => {
            var old = CultureInfo.CurrentCulture;
            try {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
                var value = (Dictionary<string, object>)BoxedValue(563.9999999999884);
                return (string)value["clr_type"] == "System.Double" &&
                    double.Parse((string)value["value"], CultureInfo.InvariantCulture) == 563.9999999999884 &&
                    !((string)value["value"]).Contains(",");
            } finally { CultureInfo.CurrentCulture = old; }
        });
        Console.WriteLine("Setter trace: " + checks + " checks, " + failures +
            " failures; actual trace and production conversion, native AutoCAD NOT_RUN.");
        return failures == 0 ? 0 : 1;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: .NET compiler/runtime unavailable; native AutoCAD NOT_RUN")
        return 2
    out = args.out or Path(tempfile.mkdtemp(prefix="native_setter_trace_"))
    out.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).with_name("NativeAdapterCheck.cs").read_text(encoding="utf-8")
    actual = "\n".join(method(source, name) for name in ("TraceAssignment", "BoxedValue", "Unwrap"))
    cs, exe = out / "SetterTraceCheck.cs", out / "SetterTraceCheck.exe"
    cs.write_text(DRIVER.replace("__ACTUAL_METHODS__", actual), encoding="utf-8")
    compiled = compile_probe([cs, ROOT / "AClad/src/ACladPlugin/LayoutSafety.cs"], [], exe)
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    return subprocess.run(command(exe)).returncode


if __name__ == "__main__":
    raise SystemExit(main())
