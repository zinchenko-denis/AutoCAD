using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ACladPlugin;

internal static class CladSafetyCheck
{
    private static int checks;
    private static int failures;

    private static void Check(string name, bool passed)
    {
        checks++;
        if (!passed)
        {
            failures++;
            Console.Error.WriteLine("FAIL " + name);
        }
    }

    private static Dictionary<string, object> Zone(string id, params object[] members)
    {
        return new Dictionary<string, object> { { "zone_id", id }, { "members", members } };
    }

    private static bool Roots(object[] zones, Dictionary<string, string> expected, params string[] roots)
    {
        return string.Join("|", LayoutSafety.IncompleteRoots(zones, expected).ToArray()) ==
               string.Join("|", roots);
    }

    private static void CheckRoots()
    {
        var parts = new Dictionary<string, string>
        {
            { "a1", "A" }, { "a2", "A" }, { "b1", "B" }, { "b2", "B" }, { "c1", "C" }
        };
        Check("empty_mapping", Roots(new object[] { Zone("a1") }, null));
        Check("empty_result", Roots(new object[0], parts));
        Check("null_result", Roots(null, parts));
        Check("all_rejected", Roots(new object[] { Zone("unknown") }, parts));
        Check("one_partial_owner", Roots(new object[] { Zone("a1") }, parts, "A"));
        Check("all_parts_complete", Roots(new object[] { Zone("a1"), Zone("a2") }, parts));
        Check("independent_rejected_owner_retained", Roots(new object[] { Zone("c1") }, parts));
        Check("two_partial_owners_sorted", Roots(new object[] { Zone("b2"), Zone("a1") }, parts, "A", "B"));
        Check("duplicate_success_does_not_complete_owner", Roots(new object[] { Zone("a1"), Zone("a1") }, parts, "A"));
        Check("merged_members_complete", Roots(new object[] { Zone("a1+a2", "a1", "a2") }, parts));
        Check("merged_members_partial", Roots(new object[] { Zone("a1+b1", "a1", "b1") }, parts, "A", "B"));
        Check("zone_and_members_both_count", Roots(new object[] { Zone("a1", "a2") }, parts));
        Check("merged_duplicate_members_partial", Roots(new object[] { Zone("merged", "a1", "a1") }, parts, "A"));
        Check("case_sensitive_owner_parts", Roots(new object[] { Zone("A1"), Zone("a2") }, parts, "A"));
        Check("root_name_does_not_claim_all_parts", Roots(new object[] { Zone("A") }, parts));
        Check("malformed_entries_ignored", Roots(new object[] { null, 42, "a2", Zone("a1", null, 4) }, parts, "A"));
        var listMembers = new Dictionary<string, object>
        { { "zone_id", "merged" }, { "members", new List<string> { "a1", "a2" } } };
        Check("enumerable_members", Roots(new object[] { listMembers }, parts));
        var stringMembers = new Dictionary<string, object> { { "zone_id", "a1" }, { "members", "a2" } };
        Check("string_is_not_member_array", Roots(new object[] { stringMembers }, parts, "A"));
    }

    private static void CheckNumbers(string culture)
    {
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture);
        Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        string prefix = culture + ":";
        object value = 10.0;
        int reads = 0, writes = 0;
        Func<object> read = delegate { reads++; return value; };
        Action<object> write = delegate(object next) { writes++; value = next; };
        Check(prefix + "write_and_readback", LayoutSafety.SetNumberChecked(read, write, 12.5) &&
              reads == 2 && writes == 1 && value is double && (double)value == 12.5);

        reads = writes = 0;
        Check(prefix + "prior_valid_no_setter", LayoutSafety.SetNumberChecked(read,
              delegate { throw new Exception("Setter must not run"); }, 12.5) && reads == 1);
        Check(prefix + "valid_readonly_value", LayoutSafety.SetNumberChecked(read, null, 12.5));
        Check(prefix + "invalid_readonly_value", !LayoutSafety.SetNumberChecked(read, null, 20));
        Check(prefix + "silently_rejected_setter", !LayoutSafety.SetNumberChecked(read, delegate { }, 20));
        Check(prefix + "throwing_setter", !LayoutSafety.SetNumberChecked(read,
              delegate { throw new InvalidOperationException(); }, 20));
        Check(prefix + "rounded_setter", !LayoutSafety.SetNumberChecked(read,
              delegate(object next) { value = Math.Round((double)next); }, 20.4));
        Check(prefix + "rounding_within_explicit_tolerance", LayoutSafety.SetNumberChecked(read,
              delegate(object next) { value = Math.Round((double)next, 3); }, 30.1234, 0.001));
        Check(prefix + "clamped_setter", !LayoutSafety.SetNumberChecked(read,
              delegate(object next) { value = Math.Min(40.0, (double)next); }, 100));

        value = "1.5";
        Check(prefix + "invariant_string_conversion", LayoutSafety.SetNumberChecked(read, write, 2.75) &&
              value is string && (string)value == "2.75");
        value = 1m;
        Check(prefix + "decimal_type_preserved", LayoutSafety.SetNumberChecked(read, write, 2.75) &&
              value is decimal && (decimal)value == 2.75m);
        value = 1;
        Check(prefix + "integer_type_preserved", LayoutSafety.SetNumberChecked(read, write, 3) &&
              value is int && (int)value == 3);
        value = 1;
        Check(prefix + "integer_rounding_detected", !LayoutSafety.SetNumberChecked(read, write, 3.4));
        value = (byte)1;
        Check(prefix + "conversion_overflow", !LayoutSafety.SetNumberChecked(read, write, 300));

        value = 12.5;
        reads = writes = 0;
        Check(prefix + "nan_request", !LayoutSafety.SetNumberChecked(read, write, double.NaN) && reads == 0 && writes == 0);
        Check(prefix + "infinite_request", !LayoutSafety.SetNumberChecked(read, write, double.PositiveInfinity) && reads == 0 && writes == 0);
        Check(prefix + "negative_infinite_request", !LayoutSafety.SetNumberChecked(read, write, double.NegativeInfinity) && reads == 0 && writes == 0);
        Check(prefix + "nan_readback", !LayoutSafety.SetNumberChecked(read, delegate { value = double.NaN; }, 20));
        value = 12.5;
        Check(prefix + "infinite_readback", !LayoutSafety.SetNumberChecked(read, delegate { value = double.PositiveInfinity; }, 20));
        value = double.NaN;
        Check(prefix + "repair_nan_value", LayoutSafety.SetNumberChecked(read, write, 20) && (double)value == 20);
        value = double.NegativeInfinity;
        Check(prefix + "repair_infinite_value", LayoutSafety.SetNumberChecked(read, write, 20) && (double)value == 20);

        Check(prefix + "negative_tolerance", !LayoutSafety.SetNumberChecked(read, write, 20, -1));
        Check(prefix + "nan_tolerance", !LayoutSafety.SetNumberChecked(read, write, 20, double.NaN));
        Check(prefix + "infinite_tolerance", !LayoutSafety.SetNumberChecked(read, write, 20, double.PositiveInfinity));
        Check(prefix + "null_reader", !LayoutSafety.SetNumberChecked(null, write, 20));
        Check(prefix + "throwing_reader", !LayoutSafety.SetNumberChecked(delegate { throw new Exception(); }, write, 20));
        value = null;
        Check(prefix + "null_prior_value", !LayoutSafety.SetNumberChecked(read, write, 20));
        value = "not a number";
        Check(prefix + "nonnumeric_prior_value", !LayoutSafety.SetNumberChecked(read, write, 20));
        value = 12.5;
        Check(prefix + "null_readback", !LayoutSafety.SetNumberChecked(read, delegate { value = null; }, 20));
        reads = 0;
        value = 12.5;
        Check(prefix + "throwing_readback", !LayoutSafety.SetNumberChecked(
              delegate { if (++reads > 1) throw new Exception(); return value; }, write, 20));
        value = 12.5;
        writes = 0;
        Check(prefix + "prior_within_tolerance", LayoutSafety.SetNumberChecked(read, write, 12.5000005) && writes == 0);
        value = 12.5;
        writes = 0;
        Check(prefix + "zero_tolerance_exact_write", LayoutSafety.SetNumberChecked(read, write, 12.5000005, 0) && writes == 1);
    }

    private static bool Shared(IEnumerable<IEnumerable<string>> groups, object[] zones,
        Dictionary<string, string> parts, params string[] missing)
    {
        return string.Join("|", LayoutSafety.IncompleteSharedGroups(groups, zones, parts).ToArray()) ==
               string.Join("|", missing);
    }

    private static void CheckSharedGroups()
    {
        var ab = new[] { new[] { "A", "B" } };
        var chain = new[] { new[] { "B", "C" }, new[] { "A", "B" } };
        var parts = new Dictionary<string, string>
        { { "a1", "A" }, { "a2", "A" }, { "b1", "B" } };
        Check("shared_a_only_blocks_b", Shared(ab, new object[] { Zone("A") }, null, "B"));
        Check("shared_both_ready", Shared(ab, new object[] { Zone("A"), Zone("B") }, null));
        Check("shared_all_rejected_preserved", Shared(ab, new object[0], null));
        Check("shared_unrelated_success", Shared(ab, new object[] { Zone("C") }, null));
        Check("shared_missing_participant_retained", Shared(ab, new object[] { Zone("A"), null }, null, "B"));
        Check("shared_null_response", Shared(ab, null, null));
        Check("shared_null_groups", Shared(null, new object[] { Zone("A") }, null));
        Check("shared_duplicate_members", Shared(new[] { new[] { "A", "B", "B" } },
              new object[] { Zone("A") }, null, "B"));
        Check("shared_transitive_a_only", Shared(chain, new object[] { Zone("A") }, null, "B", "C"));
        Check("shared_transitive_c_only", Shared(chain, new object[] { Zone("C") }, null, "A", "B"));
        Check("shared_transitive_b_only", Shared(chain, new object[] { Zone("B") }, null, "A", "C"));
        Check("shared_transitive_endpoints", Shared(chain, new object[] { Zone("A"), Zone("C") }, null, "B"));
        Check("shared_transitive_all_ready", Shared(chain,
              new object[] { Zone("A"), Zone("B"), Zone("C") }, null));
        Check("shared_disjoint_group_untouched", Shared(new[] { new[] { "A", "B" }, new[] { "C", "D" } },
              new object[] { Zone("A") }, null, "B"));
        Check("shared_mapped_root_complete", Shared(ab, new object[] { Zone("a1"), Zone("a2") }, parts, "B"));
        Check("shared_partial_root_not_ready", Shared(ab, new object[] { Zone("a1"), Zone("b1") }, parts, "A"));
        Check("shared_only_partial_root_changes_nothing", Shared(ab, new object[] { Zone("a1") }, parts));
        Check("shared_old_parts_normalized", Shared(new[] { new[] { "a1", "a2", "b1" } },
              new object[] { Zone("a1"), Zone("a2") }, parts, "B"));
        Check("shared_merged_result_members", Shared(ab, new object[] { Zone("merged", "a1", "a2", "b1") }, parts));
        Check("shared_root_id_does_not_complete_parts", Shared(ab,
              new object[] { Zone("A"), Zone("b1") }, parts, "A"));
        Check("shared_unknown_part_not_guessed", Shared(new[] { new[] { "A", "A.99" } },
              new object[] { Zone("a1"), Zone("a2") }, parts, "A.99"));
        Check("shared_raw_contour_ids", Shared(new[] { new[] { "контур AA", "контур BB" } },
              new object[] { Zone("контур AA") }, parts, "контур BB"));
        Check("shared_mixed_root_and_contour", Shared(new[] { new[] { "a1", "контур BB" } },
              new object[] { Zone("a1"), Zone("a2") }, parts, "контур BB"));
        Check("shared_ids_are_case_sensitive", Shared(ab, new object[] { Zone("a"), Zone("B") }, null, "A"));
        Check("shared_nullable_groups_and_members", Shared(new[] { null, new[] { "A", "B", null, "" } },
              new object[] { Zone("A") }, null, "B"));
    }

    public static int Main()
    {
        CultureInfo originalCulture = Thread.CurrentThread.CurrentCulture;
        CultureInfo originalUiCulture = Thread.CurrentThread.CurrentUICulture;
        try
        {
            CheckRoots();
            CheckSharedGroups();
            CheckNumbers("ru-RU");
            CheckNumbers("en-US");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = originalCulture;
            Thread.CurrentThread.CurrentUICulture = originalUiCulture;
        }
        Console.WriteLine("CladSafetyCheck: checks=" + checks + ", failures=" + failures);
        return failures == 0 ? 0 : 1;
    }
}
