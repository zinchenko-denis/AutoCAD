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
        public double Value;
        public bool ReadOnly, Reject, Throw;
    }
    class DynamicBlockReferenceProperty
    {
        readonly BlockReference block;
        readonly PropertyState state;
        readonly double snapshot;
        public DynamicBlockReferenceProperty(BlockReference b, PropertyState p)
        { block = b; state = p; snapshot = p.Value; }
        public string PropertyName { get { return state.Name; } }
        public bool ReadOnly { get { return state.ReadOnly; } }
        public object Value
        {
            get { return block.SnapshotReaders ? snapshot : state.Value; }
            set
            {
                block.Writes++;
                if (state.ReadOnly || state.Throw) throw new InvalidOperationException("TEST_SETTER_DENIED");
                if (!state.Reject) state.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (block.Coupled && state.Name == "высота") block.Properties[0].Value += 10;
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
        public int Writes, Looks, Attributes;
        public BlockReference() { Properties = new List<PropertyState>(); }
        public BlockReference(Point3d p, ObjectId def)
        {
            Properties = Next.Properties; Position = p;
            SnapshotReaders = Next.SnapshotReaders; Coupled = Next.Coupled; BadGeometry = Next.BadGeometry;
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
            double w = block.Properties[0].Value, h = block.Properties[1].Value;
            return new Extents3d { MinPoint = block.Position,
                MaxPoint = new Point3d(block.Position.X + w + (block.BadGeometry ? 1 : 0), block.Position.Y + h, 0) };
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
        }
        static string F2(double value) { return value.ToString("0.##", CultureInfo.InvariantCulture); }
        static void FillAttributes(Transaction tr, BlockReference b, string root, Dictionary<ObjectId, List<ObjectId>> cache)
        { b.Attributes++; }
__MAKE_DYN_REF__
        internal static bool Run(Proto p)
        {
            bool ok;
            var b = MakeDynRef(new Transaction(), new BlockTableRecord(), p, 125.5, -42.5,
                new Dictionary<ObjectId, List<ObjectId>>(), out ok);
            return ok && b.Position.X == 125.5 && b.Position.Y == -42.5 &&
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
                b.Properties.Add(new PropertyState { Name = "ширина", Value = 1240 });
                b.Properties.Add(new PropertyState { Name = "высота", Value = 1377 });
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
            Case("ordinary_cut_821.87x600", (b, p) => { }, true);
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
