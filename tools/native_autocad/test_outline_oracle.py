"""Test the harness's actual pure rectangle oracle; no CAD API or host emulation."""
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
using System.Linq;
class OutlineOracleCheck
{
__ACTUAL_METHODS__
    static int checks, failures;
    static readonly double[][] Corners = {
        new[] { 1000.125, -42.25, 0.0 }, new[] { 1245.125, -42.25, 0.0 },
        new[] { 1245.125, -6.4, 0.0 }, new[] { 1000.125, -6.4, 0.0 }
    };
    static void Test(string name, double[][] points, bool expected)
    {
        bool accepted = true;
        try { CheckOutline(points, 1000.125, -42.25, 245, 35.85, 1e-6); }
        catch (InvalidOperationException) { accepted = false; }
        checks++;
        if (accepted != expected) { failures++; Console.WriteLine("FAIL " + name); }
    }
    static double[][] Copy() { return Corners.Select(p => (double[])p.Clone()).ToArray(); }
    static int Main()
    {
        // All 24 orders: eight CW/CCW cyclic orders must pass; the other
        // sixteen have crossing/diagonal edges and must fail despite same bbox.
        foreach (int a in Enumerable.Range(0, 4))
        foreach (int b in Enumerable.Range(0, 4))
        foreach (int c in Enumerable.Range(0, 4))
        foreach (int d in Enumerable.Range(0, 4))
        {
            var order = new[] { a, b, c, d };
            if (order.Distinct().Count() != 4) continue;
            var steps = Enumerable.Range(0, 4).Select(i => (order[(i + 1) % 4] - order[i] + 4) % 4).ToArray();
            bool perimeter = steps.All(v => v == 1) || steps.All(v => v == 3);
            Test("order " + string.Join(",", order), order.Select(i => Corners[i]).ToArray(), perimeter);
        }
        var p = Copy(); p[0][2] = 1; Test("wrong Z", p, false);
        p = Copy(); p[0][0] += .15; Test("wrong corner", p, false);
        p = Copy(); p[0][0] = double.NaN; Test("NaN", p, false);
        p = Copy(); p[0][1] = double.PositiveInfinity; Test("infinite", p, false);
        p = Copy(); p[1] = p[0]; Test("duplicate corner", p, false);
        Test("missing corner", Copy().Take(3).ToArray(), false);
        Console.WriteLine("Outline oracle: " + checks + " checks, " + failures +
            " failures; actual pure C# methods, NOT AutoCAD execution.");
        return failures == 0 ? 0 : 1;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    if not available():
        print("BLOCKED: .NET compiler/runtime unavailable; no native execution claimed")
        return 2
    out = args.out or Path(tempfile.mkdtemp(prefix="native_outline_oracle_"))
    out.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).with_name("NativeAdapterCheck.cs").read_text(encoding="utf-8")
    actual = "\n".join(method(source, name) for name in ("CheckOutline", "Finite"))
    cs, exe = out / "OutlineOracleCheck.cs", out / "OutlineOracleCheck.exe"
    cs.write_text(DRIVER.replace("__ACTUAL_METHODS__", actual), encoding="utf-8")
    compiled = compile_probe([cs], [], exe)
    if compiled.returncode:
        print(compiled.stdout + compiled.stderr)
        return 1
    return subprocess.run(command(exe)).returncode


if __name__ == "__main__":
    raise SystemExit(main())
