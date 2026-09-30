using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace FacadeSafety
{
    internal static partial class FacadeQuantityStore
    {
        internal const string FrameOwnerKey = "AFACADE_FRAME_QUANTITY_OWNER";
        internal const string FrameElementKey = "AFACADE_FRAME_QUANTITY_ELEMENT";
        private const string FrameUnavailableKey = "AFACADE_FRAME_QUANTITY_UNAVAILABLE";
        private static readonly string[] SourceKeys = { "ATFZONE", "ATFZONE_GEOMETRY", "ATCLAD", "ATTILE",
            "ATCLAD_GEOMETRY", "ATTILE_GEOMETRY", "ATLAYOUT_CURRENT", "ATFRAME_RAIL" };

        public sealed class FrameSource
        {
            public string handle { get; set; }
            public string geometry { get; set; }
            public string metadata { get; set; }
        }
        public sealed class FrameSources
        {
            public List<FrameSource> sources { get; set; } = new List<FrameSource>();
        }

        internal static bool IsFrameCandidate(Transaction tr, Entity e)
        { return e != null && !(e is Table) && (Has(tr, e, FrameOwnerKey) || Has(tr, e, FrameElementKey) ||
            Has(tr, e, FrameUnavailableKey) || Has(tr, e, "ATFRAME") || Has(tr, e, "ATFZONE")); }

        internal static QuantitySelection ReadFrame(Transaction tr, Database db, IEnumerable<ObjectId> ids, bool byZone = true)
        { return ReadCore(tr, db, ids, true, byZone, false); }

        internal static void StoreFrame(Transaction tr, Database db, IEnumerable<Entity> carriers,
            QuantityReport report, FrameSources sources)
        {
            if (report == null || report.kind != "frame") throw new InvalidOperationException("Нет состава подсистемы.");
            var owners = new List<Entity>(carriers);
            StoreCore(tr, db, owners, "ATFRAME", report, sources);
            foreach (var e in owners) Remove(tr, e, FrameUnavailableKey);
        }

        // A partial legacy clamps operation remains available, but must never
        // label its new clamps as a verified bill of the retained whole frame.
        internal static void MarkFrameUnavailable(Transaction tr, Database db, IEnumerable<Entity> carriers, string reason)
        {
            var root = Root(tr, db, false); var oldRuns = new HashSet<string>();
            foreach (var e in carriers)
            {
                var stamp = ReadStamp(tr, e, FrameOwnerKey);
                if (stamp != null && SafeId(stamp.run_id)) oldRuns.Add(stamp.run_id);
                Remove(tr, e, FrameOwnerKey);
                Write(tr, e, FrameUnavailableKey, string.IsNullOrWhiteSpace(reason) ?
                    "Состав прежней подсистемы не подтверждён. Выполните полное построение ATFRAME." : reason);
            }
            if (root != null) foreach (var run in oldRuns) RemoveUnusedRun(tr, db, root, run);
        }

        private static void CheckFrameUnavailable(Transaction tr, Entity e)
        {
            string reason = Read(tr, e, FrameUnavailableKey);
            if (!string.IsNullOrEmpty(reason)) throw new InvalidOperationException(reason);
        }

        private static LayoutGeometryGuard.Result FrameOwnerState(Transaction tr, Entity e)
        {
            string raw = Read(tr, e, "ATFRAME");
            if (string.IsNullOrEmpty(raw)) return new LayoutGeometryGuard.Result { Ok = false,
                Reason = "У владельца отсутствует метка ATFRAME. Повторите построение подсистемы." };
            var data = Serializer().DeserializeObject(raw) as Dictionary<string, object>;
            object owner;
            if (data == null || !data.TryGetValue("owner", out owner) || Convert.ToString(owner) != e.Handle.ToString())
                return new LayoutGeometryGuard.Result { Ok = false, Reason = "Метка подсистемы скопирована или повреждена." };
            string digest = Hash(raw);
            return new LayoutGeometryGuard.Result { Ok = true, Revision = digest, Fingerprint = digest, LayoutDigest = digest };
        }

        // Capture unique input objects, plus original contours referenced by the
        // existing geometry guards. Cladding's drawn-piece references are not
        // traversed: the frame consumes its validated axes, not its rendering.
        internal static FrameSources CaptureFrameSources(Transaction tr, Database db, IEnumerable<ObjectId> ids)
        {
            var result = new FrameSources(); var queue = new Queue<ObjectId>(ids);
            var seen = new HashSet<ObjectId>(); var context = new LayoutGeometryGuard.VerificationContext();
            DefinitionCaches.Remove(tr);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!seen.Add(id)) continue;
                var e = Live(tr, id);
                if (e == null) throw new InvalidOperationException("Источник подсистемы удалён или отсутствует.");
                if (Has(tr, e, "ATFZONE"))
                {
                    ZoneGeometryResult zone;
                    if (!context.Zones.TryGetValue(id, out zone))
                    { zone = ZoneGeometryGuard.Verify(tr, db, e, null); context.Zones[id] = zone; }
                    if (!zone.Ok) throw new InvalidOperationException(zone.Reason);
                }
                string layout = Has(tr, e, "ATCLAD") ? "ATCLAD" : Has(tr, e, "ATTILE") ? "ATTILE" : null;
                if (layout != null)
                {
                    var check = LayoutGeometryGuard.Verify(tr, db, e, layout, context);
                    if (!check.Ok) throw new InvalidOperationException(check.Reason);
                }
                result.sources.Add(new FrameSource { handle = e.Handle.ToString(), geometry = FrameSourceGeometry(tr, e),
                    metadata = FrameSourceMetadata(tr, e, queue) });
            }
            if (result.sources.Count == 0) throw new InvalidOperationException("Нет проверяемых источников подсистемы.");
            return result;
        }

        private static string FrameSourceGeometry(Transaction tr, Entity e)
        {
            string geometry = Fingerprint(tr, e, DefinitionCaches.GetOrCreateValue(tr), new HashSet<ObjectId>());
            var hatch = e as Hatch;
            if (hatch == null) return geometry;
            var text = new StringBuilder(geometry); Add(text, hatch.Associative); Add(text, (int)hatch.HatchStyle);
            for (int i = 0; i < hatch.NumberOfLoops; i++)
            {
                Add(text, i);
                foreach (ObjectId id in hatch.GetAssociatedObjectIdsAt(i)) Add(text, id.IsNull ? "null" : id.Handle.ToString());
            }
            return Hash(text.ToString());
        }

        private static string FrameSourceMetadata(Transaction tr, Entity e, Queue<ObjectId> sources)
        {
            var text = new StringBuilder();
            DBDictionary ext = e.ExtensionDictionary.IsNull ? null : (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead);
            foreach (string key in SourceKeys)
            {
                var refs = new List<ObjectId>(); string value = ReadRecord(tr, ext, key, refs);
                Add(text, key); Add(text, value);
                foreach (ObjectId id in refs)
                {
                    Add(text, id.IsNull ? "null" : id.Handle.ToString());
                    if (sources != null && key.EndsWith("_GEOMETRY", StringComparison.Ordinal)) sources.Enqueue(id);
                }
            }
            return Hash(text.ToString());
        }

        private static void VerifyFrameSources(Transaction tr, Database db, FrameSources snapshot,
            Dictionary<string, FrameSource> observed = null)
        {
            if (snapshot == null || snapshot.sources == null || snapshot.sources.Count == 0)
                throw new InvalidOperationException("Нет снимка источников подсистемы. Повторите ATFRAME.");
            var seen = new HashSet<string>();
            if (observed == null) observed = new Dictionary<string, FrameSource>();
            foreach (var source in snapshot.sources)
            {
                if (source == null || !seen.Add(source.handle)) throw new InvalidOperationException("Повреждён состав источников подсистемы.");
                FrameSource current;
                if (!observed.TryGetValue(source.handle, out current))
                {
                    var e = Live(tr, Resolve(db, source.handle));
                    if (e != null) current = new FrameSource { handle = source.handle, geometry = FrameSourceGeometry(tr, e),
                        metadata = FrameSourceMetadata(tr, e, null) };
                    observed[source.handle] = current;
                }
                if (current == null || current.geometry != source.geometry || current.metadata != source.metadata)
                    throw new InvalidOperationException("Источники зон, оси облицовки или направляющие изменены. Повторите ATFRAME; прежние количества не используются.");
            }
        }
    }
}
