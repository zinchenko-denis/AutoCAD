// Independent native checks of the production, AutoCAD-free geometry helper.
// This executable does not load AutoCAD, emulate COPY, or validate database reactors.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;

internal static class GeometrySafetyCheck
{
    private static readonly List<Dictionary<string, object>> Results = new List<Dictionary<string, object>>();

    private static GeometryLoop Loop(string role, params double[][] points)
    {
        return new GeometryLoop { Role = role, Points = points, Bulges = new double[points.Length], Closed = true };
    }

    private static GeometryLoop Rectangle(string role, double x, double y, double width, double height)
    {
        return Loop(role, new[] { x, y }, new[] { x + width, y },
                    new[] { x + width, y + height }, new[] { x, y + height });
    }

    private static GeometryLoop Clone(GeometryLoop loop)
    {
        return new GeometryLoop {
            Role = loop.Role, Closed = loop.Closed,
            Points = loop.Points.Select(p => (double[])p.Clone()).ToArray(),
            Bulges = loop.Bulges == null ? null : (double[])loop.Bulges.Clone()
        };
    }

    private static GeometryLoop Translate(GeometryLoop loop, double x, double y)
    {
        var result = Clone(loop);
        foreach (var p in result.Points) { p[0] += x; p[1] += y; }
        return result;
    }

    private static GeometryLoop RotateStart(GeometryLoop loop, int offset)
    {
        int n = loop.Points.Length;
        return new GeometryLoop {
            Role = loop.Role, Closed = loop.Closed,
            Points = Enumerable.Range(0, n).Select(i => (double[])loop.Points[(i + offset) % n].Clone()).ToArray(),
            Bulges = Enumerable.Range(0, n).Select(i => loop.Bulges[(i + offset) % n]).ToArray()
        };
    }

    private static GeometryLoop ReverseClosed(GeometryLoop loop)
    {
        int n = loop.Points.Length;
        // The old edge j -> j+1 becomes j+1 -> j: move its bulge to that
        // new start vertex and negate it. Merely reversing/negating is wrong.
        return new GeometryLoop {
            Role = loop.Role, Closed = loop.Closed,
            Points = loop.Points.Reverse().Select(p => (double[])p.Clone()).ToArray(),
            Bulges = Enumerable.Range(0, n).Select(i => -loop.Bulges[(n - 2 - i + n) % n]).ToArray()
        };
    }

    private static double Area(GeometryLoop loop)
    {
        double twice = 0;
        for (int i = 0; i < loop.Points.Length; i++) {
            var p = loop.Points[i]; var q = loop.Points[(i + 1) % loop.Points.Length];
            twice += p[0] * q[1] - q[0] * p[1];
        }
        return Math.Abs(twice) / 2;
    }

    private static double Perimeter(GeometryLoop loop)
    {
        double result = 0;
        for (int i = 0; i < loop.Points.Length; i++) {
            var p = loop.Points[i]; var q = loop.Points[(i + 1) % loop.Points.Length];
            result += Math.Sqrt((p[0] - q[0]) * (p[0] - q[0]) + (p[1] - q[1]) * (p[1] - q[1]));
        }
        return result;
    }

    private static double[] Bounds(GeometryLoop loop)
    {
        return new[] { loop.Points.Min(p => p[0]), loop.Points.Min(p => p[1]),
                       loop.Points.Max(p => p[0]), loop.Points.Max(p => p[1]) };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Fingerprint(params GeometryLoop[] loops)
    {
        string fingerprint, reason;
        Require(GeometryFingerprint.TryFingerprint(loops, out fingerprint, out reason), "valid fixture rejected: " + reason);
        Require(!String.IsNullOrWhiteSpace(fingerprint), "empty successful fingerprint");
        return fingerprint;
    }

    private static void Equal(GeometryLoop[] expected, GeometryLoop[] actual)
    {
        string reason;
        Fingerprint(expected); Fingerprint(actual);
        Require(GeometryFingerprint.Equivalent(expected, actual, out reason), "equivalence rejected: " + reason);
        Require(GeometryFingerprint.Equivalent(actual, expected, out reason), "reverse equivalence rejected: " + reason);
    }

    private static void Different(GeometryLoop[] expected, GeometryLoop[] actual)
    {
        string reason;
        string oldHash = Fingerprint(expected), newHash = Fingerprint(actual);
        Require(oldHash != newHash, "different geometry has the same stored fingerprint");
        Require(!GeometryFingerprint.Equivalent(expected, actual, out reason), "changed geometry accepted as current");
        Require(!GeometryFingerprint.Equivalent(actual, expected, out reason), "reverse comparison accepted changed geometry");
    }

    private static void Invalid(params GeometryLoop[] loops)
    {
        string fingerprint, reason;
        Require(!GeometryFingerprint.TryFingerprint(loops, out fingerprint, out reason), "invalid geometry fingerprinted");
        Require(!String.IsNullOrWhiteSpace(reason), "invalid geometry lacks a diagnostic");
        Require(!GeometryFingerprint.Equivalent(loops, loops, out reason), "invalid geometry accepted against itself");
    }

    private static void Test(string name, Action action)
    {
        try {
            action();
            Results.Add(new Dictionary<string, object> { { "name", name }, { "status", "PASS" } });
            Console.WriteLine("PASS " + name);
        } catch (Exception ex) {
            Results.Add(new Dictionary<string, object> { { "name", name }, { "status", "FAIL" }, { "detail", ex.Message } });
            Console.WriteLine("FAIL " + name + ": " + ex.Message);
        }
    }

    public static int Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--report")) {
            Console.Error.WriteLine("Usage: GeometrySafetyCheck.exe [--report report.json]");
            return 2;
        }
        var outer = Rectangle("outer", 0, 0, 6000, 6000);
        var window = Rectangle("hole", 1000, 1500, 1200, 1500);
        var window2 = Rectangle("hole", 3500, 1000, 800, 1800);
        var notchA = Loop("outer", new[] { 0d, 0d }, new[] { 6000d, 0d }, new[] { 6000d, 6000d },
            new[] { 3000d, 6000d }, new[] { 3000d, 5000d }, new[] { 2000d, 5000d },
            new[] { 2000d, 6000d }, new[] { 0d, 6000d });
        var notchB = Loop("outer", new[] { 0d, 0d }, new[] { 6000d, 0d }, new[] { 6000d, 6000d },
            new[] { 4500d, 6000d }, new[] { 4500d, 5000d }, new[] { 3500d, 5000d },
            new[] { 3500d, 6000d }, new[] { 0d, 6000d });

        Test("positive_rectangle_and_window", () => Equal(new[] { outer, window }, new[] { Clone(outer), Clone(window) }));
        Test("same_area_bbox_perimeter_changed_shape", () => {
            // Independent shoelace/edge/bounds oracle proves an area, bounding-box,
            // or perimeter signature cannot distinguish these two valid contours.
            Require(Area(notchA) == 35000000 && Area(notchB) == 35000000, "fixture area changed");
            Require(Perimeter(notchA) == 26000 && Perimeter(notchB) == 26000, "fixture perimeter changed");
            Require(Bounds(notchA).SequenceEqual(Bounds(notchB)), "fixture bounding boxes differ");
            Different(new[] { notchA }, new[] { notchB });
        });
        Test("window_translation_same_net_area", () => {
            var moved = Translate(window, 200, 0);
            Require(Area(outer) - Area(window) == Area(outer) - Area(moved), "net area differs");
            Require(Perimeter(window) == Perimeter(moved), "window perimeter differs");
            Different(new[] { outer, window }, new[] { outer, moved });
        });
        Test("all_closed_cyclic_starts", () => {
            for (int offset = 0; offset < notchA.Points.Length; offset++) {
                var rotated = RotateStart(notchA, offset);
                Equal(new[] { notchA }, new[] { rotated });
                Require(Fingerprint(notchA) == Fingerprint(rotated), "cyclic start changed revision");
            }
        });
        Test("all_closed_reverse_starts", () => {
            for (int offset = 0; offset < notchA.Points.Length; offset++) {
                var reversed = RotateStart(ReverseClosed(notchA), offset);
                Equal(new[] { notchA }, new[] { reversed });
                Require(Fingerprint(notchA) == Fingerprint(reversed), "reverse start changed revision");
            }
        });
        Test("explicit_closing_vertex", () => {
            var closed = Clone(outer);
            closed.Points = closed.Points.Concat(new[] { (double[])closed.Points[0].Clone() }).ToArray();
            closed.Bulges = new double[closed.Points.Length];
            Equal(new[] { outer }, new[] { closed });
        });
        Test("collinear_linear_midpoint", () => {
            var midpoint = Loop("outer", new[] { 0d, 0d }, new[] { 3000d, 0d }, new[] { 6000d, 0d },
                new[] { 6000d, 6000d }, new[] { 0d, 6000d });
            Equal(new[] { outer }, new[] { midpoint });
        });

        var curved = Clone(outer);
        curved.Bulges = new[] { 0.25, 0, -0.5, 0 };
        Test("bulges_reverse_preserves_arcs", () => {
            for (int offset = 0; offset < curved.Points.Length; offset++) {
                var reversed = RotateStart(ReverseClosed(curved), offset);
                Equal(new[] { curved }, new[] { reversed });
                Require(Fingerprint(curved) == Fingerprint(reversed), "arc reversal changed revision");
            }
        });
        Test("bulges_naive_reverse_changes_arcs", () => {
            var wrong = ReverseClosed(curved);
            wrong.Bulges = curved.Bulges.Reverse().Select(b => -b).ToArray();
            Different(new[] { curved }, new[] { wrong });
        });
        Test("bulges_are_not_discarded", () => Different(new[] { curved }, new[] { outer }));
        Test("short_closing_arc_cannot_disappear", () => {
            var arc = Loop("outer", new[] { 0d, 0d }, new[] { 6000d, 0d }, new[] { 6000d, 6000d },
                new[] { 0d, 6000d }, new[] { 0d, 0.4 });
            arc.Bulges[4] = 10000;
            // Bulge = sagitta / half-chord. A 0.4 mm closing edge can carry
            // a 2000 mm sagitta: its short chord is not a harmless duplicate.
            Require(0.4 * arc.Bulges[4] / 2 == 2000, "arc fixture lost its material deviation");
            string reason;
            Require(!GeometryFingerprint.Equivalent(new[] { arc }, new[] { outer }, out reason),
                "2000 mm closing arc was silently deleted as a short trailing edge");
            string arcHash;
            if (GeometryFingerprint.TryFingerprint(new[] { arc }, out arcHash, out reason))
                Require(arcHash != Fingerprint(outer), "short closing arc has chord-only revision");
            else Require(!String.IsNullOrWhiteSpace(reason), "unsupported closing arc lacks diagnostic");
        });
        Test("hole_order_does_not_change_geometry", () => {
            Equal(new[] { outer, window, window2 }, new[] { window2, outer, window });
            Require(Fingerprint(outer, window, window2) == Fingerprint(window2, outer, window), "loop ordering changed revision");
        });
        Test("loop_role_is_part_of_identity", () => {
            var reclassified = Clone(window); reclassified.Role = "outer";
            Different(new[] { outer, window }, new[] { outer, reclassified });
        });
        Test("whole_snapshot_translation_is_changed", () =>
            Different(new[] { outer, window }, new[] { Translate(outer, 1000, 2000), Translate(window, 1000, 2000) }));
        Test("numerical_tolerance_does_not_use_exact_hash", () => {
            double delta = GeometryFingerprint.CoordinateTolerance * 0.4;
            Equal(new[] { outer, window }, new[] { Translate(outer, delta, delta), Translate(window, delta, delta) });
        });
        Test("movement_above_tolerance_is_changed", () =>
            Different(new[] { outer }, new[] { Translate(outer, GeometryFingerprint.CoordinateTolerance * 5, 0) }));

        var second = Rectangle("outer", 8000, 0, 3000, 4000);
        var secondHole = Rectangle("hole", 8500, 1000, 500, 800);
        var merged = new[] { outer, window, second, secondHole };
        Test("merged_parts_and_holes_reorder", () => {
            Equal(merged, new[] { secondHole, second, window, outer });
            Require(Fingerprint(merged) == Fingerprint(secondHole, second, window, outer), "merge order changed revision");
        });
        Test("merged_second_part_cannot_be_omitted", () => Different(merged, new[] { outer, window, secondHole }));
        Test("merged_second_hole_cannot_be_omitted", () => Different(merged, new[] { outer, window, second }));
        Test("merged_second_part_move_is_changed", () =>
            Different(merged, new[] { outer, window, Translate(second, 200, 0), Translate(secondHole, 200, 0) }));
        Test("merged_duplicate_part_is_not_ignored", () => Different(merged, merged.Concat(new[] { Clone(second) }).ToArray()));

        Test("open_path_reversal_is_significant", () => {
            var open = Loop("raw", new[] { 0d, 0d }, new[] { 3000d, 0d }, new[] { 3000d, 1000d });
            open.Closed = false;
            var reverse = Clone(open); reverse.Points = reverse.Points.Reverse().ToArray();
            Different(new[] { open }, new[] { reverse });
        });
        Test("nonfinite_coordinate_nan_rejected", () => { var bad = Clone(outer); bad.Points[1][0] = Double.NaN; Invalid(bad); });
        Test("nonfinite_coordinate_infinity_rejected", () => { var bad = Clone(outer); bad.Points[1][1] = Double.PositiveInfinity; Invalid(bad); });
        Test("nonfinite_bulge_rejected", () => { var bad = Clone(outer); bad.Bulges[0] = Double.NegativeInfinity; Invalid(bad); });
        Test("unsupported_point_dimension_rejected", () => { var bad = Clone(outer); bad.Points[0] = new[] { 0d, 0d, 2d }; Invalid(bad); });
        Test("malformed_point_dimension_rejected", () => { var bad = Clone(outer); bad.Points[0] = new[] { 0d }; Invalid(bad); });
        Test("bulge_count_mismatch_rejected", () => { var bad = Clone(outer); bad.Bulges = new[] { 0d }; Invalid(bad); });
        Test("missing_loop_role_rejected", () => { var bad = Clone(outer); bad.Role = null; Invalid(bad); });

        int failed = Results.Count(r => (string)r["status"] == "FAIL");
        var report = new Dictionary<string, object> {
            { "scope", "Production pure GeometryFingerprint.cs executed as native C#; no AutoCAD runtime or database adapters." },
            { "total", Results.Count }, { "passed", Results.Count - failed }, { "failed", failed },
            { "cases", Results }
        };
        if (args.Length == 2)
            File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(report));
        Console.WriteLine("Geometry fingerprint checks: " + (Results.Count - failed) + "/" + Results.Count + " passed; AutoCAD runtime: NOT RUN");
        return failed == 0 ? 0 : 1;
    }
}
