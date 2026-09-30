using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FacadeSafety
{
    // Links the axes/handles of one completed layout to the complete source set
    // captured before calculation. Does not infer freshness from area or bounds.
    internal static class LayoutGeometryGuard
    {
        private const string CurrentKey = "ATLAYOUT_CURRENT";
        internal sealed class Snapshot
        {
            internal readonly List<Dictionary<string, object>> Sources = new List<Dictionary<string, object>>();
            internal readonly List<ObjectId> Refs = new List<ObjectId>();
            public string Fingerprint;
            internal readonly string Revision = Guid.NewGuid().ToString("N");
        }

        internal sealed class Result
        {
            public bool Ok;
            public string Reason;
            public string Fingerprint;
            internal string Revision, LayoutDigest;
            public readonly List<ObjectId> RawSourceIds = new List<ObjectId>();
            public readonly List<ObjectId> ZoneHatchIds = new List<ObjectId>();
        }

        // One read-only validation operation only. Never retain across a dialog,
        // transaction mutation, regeneration or a later command.
        internal sealed class VerificationContext
        {
            internal readonly Dictionary<ObjectId, ZoneGeometryResult> Zones = new Dictionary<ObjectId, ZoneGeometryResult>();
            internal readonly Dictionary<string, List<ObjectId>> SourceGroups = new Dictionary<string, List<ObjectId>>();
        }

        private static ZoneGeometryResult VerifyZone(Transaction tr, Database db, Entity entity, VerificationContext context)
        {
            ZoneGeometryResult result;
            if (context != null && context.Zones.TryGetValue(entity.ObjectId, out result)) return result;
            result = ZoneGeometryGuard.Verify(tr, db, entity, null);
            if (context != null && result.Ok) context.Zones[entity.ObjectId] = result;
            return result;
        }

        internal static Snapshot Capture(Transaction tr, Database db, IEnumerable<ObjectId> rawSourceIds,
            IEnumerable<ZoneGeometryResult> zoneSources)
        {
            var snapshot = new Snapshot();
            var seen = new HashSet<ObjectId>();
            foreach (ObjectId id in rawSourceIds ?? new ObjectId[0])
            {
                if (!seen.Add(id)) continue;
                var pl = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                snapshot.Sources.Add(new Dictionary<string, object>
                {
                    { "kind", "raw" }, { "handle", HandleOf(pl) }, { "geometry", RawGeometry(pl) }
                });
                snapshot.Refs.Add(id);
            }
            foreach (var zone in zoneSources ?? new ZoneGeometryResult[0])
            {
                if (zone == null || !zone.Ok) throw new InvalidOperationException("Источник зоны не проверен.");
                if (!seen.Add(zone.HatchId)) continue;
                var hatch = tr.GetObject(zone.HatchId, OpenMode.ForRead) as Entity;
                snapshot.Sources.Add(new Dictionary<string, object>
                {
                    { "kind", "zone" }, { "handle", HandleOf(hatch) },
                    { "zone_id", zone.ZoneId }, { "geometry", zone.Fingerprint }
                });
                snapshot.Refs.Add(zone.HatchId);
            }
            if (snapshot.Sources.Count == 0) throw new InvalidOperationException("Нет источников геометрии раскладки.");
            snapshot.Fingerprint = Digest(Serializer().Serialize(snapshot.Sources));
            string reason;
            if (!SourcesMatch(tr, db, snapshot.Sources, snapshot.Refs, out reason))
                throw new InvalidOperationException(reason);
            return snapshot;
        }

        internal static void Store(Transaction tr, Database db, Entity carrier, string layoutKey, Snapshot snapshot)
        {
            string reason;
            if (snapshot == null || !SourcesMatch(tr, db, snapshot.Sources, snapshot.Refs, out reason))
                throw new InvalidOperationException("Источники изменились во время расчёта. Повторите раскладку.");
            string layout = Read(tr, carrier, layoutKey, null);
            if (string.IsNullOrEmpty(layout)) throw new InvalidOperationException("Нет записанной метки раскладки.");
            var data = new Dictionary<string, object>
            {
                { "schema", 1 }, { "owner", HandleOf(carrier) }, { "layout_key", layoutKey },
                { "layout_digest", Digest(layout) }, { "sources", snapshot.Sources },
                { "fingerprint", snapshot.Fingerprint }, { "revision", snapshot.Revision }
            };
            Write(tr, carrier, layoutKey + "_GEOMETRY", Serializer().Serialize(data), snapshot.Refs);
            if (carrier is Hatch || carrier is MText)
            {
                var zone = ZoneGeometryGuard.Verify(tr, db, carrier, null);
                if (!zone.Ok) throw new InvalidOperationException(zone.Reason);
                if (carrier.ObjectId == zone.HatchId)
                    Write(tr, carrier, CurrentKey, Serializer().Serialize(new Dictionary<string, object>
                    {
                        { "schema", 1 }, { "owner", HandleOf(carrier) }, { "layout_key", layoutKey },
                        { "revision", snapshot.Revision }, { "layout_digest", Digest(layout) }
                    }), new List<ObjectId>());
            }
        }

        internal static Result Verify(Transaction tr, Database db, Entity carrier, string layoutKey)
        { return VerifyCore(tr, db, carrier, layoutKey, true, null); }

        internal static Result Verify(Transaction tr, Database db, Entity carrier, string layoutKey, VerificationContext context)
        { return VerifyCore(tr, db, carrier, layoutKey, true, context); }

        private static Result VerifyCore(Transaction tr, Database db, Entity carrier, string layoutKey, bool checkCanonical, VerificationContext context)
        {
            try
            {
                var refs = new List<ObjectId>();
                string json = Read(tr, carrier, layoutKey + "_GEOMETRY", refs);
                if (string.IsNullOrEmpty(json)) return Fail("У раскладки нет снимка исходных контуров. Повторите ATTILE или ATCLAD.");
                var d = Serializer().DeserializeObject(json) as Dictionary<string, object>;
                if (Text(d, "schema") != "1" || Text(d, "revision").Length == 0 || Text(d, "owner") != HandleOf(carrier) ||
                    Text(d, "layout_key") != layoutKey)
                    return Fail("Метка раскладки скопирована или несовместима. Повторите ATTILE или ATCLAD.");
                string layout = Read(tr, carrier, layoutKey, null);
                if (string.IsNullOrEmpty(layout) || Text(d, "layout_digest") != Digest(layout))
                    return Fail("Метка раскладки изменена после расчёта. Повторите ATTILE или ATCLAD.");
                var sources = new List<Dictionary<string, object>>();
                var entries = Get(d, "sources") as object[];
                if (entries == null) return Fail("Снимок источников повреждён. Повторите раскладку.");
                foreach (var item in entries)
                {
                    var entry = item as Dictionary<string, object>;
                    if (entry == null) return Fail("Снимок источников повреждён. Повторите раскладку.");
                    sources.Add(entry);
                }
                if (Text(d, "fingerprint") != Digest(Serializer().Serialize(sources)))
                    return Fail("Снимок источников повреждён. Повторите раскладку.");
                string reason;
                List<ObjectId> priorRefs = null;
                string group = Text(d, "fingerprint");
                bool prior = context != null && context.SourceGroups.TryGetValue(group, out priorRefs) && SameRefs(priorRefs, refs);
                if (!prior)
                {
                    if (!SourcesMatch(tr, db, sources, refs, out reason, context)) return Fail(reason);
                    if (context != null) context.SourceGroups[group] = new List<ObjectId>(refs);
                }
                var result = new Result { Ok = true, Reason = "", Fingerprint = Text(d, "fingerprint"),
                    Revision = Text(d, "revision"), LayoutDigest = Text(d, "layout_digest") };
                for (int i = 0; i < sources.Count; i++)
                    (Text(sources[i], "kind") == "raw" ? result.RawSourceIds : result.ZoneHatchIds).Add(refs[i]);
                if (checkCanonical && (carrier is Hatch || carrier is MText))
                {
                    var zone = VerifyZone(tr, db, carrier, context);
                    if (!zone.Ok) return Fail(zone.Reason);
                    var hatch = tr.GetObject(zone.HatchId, OpenMode.ForRead) as Entity;
                    var current = carrier.ObjectId == zone.HatchId ? result : VerifyCore(tr, db, hatch, layoutKey, false, context);
                    string stampJson = Read(tr, hatch, CurrentKey, null);
                    var stamp = string.IsNullOrEmpty(stampJson) ? null :
                        Serializer().DeserializeObject(stampJson) as Dictionary<string, object>;
                    if (!current.Ok || current.Revision != result.Revision || current.Fingerprint != result.Fingerprint ||
                        Text(stamp, "schema") != "1" || Text(stamp, "owner") != HandleOf(hatch) ||
                        Text(stamp, "layout_key") != layoutKey || Text(stamp, "revision") != result.Revision ||
                        Text(stamp, "layout_digest") != current.LayoutDigest)
                        return Fail("Эта марка относится к прежней раскладке зоны. Выберите текущую штриховку или повторите ATTILE/ATCLAD.");
                }
                return result;
            }
            catch (Exception) { return Fail("Не удалось проверить источники раскладки. Повторите ATTILE или ATCLAD."); }
        }

        private static bool SameRefs(List<ObjectId> a, List<ObjectId> b)
        {
            if (a == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool SourcesMatch(Transaction tr, Database db, List<Dictionary<string, object>> sources,
            List<ObjectId> refs, out string reason, VerificationContext context = null)
        {
            reason = "Исходные контуры или зона изменены. Повторите ATTILE или ATCLAD; для зоны сначала ATFZONE.";
            if (sources == null || refs == null || sources.Count == 0 || sources.Count != refs.Count) return false;
            for (int i = 0; i < sources.Count; i++)
            {
                ObjectId id = refs[i];
                if (id.IsNull || id.IsErased) return false;
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                var source = sources[i];
                if (ent == null || Text(source, "handle") != HandleOf(ent)) return false;
                if (Text(source, "kind") == "raw")
                {
                    if (Text(source, "geometry") != RawGeometry(ent as Polyline)) return false;
                }
                else if (Text(source, "kind") == "zone")
                {
                    var zone = VerifyZone(tr, db, ent, context);
                    if (!zone.Ok || zone.ZoneId != Text(source, "zone_id") || zone.Fingerprint != Text(source, "geometry"))
                        return false;
                }
                else return false;
            }
            reason = "";
            return true;
        }

        // Deliberately exact structural snapshot. REVERSE/reindexing can require
        // a fresh layout; a changed contour is never accepted by an area match.
        private static string RawGeometry(Polyline pl)
        {
            if (pl == null || pl.NumberOfVertices < 3 ||
                !(Math.Abs(pl.Elevation) <= 1e-6) || !((pl.Normal - Vector3d.ZAxis).Length <= 1e-9))
                throw new InvalidOperationException("Контур должен лежать в плоскости XY/Z=0.");
            if (!pl.Closed && !(pl.GetPoint2dAt(0).GetDistanceTo(pl.GetPoint2dAt(pl.NumberOfVertices - 1)) <= 0.5))
                throw new InvalidOperationException("Исходный контур не замкнут.");
            var s = new StringBuilder(pl.Closed ? "closed;" : "joined;");
            for (int i = 0; i < pl.NumberOfVertices; i++)
            {
                Point2d p = pl.GetPoint2dAt(i);
                double bulge = pl.GetBulgeAt(i);
                // An unsupported hole must not disappear before nesting and turn
                // into solid cladding. Reject the selected set before calculation.
                if (!(Math.Abs(bulge) <= 1e-9))
                    throw new InvalidOperationException("Дуги исходных контуров и проёмов пока не поддерживаются.");
                Number(s, p.X); Number(s, p.Y); Number(s, bulge);
            }
            return s.ToString();
        }

        private static void Number(StringBuilder s, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidOperationException("Нечисловая координата.");
            s.Append(value.ToString("R", CultureInfo.InvariantCulture)).Append(';');
        }
        private static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        private static string HandleOf(Entity e) { return e == null ? "" : e.Handle.ToString(); }
        private static object Get(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
        private static string Text(Dictionary<string, object> d, string key) { return Convert.ToString(Get(d, key), CultureInfo.InvariantCulture) ?? ""; }
        private static Result Fail(string reason) { return new Result { Ok = false, Reason = reason }; }
        private static string Digest(string value)
        {
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "");
        }
        private static string Read(Transaction tr, Entity carrier, string key, List<ObjectId> refs)
        {
            if (carrier == null || carrier.ExtensionDictionary.IsNull) return null;
            var ext = (DBDictionary)tr.GetObject(carrier.ExtensionDictionary, OpenMode.ForRead);
            if (!ext.Contains(key)) return null;
            var xr = tr.GetObject(ext.GetAt(key), OpenMode.ForRead) as Xrecord;
            if (xr == null || xr.Data == null) return null;
            var s = new StringBuilder();
            foreach (TypedValue tv in xr.Data)
                if (tv.TypeCode == (int)DxfCode.Text) s.Append(Convert.ToString(tv.Value, CultureInfo.InvariantCulture));
                else if (refs != null && tv.TypeCode == (int)DxfCode.SoftPointerId && tv.Value is ObjectId) refs.Add((ObjectId)tv.Value);
            return s.ToString();
        }
        private static void Write(Transaction tr, Entity carrier, string key, string json, List<ObjectId> refs)
        {
            if (carrier.ExtensionDictionary.IsNull) carrier.CreateExtensionDictionary();
            var ext = (DBDictionary)tr.GetObject(carrier.ExtensionDictionary, OpenMode.ForWrite);
            var rb = new ResultBuffer();
            for (int i = 0; i < json.Length; i += 250)
                rb.Add(new TypedValue((int)DxfCode.Text, json.Substring(i, Math.Min(250, json.Length - i))));
            foreach (ObjectId id in refs) rb.Add(new TypedValue((int)DxfCode.SoftPointerId, id));
            Xrecord xr;
            if (ext.Contains(key)) { xr = (Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForWrite); xr.Data = rb; }
            else { xr = new Xrecord { Data = rb }; ext.SetAt(key, xr); tr.AddNewlyCreatedDBObject(xr, true); }
            xr.XlateReferences = true;
        }
    }
}
