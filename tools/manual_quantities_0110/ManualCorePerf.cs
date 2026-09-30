// Complexity checks gate deterministic work counts, never elapsed time or memory.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using FacadeSafety;

namespace ManualQuantitiesChecks
{
    internal static class ManualCorePerf
    {
        private static void Need(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        private static ManualQuantityRule Rule()
        {
            var rule = new ManualQuantityRule { rule_id = "perf-rule", revision = "1", kind = "frame", role = "bracket", adapter = "symbol",
                geometry_unit = "mm", one_physical_part = true, sample_key = "perf-sample" };
            foreach (string key in new[] { "mark", "type", "material", "coating", "system", "color", "orientation", "piece_kind" })
                rule.fields.Add(key, new ManualQuantityField { source = "literal", value = "fixture:" + key });
            return rule;
        }
        private static object Measure(ManualQuantityRule rule, List<ManualQuantityObservation> observations, int uniqueCount, int repeatedCount)
        {
            var timer = Stopwatch.StartNew();
            var result = ManualQuantitiesCore.BuildPreview(rule, observations, "Z1");
            timer.Stop();
            Need(result.accepted_count == uniqueCount && result.rejected_count == 0 && result.items.Count == uniqueCount, "physical instance count is incorrect");
            Need(result.repeated_count == repeatedCount, "repeated selection count is incorrect");
            Need(result.items.All(x => x.status == "accepted" && x.element != null && x.element.cad_entities.Count == 1 && x.element.product_id == null), "lost physical CAD provenance or invented catalog");
            Need(result.items.Select(x => x.element.element_id).Distinct().Count() == uniqueCount, "distinct physical identities lost");
            Need(!result.issues.Any(x => x.code == "MQ_DUPLICATE_POSSIBLE"), "separate grid positions marked as coincident");
            var d = result.diagnostics;
            Need(d.observations_seen == uniqueCount + repeatedCount && d.observations_evaluated == uniqueCount, "not one evaluation per unique physical observation");
            Need(d.fields_read == 8L * uniqueCount, "not exactly one read per configured mapping per unique observation");
            Need(d.shapes_validated == 0 && d.duplicate_keys_built == uniqueCount && d.duplicate_buckets == uniqueCount, "unexpected geometry validation or duplicate search work");
            Need(!string.IsNullOrWhiteSpace(result.fingerprint), "preview fingerprint missing");
            return new { selection = repeatedCount == 0 ? "unique" : "repeated_handles", status = "PASS", count = uniqueCount,
                repeated_count = repeatedCount, accepted_count = result.accepted_count, core_s = timer.Elapsed.TotalSeconds, diagnostics = d };
        }
        private static object MeasureMissing(List<ManualQuantityObservation> observations, int count)
        {
            var rule = Rule();
            foreach (string key in rule.fields.Keys.ToArray()) rule.fields[key] = new ManualQuantityField { source = "attribute", name = key };
            var timer = Stopwatch.StartNew();
            var result = ManualQuantitiesCore.BuildPreview(rule, observations.Take(count), "Z1");
            timer.Stop();
            Need(result.accepted_count == count && result.rejected_count == 0 && result.items.Count == count, "unknown metadata prevents valid geometric count");
            Need(result.items.All(x => x.element.mark == null && x.element.product_id == null && x.issues.Count(y => y.code == "MQ_FIELD_UNKNOWN") == 1), "unknown metadata invented or warnings not compact per object");
            var warnings = result.issues.Where(x => x.code == "MQ_FIELD_UNKNOWN").ToList();
            Need(warnings.Count == 1 && warnings[0].element_count == count && warnings[0].element_ids.Count == count && warnings[0].element_ids.Distinct().Count() == count,
                "missing-field pattern not aggregated with complete physical provenance");
            var d = result.diagnostics;
            Need(d.observations_seen == count && d.observations_evaluated == count && d.fields_read == 8L * count && d.duplicate_keys_built == count && d.duplicate_buckets == count && d.shapes_validated == 0,
                "unknown metadata changes deterministic validation work coverage");
            return new { selection = "missing_attributes", status = "PASS", count = count, accepted_count = result.accepted_count,
                warning_count = warnings.Count, warning_element_ids = warnings[0].element_ids.Count, core_s = timer.Elapsed.TotalSeconds, diagnostics = d };
        }
        private static object MeasurePrepared(ManualQuantityRule rule, List<ManualQuantityObservation> observations, int count)
        {
            var diagnostics = new ManualQuantityDiagnostics(); var timer = Stopwatch.StartNew();
            var context = ManualQuantitiesCore.Prepare(rule); int accepted = 0;
            for (int i = 0; i < count; i++) {
                var observation = observations[i];
                var result = ManualQuantitiesCore.Evaluate(context, observation, "Z1", diagnostics);
                Need(result.status == "accepted" && result.element != null && result.element.element_id == "manual:" + observation.handle.ToUpperInvariant()
                    && result.element.mark == "fixture:mark" && result.element.cad_entities.Count == 1
                    && result.element.cad_entities[0].fingerprint == observation.cad_fingerprint && result.element.product_id == null,
                    "prepared store-read style evaluation lost mapping or physical provenance");
                accepted++;
            }
            timer.Stop();
            Need(accepted == count && diagnostics.observations_evaluated == count && diagnostics.fields_read == 8L * count
                && diagnostics.duplicate_keys_built == count && diagnostics.shapes_validated == 0,
                "prepared context skipped or multiplied per-physical-element validation work");
            Need(diagnostics.observations_seen == 0 && diagnostics.duplicate_buckets == 0, "direct prepared evaluation invents preview traversal counters");
            return new { selection = "prepared_store_read", status = "PASS", count = count, accepted_count = accepted,
                core_s = timer.Elapsed.TotalSeconds, diagnostics = diagnostics };
        }
        public static int Main(string[] args)
        {
            try {
                int count = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 150000;
                Need(count > 0, "count must be positive");
                var rule = Rule(); var observations = new List<ManualQuantityObservation>(count + 100);
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < count; i++) {
                    string id = (1000L + i).ToString("X", CultureInfo.InvariantCulture);
                    observations.Add(new ManualQuantityObservation { handle = id, entity_type = "BlockReference", is_block = true,
                        effective_block_name = "FixtureBracket", sample_key = "perf-sample", fingerprint = "snapshot:" + id, cad_fingerprint = "cad:" + id,
                        position = new[] { (i % 1000) * 100.0, (i / 1000) * 100.0 }, rotation = 0, scale_x = 1, scale_y = 1, scale_z = 1 });
                }
                timer.Stop(); double generatedSeconds = timer.Elapsed.TotalSeconds;
                var unique = Measure(rule, observations, count, 0);
                int repeatedCount = Math.Min(100, count);
                for (int i = 0; i < repeatedCount; i++) observations.Add(observations[i]);
                var repeated = Measure(rule, observations, count, repeatedCount);
                var workloads = new List<object> { unique, repeated, MeasurePrepared(rule, observations, count) };
                if (args.Length > 1 && args[1] == "--missing") workloads.Add(MeasureMissing(observations, count));
                long measuredPeak = Process.GetCurrentProcess().PeakWorkingSet64;
                Console.WriteLine(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(new {
                    count = count, status = "PASS", clock_is_observation_only = true, generated_s = generatedSeconds,
                    managed_bytes = GC.GetTotalMemory(false), peak_working_set = measuredPeak > 0 ? (long?)measuredPeak : null,
                    scope = "Pure manual mapping and preview; no AutoCAD host, live DWG or transaction.", workloads = workloads }));
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
