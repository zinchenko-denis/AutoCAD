using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using AFramePlugin;
using FacadeSafety;

internal static class FrameProducerProbe
{
    public static int Main(string[] args)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        if (args[0] == "--profiles")
        {
            var producer = new FrameQuantities();
            var method = typeof(FrameQuantities).GetMethod("RailProfileMatches", BindingFlags.Instance | BindingFlags.NonPublic);
            Func<BlockReference, string, bool> matches = (block, mark) => (bool)method.Invoke(producer, new object[] { block, mark });
            var outcomes = new Dictionary<string, bool>();
            var generic = BlockReference.Make("generic", "ГП", "ГП", "ГП-60-40");
            outcomes["family_prefix_cannot_confirm_full_mark"] = !matches(generic, "ГП-40-40-1,2");
            outcomes["negative_mapping_is_cached"] = !matches(generic, "ГП-40-40-1,2") && generic.Property.AllowedCalls == 1;
            var integerThickness = BlockReference.Make("integer", "ГП-40-40-12", "ГП-40-40-12");
            outcomes["decimal_thickness_cannot_be_integer_thickness"] = !matches(integerThickness, "ГП-40-40-1,2");
            var differentDimensions = BlockReference.Make("dimensions", "ГП-4-040-1,2", "ГП-4-040-1,2");
            outcomes["dimension_separators_preserved"] = !matches(differentDimensions, "ГП-40-40-1,2");
            var decimalDot = BlockReference.Make("decimal-dot", "ГП-40-40-1.2", "ГП-40-40-1.2");
            outcomes["decimal_comma_and_dot_have_equal_numeric_meaning"] = matches(decimalDot, "ГП-40-40-1,2");
            var unicodeHyphen = BlockReference.Make("unicode-hyphen", "гп–40–40–1,2", "гп–40–40–1,2");
            outcomes["unicode_hyphen_and_letter_case_preserve_same_dimensions"] = matches(unicodeHyphen, "ГП-40-40-1,2");
            var exact = BlockReference.Make("exact", "ГП-40-40-1,2", "ГП-40-40-1,2", "ГП-60-40");
            outcomes["exact_full_mark_confirmed"] = matches(exact, "ГП-40-40-1,2");
            exact.Property.SetInitial("ГП-60-40");
            outcomes["actual_state_rechecked_after_cache"] = !matches(exact, "ГП-40-40-1,2") && exact.Property.AllowedCalls == 1;
            exact.Property.SetInitial("ГП-40-40-1,2");
            outcomes["current_value_rechecked_without_allowed_rescan"] = matches(exact, "ГП-40-40-1,2") && exact.Property.AllowedCalls == 1;
            var other = BlockReference.Make("exact", "ГП-40-40-1,2", "ГП-40-40-1,2");
            outcomes["same_definition_profile_reuses_allowed_mapping"] = matches(other, "ГП-40-40-1,2") && other.Property.AllowedCalls == 0;
            var missing = new BlockReference { DynamicBlockTableRecord = new ProfileObjectId { Handle = "missing" } };
            outcomes["missing_property_refused"] = !matches(missing, "ГП-40-40-1,2");
            outcomes["unknown_mark_remains_unknown"] = matches(missing, null);
            var fixedBlock = BlockReference.Make("fixed", "ГП-40-40-1,2", "ГП-40-40-1,2");
            fixedBlock.IsDynamicBlock = false;
            outcomes["fixed_block_does_not_confirm_dynamic_mark"] = !matches(fixedBlock, "ГП-40-40-1,2");
            var readOnly = BlockReference.Make("readonly", "ГП-40-40-1,2", "ГП-40-40-1,2");
            readOnly.Property.ReadOnly = true;
            outcomes["readonly_unselectable_mapping_refused"] = !matches(readOnly, "ГП-40-40-1,2");
            outcomes["verification_writes_no_dynamic_properties"] = generic.Property.Writes == 0 && exact.Property.Writes == 0 && other.Property.Writes == 0;
            File.WriteAllText(args[1], json.Serialize(outcomes));
            return 0;
        }
        if (args[0] == "--provenance")
        {
            var original = new QuantityReport { kind = "frame", report_id = "report-original", run_id = "run-original",
                algorithm = "ATFRAME-original", engineering_coverage = "limited_static_chain" };
            original.parameters["wind_region"] = "II";
            original.source_revisions["engine"] = "original-engine-sha";
            original.engine_summary["calc_report"] = new Dictionary<string, object> { { "original_check", 123.456 } };
            original.engine_summary["design_scope"] = new Dictionary<string, object> { { "static_model_verification", "not_verified" } };
            var first = new List<object>();
            FrameQuantities.AppendRetainedProvenance(original, new HashSet<string> { "R" }, first);
            var afterFirst = new QuantityReport { kind = "frame", report_id = "report-second", run_id = "run-second",
                algorithm = "ATFRAME-clamps", engineering_coverage = "clamps_geometry_only_with_retained_frame" };
            afterFirst.parameters["retained_sources"] = json.DeserializeObject(json.Serialize(first));
            afterFirst.parameters["new_setting"] = "second-setting";
            var second = new List<object>();
            FrameQuantities.AppendRetainedProvenance(afterFirst, new HashSet<string> { "R" }, second);
            var mixed = new List<object>();
            FrameQuantities.AppendRetainedProvenance(afterFirst, new HashSet<string> { "R", "C2" }, mixed);
            var afterSecond = new QuantityReport { kind = "frame", report_id = "report-third", run_id = "run-third" };
            afterSecond.parameters["retained_sources"] = json.DeserializeObject(json.Serialize(mixed));
            var last = new List<object>();
            FrameQuantities.AppendRetainedProvenance(afterSecond, new HashSet<string> { "C2" }, last);
            var none = new List<object>();
            FrameQuantities.AppendRetainedProvenance(afterSecond, new HashSet<string>(), none);
            File.WriteAllText(args[1], json.Serialize(new { first, second, mixed, last, none }));
            return 0;
        }
        var payload = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(args[0]));
        try
        {
            var mapping = new Dictionary<string, string>();
            foreach (var item in (Dictionary<string, object>)payload["mapping"])
                mapping[item.Key] = (string)item.Value;
            var report = FrameQuantities.BuildReport((Dictionary<string, object>)payload["response"],
                (Dictionary<string, object>)payload["request"], mapping);
            report.document_id = "synthetic-frame-document";
            for (int i = 0; i < report.elements.Count; i++)
                report.elements[i].cad_entities.Add(new QuantityCadEntity {
                    handle = (i + 1).ToString("X"), role = "primary", fingerprint = "synthetic-frame:" + i });
            var withEstimates = FacadeQuantitiesCore.BuildRows(new[] { report }, report.zone_ids, true, true);
            var withoutCladCutting = FacadeQuantitiesCore.BuildRows(new[] { report }, report.zone_ids, true, false);
            QuantityResult selected = null;
            if (report.zone_ids.Count > 1)
                selected = FacadeQuantitiesCore.BuildRows(new[] { report }, new[] { report.zone_ids[0] }, true, true);
            File.WriteAllText(args[1], json.Serialize(new { ok = true, report, withEstimates, withoutCladCutting, selected }));
        }
        catch (InvalidOperationException error)
        {
            File.WriteAllText(args[1], json.Serialize(new { ok = false, error = error.Message }));
        }
        return 0;
    }
}

// Only the dynamic properties read by the actual profile guard are doubled.
namespace AFramePlugin
{
    internal sealed class ProfileObjectId { public string Handle; }
    internal sealed class DynamicBlockReferenceProperty
    {
        public bool ReadOnly;
        public string PropertyName = "Visibility";
        public object[] Allowed;
        public int AllowedCalls, Writes;
        private object value;
        public object Value { get { return value; } set { this.value = value; Writes++; } }
        public void SetInitial(object initial) { value = initial; }
        public object[] GetAllowedValues() { AllowedCalls++; return Allowed; }
    }
    internal sealed class BlockReference
    {
        public bool IsDynamicBlock = true;
        public ProfileObjectId DynamicBlockTableRecord;
        public List<DynamicBlockReferenceProperty> DynamicBlockReferencePropertyCollection = new List<DynamicBlockReferenceProperty>();
        public DynamicBlockReferenceProperty Property { get { return DynamicBlockReferencePropertyCollection[0]; } }
        public static BlockReference Make(string definition, string current, params object[] allowed)
        {
            var property = new DynamicBlockReferenceProperty { Allowed = allowed };
            property.SetInitial(current);
            var result = new BlockReference { DynamicBlockTableRecord = new ProfileObjectId { Handle = definition } };
            result.DynamicBlockReferencePropertyCollection.Add(property);
            return result;
        }
    }
}
