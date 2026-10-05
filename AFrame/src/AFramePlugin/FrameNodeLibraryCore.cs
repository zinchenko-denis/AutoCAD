using System;
using System.Collections.Generic;

namespace AFramePlugin
{
    // This contract describes explicit drawing parameters, not a mounting or
    // strength calculation. The native definition owns geometry and labels.
    internal static class FrameNodeLibraryContract
    {
        internal const string Key = "AF_NODE_LIBRARY";
        // Separate identity: the first source contains explicitly conditional
        // undimensioned details and must not impersonate a production drawing.
        internal const string NodeId = "vector1_2015_type1_4_2_1_training";
        internal const int Version = 1;
        internal const string Insulation = "AFN_INSULATION";
        internal const string Cladding = "AFN_CLADDING_X";
        internal const string Variant = "AFN_VARIANT";
        internal const string VariantA = "V1_KR2_70_200_GP_40_40_1p2";
        internal const string VariantB = "V1_KR2_70_250_GP_60_40_1p2";
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static bool Equal(double a, double b) { return Finite(a) && Finite(b) && Math.Abs(a - b) <= 1e-7; }
        internal static void Fail(string text) { throw new InvalidOperationException(text); }
    }

    internal sealed class FrameNodeLibraryValues
    {
        internal readonly double Insulation, Cladding;
        internal readonly string Variant;
        internal FrameNodeLibraryValues(double insulation, double cladding, string variant)
        { Insulation = insulation; Cladding = cladding; Variant = variant; }
        internal void Validate()
        {
            if (!FrameNodeLibraryContract.Finite(Insulation) || !FrameNodeLibraryContract.Finite(Cladding) ||
                Insulation <= 0 || Cladding <= Insulation)
                FrameNodeLibraryContract.Fail("Толщина должна быть положительной, плоскость облицовки — дальше наружной грани утеплителя; оба размера конечные, в мм.");
            if (Variant != FrameNodeLibraryContract.VariantA && Variant != FrameNodeLibraryContract.VariantB)
                FrameNodeLibraryContract.Fail("Неизвестный вариант узла. Выберите один из двух наборов пилота.");
        }
        internal bool Same(FrameNodeLibraryValues other)
        {
            return other != null && FrameNodeLibraryContract.Equal(Insulation, other.Insulation) &&
                FrameNodeLibraryContract.Equal(Cladding, other.Cladding) && Variant == other.Variant;
        }
    }

    internal sealed class FrameNodeLibrarySnapshot
    {
        internal string Definition, Placement, Attributes;
        internal FrameNodeLibraryValues Values;
        internal void VerifyPreserved(FrameNodeLibrarySnapshot after)
        {
            if (Definition != after.Definition || Placement != after.Placement || Attributes != after.Attributes)
                FrameNodeLibraryContract.Fail("Изменились исходное определение, положение вставки или её атрибуты; вся выбранная группа отменена.");
        }
    }

    // One CAD transaction per selected group. Doubles exercise this actual
    // coordinator and the actual CAD adapter; native evaluation remains a host test.
    internal interface IFrameNodeLibraryTransaction : IDisposable
    {
        FrameNodeLibrarySnapshot Read(object id);
        void ValidateTarget(object id, FrameNodeLibraryValues values);
        void Write(object id, FrameNodeLibraryValues values);
        void Commit();
    }

    internal static class FrameNodeLibraryEdit
    {
        internal static int Apply(IFrameNodeLibraryTransaction transaction, IEnumerable<object> selected,
            FrameNodeLibraryValues target)
        {
            target.Validate();
            var ids = new List<object>(); var seen = new HashSet<object>();
            foreach (var id in selected) if (id != null && seen.Add(id)) ids.Add(id);
            if (ids.Count == 0) FrameNodeLibraryContract.Fail("Не выбраны библиотечные узлы.");
            var before = new List<FrameNodeLibrarySnapshot>(ids.Count);
            // No setter is reached until every selected reference is accepted.
            foreach (var id in ids)
            {
                before.Add(transaction.Read(id));
                transaction.ValidateTarget(id, target);
            }
            int changed = 0;
            for (int i = 0; i < ids.Count; i++)
                if (!before[i].Values.Same(target)) { transaction.Write(ids[i], target); changed++; }
            // Read fresh collections only after the complete final tuple has
            // been assigned. A native setter can reject silently or couple values.
            for (int i = 0; i < ids.Count; i++)
            {
                var after = transaction.Read(ids[i]);
                if (!target.Same(after.Values))
                    FrameNodeLibraryContract.Fail("AutoCAD не принял итоговые параметры узла; вся выбранная группа отменена.");
                before[i].VerifyPreserved(after);
            }
            transaction.Commit();
            return changed;
        }
    }
}
