// Deterministic complexity checks plus observations. Wall-clock times never gate correctness.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;

namespace FrameQuantitiesChecks
{
    internal static class FrameCorePerf
    {
        private static readonly string[] Roles = { "rail", "hrail", "shina", "bracket", "clamp", "fitting" };
        private static void Need(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        private static void Near(double actual, double expected, string reason)
        { Need(Math.Abs(actual-expected) <= Math.Max(1, Math.Abs(expected))*1e-10, reason); }

        private static object Measure(QuantityReport report, int count, bool subset, double expectedLength)
        {
            var diagnostics = new QuantityDiagnostics();
            var timer = Stopwatch.StartNew();
            var result = FacadeQuantitiesCore.BuildRows(new[] { report }, subset ? new[] { "Z0" } : null, false, false, diagnostics);
            timer.Stop();
            int selectedCount = subset ? (count + 3) / 4 : count;
            Need(result.ok && result.completeness == "complete", "valid frame fixture rejected or downgraded");
            Need(result.rows.Sum(x => x.quantity) == selectedCount, "installed piece count differs from independent fixture");
            Need(result.rows.Sum(x => x.element_ids.Count) == selectedCount, "physical provenance lost");
            Near(result.rows.Sum(x => x.total_length_m ?? 0), expectedLength, "installed length differs from endpoint fixture");
            Need(result.rows.All(x => x.basis == "installed" && x.unit == "шт." && x.area_m2 == null && x.shape_id == null), "invented installed data");
            Need(diagnostics.reports_seen == 1 && diagnostics.elements_seen == count && diagnostics.elements_validated == count,
                "not every physical member was validated");
            Need(diagnostics.cad_links_checked == 2L * count, "not every primary and helper CAD link was checked");
            Need(diagnostics.aggregate_keys_built == selectedCount, "aggregation operation count is not one per selected physical part");
            Need(diagnostics.reports_compared == 0 && diagnostics.elements_compared == 0 && diagnostics.canonical_report_keys == 0 && diagnostics.canonical_element_keys == 0,
                "unique identities unexpectedly performed canonical duplicate work");
            Need(diagnostics.shapes_validated == 0, "frame validation performed cladding shape work");
            return new { selection = subset ? "Z0" : "all", count = count, selected_count = selectedCount, status = "PASS",
                core_s = timer.Elapsed.TotalSeconds, rows = result.rows.Count, actual_length_m = result.rows.Sum(x => x.total_length_m ?? 0),
                diagnostics = diagnostics };
        }

        public static int Main(string[] args)
        {
            try {
                int count = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 150000;
                Need(count > 0, "count must be positive");
                var report = new QuantityReport { kind = "frame", report_id = "perf-report", run_id = "perf-run", document_id = "perf-document",
                    algorithm = "frame-perf-fixture/1", completeness = "complete", zone_ids = new List<string> { "Z0", "Z1", "Z2", "Z3" } };
                var timer = Stopwatch.StartNew(); double allLength = 0, subsetLength = 0;
                for (int i = 0; i < count; i++) {
                    int roleIndex = i % Roles.Length, variant = (i / Roles.Length) % 8;
                    double? length = roleIndex < 3 ? 1800 + variant * 25.0 : (double?)null;
                    string identity = i.ToString(CultureInfo.InvariantCulture);
                    report.elements.Add(new QuantityElement { element_id = "P" + identity, role = Roles[roleIndex], zone_ids = new List<string> { "Z" + (i % 4) },
                        product_id = "CAT-01", mark = "M1", type = "T1", material = "steel", coating = "zinc", system = "SYS-01", orientation = "vertical",
                        length_mm = length, origin = "generated:perf-fixture", cad_entities = new List<QuantityCadEntity> {
                            new QuantityCadEntity { handle = "P" + identity, role = "primary", fingerprint = "p:" + identity },
                            new QuantityCadEntity { handle = "L" + identity, role = "label", fingerprint = "l:" + identity } } });
                    allLength += (length ?? 0) / 1000.0;
                    if (i % 4 == 0) subsetLength += (length ?? 0) / 1000.0;
                }
                timer.Stop(); double generation = timer.Elapsed.TotalSeconds;
                var workloads = new[] { Measure(report, count, false, allLength), Measure(report, count, true, subsetLength) };
                long measuredPeak = Process.GetCurrentProcess().PeakWorkingSet64;
                Console.WriteLine(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new {
                    count = count, status = "PASS", clock_is_observation_only = true, generated_s = generation,
                    managed_bytes = GC.GetTotalMemory(false), peak_working_set = measuredPeak > 0 ? (long?)measuredPeak : null,
                    scope = "Pure C# frame quantities; no AutoCAD host, DWG or UI.", workloads = workloads }));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
