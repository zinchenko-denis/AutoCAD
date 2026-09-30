using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FacadeSafety
{
    // Public JSON contract. All geometry and dimensions are millimetres unless the field says otherwise.
    public sealed class QuantityReport
    {
        public string schema { get; set; } = "facade_quantities/1";
        public string report_id { get; set; }
        public string kind { get; set; } = "cladding";
        public string document_id { get; set; }
        public string scope { get; set; }
        public string run_id { get; set; }
        public string algorithm { get; set; }
        public string completeness { get; set; } = "partial";
        public string engineering_coverage { get; set; } = "geometry_only";
        public List<string> zone_ids { get; set; } = new List<string>();
        public Dictionary<string, string> source_revisions { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> parameters { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> engine_summary { get; set; } = new Dictionary<string, object>();
        public List<QuantityElement> elements { get; set; } = new List<QuantityElement>();
        public List<QuantityCuttingGroup> cutting { get; set; } = new List<QuantityCuttingGroup>();
        public List<QuantityEstimateGroup> estimates { get; set; } = new List<QuantityEstimateGroup>();
        public List<QuantityIssue> issues { get; set; } = new List<QuantityIssue>();
        // Null is the explicit legacy state. Connection inventories are stored once,
        // independent of quantity rows and never imply a verified static model.
        public List<QuantityConnectionPassport> connection_passports { get; set; }
    }

    public sealed class QuantityConnectionPassport
    {
        public string schema { get; set; } = "aframe_connection_passport/1";
        public string passport_id { get; set; }
        public string source_run_id { get; set; }
        public List<string> zone_ids { get; set; } = new List<string>();
        public string status { get; set; }
        public string reason { get; set; }
        public string scheme { get; set; }
        public string catalog_revision { get; set; }
        public string catalog_scope { get; set; }
        public List<QuantityConnectionSource> sources { get; set; } = new List<QuantityConnectionSource>();
        public List<QuantityConnectionReference> references { get; set; } = new List<QuantityConnectionReference>();
        public List<QuantityConnectionMember> members { get; set; } = new List<QuantityConnectionMember>();
        public List<QuantityConnectionJoint> joints { get; set; } = new List<QuantityConnectionJoint>();
        public QuantityConnectionCoverage coverage { get; set; } = new QuantityConnectionCoverage();
        public QuantityConnectionSummary summary { get; set; } = new QuantityConnectionSummary();
        public List<QuantityConnectionIssue> issues { get; set; } = new List<QuantityConnectionIssue>();
    }
    public sealed class QuantityConnectionSource
    {
        public string source_id { get; set; }
        public string sha256 { get; set; }
        public string kind { get; set; }
        public string edition { get; set; }
        public string locator { get; set; }
    }
    public sealed class QuantityConnectionReference
    {
        public string reference_id { get; set; }
        public string kind { get; set; }
        public string designation { get; set; }
        public string role { get; set; }
        public string source_id { get; set; }
        public List<int> pdf_pages { get; set; } = new List<int>();
        public string properties_status { get; set; }
    }
    public sealed class QuantityConnectionMember
    {
        public string rail_element_id { get; set; }
        public string zone_id { get; set; }
        public int support_count { get; set; }
        public int span_count { get; set; }
        public List<double> intervals_mm { get; set; } = new List<double>();
        public double bottom_free_mm { get; set; }
        public double top_free_mm { get; set; }
        public List<QuantityConnectionSupport> supports { get; set; } = new List<QuantityConnectionSupport>();
        public QuantityConnectionProfileMatch profile_match { get; set; } = new QuantityConnectionProfileMatch();
    }
    public sealed class QuantityConnectionSupport
    {
        public double offset_mm { get; set; }
        public List<string> bracket_element_ids { get; set; } = new List<string>();
    }
    public sealed class QuantityConnectionProfileMatch
    {
        public string status { get; set; }
        public List<string> reference_ids { get; set; } = new List<string>();
    }
    public sealed class QuantityConnectionJoint
    {
        public string first_rail_element_id { get; set; }
        public string second_rail_element_id { get; set; }
        public double gap_mm { get; set; }
        public string status { get; set; }
    }
    public sealed class QuantityConnectionCoverage
    {
        public string fixed_sliding { get; set; } = "not_modeled";
        public string splice_continuity { get; set; } = "not_modeled";
        public string gravity_load_distribution { get; set; } = "not_verified";
        public string strength { get; set; } = "not_verified";
    }
    public sealed class QuantityConnectionSummary
    {
        public int members { get; set; }
        public int support_positions { get; set; }
        public int support_links { get; set; }
        public int geometric_joints { get; set; }
        public int matched_profile_references { get; set; }
        public int unresolved_profile_references { get; set; }
    }
    public sealed class QuantityConnectionIssue
    {
        public string code { get; set; }
        public string message { get; set; }
        public int count { get; set; }
    }

    public sealed class QuantityElement
    {
        public string element_id { get; set; }
        public string zone_id { get; set; }
        public List<string> zone_ids { get; set; } = new List<string>();
        public string role { get; set; } = "cladding";
        public string product_id { get; set; }
        public string identity_group { get; set; }
        public string mark { get; set; }
        public string type { get; set; }
        public string material { get; set; }
        public string coating { get; set; }
        public string system { get; set; }
        public string color { get; set; }
        public string orientation { get; set; }
        public string piece_kind { get; set; }
        public double? width_mm { get; set; }
        public double? height_mm { get; set; }
        public double? area_mm2 { get; set; }
        public double? length_mm { get; set; }
        public string shape_id { get; set; }
        public string origin { get; set; }
        public List<QuantityRing> rings { get; set; } = new List<QuantityRing>();
        public List<QuantityCadEntity> cad_entities { get; set; } = new List<QuantityCadEntity>();
    }

    public sealed class QuantityCadEntity
    {
        public string handle { get; set; }
        public string role { get; set; }
        public string fingerprint { get; set; }
    }

    public sealed class QuantityRing
    {
        public string role { get; set; } = "outer";
        public double[][] points { get; set; }
    }

    public sealed class QuantityShape
    {
        public string shape_id { get; set; }
        public double area_mm2 { get; set; }
        public double width_mm { get; set; }
        public double height_mm { get; set; }
    }

    public sealed class QuantityCuttingGroup
    {
        public string group_id { get; set; }
        public List<string> scope_zone_ids { get; set; } = new List<string>();
        public Dictionary<string, object> parameters { get; set; } = new Dictionary<string, object>();
        public List<QuantityRow> rows { get; set; } = new List<QuantityRow>();
    }

    public sealed class QuantityRow
    {
        public string basis { get; set; } = "installed";
        public string zone_id { get; set; }
        public List<string> zone_ids { get; set; } = new List<string>();
        public string role { get; set; }
        public string product_id { get; set; }
        public string identity_group { get; set; }
        public string mark { get; set; }
        public string type { get; set; }
        public string material { get; set; }
        public string coating { get; set; }
        public string system { get; set; }
        public string color { get; set; }
        public string orientation { get; set; }
        public string piece_kind { get; set; }
        public double? width_mm { get; set; }
        public double? height_mm { get; set; }
        public string shape_id { get; set; }
        public string unit { get; set; } = "шт.";
        public double quantity { get; set; }
        public double? area_m2 { get; set; }
        public double? length_mm { get; set; }
        public double? total_length_m { get; set; }
        public List<string> element_ids { get; set; } = new List<string>();
        public string note { get; set; }
    }

    public sealed class QuantityEstimateGroup
    {
        public string group_id { get; set; }
        public List<string> scope_zone_ids { get; set; } = new List<string>();
        public Dictionary<string, object> parameters { get; set; } = new Dictionary<string, object>();
        public List<QuantityRow> rows { get; set; } = new List<QuantityRow>();
    }

    public sealed class QuantityIssue
    {
        public string code { get; set; }
        public string message { get; set; }
        public string report_id { get; set; }
        public string element_id { get; set; }
        public List<string> element_ids { get; set; } = new List<string>();
        public int element_count { get; set; }
        public string severity { get; set; } = "warning";
    }

    public sealed class QuantityResult
    {
        public bool ok { get; set; }
        public string completeness { get; set; }
        public string engineering_coverage { get; set; } = "geometry_only";
        public List<string> source_engineering_coverage { get; set; } = new List<string>();
        public List<QuantityRow> rows { get; set; } = new List<QuantityRow>();
        public List<QuantityIssue> issues { get; set; } = new List<QuantityIssue>();
    }

    /// <summary>Deterministic operation counters for diagnostics; no wall-clock correctness threshold.</summary>
    public sealed class QuantityDiagnostics
    {
        public long reports_seen { get; set; }
        public long reports_compared { get; set; }
        public long elements_seen { get; set; }
        public long elements_validated { get; set; }
        public long elements_compared { get; set; }
        public long cad_links_checked { get; set; }
        public long shapes_validated { get; set; }
        public long canonical_report_keys { get; set; }
        public long canonical_element_keys { get; set; }
        public long aggregate_keys_built { get; set; }
        public long connection_elements_indexed { get; set; }
        public long connection_members_validated { get; set; }
        public long connection_supports_validated { get; set; }
        public long connection_links_validated { get; set; }
    }

    public static class FacadeQuantitiesCore
    {
        public const string Schema = "facade_quantities/1";
        public static QuantityResult BuildRows(IEnumerable<QuantityReport> reports,
            IEnumerable<string> selectedZoneIds, bool byZone, bool includeCutting)
        { return BuildRows(reports, selectedZoneIds, byZone, includeCutting, null); }

        public static QuantityResult BuildRows(IEnumerable<QuantityReport> reports,
            IEnumerable<string> selectedZoneIds, bool byZone, bool includeCutting, QuantityDiagnostics diagnostics)
        {
            diagnostics = diagnostics ?? new QuantityDiagnostics();
            Reset(diagnostics);
            var result = new QuantityResult { completeness = "partial" };
            var uniqueReports = new List<QuantityReport>();
            var reportsById = new Dictionary<string, QuantityReport>(StringComparer.Ordinal);
            var connectionsById = new Dictionary<string, QuantityConnectionPassport>(StringComparer.Ordinal);
            var allZones = new HashSet<string>(StringComparer.Ordinal);
            var elements = new Dictionary<string, QuantityElement>(StringComparer.Ordinal);
            var handles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var unknownIssues = new Dictionary<string, QuantityIssue>(StringComparer.Ordinal);
            var coverage = new HashSet<string>(StringComparer.Ordinal);
            string document = null, kind = null;
            bool sourcesComplete = true;
            if (reports != null)
                foreach (var report in reports)
                {
                    diagnostics.reports_seen++;
                    if (report == null || report.schema != Schema || (report.kind != "cladding" && report.kind != "frame") ||
                        Empty(report.report_id) || Empty(report.run_id) || Empty(report.document_id))
                    { Error(result, "Q_REPORT_INVALID", "Паспорт элементов отсутствует, повреждён или имеет неподдержанную версию.", report); continue; }
                    if (document == null) document = report.document_id;
                    if (document != report.document_id)
                        Error(result, "Q_DOCUMENT_MISMATCH", "Ведомость поддерживает только один исходный чертёж.", report);
                    if (kind == null) kind = report.kind;
                    if (kind != report.kind)
                        Error(result, "Q_KIND_MISMATCH", "Облицовка и подсистема выводятся отдельными ведомостями.", report);
                    var scope = Set(report.zone_ids);
                    if (scope.Count == 0)
                        Error(result, "Q_SCOPE_EMPTY", "Не указана полная область исходной раскладки.", report);
                    foreach (string z in scope) allZones.Add(z);
                    QuantityReport priorReport;
                    if (reportsById.TryGetValue(report.report_id, out priorReport))
                    {
                        diagnostics.reports_compared++;
                        // A normal read provides one unique run. Canonicalizing its whole
                        // payload would duplicate all per-element work and allocate a large string.
                        // Deep identity comparison is needed only when a report ID repeats.
                        if (!ReferenceEquals(priorReport, report) && ReportKey(priorReport, diagnostics) != ReportKey(report, diagnostics))
                            Error(result, "Q_REPORT_CONFLICT", "Копии паспорта одного результата имеют разный состав.", report);
                        continue;
                    }
                    reportsById.Add(report.report_id, report);
                    uniqueReports.Add(report);
                    if (!Empty(report.engineering_coverage)) coverage.Add(report.engineering_coverage);
                    if (report.completeness != "complete") sourcesComplete = false;
                    if (Empty(report.algorithm)) result.issues.Add(new QuantityIssue { code = "Q_ALGORITHM_UNKNOWN", report_id = report.report_id,
                        message = "В паспорте не указана версия алгоритма раскладки." });
                    if (report.issues != null)
                        foreach (var issue in report.issues) if (issue != null) result.issues.Add(issue);
                    if (report.elements == null)
                    { Error(result, "Q_ELEMENTS_MISSING", "В паспорте отсутствует состав физических элементов.", report); continue; }
                    foreach (var element in report.elements)
                    {
                        diagnostics.elements_seen++;
                        if (element == null || Empty(element.element_id))
                        { Error(result, "Q_ELEMENT_INVALID", "У физической детали отсутствует идентификатор.", report); continue; }
                        var zones = ElementZones(element);
                        if (zones.Count == 0 || !zones.IsSubsetOf(scope))
                            Error(result, "Q_ELEMENT_SCOPE", "Область детали отсутствует или выходит за область раскладки.", report, element);
                        QuantityElement priorElement;
                        if (elements.TryGetValue(element.element_id, out priorElement))
                        {
                            diagnostics.elements_compared++;
                            if (!ReferenceEquals(priorElement, element) && ElementKey(priorElement, diagnostics) != ElementKey(element, diagnostics))
                                Error(result, "Q_ELEMENT_CONFLICT", "Один идентификатор физической детали имеет разные данные.", report, element);
                            continue;
                        }
                        elements.Add(element.element_id, element);
                        diagnostics.elements_validated++;
                        ValidateElement(result, report, element, unknownIssues, diagnostics);
                        if (element.cad_entities != null)
                            foreach (var cad in element.cad_entities)
                            {
                                diagnostics.cad_links_checked++;
                                if (cad == null || Empty(cad.handle) || Empty(cad.role) || Empty(cad.fingerprint))
                                { Error(result, "Q_CAD_LINK_INVALID", "Неполная ссылка на объект чертежа.", report, element); continue; }
                                string owner;
                                if (handles.TryGetValue(cad.handle, out owner))
                                    Error(result, "Q_CAD_LINK_DUPLICATE", "Один объект чертежа включён в состав несколько раз.", report, element);
                                else handles.Add(cad.handle, element.element_id);
                            }
                    }
                    ValidateConnections(result, report, scope, diagnostics);
                    if (report.connection_passports != null)
                        foreach (var passport in report.connection_passports)
                        {
                            if (passport == null || Empty(passport.passport_id)) continue;
                            QuantityConnectionPassport oldPassport;
                            if (connectionsById.TryGetValue(passport.passport_id, out oldPassport))
                            {
                                if (!ReferenceEquals(passport, oldPassport) &&
                                    ConnectionKey(new List<QuantityConnectionPassport> { passport }) !=
                                    ConnectionKey(new List<QuantityConnectionPassport> { oldPassport }))
                                    Error(result, "Q_CONNECTION_CONFLICT", "Копии одного паспорта соединений имеют разные данные.", report);
                            }
                            else connectionsById.Add(passport.passport_id, passport);
                        }
                    ValidateCutting(result, report, scope);
                    ValidateEstimates(result, report, scope);
                }
            result.source_engineering_coverage = Sorted(coverage);
            foreach (var issue in unknownIssues.Values)
            {
                issue.element_ids.Sort(StringComparer.Ordinal);
                issue.message += " Деталей: " + issue.element_count.ToString(CultureInfo.InvariantCulture) + ".";
            }
            if (uniqueReports.Count == 0)
                Error(result, "Q_REPORTS_EMPTY", "Нет паспортов для формирования ведомости.", null);
            var selected = selectedZoneIds == null ? new HashSet<string>(allZones, StringComparer.Ordinal) : Set(selectedZoneIds);
            if (selected.Count == 0)
                Error(result, "Q_SELECTION_EMPTY", "Не выбраны зоны ведомости.", null);
            if (!selected.IsSubsetOf(allZones))
                Error(result, "Q_ZONE_NOT_FOUND", "У выбранной зоны нет паспорта в проверенной области.", null);

            // Every complete report is validated first, including its unselected source zones.
            // Selection cannot hide a corrupted member of a shared result.
            var rows = new Dictionary<string, QuantityRow>(StringComparer.Ordinal);
            foreach (var element in elements.Values)
            {
                var zones = ElementZones(element);
                if (!zones.Overlaps(selected)) continue;
                if (!zones.IsSubsetOf(selected))
                { Error(result, "Q_SHARED_PIECE_PARTIAL", "Физическая деталь относится к нескольким зонам. Выберите всю её область.", null, element); continue; }
                var row = InstalledRow(element, zones, byZone);
                diagnostics.aggregate_keys_built++;
                string key = RowKey(row, byZone);
                QuantityRow aggregate;
                if (!rows.TryGetValue(key, out aggregate)) { aggregate = row; rows.Add(key, row); }
                else
                {
                    aggregate.quantity += 1;
                    aggregate.area_m2 = aggregate.area_m2.HasValue && row.area_m2.HasValue
                        ? aggregate.area_m2.Value + row.area_m2.Value : (double?)null;
                    aggregate.total_length_m = aggregate.total_length_m.HasValue && row.total_length_m.HasValue
                        ? aggregate.total_length_m.Value + row.total_length_m.Value : (double?)null;
                    aggregate.element_ids.Add(element.element_id);
                    aggregate.zone_ids = SortedUnion(aggregate.zone_ids, row.zone_ids);
                }
            }
            var installedKeys = new List<string>(rows.Keys); installedKeys.Sort(StringComparer.Ordinal);
            foreach (string key in installedKeys)
            {
                rows[key].element_ids.Sort(StringComparer.Ordinal);
                result.rows.Add(rows[key]);
            }
            if (includeCutting)
                foreach (var report in uniqueReports)
                    if (report.cutting != null)
                        foreach (var group in report.cutting)
                        {
                            if (group == null) continue;
                            var scope = Set(group.scope_zone_ids);
                            if (!scope.Overlaps(selected)) continue;
                            if (!scope.IsSubsetOf(selected))
                            { Error(result, "Q_CUTTING_SCOPE_PARTIAL", "Раскрой относится к общей группе зон. Выберите всю группу или только установленные детали.", report); continue; }
                            if (group.rows == null) continue;
                            foreach (var row in group.rows)
                            {
                                if (row == null) continue;
                                var copy = CopyRow(row);
                                copy.basis = "cutting";
                                copy.zone_ids = Sorted(scope);
                                copy.zone_id = string.Join(" + ", copy.zone_ids.ToArray());
                                copy.note = JoinNote(copy.note, "Область раскроя: " + copy.zone_id);
                                result.rows.Add(copy);
                            }
                        }
            foreach (var report in uniqueReports)
                if (report.estimates != null)
                    foreach (var group in report.estimates)
                    {
                        if (group == null) continue;
                        var scope = Set(group.scope_zone_ids);
                        if (!scope.Overlaps(selected)) continue;
                        if (!scope.IsSubsetOf(selected))
                        {
                            result.issues.Add(new QuantityIssue { code = "Q_ESTIMATE_SCOPE_PARTIAL", severity = "info",
                                report_id = report.report_id,
                                message = "Оценка хлыстов не включена: она относится ко всей группе зон " + string.Join(" + ", Sorted(scope).ToArray()) + ". Установленные элементы выбранных зон учтены отдельно." });
                            continue;
                        }
                        if (group.rows == null) continue;
                        foreach (var row in group.rows)
                        {
                            if (row == null) continue;
                            var copy = CopyRow(row);
                            copy.basis = "estimate";
                            copy.zone_ids = Sorted(scope);
                            copy.zone_id = string.Join(" + ", copy.zone_ids.ToArray());
                            copy.note = JoinNote(copy.note, "Оценка хлыстов по сумме длин; не раскрой и не закупка. Область: " + copy.zone_id);
                            result.rows.Add(copy);
                        }
                    }
            if (result.rows.Count == 0 && !HasErrors(result))
                result.issues.Add(new QuantityIssue { code = "Q_ZERO_INSTALLED_ELEMENTS", severity = "info",
                    message = "В выбранной проверенной области нет установленных элементов" + (kind == "frame" ? " подсистемы" : " облицовки") + ". Количество: 0 шт." });
            result.rows.Sort(delegate(QuantityRow a, QuantityRow b)
            {
                int basisOrder = (a.basis == "installed" ? 0 : 1).CompareTo(b.basis == "installed" ? 0 : 1);
                if (basisOrder != 0) return basisOrder;
                return string.CompareOrdinal(Tokens(RowKey(a, true), a.note, F(a.quantity), N(a.area_m2), N(a.total_length_m)),
                    Tokens(RowKey(b, true), b.note, F(b.quantity), N(b.area_m2), N(b.total_length_m)));
            });
            result.ok = !HasErrors(result);
            if (!result.ok) result.rows.Clear();
            bool incomplete = false;
            foreach (var issue in result.issues) if (issue.severity != "info") { incomplete = true; break; }
            result.completeness = result.ok && sourcesComplete && !incomplete ? "complete" : "partial";
            return result;
        }

        public static bool TryShape(IEnumerable<QuantityRing> rings, out QuantityShape shape, out string reason)
        {
            shape = null; reason = null;
            var normalized = new List<QuantityRing>();
            QuantityRing outer = null;
            if (rings != null)
                foreach (var ring in rings)
                {
                    if (ring == null || (ring.role != "outer" && ring.role != "hole") || ring.points == null)
                    { reason = "контур не содержит корректную роль и вершины"; return false; }
                    var points = new List<double[]>();
                    foreach (var p in ring.points)
                    {
                        if (p == null || p.Length != 2 || !Finite(p[0]) || !Finite(p[1]) || Math.Abs(p[0]) > 1e12 || Math.Abs(p[1]) > 1e12)
                        { reason = "некорректные координаты контура"; return false; }
                        if (points.Count == 0 || !Same(points[points.Count - 1], p)) points.Add(new[] { p[0], p[1] });
                    }
                    if (points.Count > 1 && Same(points[0], points[points.Count - 1])) points.RemoveAt(points.Count - 1);
                    RemoveCollinear(points);
                    if (points.Count < 3 || Math.Abs(SignedArea(points)) <= 1e-6 || SelfIntersects(points))
                    { reason = "вырожденный или самопересекающийся контур"; return false; }
                    var clean = new QuantityRing { role = ring.role, points = points.ToArray() };
                    if (ring.role == "outer")
                    {
                        if (outer != null) { reason = "одна физическая деталь должна иметь один внешний контур"; return false; }
                        outer = clean;
                    }
                    normalized.Add(clean);
                }
            if (outer == null) { reason = "нет внешнего контура физической детали"; return false; }
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
            foreach (var p in outer.points)
            { minX = Math.Min(minX, p[0]); minY = Math.Min(minY, p[1]); maxX = Math.Max(maxX, p[0]); maxY = Math.Max(maxY, p[1]); }
            double area = Math.Abs(SignedArea(outer.points));
            for (int i = 0; i < normalized.Count; i++)
            {
                var hole = normalized[i]; if (hole.role != "hole") continue;
                if (!Inside(hole.points[0], outer.points) || RingsIntersect(hole.points, outer.points))
                { reason = "отверстие выходит за внешний контур или касается его"; return false; }
                for (int j = 0; j < i; j++)
                {
                    var other = normalized[j]; if (other.role != "hole") continue;
                    if (RingsIntersect(hole.points, other.points) || Inside(hole.points[0], other.points) || Inside(other.points[0], hole.points))
                    { reason = "отверстия пересекаются или вложены друг в друга"; return false; }
                }
                area -= Math.Abs(SignedArea(hole.points));
            }
            if (!Finite(area) || area <= 0) { reason = "неположительная площадь физической детали"; return false; }
            var parts = new List<string>();
            foreach (var ring in normalized) parts.Add(CanonicalRing(ring, minX, minY));
            parts.Sort(StringComparer.Ordinal);
            shape = new QuantityShape { shape_id = "shape1:" + Hash(string.Join("|", parts.ToArray())),
                area_mm2 = area, width_mm = maxX - minX, height_mm = maxY - minY };
            return true;
        }

        private static void ValidateElement(QuantityResult result, QuantityReport report, QuantityElement e,
            Dictionary<string, QuantityIssue> unknownIssues, QuantityDiagnostics diagnostics)
        {
            if (e.cad_entities == null || e.cad_entities.Count == 0)
                Error(result, "Q_CAD_LINK_MISSING", "У физической детали нет проверяемых объектов чертежа.", report, e);
            if (!ValidOptional(e.width_mm) || !ValidOptional(e.height_mm) || !ValidOptional(e.area_mm2) || !ValidOptional(e.length_mm))
                Error(result, "Q_MEASURE_INVALID", "Размер, длина или площадь элемента неположительны либо не являются конечным числом.", report, e);
            QuantityShape shape; string reason;
            bool frame = report.kind == "frame";
            if (frame)
            {
                if (!IsFrameRole(e.role))
                    Error(result, "Q_FRAME_ROLE_INVALID", "Не распознана категория физического элемента подсистемы.", report, e);
                if (e.area_mm2.HasValue || !Empty(e.shape_id) || (e.rings != null && e.rings.Count > 0))
                    Error(result, "Q_FRAME_GEOMETRY_INVALID", "Элемент подсистемы не должен содержать условную площадь или контуры плиты облицовки.", report, e);
            }
            else if (e.rings == null || e.rings.Count == 0)
            {
                // A compact passport may omit rings only after the DWG adapter has captured
                // its geometry and retained the shape identity. The adapter still verifies every CAD link.
                if (Empty(e.shape_id)) Error(result, "Q_GEOMETRY_MISSING", "У детали отсутствует идентификатор проверенной формы.", report, e);
            }
            else
            {
                diagnostics.shapes_validated++;
                if (!TryShape(e.rings, out shape, out reason))
                    Error(result, "Q_GEOMETRY_INVALID", "Невозможно проверить форму детали: " + reason + ".", report, e);
                else
                {
                    if (e.shape_id != shape.shape_id)
                        Error(result, "Q_SHAPE_MISMATCH", "Идентификатор формы не соответствует сохранённым контурам детали.", report, e);
                    if ((e.area_mm2.HasValue && !Close(e.area_mm2.Value, shape.area_mm2, 0.01)) ||
                        (e.width_mm.HasValue && !Close(e.width_mm.Value, shape.width_mm, 0.001)) ||
                        (e.height_mm.HasValue && !Close(e.height_mm.Value, shape.height_mm, 0.001)))
                        Error(result, "Q_MEASURE_MISMATCH", "Размер или площадь не соответствует сохранённым контурам детали.", report, e);
                }
            }
            var missing = new List<string>();
            if (Empty(e.product_id)) missing.Add("каталожное изделие");
            if (Empty(e.mark)) missing.Add("марка");
            if (Empty(e.material)) missing.Add("материал");
            if (Empty(e.type)) missing.Add("тип");
            if (frame)
            {
                if (Empty(e.coating)) missing.Add("покрытие");
                if (Empty(e.system)) missing.Add("система");
                if (IsLinearRole(e.role) && !e.length_mm.HasValue) missing.Add("длина");
            }
            else
            {
                if (Empty(e.color)) missing.Add("цвет");
                if (Empty(e.orientation)) missing.Add("ориентация значимой поверхности");
                if (Empty(e.piece_kind)) missing.Add("класс детали");
                if (!e.width_mm.HasValue || !e.height_mm.HasValue) missing.Add("размеры");
                if (!e.area_mm2.HasValue) missing.Add("площадь");
            }
            if (missing.Count > 0)
            {
                string missingText = string.Join(", ", missing.ToArray());
                string key = Tokens(report.report_id, missingText);
                QuantityIssue issue;
                if (!unknownIssues.TryGetValue(key, out issue))
                {
                    issue = new QuantityIssue { code = "Q_VALUE_UNKNOWN", report_id = report.report_id,
                        message = "Не указано: " + missingText + "." };
                    unknownIssues.Add(key, issue); result.issues.Add(issue);
                }
                issue.element_ids.Add(e.element_id); issue.element_count++;
                issue.element_id = issue.element_count == 1 ? e.element_id : null;
            }
        }

        private static void ValidateConnections(QuantityResult result, QuantityReport report, HashSet<string> scope, QuantityDiagnostics diagnostics)
        {
            if (report.connection_passports == null) return; // published v1/v2 quantity passports
            if (report.kind != "frame")
            { Error(result, "Q_CONNECTION_INVALID", "Паспорт соединений относится только к подсистеме.", report); return; }
            var parts = new Dictionary<string, QuantityElement>(StringComparer.Ordinal);
            var railsByRun = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var e in report.elements)
                if (e != null && !Empty(e.element_id) && !parts.ContainsKey(e.element_id))
                {
                    diagnostics.connection_elements_indexed++;
                    parts.Add(e.element_id, e);
                    int split = e.role == "rail" ? e.element_id.IndexOf(":rails:", StringComparison.Ordinal) : -1;
                    if (split <= 0) continue;
                    string run = e.element_id.Substring(0, split);
                    HashSet<string> railIds;
                    if (!railsByRun.TryGetValue(run, out railIds))
                    { railIds = new HashSet<string>(StringComparer.Ordinal); railsByRun.Add(run, railIds); }
                    railIds.Add(e.element_id);
                }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var sourceRuns = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in report.connection_passports)
            {
                string reason = ConnectionProblem(p, parts, railsByRun, scope, diagnostics);
                if (reason == null && (!ids.Add(p.passport_id) || !sourceRuns.Add(p.source_run_id))) reason = "повторяется паспорт исходного прогона";
                if (reason != null) Error(result, "Q_CONNECTION_INVALID", "Повреждён паспорт соединений: " + reason + ".", report);
            }
        }

        // Pure validation uses one element index for a run. It never reopens CAD
        // entities and cannot turn geometric candidates into designed supports.
        private static string ConnectionProblem(QuantityConnectionPassport p,
            Dictionary<string, QuantityElement> parts, Dictionary<string, HashSet<string>> railsByRun, HashSet<string> reportScope, QuantityDiagnostics diagnostics)
        {
            if (p == null || p.schema != "aframe_connection_passport/1" || Empty(p.passport_id) || Empty(p.source_run_id) || p.source_run_id.IndexOf(':') >= 0 ||
                (p.status != "inventory_only" && p.status != "unavailable")) return "неподдержанная версия или статус";
            var scope = Set(p.zone_ids);
            if (scope.Count == 0 || !scope.IsSubsetOf(reportScope)) return "неверная область зон";
            if (p.coverage == null || p.coverage.fixed_sliding != "not_modeled" ||
                p.coverage.splice_continuity != "not_modeled" || p.coverage.gravity_load_distribution != "not_verified" ||
                p.coverage.strength != "not_verified") return "геометрическая опись выдана за инженерную проверку";
            if (p.sources == null || p.references == null || p.members == null || p.joints == null ||
                p.summary == null || p.issues == null) return "отсутствует обязательный массив или сводка";
            if (p.status == "unavailable")
            {
                if (Empty(p.reason) || p.members.Count != 0 || p.joints.Count != 0 ||
                    p.summary.members != 0 || p.summary.support_positions != 0 || p.summary.support_links != 0 ||
                    p.summary.geometric_joints != 0 || p.summary.matched_profile_references != 0 ||
                    p.summary.unresolved_profile_references != 0) return "недоступная опись содержит выдаваемые результаты";
            }
            else if (p.scheme != "vertical" || !HexHash(p.catalog_revision) || p.catalog_scope != "reference_project_only")
                return "неподдержанная область каталога";
            var sources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in p.sources)
                if (source == null || Empty(source.source_id) || !sources.Add(source.source_id) || !HexHash(source.sha256) ||
                    Empty(source.kind) || Empty(source.edition) || Empty(source.locator)) return "неполное происхождение источника";
            var references = new Dictionary<string, QuantityConnectionReference>(StringComparer.Ordinal);
            foreach (var reference in p.references)
            {
                if (reference == null || Empty(reference.reference_id) || references.ContainsKey(reference.reference_id) ||
                    !sources.Contains(reference.source_id ?? "") || Empty(reference.kind) || Empty(reference.designation) ||
                    Empty(reference.role) || reference.properties_status != "not_imported" || reference.pdf_pages == null ||
                    reference.pdf_pages.Count == 0) return "некорректная ссылка на обозначение в источнике";
                references.Add(reference.reference_id, reference);
                var pages = new HashSet<int>();
                foreach (int page in reference.pdf_pages) if (page <= 0 || !pages.Add(page)) return "неверные страницы источника";
            }
            if (p.status == "inventory_only" && p.members.Count == 0) return "геометрическая опись направляющих пуста";
            var memberIds = new HashSet<string>(StringComparer.Ordinal);
            var memberZones = new HashSet<string>(StringComparer.Ordinal);
            int positions = 0, links = 0, matched = 0;
            foreach (var member in p.members)
            {
                diagnostics.connection_members_validated++;
                QuantityElement rail;
                if (member == null || Empty(member.rail_element_id) || !member.rail_element_id.StartsWith(p.source_run_id + ":rails:", StringComparison.Ordinal) ||
                    !memberIds.Add(member.rail_element_id) ||
                    !parts.TryGetValue(member.rail_element_id, out rail) || rail.role != "rail" || !rail.length_mm.HasValue ||
                    !Finite(rail.length_mm.Value) || rail.length_mm.Value <= 0 || !scope.Contains(member.zone_id ?? "") ||
                    !ElementZones(rail).SetEquals(new[] { member.zone_id })) return "направляющая отсутствует или относится к другой зоне";
                memberZones.Add(member.zone_id);
                if (member.supports == null || member.intervals_mm == null || member.support_count != member.supports.Count ||
                    member.span_count != Math.Max(0, member.support_count - 1) || member.intervals_mm.Count != member.span_count ||
                    !Finite(member.bottom_free_mm) || !Finite(member.top_free_mm) || member.bottom_free_mm < 0 ||
                    member.top_free_mm < 0) return "несогласованное число позиций, интервалов или свободных концов";
                var bracketIds = new HashSet<string>(StringComparer.Ordinal);
                double prior = -1;
                for (int i = 0; i < member.supports.Count; i++)
                {
                    diagnostics.connection_supports_validated++;
                    var support = member.supports[i];
                    if (support == null || !Finite(support.offset_mm) || support.offset_mm < -0.5001 ||
                        support.offset_mm > rail.length_mm.Value + 0.5001 || (i > 0 && support.offset_mm <= prior) ||
                        support.bracket_element_ids == null || support.bracket_element_ids.Count == 0) return "неверная позиция кандидата опоры";
                    if (i > 0 && (!Finite(member.intervals_mm[i - 1]) || member.intervals_mm[i - 1] <= 0 ||
                        !Close(member.intervals_mm[i - 1], support.offset_mm - prior, 0.01))) return "интервалы не соответствуют позициям";
                    foreach (string id in support.bracket_element_ids)
                    {
                        diagnostics.connection_links_validated++;
                        QuantityElement bracket;
                        if (Empty(id) || !id.StartsWith(p.source_run_id + ":brackets:", StringComparison.Ordinal) ||
                            !bracketIds.Add(id) || !parts.TryGetValue(id, out bracket) || bracket.role != "bracket" ||
                            !ElementZones(bracket).SetEquals(new[] { member.zone_id })) return "кронштейн отсутствует, повторяется или относится к другой зоне";
                        links++;
                    }
                    prior = support.offset_mm;
                }
                if (member.support_count > 0 && (!Close(member.bottom_free_mm, Math.Max(0, member.supports[0].offset_mm), 0.0002) ||
                    !Close(member.top_free_mm, Math.Max(0, rail.length_mm.Value - prior), 0.0002))) return "свободные концы не соответствуют длине направляющей";
                if (member.support_count == 0 && (!Close(member.bottom_free_mm, rail.length_mm.Value, 0.0002) ||
                    !Close(member.top_free_mm, rail.length_mm.Value, 0.0002))) return "неподтверждённые свободные концы направляющей без опор";
                var profile = member.profile_match;
                if (profile == null || profile.reference_ids == null ||
                    (profile.status != "matched_source_identity" && profile.status != "unknown") ||
                    (profile.status == "unknown" && profile.reference_ids.Count != 0) ||
                    (profile.status == "matched_source_identity" && profile.reference_ids.Count == 0)) return "неверный статус обозначения профиля";
                var profileIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (string id in profile.reference_ids)
                    if (id == null || !profileIds.Add(id) || !references.ContainsKey(id) || references[id].role != "rail" ||
                        references[id].designation != rail.mark) return "обозначение не найдено в источниках или не соответствует марке направляющей";
                if (profile.status == "matched_source_identity") matched++;
                positions += member.support_count;
            }
            HashSet<string> expectedRails;
            if (p.status == "inventory_only" && (!railsByRun.TryGetValue(p.source_run_id, out expectedRails) ||
                !memberIds.SetEquals(expectedRails) || !memberZones.SetEquals(scope))) return "из описи исключена направляющая или зона исходного прогона";
            var pairs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var joint in p.joints)
            {
                QuantityElement first, second;
                if (joint == null || Empty(joint.first_rail_element_id) || Empty(joint.second_rail_element_id) ||
                    joint.first_rail_element_id == joint.second_rail_element_id || !memberIds.Contains(joint.first_rail_element_id) ||
                    !memberIds.Contains(joint.second_rail_element_id) || joint.status != "geometric_adjacency_only" ||
                    !Finite(joint.gap_mm)) return "неверная геометрическая смежность направляющих";
                first = parts[joint.first_rail_element_id]; second = parts[joint.second_rail_element_id];
                if (!ElementZones(first).SetEquals(ElementZones(second)) ||
                    !pairs.Add(Tokens(string.CompareOrdinal(joint.first_rail_element_id, joint.second_rail_element_id) < 0 ? joint.first_rail_element_id : joint.second_rail_element_id,
                        string.CompareOrdinal(joint.first_rail_element_id, joint.second_rail_element_id) < 0 ? joint.second_rail_element_id : joint.first_rail_element_id)))
                    return "повторная смежность или соединение разных зон";
            }
            if (p.summary.members != p.members.Count || p.summary.support_positions != positions || p.summary.support_links != links ||
                p.summary.geometric_joints != p.joints.Count || p.summary.matched_profile_references != matched ||
                p.summary.unresolved_profile_references != p.members.Count - matched) return "сводка расходится с физическими ссылками";
            foreach (var issue in p.issues)
                if (issue == null || Empty(issue.code) || Empty(issue.message) || issue.count < 0) return "повреждённое замечание";
            return null;
        }

        private static bool HexHash(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char c in value) if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        private static string ConnectionKey(List<QuantityConnectionPassport> passports)
        {
            if (passports == null) return "legacy:null";
            var b = new StringBuilder();
            foreach (var p in passports)
            {
                if (p == null) { b.Append("nullpassport"); continue; }
                b.Append(Tokens(p.schema, p.passport_id, p.source_run_id, Tokens(Sorted(Set(p.zone_ids)).ToArray()), p.status, p.reason,
                    p.scheme, p.catalog_revision, p.catalog_scope));
                b.Append(p.coverage == null ? "nullcoverage" : Tokens(p.coverage.fixed_sliding, p.coverage.splice_continuity,
                    p.coverage.gravity_load_distribution, p.coverage.strength));
                b.Append(p.summary == null ? "nullsummary" : Tokens(p.summary.members.ToString(CultureInfo.InvariantCulture),
                    p.summary.support_positions.ToString(CultureInfo.InvariantCulture), p.summary.support_links.ToString(CultureInfo.InvariantCulture),
                    p.summary.geometric_joints.ToString(CultureInfo.InvariantCulture), p.summary.matched_profile_references.ToString(CultureInfo.InvariantCulture),
                    p.summary.unresolved_profile_references.ToString(CultureInfo.InvariantCulture)));
                b.Append(Tokens("sources", p.sources == null ? null : p.sources.Count.ToString(CultureInfo.InvariantCulture)));
                if (p.sources == null) b.Append("nullsources"); else foreach (var s in p.sources)
                    b.Append(s == null ? "nullsource" : Tokens(s.source_id, s.sha256, s.kind, s.edition, s.locator));
                b.Append(Tokens("references", p.references == null ? null : p.references.Count.ToString(CultureInfo.InvariantCulture)));
                if (p.references == null) b.Append("nullreferences"); else foreach (var r in p.references)
                    b.Append(r == null ? "nullreference" : Tokens(r.reference_id, r.kind, r.designation, r.role, r.source_id,
                        ObjectKey(r.pdf_pages), r.properties_status));
                b.Append(Tokens("members", p.members == null ? null : p.members.Count.ToString(CultureInfo.InvariantCulture)));
                if (p.members == null) b.Append("nullmembers"); else foreach (var m in p.members)
                {
                    if (m == null) { b.Append("nullmember"); continue; }
                    b.Append(Tokens(m.rail_element_id, m.zone_id, m.support_count.ToString(CultureInfo.InvariantCulture),
                        m.span_count.ToString(CultureInfo.InvariantCulture), ObjectKey(m.intervals_mm), F(m.bottom_free_mm), F(m.top_free_mm)));
                    b.Append(m.profile_match == null ? "nullmatch" : Tokens(m.profile_match.status, ObjectKey(m.profile_match.reference_ids)));
                    b.Append(Tokens("supports", m.supports == null ? null : m.supports.Count.ToString(CultureInfo.InvariantCulture)));
                    if (m.supports == null) b.Append("nullsupports"); else foreach (var s in m.supports)
                        b.Append(s == null ? "nullsupport" : Tokens(F(s.offset_mm), ObjectKey(s.bracket_element_ids)));
                }
                b.Append(Tokens("joints", p.joints == null ? null : p.joints.Count.ToString(CultureInfo.InvariantCulture)));
                if (p.joints == null) b.Append("nulljoints"); else foreach (var j in p.joints)
                    b.Append(j == null ? "nulljoint" : Tokens(j.first_rail_element_id, j.second_rail_element_id, F(j.gap_mm), j.status));
                b.Append(Tokens("issues", p.issues == null ? null : p.issues.Count.ToString(CultureInfo.InvariantCulture)));
                if (p.issues == null) b.Append("nullissues"); else foreach (var i in p.issues)
                    b.Append(i == null ? "nullissue" : Tokens(i.code, i.message, i.count.ToString(CultureInfo.InvariantCulture)));
            }
            return Hash(b.ToString());
        }

        private static void ValidateCutting(QuantityResult result, QuantityReport report, HashSet<string> scope)
        {
            var groups = new HashSet<string>(StringComparer.Ordinal);
            if (report.cutting == null) return;
            if (report.kind == "frame" && report.cutting.Count > 0)
                Error(result, "Q_FRAME_CUTTING_UNSUPPORTED", "Раскрой подсистемы не реализован. Оценка хлыстов должна иметь отдельное основание «estimate».", report);
            foreach (var group in report.cutting)
            {
                if (group == null || Empty(group.group_id) || !groups.Add(group.group_id) ||
                    Set(group.scope_zone_ids).Count == 0 || !Set(group.scope_zone_ids).IsSubsetOf(scope))
                { Error(result, "Q_CUTTING_GROUP_INVALID", "Отсутствует или повторяется группа раскроя либо неверна её область.", report); continue; }
                if (group.rows == null) { Error(result, "Q_CUTTING_ROWS_INVALID", "Отсутствуют строки сохранённого раскроя.", report); continue; }
                foreach (var row in group.rows)
                {
                    if (row == null || row.basis != "cutting" || (row.unit != "шт." && row.unit != "м²") || !Finite(row.quantity) || row.quantity < 0 ||
                        (row.area_m2.HasValue && (!Finite(row.area_m2.Value) || row.area_m2.Value < 0)) ||
                        !ValidOptional(row.width_mm) || !ValidOptional(row.height_mm))
                    { Error(result, "Q_CUTTING_ROW_INVALID", "Строка раскроя содержит неверное основание, единицу или количество.", report); continue; }
                    if (row.unit == "шт." && Math.Abs(row.quantity - Math.Round(row.quantity)) > 1e-9)
                        Error(result, "Q_CUTTING_COUNT_INVALID", "Число исходных заготовок должно быть целым.", report);
                    if (Empty(row.type)) result.issues.Add(new QuantityIssue { code = "Q_CUTTING_VALUE_UNKNOWN", report_id = report.report_id,
                        message = "Не указан тип облицовки в области раскроя " + string.Join(" + ", Sorted(Set(group.scope_zone_ids)).ToArray()) + "." });
                }
            }
        }

        private static void ValidateEstimates(QuantityResult result, QuantityReport report, HashSet<string> scope)
        {
            if (report.estimates == null) return;
            if (report.kind != "frame" && report.estimates.Count > 0)
                Error(result, "Q_ESTIMATES_UNSUPPORTED", "Оценка хлыстов относится только к ведомости подсистемы.", report);
            var groups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in report.estimates)
            {
                if (group == null || Empty(group.group_id) || !groups.Add(group.group_id) ||
                    Set(group.scope_zone_ids).Count == 0 || !Set(group.scope_zone_ids).IsSubsetOf(scope))
                { Error(result, "Q_ESTIMATE_GROUP_INVALID", "Не задана или повторяется группа оценки хлыстов либо неверна её область.", report); continue; }
                if (group.rows == null)
                { Error(result, "Q_ESTIMATE_ROW_INVALID", "Нет строк сохранённой оценки хлыстов.", report); continue; }
                foreach (var row in group.rows)
                {
                    if (row == null || row.basis != "estimate" || row.role != "rail_stock_est" || row.unit != "шт." ||
                        !Finite(row.quantity) || row.quantity < 0 || Math.Abs(row.quantity - Math.Round(row.quantity)) > 1e-9 ||
                        !ValidOptional(row.length_mm) || (row.total_length_m.HasValue && (!Finite(row.total_length_m.Value) || row.total_length_m.Value < 0)) ||
                        row.area_m2.HasValue)
                        Error(result, "Q_ESTIMATE_ROW_INVALID", "Оценка хлыстов содержит неверное основание, единицу или количество.", report);
                }
            }
        }

        private static QuantityRow InstalledRow(QuantityElement e, HashSet<string> zones, bool byZone)
        {
            var sorted = Sorted(zones);
            return new QuantityRow { basis = "installed", zone_id = byZone ? string.Join(" + ", sorted.ToArray()) : "Все выбранные",
                zone_ids = sorted, role = e.role, product_id = e.product_id, identity_group = e.identity_group, mark = e.mark, type = e.type,
                material = e.material, coating = e.coating, system = e.system, color = e.color, orientation = e.orientation, piece_kind = e.piece_kind,
                width_mm = e.width_mm, height_mm = e.height_mm, shape_id = e.shape_id, quantity = 1,
                area_m2 = e.area_mm2.HasValue ? e.area_mm2.Value / 1000000.0 : (double?)null,
                length_mm = e.length_mm, total_length_m = IsLinearRole(e.role) && e.length_mm.HasValue ? e.length_mm.Value / 1000.0 : (double?)null,
                element_ids = new List<string> { e.element_id } };
        }

        private static QuantityRow CopyRow(QuantityRow r)
        {
            return new QuantityRow { basis = r.basis, zone_id = r.zone_id, zone_ids = r.zone_ids == null ? new List<string>() : new List<string>(r.zone_ids),
                role = r.role, product_id = r.product_id, identity_group = r.identity_group, mark = r.mark, type = r.type, material = r.material, color = r.color,
                coating = r.coating, system = r.system, length_mm = r.length_mm, total_length_m = r.total_length_m,
                orientation = r.orientation, piece_kind = r.piece_kind, width_mm = r.width_mm, height_mm = r.height_mm,
                shape_id = r.shape_id, unit = r.unit, quantity = r.quantity, area_m2 = r.area_m2,
                element_ids = r.element_ids == null ? new List<string>() : new List<string>(r.element_ids), note = r.note };
        }

        private static string RowKey(QuantityRow row, bool byZone)
        {
            return Tokens(byZone ? Tokens(Sorted(Set(row.zone_ids)).ToArray()) : null, row.basis, row.role, row.product_id, row.identity_group, row.mark,
                row.type, row.material, row.coating, row.system, row.color, row.orientation, row.piece_kind,
                N(row.width_mm), N(row.height_mm), N(row.length_mm), row.shape_id, row.unit);
        }
        private static string ElementKey(QuantityElement e, QuantityDiagnostics diagnostics)
        {
            diagnostics.canonical_element_keys++;
            var sb = new StringBuilder(Tokens(e.element_id, e.zone_id, Tokens(Sorted(ElementZones(e)).ToArray()),
                e.role, e.product_id, e.identity_group, e.mark, e.type, e.material, e.coating, e.system, e.color, e.orientation, e.piece_kind,
                N(e.width_mm), N(e.height_mm), N(e.area_mm2), N(e.length_mm), e.shape_id, e.origin));
            if (e.rings != null) foreach (var ring in e.rings)
            {
                if (ring == null) { sb.Append("nullring"); continue; }
                sb.Append(Tokens(ring.role));
                if (ring.points != null) foreach (var p in ring.points)
                    sb.Append(p == null ? "nullpoint" : Tokens(Array.ConvertAll(p, F)));
            }
            if (e.cad_entities != null) foreach (var cad in e.cad_entities)
                sb.Append(cad == null ? "nullcad" : Tokens(cad.handle, cad.role, cad.fingerprint));
            return Hash(sb.ToString());
        }
        private static string ReportKey(QuantityReport r, QuantityDiagnostics diagnostics)
        {
            diagnostics.canonical_report_keys++;
            var sb = new StringBuilder(Tokens(r.schema, r.report_id, r.kind, r.document_id, r.scope, r.run_id, r.algorithm,
                r.completeness, r.engineering_coverage, Tokens(Sorted(Set(r.zone_ids)).ToArray())));
            if (r.source_revisions != null)
                foreach (string key in Sorted(r.source_revisions.Keys)) sb.Append(Tokens(key, r.source_revisions[key]));
            sb.Append(Tokens(ObjectKey(r.parameters), ObjectKey(r.engine_summary), ConnectionKey(r.connection_passports)));
            if (r.elements != null) foreach (var e in r.elements) sb.Append(e == null ? "nullelement" : ElementKey(e, diagnostics));
            if (r.cutting != null) foreach (var group in r.cutting)
            {
                if (group == null) { sb.Append("nullgroup"); continue; }
                sb.Append(Tokens(group.group_id, Tokens(Sorted(Set(group.scope_zone_ids)).ToArray())));
                if (group.parameters != null)
                    foreach (string key in Sorted(group.parameters.Keys)) sb.Append(Tokens(key, ObjectKey(group.parameters[key])));
                if (group.rows != null) foreach (var row in group.rows)
                    sb.Append(row == null ? "nullrow" : Tokens(RowKey(row, true), F(row.quantity), N(row.area_m2), N(row.total_length_m), row.note,
                        Tokens(Sorted(Set(row.zone_ids)).ToArray()), Tokens(Sorted(Set(row.element_ids)).ToArray())));
            }
            if (r.estimates != null) foreach (var group in r.estimates)
            {
                if (group == null) { sb.Append("nullestimate"); continue; }
                sb.Append(Tokens(group.group_id, Tokens(Sorted(Set(group.scope_zone_ids)).ToArray()), ObjectKey(group.parameters)));
                if (group.rows != null) foreach (var row in group.rows)
                    sb.Append(row == null ? "nullestimaterow" : Tokens(RowKey(row, true), F(row.quantity), N(row.area_m2), N(row.total_length_m), row.note,
                        Tokens(Sorted(Set(row.zone_ids)).ToArray()), Tokens(Sorted(Set(row.element_ids)).ToArray())));
            }
            if (r.issues != null) foreach (var issue in r.issues)
                sb.Append(issue == null ? "nullissue" : Tokens(issue.code, issue.message, issue.report_id, issue.element_id,
                    Tokens(Sorted(Set(issue.element_ids)).ToArray()), issue.element_count.ToString(CultureInfo.InvariantCulture), issue.severity));
            return Hash(sb.ToString());
        }
        private static string ObjectKey(object value)
        {
            if (value == null) return "null";
            var dictionary = value as System.Collections.IDictionary;
            if (dictionary != null)
            {
                var keys = new List<string>(); foreach (object key in dictionary.Keys) keys.Add(Convert.ToString(key, CultureInfo.InvariantCulture));
                keys.Sort(StringComparer.Ordinal); var sb = new StringBuilder("dict");
                foreach (string key in keys) sb.Append(Tokens(key, ObjectKey(dictionary[key])));
                return sb.ToString();
            }
            if (!(value is string) && value is System.Collections.IEnumerable)
            {
                var sb = new StringBuilder("array");
                foreach (object item in (System.Collections.IEnumerable)value) sb.Append(Tokens(ObjectKey(item)));
                return sb.ToString();
            }
            // JavaScriptSerializer reads an integral JSON number as Int32 even if the
            // producer stored Double. A JSON round trip must not make equal passports conflict.
            if (value is double) return Tokens("number", F((double)value));
            if (value is float) return Tokens("number", ((float)value).ToString("R", CultureInfo.InvariantCulture));
            if (value is byte || value is sbyte || value is short || value is ushort || value is int ||
                value is uint || value is long || value is ulong || value is decimal)
                return Tokens("number", Convert.ToString(value, CultureInfo.InvariantCulture));
            return Tokens(value.GetType().FullName, Convert.ToString(value, CultureInfo.InvariantCulture));
        }
        private static string Tokens(params string[] values)
        { var sb = new StringBuilder(); foreach (string v in values) sb.Append(v == null ? "-1:" : v.Length.ToString(CultureInfo.InvariantCulture) + ":" + v); return sb.ToString(); }
        private static HashSet<string> Set(IEnumerable<string> values)
        { var s = new HashSet<string>(StringComparer.Ordinal); if (values != null) foreach (string v in values) if (!Empty(v)) s.Add(v); return s; }
        private static HashSet<string> ElementZones(QuantityElement e)
        { var s = Set(e.zone_ids); if (s.Count == 0 && !Empty(e.zone_id)) s.Add(e.zone_id); return s; }
        private static List<string> Sorted(IEnumerable<string> values)
        { var l = new List<string>(values); l.Sort(StringComparer.Ordinal); return l; }
        private static List<string> SortedUnion(IEnumerable<string> a, IEnumerable<string> b)
        { var s = Set(a); if (b != null) foreach (string v in b) s.Add(v); return Sorted(s); }
        private static bool Empty(string value) { return string.IsNullOrWhiteSpace(value); }
        private static bool IsLinearRole(string role) { return role == "rail" || role == "hrail" || role == "shina"; }
        private static bool IsFrameRole(string role) { return IsLinearRole(role) || role == "bracket" || role == "clamp" || role == "fitting"; }
        private static void Reset(QuantityDiagnostics diagnostics)
        {
            diagnostics.reports_seen = diagnostics.reports_compared = diagnostics.elements_seen = diagnostics.elements_validated =
                diagnostics.elements_compared = diagnostics.cad_links_checked = diagnostics.shapes_validated =
                diagnostics.canonical_report_keys = diagnostics.canonical_element_keys = diagnostics.aggregate_keys_built = 0;
            diagnostics.connection_elements_indexed = diagnostics.connection_members_validated =
                diagnostics.connection_supports_validated = diagnostics.connection_links_validated = 0;
        }
        private static string JoinNote(string a, string b) { return Empty(a) ? b : a + "; " + b; }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool ValidOptional(double? value) { return !value.HasValue || (Finite(value.Value) && value.Value > 0); }
        private static bool Close(double a, double b, double tolerance) { return Math.Abs(a - b) <= Math.Max(tolerance, Math.Abs(b) * 1e-9); }
        private static string F(double value) { return (value == 0 ? 0 : value).ToString("R", CultureInfo.InvariantCulture); }
        private static string N(double? value) { return value.HasValue ? F(value.Value) : null; }
        private static string Hash(string text)
        { using (var sha = SHA256.Create()) { var sb = new StringBuilder(); foreach (byte b in sha.ComputeHash(Encoding.UTF8.GetBytes(text))) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture)); return sb.ToString(); } }
        private static void Error(QuantityResult r, string code, string message, QuantityReport report, QuantityElement element = null)
        { r.issues.Add(new QuantityIssue { code = code, message = message, severity = "error", report_id = report == null ? null : report.report_id, element_id = element == null ? null : element.element_id }); }
        private static bool HasErrors(QuantityResult result)
        { foreach (var issue in result.issues) if (issue.severity == "error") return true; return false; }

        private static double Cross(double[] a, double[] b, double[] c)
        { return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]); }
        private static bool Same(double[] a, double[] b) { return Math.Abs(a[0] - b[0]) <= 1e-8 && Math.Abs(a[1] - b[1]) <= 1e-8; }
        private static double SignedArea(IList<double[]> p)
        { double a = 0; for (int i = 1; i < p.Count - 1; i++) a += Cross(p[0], p[i], p[i + 1]); return a / 2; }
        private static bool OnSegment(double[] p, double[] a, double[] b)
        {
            double length = Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
            return Math.Abs(Cross(a, b, p)) <= 1e-8 * Math.Max(1, length) &&
                p[0] >= Math.Min(a[0], b[0]) - 1e-8 && p[0] <= Math.Max(a[0], b[0]) + 1e-8 &&
                p[1] >= Math.Min(a[1], b[1]) - 1e-8 && p[1] <= Math.Max(a[1], b[1]) + 1e-8;
        }
        private static void RemoveCollinear(List<double[]> points)
        {
            bool changed = true;
            while (changed && points.Count > 3)
            {
                changed = false;
                for (int i = 0; i < points.Count; i++)
                    if (OnSegment(points[i], points[(i + points.Count - 1) % points.Count], points[(i + 1) % points.Count]))
                    { points.RemoveAt(i); changed = true; break; }
            }
        }
        private static bool SegmentsIntersect(double[] a, double[] b, double[] c, double[] d)
        {
            if (OnSegment(a, c, d) || OnSegment(b, c, d) || OnSegment(c, a, b) || OnSegment(d, a, b)) return true;
            double abC = Cross(a, b, c), abD = Cross(a, b, d), cdA = Cross(c, d, a), cdB = Cross(c, d, b);
            return (abC > 0) != (abD > 0) && (cdA > 0) != (cdB > 0);
        }
        private static bool SelfIntersects(IList<double[]> ring)
        {
            for (int i = 0; i < ring.Count; i++) for (int j = i + 1; j < ring.Count; j++)
            {
                if (j == i + 1 || (i == 0 && j == ring.Count - 1)) continue;
                if (SegmentsIntersect(ring[i], ring[(i + 1) % ring.Count], ring[j], ring[(j + 1) % ring.Count])) return true;
            }
            return false;
        }
        private static bool RingsIntersect(IList<double[]> a, IList<double[]> b)
        {
            for (int i = 0; i < a.Count; i++) for (int j = 0; j < b.Count; j++)
                if (SegmentsIntersect(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
            return false;
        }
        private static bool Inside(double[] point, IList<double[]> ring)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                if (OnSegment(point, ring[j], ring[i])) return false;
                if ((ring[i][1] > point[1]) != (ring[j][1] > point[1]) && point[0] <
                    (ring[j][0] - ring[i][0]) * (point[1] - ring[i][1]) / (ring[j][1] - ring[i][1]) + ring[i][0]) inside = !inside;
            }
            return inside;
        }
        private static string CanonicalRing(QuantityRing ring, double minX, double minY)
        {
            // A 0.001 mm identity grid suppresses translation noise only. Rotation and mirror
            // operations are deliberately absent; surface orientation is also a separate row key.
            int n = ring.points.Length; string best = null;
            var vertices = new string[n];
            for (int i = 0; i < n; i++) vertices[i] = Tokens(
                Math.Round((ring.points[i][0] - minX) * 1000, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture),
                Math.Round((ring.points[i][1] - minY) * 1000, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture));
            for (int start = 0; start < n; start++) for (int direction = -1; direction <= 1; direction += 2)
            {
                var sb = new StringBuilder(ring.role + ":");
                for (int i = 0; i < n; i++) sb.Append(vertices[(start + direction * i + n) % n]);
                string value = sb.ToString(); if (best == null || string.CompareOrdinal(value, best) < 0) best = value;
            }
            return best;
        }
    }
}
