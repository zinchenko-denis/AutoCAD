using System;
using System.Collections.Generic;
using System.Globalization;

// Deliberate API doubles: both immediate and snapshot-style property readers
// are exercised. This is a regression in our acceptance logic, not a claim
// that a particular AutoCAD release always returns snapshots.
namespace ACladPlugin
{
    struct ObjectId { }
    class Point3d
    {
        public double X, Y, Z;
        public Point3d(double x, double y, double z) { X = x; Y = y; Z = z; }
    }
    class Extents3d
    {
        public Point3d MinPoint, MaxPoint;
    }
    class Transaction { public void AddNewlyCreatedDBObject(object value, bool add) { } }
    class BlockTableRecord { public void AppendEntity(BlockReference value) { } }
    class PropertyState
    {
        public string Name;
        public object Value;
        public bool ReadOnly, Reject, Throw, ThrowMetadata;
        public double Increment;
        public short TypeCode = 1;
        public string Units = "Distance";
        public object[] Allowed = new object[0];
    }
    class DynamicBlockReferenceProperty
    {
        readonly BlockReference block;
        readonly PropertyState state;
        readonly object snapshot;
        public DynamicBlockReferenceProperty(BlockReference b, PropertyState p)
        { block = b; state = p; snapshot = p.Value; }
        public string PropertyName { get { return state.Name; } }
        public bool ReadOnly { get { return state.ReadOnly; } }
        public short PropertyTypeCode { get {
            if (state.ThrowMetadata) throw new InvalidOperationException("TEST_METADATA_DENIED");
            return state.TypeCode;
        } }
        public string UnitsType { get { return state.Units; } }
        public object[] GetAllowedValues() { return state.Allowed; }
        public object Value
        {
            get { return block.SnapshotReaders ? snapshot : state.Value; }
            set
            {
                block.Writes++;
                if (state.ReadOnly || state.Throw) throw new InvalidOperationException("TEST_SETTER_DENIED");
                if (!state.Reject)
                    state.Value = state.Increment > 0
                        ? (object)(Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture) /
                            state.Increment) * state.Increment) : value;
                if (block.Coupled && state.Name == "высота")
                    block.Properties[0].Value = Convert.ToDouble(block.Properties[0].Value) + 10;
            }
        }
    }
    class BlockReference
    {
        public static BlockReference Next;
        public readonly List<PropertyState> Properties;
        public readonly Point3d Position;
        public string Layer;
        public bool SnapshotReaders, Coupled, BadGeometry;
        // Independent geometry errors: matching numeric properties do not
        // prove that the native stretch operation moved the actual outline.
        public double GeometryWidthDelta, GeometryHeightDelta, GeometryXDelta, GeometryYDelta;
        public double LocalBaseX, LocalBaseY;
        public int Writes, Looks, Attributes;
        public BlockReference() { Properties = new List<PropertyState>(); }
        public BlockReference(Point3d p, ObjectId def)
        {
            Properties = Next.Properties; Position = p;
            SnapshotReaders = Next.SnapshotReaders; Coupled = Next.Coupled; BadGeometry = Next.BadGeometry;
            GeometryWidthDelta = Next.GeometryWidthDelta; GeometryHeightDelta = Next.GeometryHeightDelta;
            GeometryXDelta = Next.GeometryXDelta; GeometryYDelta = Next.GeometryYDelta;
            LocalBaseX = Next.LocalBaseX; LocalBaseY = Next.LocalBaseY;
            Next = this;
        }
        public IEnumerable<DynamicBlockReferenceProperty> DynamicBlockReferencePropertyCollection
        {
            get
            {
                var list = new List<DynamicBlockReferenceProperty>();
                foreach (var p in Properties) list.Add(new DynamicBlockReferenceProperty(this, p));
                return list;
            }
        }
    }
    static class CladCommand
    {
        internal static Extents3d CellExtents(Transaction tr, BlockReference block)
        {
            double w = Convert.ToDouble(block.Properties[0].Value),
                h = Convert.ToDouble(block.Properties[1].Value);
            double x = block.Position.X + block.LocalBaseX + block.GeometryXDelta,
                y = block.Position.Y + block.LocalBaseY + block.GeometryYDelta;
            return new Extents3d { MinPoint = new Point3d(x, y, 0),
                MaxPoint = new Point3d(block.Position.X + (block.LocalBaseX + w + block.GeometryWidthDelta) +
                    block.GeometryXDelta + (block.BadGeometry ? 1 : 0),
                    block.Position.Y + (block.LocalBaseY + h + block.GeometryHeightDelta) + block.GeometryYDelta, 0) };
        }
__CLAD_METHODS__
    }
    static class TilePatternCommand
    {
        internal class SampleElem { public void ApplyLook(BlockReference b) { b.Looks++; } }
        internal class Proto
        {
            public ObjectId Def;
            public SampleElem Te;
            public string Layer = "cassette", PW = "ширина", PH = "высота", Root = "Zone";
            public double W = 821.87, H = 600, Ox, Oy;
            public double TestX = 125.5, TestY = -42.5;
        }
        static string F2(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
        static void FillAttributes(Transaction tr, BlockReference b, string root, Dictionary<ObjectId, List<ObjectId>> cache)
        { b.Attributes++; }
__MAKE_DYN_REF__
__PROTO_SIZE_KEY__
        internal static bool Run(Proto p)
        {
            bool ok;
            var b = MakeDynRef(new Transaction(), new BlockTableRecord(), p, p.TestX, p.TestY,
                new Dictionary<ObjectId, List<ObjectId>>(), out ok);
            return ok && b.Position.X == p.TestX - p.Ox && b.Position.Y == p.TestY - p.Oy &&
                b.Attributes == 1 && b.Looks == (p.Te == null ? 0 : 1);
        }
    }
    static class DynamicBlockSizeCheck
    {
        static int checks, failures;
        static void Case(string name, Action<BlockReference, TilePatternCommand.Proto> setup, bool expected)
        {
            foreach (bool sample in new[] { false, true })
            {
                var b = new BlockReference();
                b.Properties.Add(new PropertyState { Name = "ширина", Value = 1240.0 });
                b.Properties.Add(new PropertyState { Name = "высота", Value = 1377.0 });
                var p = new TilePatternCommand.Proto { Te = sample ? new TilePatternCommand.SampleElem() : null };
                setup(b, p); BlockReference.Next = b;
                bool accepted;
                try { accepted = TilePatternCommand.Run(p); }
                catch (InvalidOperationException) { accepted = false; }
                checks++;
                if (accepted != expected) { failures++; Console.WriteLine("FAIL " + name + "/sample=" + sample); }
            }
        }
        static int Main()
        {
            foreach (string culture in new[] { "ru-RU", "en-US" })
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                string first = TilePatternCommand.PrototypeSizeKey(245.001, 35.85);
                string second = TilePatternCommand.PrototypeSizeKey(245.004, 35.85);
                checks++;
                if (first == second)
                { failures++; Console.WriteLine("FAIL different_cut_sizes_share_prototype/" + culture); }
                checks++;
                if (first != TilePatternCommand.PrototypeSizeKey(245.001, 35.85) || first.Contains(","))
                { failures++; Console.WriteLine("FAIL prototype_key_not_reusable_or_invariant/" + culture); }
                // Два типа подрезки повторяются в пачке. Для каждой копии
                // размер должен совпасть с её прототипом, не с первым ключом F2.
                var protos = new Dictionary<string, double>();
                foreach (double width in new[] { 245.001, 245.004, 245.001, 245.004 })
                {
                    string key = TilePatternCommand.PrototypeSizeKey(width, 35.85);
                    if (!protos.ContainsKey(key)) protos.Add(key, width);
                    checks++;
                    if (Math.Abs(protos[key] - width) > 1e-6)
                    { failures++; Console.WriteLine("FAIL cloned_cut_has_wrong_geometry/" + culture); }
                }
                checks++;
                if (protos.Count != 2)
                { failures++; Console.WriteLine("FAIL expected_two_prototypes_for_four_cuts/" + culture); }
            }
            Case("ordinary_cut_821.87x600", (b, p) => { }, true);
            Case("ordinary_fractional_cut_245x35.85", (b, p) => {
                p.W = 245; p.H = 35.85;
                b.Properties[0].Value = 244.99999999999884;
                b.Properties[1].Value = 65.0;
            }, true);
            checks++;
            if (!(BlockReference.Next.Properties[1].Value is double) ||
                (double)BlockReference.Next.Properties[1].Value != 35.85)
            { failures++; Console.WriteLine("FAIL fractional_native_double_type_and_value_must_be_preserved"); }
            Case("native_increment_1mm_is_not_success", (b, p) => {
                p.W = 245; p.H = 35.85;
                b.Properties[0].Value = 244.99999999999884;
                b.Properties[1].Value = 65.0; b.Properties[1].Increment = 1;
            }, false);
            checks++;
            string rounded = CladCommand.DynSizeDescription(BlockReference.Next, "ширина", "высота");
            if (!rounded.Contains("«высота»=36 [тип=System.Double] [API=1, единицы=Distance]"))
            { failures++; Console.WriteLine("FAIL native_rounding_diagnostic: " + rounded); }
            Case("integer_conversion_must_not_write_rounded_size", (b, p) => {
                p.W = 245; p.H = 35.85;
                b.Properties[0].Value = 244.99999999999884;
                b.Properties[1].Value = (short)65; b.Properties[1].TypeCode = 70;
            }, false);
            checks++;
            if (BlockReference.Next.Writes != 0 || Convert.ToDouble(BlockReference.Next.Properties[1].Value) != 65)
            { failures++; Console.WriteLine("FAIL application_rounded_35.85_to_36_before_native_setter"); }
            string integer = CladCommand.DynSizeDescription(BlockReference.Next, "ширина", "высота");
            checks++;
            if (!integer.Contains("System.Int16") || !integer.Contains("API=70"))
            { failures++; Console.WriteLine("FAIL integer_conversion_diagnostic: " + integer); }
            Case("integer_property_exact_size", (b, p) => {
                p.W = 245; p.H = 36;
                b.Properties[0].Value = 245.0; b.Properties[1].Value = (short)65;
            }, true);
            checks++;
            if (!(BlockReference.Next.Properties[1].Value is short) ||
                (short)BlockReference.Next.Properties[1].Value != 36 || BlockReference.Next.Writes != 1)
            { failures++; Console.WriteLine("FAIL exact_native_int16_type_and_value_must_be_preserved"); }
            Case("restricted_property_reports_allowed_values", (b, p) => {
                p.W = 245; p.H = 35.85;
                b.Properties[0].Value = 245.0; b.Properties[1].Value = 65.0;
                b.Properties[1].Reject = true; b.Properties[1].Allowed = new object[] { 36.0, 65.0 };
            }, false);
            checks++;
            if (!CladCommand.DynSizeDescription(BlockReference.Next, "ширина", "высота").Contains("список=36; 65"))
            { failures++; Console.WriteLine("FAIL allowed_values_diagnostic"); }
            BlockReference.Next.Properties[1].ThrowMetadata = true;
            checks++;
            if (!CladCommand.DynSizeDescription(BlockReference.Next, "ширина", "высота").Contains("«высота»=65 [тип=System.Double]"))
            { failures++; Console.WriteLine("FAIL metadata_error_hides_original_value"); }
            Case("fresh_final_snapshot", (b, p) => b.SnapshotReaders = true, true);
            Case("already_exact_readonly", (b, p) => {
                b.Properties[0].Value = p.W; b.Properties[0].ReadOnly = true;
                b.Properties[1].Value = p.H; b.Properties[1].ReadOnly = true;
            }, true);
            Case("readonly_wrong", (b, p) => b.Properties[0].ReadOnly = true, false);
            Case("setter_silently_rejects", (b, p) => b.Properties[0].Reject = true, false);
            Case("setter_throws", (b, p) => b.Properties[0].Throw = true, false);
            Case("height_changes_width", (b, p) => b.Coupled = true, false);
            Case("missing_dimension", (b, p) => b.Properties[1].Name = "длина", false);
            Case("properties_right_geometry_wrong", (b, p) => b.BadGeometry = true, false);
            // The previous 0.5 mm acceptance hid a real 0.15 mm size/base error.
            // Repeat near the origin and at previously supported/far coordinates.
            foreach (double origin in new[] { 125.5, 4e9, -4e9, 1e11, -1e11 })
            {
                foreach (double width in new[] { 245.0, 821.87 })
                {
                    Case("exact_fractional_geometry_at_" + origin + "_width_" + width, (b, p) => {
                        p.TestX = origin; p.TestY = -origin; p.W = width; p.H = 35.85;
                    }, true);
                    Case("exact_fractional_geometry_nonzero_base_at_" + origin + "_width_" + width, (b, p) => {
                        p.TestX = origin; p.TestY = -origin; p.W = width; p.H = 35.85;
                        b.LocalBaseX = p.Ox = 0.01; b.LocalBaseY = p.Oy = 0.03;
                    }, true);
                }
                foreach (double error in new[] { -0.15, 0.15 })
                {
                    Case("exact_properties_wrong_width_" + origin + "_" + error, (b, p) => {
                        p.TestX = origin; p.TestY = -origin; b.GeometryWidthDelta = error;
                    }, false);
                    Case("exact_properties_wrong_height_" + origin + "_" + error, (b, p) => {
                        p.TestX = origin; p.TestY = -origin; b.GeometryHeightDelta = error;
                    }, false);
                    Case("exact_properties_wrong_base_x_" + origin + "_" + error, (b, p) => {
                        p.TestX = origin; p.TestY = -origin; b.GeometryXDelta = error;
                    }, false);
                    Case("exact_properties_wrong_base_y_" + origin + "_" + error, (b, p) => {
                        p.TestX = origin; p.TestY = -origin; b.GeometryYDelta = error;
                    }, false);
                }
            }
            Case("fractional_geometry_roundoff_is_accepted", (b, p) => {
                p.W = 245; p.H = 35.85; b.GeometryWidthDelta = 1e-12;
                b.GeometryHeightDelta = -1e-12; b.GeometryXDelta = 1e-12; b.GeometryYDelta = -1e-12;
            }, true);
            Case("geometry_error_exceeding_numerical_tolerance", (b, p) => b.GeometryWidthDelta = 0.00001, false);
            Case("large_y_must_not_expand_x_tolerance", (b, p) => {
                p.TestY = 1e11; b.GeometryWidthDelta = 0.00001;
            }, false);
            Case("large_x_must_not_expand_y_tolerance", (b, p) => {
                p.TestX = 1e11; b.GeometryHeightDelta = 0.00001;
            }, false);
            foreach (double localBase in new[] { 1e11, -1e11 })
            {
                Case("large_local_base_at_small_target_" + localBase, (b, p) => {
                    b.LocalBaseX = p.Ox = localBase; b.LocalBaseY = p.Oy = -localBase;
                    p.H = 35.85;
                }, true);
                Case("large_local_base_does_not_hide_0.15mm_" + localBase, (b, p) => {
                    b.LocalBaseX = p.Ox = localBase; b.LocalBaseY = p.Oy = -localBase;
                    p.H = 35.85; b.GeometryWidthDelta = 0.15;
                }, false);
            }
            Case("coordinate_roundoff_tolerance_is_capped", (b, p) => {
                p.TestX = 1e12; p.TestY = -1e12; b.GeometryWidthDelta = 0.00025;
            }, false);
            foreach (double origin in new[] { 1e13, 1e14, -1e14 })
                Case("coordinate_scale_must_not_hide_0.15mm_" + origin, (b, p) => {
                    p.TestX = origin; p.TestY = -origin; b.GeometryWidthDelta = 0.15;
                }, false);
            Case("nonfinite_geometry_nan", (b, p) => b.GeometryWidthDelta = double.NaN, false);
            Case("nonfinite_geometry_infinity", (b, p) => b.GeometryHeightDelta = double.PositiveInfinity, false);
            Case("nan_request", (b, p) => p.W = double.NaN, false);
            Case("wrong_base", (b, p) => p.Ox = 5, false);
            Case("unchanged_size", (b, p) => { b.Properties[0].Value = p.W; b.Properties[1].Value = p.H; }, true);
            checks++;
            if (BlockReference.Next.Writes != 0) { failures++; Console.WriteLine("FAIL unnecessary_dynamic_evaluation"); }
            Console.WriteLine("ATTILE dynamic size: " + checks + " checks, " + failures + " failures; CAD doubles, not AutoCAD");
            return failures == 0 ? 0 : 1;
        }
    }
}
