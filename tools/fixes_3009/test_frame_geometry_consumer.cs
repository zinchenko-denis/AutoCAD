// Actual FrameCommand consumers are inserted below by the Python driver.
// CAD lookup and guard outcomes are deliberately test doubles: this isolates
// whether ATFRAME consumes rejected data or an incomplete source selection.
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

struct ObjectId : IEquatable<ObjectId>
{
    public int Value;
    public bool IsErased;
    public bool IsNull { get { return Value == 0; } }
    public ObjectId(int value, bool erased = false) { Value = value; IsErased = erased; }
    public bool Equals(ObjectId other) { return Value == other.Value; }
    public override bool Equals(object obj) { return obj is ObjectId && Equals((ObjectId)obj); }
    public override int GetHashCode() { return Value; }
}
enum OpenMode { ForRead }
class Database { }
class Entity
{
    public ObjectId ObjectId;
    public Dictionary<string, string> Records = new Dictionary<string, string>();
    public Dictionary<string, FacadeSafety.LayoutGeometryGuard.Result> Layouts =
        new Dictionary<string, FacadeSafety.LayoutGeometryGuard.Result>();
    public FacadeSafety.ZoneGeometryResult Zone;
    public bool ThrowGuard;
    public Entity(int id) { ObjectId = new ObjectId(id); }
}
class Transaction
{
    public Dictionary<ObjectId, Entity> Objects = new Dictionary<ObjectId, Entity>();
    public Entity GetObject(ObjectId id, OpenMode mode) { return Objects[id]; }
    public Entity Add(int id) { var e = new Entity(id); Objects[e.ObjectId] = e; return e; }
}
namespace FacadeSafety
{
    class ZoneGeometryResult
    {
        public bool Ok;
        public string Reason, ZoneId, Fingerprint;
        public ObjectId HatchId;
        public List<Dictionary<string, object>> Parts;
    }
    static class ZoneGeometryGuard
    {
        public static IDictionary<string, Dictionary<string, object>> LastSidecar;
        public static ZoneGeometryResult Verify(Transaction tr, Database db, Entity e,
            IDictionary<string, Dictionary<string, object>> sidecar)
        {
            LastSidecar = sidecar;
            if (e.ThrowGuard) throw new InvalidOperationException("unreadable source");
            return e.Zone;
        }
    }
    static class LayoutGeometryGuard
    {
        public class Result
        {
            public bool Ok;
            public string Reason, Fingerprint;
            public readonly List<ObjectId> RawSourceIds = new List<ObjectId>();
            public readonly List<ObjectId> ZoneHatchIds = new List<ObjectId>();
        }
        public static List<string> Keys = new List<string>();
        public static Result Verify(Transaction tr, Database db, Entity e, string key)
        {
            Keys.Add(key);
            if (e.ThrowGuard) throw new InvalidOperationException("unreadable snapshot");
            return e.Layouts.ContainsKey(key) ? e.Layouts[key] : new Result { Ok = false, Reason = "missing snapshot" };
        }
    }
}
class FrameCommand
{
    const string XKeyClad = "ATCLAD";
    static string ReadData(Transaction tr, Entity e, string key)
    { return e.Records.ContainsKey(key) ? e.Records[key] : null; }
    static object Get(Dictionary<string, object> d, string key)
    { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
    static string SafeStr(object value) { return Convert.ToString(value) ?? ""; }
__ACTUAL_METHODS__
    static int checks, failures;
    static void Check(bool condition, string label)
    {
        checks++;
        if (!condition) { failures++; Console.WriteLine("FAIL " + label); }
    }
    static FacadeSafety.LayoutGeometryGuard.Result Proof(int[] raw, int[] zones)
    {
        var result = new FacadeSafety.LayoutGeometryGuard.Result { Ok = true, Reason = "", Fingerprint = "verified" };
        foreach (int i in raw) result.RawSourceIds.Add(new ObjectId(i));
        foreach (int i in zones) result.ZoneHatchIds.Add(new ObjectId(i));
        return result;
    }
    static FacadeSafety.ZoneGeometryResult Zone(string name, int hatch, string revision)
    {
        return new FacadeSafety.ZoneGeometryResult { Ok = true, ZoneId = name,
            HatchId = new ObjectId(hatch), Fingerprint = revision,
            Parts = new List<Dictionary<string, object>> { new Dictionary<string, object> { { "id", name }, { "verified", true } } } };
    }
    static void TestLayoutRead()
    {
        var tr = new Transaction(); var db = new Database(); var ser = new JavaScriptSerializer();
        Dictionary<string, object> metadata; FacadeSafety.LayoutGeometryGuard.Result proof; string reason;
        var e = tr.Add(1);
        int calls = FacadeSafety.LayoutGeometryGuard.Keys.Count;
        Check(TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && metadata == null && proof == null && calls == FacadeSafety.LayoutGeometryGuard.Keys.Count,
            "unlabelled raw contours stay manual without a guessed snapshot");
        foreach (string missingKey in new[] { "ATCLAD", "ATTILE", "ATFZONE" })
        {
            e.Records[missingKey + "_GEOMETRY"] = "{}";
            Check(!TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
                && metadata == null && reason.Contains(missingKey),
                "remaining " + missingKey + " snapshot without data cannot become manual input");
            e.Records.Clear();
        }
        e.Records["ATCLAD"] = "{\"zone_id\":\"A\",\"joints_x\":[100],\"rows_y\":[200]}";
        Check(!TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && metadata == null && reason.Contains("missing"), "legacy axes without proof never returned");
        e.Layouts["ATCLAD"] = Proof(new[] { 1, 2 }, new int[0]);
        Check(TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && ((object[])metadata["joints_x"]).Length == 1 && proof.RawSourceIds.Count == 2,
            "fresh raw outer and opening proof accompanies axes");
        e.Layouts["ATCLAD"].Ok = false; e.Layouts["ATCLAD"].Reason = "source changed with same area";
        Check(!TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && metadata == null && reason.Contains("same area"), "same-area source rejection stops old axes");
        e.Records.Remove("ATCLAD"); e.Records["ATTILE"] = "{\"zone_id\":\"A+B\",\"joints_x\":[],\"rows_y\":[200]}";
        e.Layouts["ATTILE"] = Proof(new int[0], new[] { 10, 20 });
        Check(TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && FacadeSafety.LayoutGeometryGuard.Keys[FacadeSafety.LayoutGeometryGuard.Keys.Count - 1] == "ATTILE",
            "ATTILE checks the ATTILE snapshot key");
        e.Records["ATCLAD"] = "{}";
        Check(!TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && metadata == null, "stale preferred ATCLAD cannot fall through to a different ATTILE snapshot");
        e.Records.Remove("ATCLAD"); e.Records["ATTILE"] = "broken JSON";
        Check(!TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && metadata == null, "unreadable verified metadata still refuses");
        e.ThrowGuard = true;
        Check(!TryReadVerifiedLayout(tr, db, e, ser, out metadata, out proof, out reason)
            && metadata == null && reason.Length > 0, "guard read exception fails closed");
    }
    static void TestZonesAndCompleteness()
    {
        var tr = new Transaction(); var db = new Database(); string reason;
        var a = tr.Add(1); a.Zone = Zone("A", 10, "revisionA");
        var mark = tr.Add(2); mark.Zone = Zone("A", 10, "revisionA");
        var b = tr.Add(3); b.Zone = Zone("B", 20, "revisionB");
        var sidecar = new Dictionary<string, Dictionary<string, object>> {
            { "untrusted", new Dictionary<string, object> { { "id", "untrusted" } } } };
        List<Dictionary<string, object>> parts; List<ObjectId> hatches;
        Check(TryGetVerifiedZoneParts(tr, db, new[] { a.ObjectId, mark.ObjectId }, sidecar, out parts, out hatches, out reason)
            && parts.Count == 1 && hatches.Count == 1 && (bool)parts[0]["verified"]
            && Object.ReferenceEquals(sidecar, FacadeSafety.ZoneGeometryGuard.LastSidecar),
            "payload uses verified parts, whole sidecar forwarded, hatch and mark deduplicated");
        mark.Zone.Fingerprint = "other revision";
        Check(!TryGetVerifiedZoneParts(tr, db, new[] { a.ObjectId, mark.ObjectId }, sidecar, out parts, out hatches, out reason)
            && parts.Count == 0 && hatches.Count == 0, "conflicting capture revisions clear entire pending zone result");
        mark.Zone.Ok = false; mark.Zone.Reason = "stale hatch";
        Check(!TryGetVerifiedZoneParts(tr, db, new[] { a.ObjectId, mark.ObjectId }, sidecar, out parts, out hatches, out reason)
            && parts.Count == 0 && hatches.Count == 0 && reason == "stale hatch",
            "valid plus stale selection yields no partial zone payload");
        Check(!TryGetVerifiedZoneParts(tr, db, new[] { new ObjectId(1, true) }, sidecar, out parts, out hatches, out reason),
            "erased source carrier rejected");
        Check(!TryGetVerifiedZoneParts(tr, db, new ObjectId[0], sidecar, out parts, out hatches, out reason),
            "empty selection cannot authorize a sidecar zone");
        var bothZones = new[] { Proof(new int[0], new[] { 10, 20 }) };
        Check(TryGetVerifiedZoneParts(tr, db, new[] { a.ObjectId }, sidecar, out parts, out hatches, out reason)
            && !IsLayoutSelectionComplete(bothZones, new ObjectId[0], hatches, out reason),
            "fresh A-only selection cannot replace previous A+B frame");
        Check(TryGetVerifiedZoneParts(tr, db, new[] { a.ObjectId, b.ObjectId }, sidecar, out parts, out hatches, out reason)
            && parts.Count == 2 && IsLayoutSelectionComplete(bothZones, new ObjectId[0], hatches, out reason),
            "complete A+B passes with two physical verified roots");
        var raw = new[] { Proof(new[] { 100, 101 }, new int[0]) };
        Check(!IsLayoutSelectionComplete(raw, new[] { new ObjectId(100) }, new ObjectId[0], out reason),
            "fresh raw outer without recorded opening refuses before engine");
        Check(IsLayoutSelectionComplete(raw, new[] { new ObjectId(100), new ObjectId(101) }, new ObjectId[0], out reason),
            "raw outer plus recorded opening passes");
        var mixed = new[] { Proof(new[] { 100 }, new[] { 10 }) };
        Check(!IsLayoutSelectionComplete(mixed, new[] { new ObjectId(100) }, new ObjectId[0], out reason),
            "mixed saved sources require their zone as well as raw geometry");
        Check(IsLayoutSelectionComplete(new FacadeSafety.LayoutGeometryGuard.Result[0],
            new[] { new ObjectId(999) }, new ObjectId[0], out reason), "unlabelled manual workflow has no invented source requirements");
        a.ThrowGuard = true;
        Check(!TryGetVerifiedZoneParts(tr, db, new[] { a.ObjectId }, sidecar, out parts, out hatches, out reason)
            && parts.Count == 0 && hatches.Count == 0, "zone guard exception clears pending output");
    }
    static int Main()
    {
        TestLayoutRead(); TestZonesAndCompleteness();
        string reason;
        var outer = new List<double[]> { new[] { 0.0, 0.0 }, new[] { 6000.0, 0.0 },
            new[] { 6000.0, 6000.0 }, new[] { 0.0, 6000.0 } };
        var opening = new List<double[]> { new[] { 1000.0, 1500.0 }, new[] { 2200.0, 1500.0 },
            new[] { 2200.0, 3000.0 }, new[] { 1000.0, 3000.0 } };
        var lines = new List<double> { 0, 0, 0, 0 };
        Check(IsSupportedRawContour(outer, lines, out reason) &&
            IsSupportedRawContour(opening, lines, out reason), "manual outer and straight opening remain supported");
        var arc = new List<double> { 0, 0.2, 0, 0 };
        Check(IsSupportedRawContour(outer, lines, out reason) &&
            !IsSupportedRawContour(opening, arc, out reason) && reason.Contains("дуговые"),
            "curved raw opening rejects complete manual request instead of disappearing before nesting");
        opening[2][0] = double.NaN;
        Check(!IsSupportedRawContour(opening, lines, out reason), "nonfinite manual opening coordinates refuse");
        opening[2][0] = 2200;
        lines[1] = double.PositiveInfinity;
        Check(!IsSupportedRawContour(opening, lines, out reason), "nonfinite manual bulge refuses");
        Console.WriteLine("Frame geometry consumers: " + checks + " checks, " + failures +
            " failures; actual extracted methods, CAD/guard doubles, not AutoCAD");
        return failures == 0 ? 0 : 1;
    }
}
