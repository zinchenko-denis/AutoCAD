using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Web.Script.Serialization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace FacadeSafety
{
    internal sealed class ZoneGeometryResult
    {
        public bool Ok;
        public string Reason, ZoneId, Fingerprint;
        public ObjectId HatchId;
        public List<Dictionary<string, object>> Parts = new List<Dictionary<string, object>>();
    }

    /// <summary>Read-only freshness guard. A passport belongs to a carrier and one ATFZONE generation.
    /// Soft pointers identify registered sources; full geometry, not area, proves their freshness.</summary>
    internal static class ZoneGeometryGuard
    {
        internal const string Key = "ATFZONE_GEOMETRY";
        private const string DataKey = "ATFZONE";
        private static JavaScriptSerializer Serializer { get { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; } }

        internal static ZoneGeometryResult Capture(Transaction tr, Database db, Hatch hatch, MText mark,
            Dictionary<string, object> zoneData, List<Dictionary<string, object>> exactParts,
            IList<ObjectId> outerIds, IList<ObjectId> holeIds)
        {
            string zoneId = S(Get(zoneData, "zone_id"));
            try
            {
                if (hatch == null || mark == null || zoneId.Length == 0 || exactParts == null || exactParts.Count == 0)
                    return Fail(zoneId, "неполный результат расчёта зоны");
                var ids = new List<ObjectId>(); var sources = new List<GeometryLoop>();
                var handles = new List<string>(); string reason;
                foreach (var group in new[] { outerIds, holeIds })
                {
                    if (group == null) return Fail(zoneId, "нет исходных контуров");
                    foreach (var id in group)
                    {
                        if (ids.Contains(id)) return Fail(zoneId, "исходный контур повторяется");
                        var poly = tr.GetObject(id, OpenMode.ForRead) as Polyline;
                        GeometryLoop loop;
                        if (!TryReadPolyline(poly, group == outerIds ? "outer" : "hole", out loop, out reason))
                            return Fail(zoneId, reason);
                        if (!CanClose(loop)) return Fail(zoneId, "исходный контур не замкнут");
                        ids.Add(id); handles.Add(poly.Handle.ToString()); sources.Add(loop);
                    }
                }
                List<GeometryLoop> hatchLoops; List<int> loopTypes;
                if (!ReadHatch(hatch, ids, sources, out hatchLoops, out loopTypes, out reason)) return Fail(zoneId, reason);
                if (!GeometryFingerprint.Equivalent(Boundaries(sources), hatchLoops, out reason))
                    return Fail(zoneId, "границы штриховки не совпадают с исходными контурами: " + reason);
                List<GeometryLoop> partsLoops;
                if (!PartsGeometry(exactParts, out partsLoops, out reason) ||
                    !GeometryFingerprint.Equivalent(Boundaries(sources), partsLoops, out reason))
                    return Fail(zoneId, "результат расчёта не совпадает с контурами: " + reason);
                string sourceHash, hatchHash;
                if (!GeometryFingerprint.TryFingerprint(sources, out sourceHash, out reason) ||
                    !GeometryFingerprint.TryFingerprint(hatchLoops, out hatchHash, out reason)) return Fail(zoneId, reason);
                var state = new Dictionary<string, object> {
                    { "schema", 1 }, { "revision", Guid.NewGuid().ToString("N") }, { "zone_id", zoneId },
                    { "hatch_handle", hatch.Handle.ToString() }, { "source_handles", handles.ToArray() },
                    { "sources", PackLoops(sources) }, { "hatch_loops", PackLoops(hatchLoops) },
                    { "loop_types", loopTypes.ToArray() }, { "parts", exactParts },
                    { "report_hash", GeometryFingerprint.Hash(Canonical(zoneData)) },
                    { "geometry_hash", GeometryFingerprint.Hash(sourceHash + ":" + hatchHash + ":" + Canonical(exactParts)) }
                };
                // One immutable generation is written to both carriers; carrier identity remains individual.
                var refs = new List<ObjectId> { hatch.ObjectId }; refs.AddRange(ids);
                Store(tr, hatch, state, refs);
                Store(tr, mark, state, refs);
                return Verify(tr, db, hatch, null);
            }
            catch (System.Exception ex) { return Fail(zoneId, "не удалось сохранить проверяемую геометрию: " + ex.Message); }
        }

        internal static ZoneGeometryResult Verify(Transaction tr, Database db, Entity carrier,
            IDictionary<string, Dictionary<string, object>> sidecarOrNull)
        {
            string zoneId = "";
            try
            {
                List<ObjectId> ignored; List<ObjectId> refs; var state = Read(tr, carrier, Key, out refs);
                var data = Read(tr, carrier, DataKey, out ignored);
                zoneId = S(Get(data, "zone_id"));
                if (state == null || S(Get(state, "schema")) != "1")
                {
                    if (HasSnapshot(tr, carrier)) return Fail(zoneId, "снимок геометрии повреждён или имеет неподдерживаемую версию");
                    return new ZoneGeometryResult { ZoneId = zoneId,
                        Reason = "Зона «" + (zoneId.Length == 0 ? "без марки" : zoneId) +
                        "»: нет проверяемого снимка геометрии (старая зона). Выполните ATFZONEACCEPT, чтобы явно принять текущие контуры без пересоздания зоны. " +
                        "Старая раскладка и ведомости при этом не подтверждаются; их нужно пересчитать." };
                }
                if (!(carrier is Hatch) && !(carrier is MText)) return Fail(zoneId, "неподдерживаемый объект зоны");
                if (S(Get(state, "revision")).Length == 0 || zoneId.Length == 0 || zoneId != S(Get(state, "zone_id"))) return Fail(zoneId, "повреждены данные зоны");
                if (S(Get(state, "carrier_handle")) != carrier.Handle.ToString()) return Fail(zoneId, "выбрана копия штриховки или марки");
                if (refs.Count < 2 || refs[0].IsNull || refs[0].IsErased) return Fail(zoneId, "потеряна связь со штриховкой");
                var hatch = tr.GetObject(refs[0], OpenMode.ForRead) as Hatch;
                if (hatch == null || S(Get(state, "hatch_handle")) != hatch.Handle.ToString() ||
                    (carrier is Hatch && carrier.ObjectId != hatch.ObjectId)) return Fail(zoneId, "изменена связь со штриховкой");
                List<ObjectId> hatchRefs; var hatchState = Read(tr, hatch, Key, out hatchRefs);
                if (hatchState == null || S(Get(hatchState, "carrier_handle")) != hatch.Handle.ToString() ||
                    StateDigest(state) != StateDigest(hatchState) || !SameRefs(refs, hatchRefs))
                    return Fail(zoneId, "марка и штриховка относятся к разным расчётам");
                string reportHash = S(Get(state, "report_hash"));
                if (reportHash.Length == 0 || reportHash != GeometryFingerprint.Hash(Canonical(data)) ||
                    reportHash != GeometryFingerprint.Hash(Canonical(Read(tr, hatch, DataKey, out ignored))))
                    return Fail(zoneId, "расчётные данные зоны изменились");
                var expectedSources = UnpackLoops(Get(state, "sources"));
                var expectedHatch = UnpackLoops(Get(state, "hatch_loops"));
                var sourceHandles = Items(Get(state, "source_handles"));
                if (expectedSources.Count == 0 || refs.Count != expectedSources.Count + 1 || sourceHandles.Count != expectedSources.Count)
                    return Fail(zoneId, "неполные ссылки на исходные контуры");
                var actualSources = new List<GeometryLoop>(); var ids = new List<ObjectId>(); string reason;
                for (int i = 0; i < expectedSources.Count; i++)
                {
                    var id = refs[i + 1];
                    if (id.IsNull || id.IsErased || ids.Contains(id)) return Fail(zoneId, "исходный контур удалён или связь повреждена");
                    var poly = tr.GetObject(id, OpenMode.ForRead) as Polyline; GeometryLoop loop;
                    if (poly == null || poly.Handle.ToString() != S(sourceHandles[i])) return Fail(zoneId, "изменена ссылка на исходный контур");
                    if (!TryReadPolyline(poly, expectedSources[i].Role, out loop, out reason)) return Fail(zoneId, reason);
                    if (!GeometryFingerprint.Equivalent(new[] { expectedSources[i] }, new[] { loop }, out reason))
                        return Fail(zoneId, "изменился исходный контур " + poly.Handle + ": " + reason);
                    ids.Add(id); actualSources.Add(loop);
                }
                List<GeometryLoop> currentHatch; List<int> types;
                if (!ReadHatch(hatch, ids, actualSources, out currentHatch, out types, out reason)) return Fail(zoneId, reason);
                if (!GeometryFingerprint.Equivalent(expectedHatch, currentHatch, out reason) ||
                    !GeometryFingerprint.Equivalent(Boundaries(actualSources), currentHatch, out reason))
                    return Fail(zoneId, "изменились границы штриховки: " + reason);
                if (Canonical(Get(state, "loop_types")) != Canonical(types)) return Fail(zoneId, "изменился тип границ штриховки");
                var parts = Dictionaries(Get(state, "parts"));
                List<GeometryLoop> savedGeometry;
                if (!PartsGeometry(parts, out savedGeometry, out reason) ||
                    !GeometryFingerprint.Equivalent(Boundaries(actualSources), savedGeometry, out reason))
                    return Fail(zoneId, "снимок рассчитанных частей не совпадает с контурами: " + reason);
                string sourceHash, hatchHash;
                if (!GeometryFingerprint.TryFingerprint(expectedSources, out sourceHash, out reason) ||
                    !GeometryFingerprint.TryFingerprint(expectedHatch, out hatchHash, out reason) ||
                    S(Get(state, "geometry_hash")) != GeometryFingerprint.Hash(sourceHash + ":" + hatchHash + ":" + Canonical(parts)))
                    return Fail(zoneId, "повреждён снимок геометрии");
                if (sidecarOrNull != null)
                {
                    var candidates = SelectParts(sidecarOrNull, zoneId);
                    if (!EquivalentParts(parts, candidates, out reason)) return Fail(zoneId, "файл _fzones.json не соответствует расчёту: " + reason);
                    parts = candidates;
                }
                return new ZoneGeometryResult { Ok = true, ZoneId = zoneId, HatchId = hatch.ObjectId,
                    Fingerprint = "fz1:" + S(Get(state, "revision")) + ":" + StateDigest(state), Parts = parts };
            }
            catch (System.Exception ex) { return Fail(zoneId, "геометрию нельзя проверить: " + ex.Message); }
        }

        internal static bool HasSnapshot(Transaction tr, Entity carrier)
        {
            if (carrier == null || carrier.ExtensionDictionary.IsNull) return false;
            var ext = tr.GetObject(carrier.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            return ext != null && ext.Contains(Key);
        }

        internal static bool TryReadPolyline(Polyline poly, string role, out GeometryLoop loop, out string reason)
        {
            loop = null; reason = null;
            if (poly == null) { reason = "исходный объект не является полилинией"; return false; }
            if (!WorldPlane(poly.Normal, poly.Elevation)) { reason = "поддерживается только плоскость XY с Z = 0"; return false; }
            var pts = new double[poly.NumberOfVertices][]; var bulges = new double[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                var p = poly.GetPoint2dAt(i); pts[i] = new[] { p.X, p.Y }; bulges[i] = poly.GetBulgeAt(i);
            }
            loop = new GeometryLoop { Role = role, Points = pts, Bulges = bulges, Closed = poly.Closed };
            string unused;
            return GeometryFingerprint.TryFingerprint(new[] { loop }, out unused, out reason);
        }

        private static bool ReadHatch(Hatch hatch, IList<ObjectId> ids, IList<GeometryLoop> sources,
            out List<GeometryLoop> loops, out List<int> types, out string reason)
        {
            loops = new List<GeometryLoop>(); types = new List<int>(); reason = null;
            if (!WorldPlane(hatch.Normal, hatch.Elevation)) { reason = "штриховка находится вне плоскости XY с Z = 0"; return false; }
            if (hatch.HatchStyle != HatchStyle.Normal) { reason = "изменён режим учёта островков штриховки"; return false; }
            if (!hatch.Associative || hatch.NumberOfLoops != ids.Count) { reason = "изменились связи или число границ штриховки"; return false; }
            var used = new HashSet<ObjectId>(); var bySource = new int[ids.Count];
            for (int i = 0; i < hatch.NumberOfLoops; i++)
            {
                var associated = hatch.GetAssociatedObjectIdsAt(i);
                if (associated == null || associated.Count != 1 || !ids.Contains(associated[0]) || !used.Add(associated[0]))
                { reason = "штриховка связана с другим набором контуров"; return false; }
                int sourceIndex = ids.IndexOf(associated[0]);
                var boundary = hatch.GetLoopAt(i);
                var pts = new List<double[]>(); var bulges = new List<double>();
                if (boundary.IsPolyline)
                {
                    foreach (BulgeVertex vertex in boundary.Polyline)
                    { pts.Add(new[] { vertex.Vertex.X, vertex.Vertex.Y }); bulges.Add(vertex.Bulge); }
                }
                else
                {
                    var curves = boundary.Curves;
                    if (curves == null || curves.Count == 0) { reason = "граница штриховки не содержит сегментов"; return false; }
                    Point2d first = new Point2d(), previousEnd = new Point2d();
                    for (int j = 0; j < curves.Count; j++)
                    {
                        var curve = curves[j];
                        if (!(curve is LineSegment2d) && !(curve is CircularArc2d))
                        { reason = "граница штриховки содержит неподдерживаемую кривую"; return false; }
                        var start = curve.StartPoint; var end = curve.EndPoint;
                        if (j == 0) first = start;
                        else if (start.GetDistanceTo(previousEnd) > GeometryFingerprint.CoordinateTolerance)
                        { reason = "сегменты границы штриховки не соединены"; return false; }
                        double bulge = 0;
                        var arc = curve as CircularArc2d;
                        if (arc != null)
                        {
                            double sweep = arc.EndAngle - arc.StartAngle;
                            while (sweep < 0) sweep += 2 * Math.PI;
                            while (sweep > 2 * Math.PI) sweep -= 2 * Math.PI;
                            if (sweep < 1e-12 || sweep >= 2 * Math.PI - 1e-12)
                            { reason = "полная окружность в границе штриховки требует повторного построения полилинией"; return false; }
                            bulge = Math.Tan(sweep / 4) * (arc.IsClockWise ? -1 : 1);
                        }
                        pts.Add(new[] { start.X, start.Y }); bulges.Add(bulge); previousEnd = end;
                    }
                    if (previousEnd.GetDistanceTo(first) > GeometryFingerprint.CoordinateTolerance)
                    { reason = "граница штриховки не замкнута"; return false; }
                }
                loops.Add(new GeometryLoop { Role = sources[sourceIndex].Role, Points = pts.ToArray(), Bulges = bulges.ToArray(), Closed = true });
                bySource[sourceIndex] = (int)boundary.LoopType;
            }
            types.AddRange(bySource);
            string unused;
            return GeometryFingerprint.TryFingerprint(loops, out unused, out reason);
        }

        private static bool WorldPlane(Vector3d normal, double elevation)
        {
            return Finite(elevation) && Math.Abs(elevation) <= GeometryFingerprint.CoordinateTolerance &&
                Finite(normal.X) && Finite(normal.Y) && Finite(normal.Z) &&
                Math.Abs(normal.X) <= 1e-9 && Math.Abs(normal.Y) <= 1e-9 && Math.Abs(normal.Z - 1) <= 1e-9;
        }
        private static bool Finite(double d) { return !double.IsNaN(d) && !double.IsInfinity(d); }
        private static bool CanClose(GeometryLoop loop)
        {
            if (loop.Closed) return true;
            if (loop.Points.Length < 3) return false;
            double[] a = loop.Points[0], b = loop.Points[loop.Points.Length - 1];
            return Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1])) <= 0.5;
        }
        private static List<GeometryLoop> Boundaries(IList<GeometryLoop> sources)
        {
            var result = new List<GeometryLoop>();
            foreach (var loop in sources)
                result.Add(new GeometryLoop { Role = loop.Role, Points = loop.Points, Bulges = loop.Bulges, Closed = true });
            return result;
        }

        private static bool PartsGeometry(List<Dictionary<string, object>> parts, out List<GeometryLoop> loops, out string reason)
        {
            loops = new List<GeometryLoop>(); reason = null;
            if (parts == null || parts.Count == 0) { reason = "нет рассчитанных частей зоны"; return false; }
            var ids = new HashSet<string>();
            foreach (var part in parts)
            {
                if (S(Get(part, "schema")) != "facade_zone/1" || !ids.Add(S(Get(part, "id"))))
                { reason = "неподдерживаемая схема или повторённая часть зоны"; return false; }
                double scale; string units = S(Get(part, "units"));
                if (units == "mm") scale = 1; else if (units == "m") scale = 1000;
                else { reason = "неподдерживаемые единицы зоны"; return false; }
                GeometryLoop loop;
                if (!PartLoop(Get(part, "outer") as Dictionary<string, object>, "outer", scale, out loop, out reason)) return false;
                loops.Add(loop);
                var openings = Items(Get(part, "openings"));
                foreach (var value in openings)
                {
                    var opening = value as Dictionary<string, object>;
                    if (opening == null || S(Get(opening, "id")).Length == 0 || S(Get(opening, "kind")).Length == 0)
                    { reason = "повреждены данные проёма"; return false; }
                    if (!PartLoop(Get(opening, "poly") as Dictionary<string, object>, "hole", scale, out loop, out reason)) return false;
                    loops.Add(loop);
                }
            }
            string unused;
            return GeometryFingerprint.TryFingerprint(loops, out unused, out reason);
        }
        private static bool PartLoop(Dictionary<string, object> poly, string role, double scale, out GeometryLoop loop, out string reason)
        {
            loop = null; reason = null;
            if (poly == null) { reason = "отсутствует геометрия части"; return false; }
            var values = Items(Get(poly, "pts")); var pts = new List<double[]>();
            foreach (var p in values)
            {
                var xy = Items(p);
                if (xy.Count != 2) { reason = "неверный формат координат"; return false; }
                pts.Add(new[] { D(xy[0]) * scale, D(xy[1]) * scale });
            }
            var bs = Items(Get(poly, "bulges")); var bulges = new double[pts.Count];
            if (bs.Count != 0 && bs.Count != pts.Count) { reason = "неверное число дуг"; return false; }
            for (int i = 0; i < bs.Count; i++) bulges[i] = D(bs[i]);
            loop = new GeometryLoop { Role = role, Points = pts.ToArray(), Bulges = bulges, Closed = true };
            string unused;
            return GeometryFingerprint.TryFingerprint(new[] { loop }, out unused, out reason);
        }

        private static List<Dictionary<string, object>> SelectParts(IDictionary<string, Dictionary<string, object>> map, string zoneId)
        {
            var parts = new List<Dictionary<string, object>>();
            foreach (var kv in map)
            {
                var part = kv.Value; string id = S(Get(part, "id"));
                string group = S(Get(Get(part, "meta") as Dictionary<string, object>, "group"));
                if (id == zoneId || group == zoneId || id.StartsWith(zoneId + ".", StringComparison.Ordinal)) parts.Add(part);
            }
            return parts;
        }
        private static bool EquivalentParts(List<Dictionary<string, object>> expected, List<Dictionary<string, object>> actual, out string reason)
        {
            reason = null;
            if (expected.Count != actual.Count) { reason = "изменилось число частей зоны"; return false; }
            var used = new HashSet<string>();
            foreach (var part in expected)
            {
                string id = S(Get(part, "id")); Dictionary<string, object> other = null;
                foreach (var candidate in actual) if (S(Get(candidate, "id")) == id) { if (other != null) { reason = "повторены части зоны"; return false; } other = candidate; }
                if (other == null || !used.Add(id)) { reason = "изменён состав частей зоны"; return false; }
                List<GeometryLoop> a, b;
                if (!PartsGeometry(new List<Dictionary<string, object>> { part }, out a, out reason) ||
                    !PartsGeometry(new List<Dictionary<string, object>> { other }, out b, out reason) ||
                    !GeometryFingerprint.Equivalent(a, b, out reason)) return false;
                // Opening identity and kind belong to the report, even if their polygons have equal area.
                if (PartAttributes(part) != PartAttributes(other)) { reason = "изменились атрибуты части или проёмов"; return false; }
                var ao = OpeningMap(part); var bo = OpeningMap(other);
                foreach (var entry in ao)
                {
                    if (!bo.ContainsKey(entry.Key)) { reason = "изменился состав проёмов"; return false; }
                    GeometryLoop al, bl;
                    if (!PartLoop(Get(entry.Value, "poly") as Dictionary<string, object>, "hole", UnitScale(part), out al, out reason) ||
                        !PartLoop(Get(bo[entry.Key], "poly") as Dictionary<string, object>, "hole", UnitScale(other), out bl, out reason) ||
                        !GeometryFingerprint.Equivalent(new[] { al }, new[] { bl }, out reason)) return false;
                }
                // Consumers also use bbox. Refuse a changed cache even when contour arrays were not edited.
                var ab = Items(Get(Get(part, "meta") as Dictionary<string, object>, "bbox"));
                var bb = Items(Get(Get(other, "meta") as Dictionary<string, object>, "bbox"));
                if (ab.Count != bb.Count) { reason = "изменились габариты части"; return false; }
                for (int i = 0; i < ab.Count; i++)
                    if (!Finite(D(bb[i])) || Math.Abs(D(ab[i]) * UnitScale(part) - D(bb[i]) * UnitScale(other)) > GeometryFingerprint.CoordinateTolerance)
                    { reason = "изменились габариты части"; return false; }
            }
            return true;
        }
        private static double UnitScale(Dictionary<string, object> part) { return S(Get(part, "units")) == "m" ? 1000 : 1; }
        private static Dictionary<string, Dictionary<string, object>> OpeningMap(Dictionary<string, object> part)
        {
            var result = new Dictionary<string, Dictionary<string, object>>();
            foreach (var value in Items(Get(part, "openings")))
            { var opening = value as Dictionary<string, object>; result.Add(S(Get(opening, "id")), opening); }
            return result;
        }
        private static string PartAttributes(Dictionary<string, object> part)
        {
            var openings = new List<string>();
            foreach (var entry in OpeningMap(part)) openings.Add(entry.Key + ":" + S(Get(entry.Value, "kind")));
            openings.Sort(StringComparer.Ordinal);
            var meta = Get(part, "meta") as Dictionary<string, object>;
            return Canonical(new object[] { Get(part, "id"), Get(part, "schema"), Get(part, "cladding"),
                Get(meta, "group"), Get(meta, "outer_contour_id"), openings });
        }

        private static object PackLoops(IList<GeometryLoop> loops)
        {
            var result = new List<object>();
            foreach (var loop in loops) result.Add(new Dictionary<string, object> {
                { "role", loop.Role }, { "points", loop.Points }, { "bulges", loop.Bulges }, { "closed", loop.Closed } });
            return result;
        }
        private static List<GeometryLoop> UnpackLoops(object values)
        {
            var result = new List<GeometryLoop>();
            foreach (var item in Items(values))
            {
                var d = item as Dictionary<string, object>;
                var points = new List<double[]>();
                foreach (var p in Items(Get(d, "points"))) { var xy = Items(p); if (xy.Count != 2) throw new FormatException("неверный снимок координат"); points.Add(new[] { SnapshotNumber(xy[0]), SnapshotNumber(xy[1]) }); }
                var bulges = new List<double>(); foreach (var b in Items(Get(d, "bulges"))) bulges.Add(SnapshotNumber(b));
                result.Add(new GeometryLoop { Role = S(Get(d, "role")), Points = points.ToArray(), Bulges = bulges.ToArray(), Closed = Convert.ToBoolean(Get(d, "closed"), CultureInfo.InvariantCulture) });
            }
            return result;
        }

        // JavaScriptSerializer writes CAD doubles in round-trip format but reads
        // their JSON literals as Decimal. Decimal -> Double can round via an
        // intermediate value and change the last bit (e.g. -98765.432109876536).
        // Read the stored literal directly as Double, as required by that format.
        // Keep Canonical/part/report conversion unchanged for existing schema 1 hashes.
        private static double SnapshotNumber(object value)
        {
            if (value is decimal)
                return double.Parse(((decimal)value).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            return D(value);
        }

        private static void Store(Transaction tr, Entity carrier, Dictionary<string, object> state, IList<ObjectId> refs)
        {
            var own = new Dictionary<string, object>(state); own["carrier_handle"] = carrier.Handle.ToString();
            string json = Serializer.Serialize(own);
            if (!carrier.IsWriteEnabled) carrier.UpgradeOpen();
            if (carrier.ExtensionDictionary.IsNull) carrier.CreateExtensionDictionary();
            var ext = (DBDictionary)tr.GetObject(carrier.ExtensionDictionary, OpenMode.ForWrite);
            using (var buffer = new ResultBuffer())
            {
                for (int i = 0; i < json.Length; i += 250) buffer.Add(new TypedValue((int)DxfCode.Text, json.Substring(i, Math.Min(250, json.Length - i))));
                foreach (var id in refs) buffer.Add(new TypedValue((int)DxfCode.SoftPointerId, id));
                Xrecord record;
                if (ext.Contains(Key)) record = (Xrecord)tr.GetObject(ext.GetAt(Key), OpenMode.ForWrite);
                else { record = new Xrecord(); ext.SetAt(Key, record); tr.AddNewlyCreatedDBObject(record, true); }
                record.XlateReferences = true; record.Data = buffer;
            }
        }
        private static Dictionary<string, object> Read(Transaction tr, Entity entity, string key, out List<ObjectId> refs)
        {
            refs = new List<ObjectId>();
            if (entity == null || entity.ExtensionDictionary.IsNull) return null;
            var ext = tr.GetObject(entity.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (ext == null || !ext.Contains(key)) return null;
            var record = tr.GetObject(ext.GetAt(key), OpenMode.ForRead) as Xrecord;
            if (record == null || record.Data == null) return null;
            var text = new StringBuilder();
            using (var data = record.Data)
                foreach (TypedValue value in data)
                {
                    if (value.TypeCode == (int)DxfCode.Text) text.Append(S(value.Value));
                    else if (value.TypeCode == (int)DxfCode.SoftPointerId && value.Value is ObjectId) refs.Add((ObjectId)value.Value);
                }
            return text.Length == 0 ? null : Serializer.DeserializeObject(text.ToString()) as Dictionary<string, object>;
        }
        private static string StateDigest(Dictionary<string, object> state)
        {
            var copy = new Dictionary<string, object>(state); copy.Remove("carrier_handle");
            return GeometryFingerprint.Hash(Canonical(copy));
        }
        private static bool SameRefs(IList<ObjectId> a, IList<ObjectId> b)
        { if (a.Count != b.Count) return false; for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false; return true; }
        private static ZoneGeometryResult Fail(string id, string reason)
        {
            return new ZoneGeometryResult { ZoneId = id, Reason = "Зона «" + (string.IsNullOrEmpty(id) ? "без марки" : id) + "»: " + reason +
                ". Повторите ATFZONE по исходным контурам зоны и всем её проёмам; прежние штриховку и марку удалите вручную, сохранив исходные контуры." };
        }
        private static object Get(Dictionary<string, object> d, string key) { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
        private static string S(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
        private static double D(object value) { if (value == null) throw new FormatException("отсутствует числовое значение"); return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
        private static List<object> Items(object value)
        {
            var result = new List<object>(); var enumerable = value as IEnumerable;
            if (enumerable != null && !(value is string) && !(value is IDictionary)) foreach (var item in enumerable) result.Add(item);
            return result;
        }
        private static List<Dictionary<string, object>> Dictionaries(object value)
        {
            var result = new List<Dictionary<string, object>>();
            foreach (var item in Items(value)) { var d = item as Dictionary<string, object>; if (d == null) throw new FormatException("неверный снимок части"); result.Add(d); }
            return result;
        }
        // Stable across JS serializer integer/decimal/double representations and dictionary enumeration order.
        private static string Canonical(object value)
        {
            if (value == null) return "null";
            var dict = value as IDictionary;
            if (dict != null)
            {
                var keys = new List<string>(); foreach (var k in dict.Keys) keys.Add(S(k)); keys.Sort(StringComparer.Ordinal);
                var values = new List<string>(); foreach (var key in keys) values.Add(Serializer.Serialize(key) + ":" + Canonical(dict[key]));
                return "{" + string.Join(",", values.ToArray()) + "}";
            }
            if (value is string || value is bool) return Serializer.Serialize(value);
            var sequence = value as IEnumerable;
            if (sequence != null) { var values = new List<string>(); foreach (var item in sequence) values.Add(Canonical(item)); return "[" + string.Join(",", values.ToArray()) + "]"; }
            double number = D(value); if (!Finite(number)) throw new FormatException("неконечное число в снимке");
            return (number == 0 ? 0 : number).ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
