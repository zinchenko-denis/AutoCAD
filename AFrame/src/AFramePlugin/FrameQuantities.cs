using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using FacadeSafety;

namespace AFramePlugin
{
    // A physical emitted item has one drawing representation. Dimensions are
    // measured on the engine axis, not on the width of a conventional symbol.
    // No dynamic property is changed by the quantities path.
    internal sealed class FrameQuantities
    {
        private static readonly string[] Categories = { "rails", "hrails", "brackets", "clamps", "fittings" };
        private readonly QuantityReport report;
        private readonly Dictionary<string, List<QuantityElement>> elements = new Dictionary<string, List<QuantityElement>>();
        private readonly Dictionary<string, object[]> inputs = new Dictionary<string, object[]>();
        private readonly Dictionary<ObjectId, Extents3d?> blockBounds = new Dictionary<ObjectId, Extents3d?>();
        private readonly Dictionary<ObjectId, string> layerColors = new Dictionary<ObjectId, string>();
        private readonly Dictionary<string, string[]> profileStates = new Dictionary<string, string[]>();
        private readonly List<QuantityReport> retained = new List<QuantityReport>();
        private string unavailable;
        private int operations;
        private long captureTicks;
        internal double CaptureMilliseconds { get { return captureTicks * 1000.0 / Stopwatch.Frequency; } }
        internal string UnavailableReason { get { return unavailable; } }

        internal FrameQuantities(Dictionary<string, object> response, Dictionary<string, object> request,
            Dictionary<string, string> partToRoot, bool clampsOnly, QuantitySelection previous)
        {
            try
            {
                report = BuildReport(response, request, partToRoot);
                int offset = 0;
                foreach (string category in Categories)
                {
                    object[] source = Array(Get(response, category));
                    inputs[category] = source;
                    elements[category] = report.elements.GetRange(offset, source.Length);
                    offset += source.Length;
                }
                string assembly = Assembly.GetExecutingAssembly().Location;
                report.source_revisions["producer_dll_sha256"] = FileHash(assembly);
                report.source_revisions["engine_sha256"] = FileHash(Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(assembly) ?? ".", "..", "engine", "frame_engine.exe")));
                if (clampsOnly)
                {
                    if (previous == null || !previous.Ok)
                        Unavailable("Режим «только кляммеры»: прежние элементы не имеют проверенного паспорта. " +
                            (previous == null ? "" : previous.Reason) + " Для полной ведомости перестройте подсистему целиком.");
                    else
                    {
                        var selected = new HashSet<string>(previous.SelectedZoneIds, StringComparer.Ordinal);
                        foreach (var old in previous.Reports)
                        {
                            // Manual registrations remain in their independent zone index.
                            // Copying them here would stamp them as generated ATFRAME parts.
                            if (IsManualContribution(old)) continue;
                            if (!IsGeneratedFrameReport(old))
                            {
                                Unavailable("Прежний паспорт подсистемы содержит неподтверждённое или смешанное происхождение деталей. " +
                                    "Ручные регистрации должны оставаться отдельными. Перестройте подсистему целиком.");
                                continue;
                            }
                            foreach (string zone in old.zone_ids)
                                if (!selected.Contains(zone))
                                    Unavailable("Режим «только кляммеры»: выбрана часть группы прежнего паспорта. " +
                                        "Для ведомости обновите всю группу или перестройте подсистему целиком.");
                            retained.Add(old);
                        }
                        if (retained.Count == 0)
                            Unavailable("Режим «только кляммеры»: отсутствует паспорт сохранённых элементов. Перестройте подсистему целиком.");
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (System.Exception ex)
            {
                report = report ?? new QuantityReport { kind = "frame" };
                Unavailable("Паспорт подсистемы не подготовлен: " + ex.Message);
            }
        }

        internal void Unavailable(string reason)
        { if (string.IsNullOrEmpty(unavailable)) unavailable = reason; }

        internal void Owner(Entity entity, string zone)
        { report.source_revisions["owner_zone:" + entity.Handle.ToString()] = zone; }

        internal void Poll()
        { if ((operations++ & 127) == 0) CheckCancel(); }

        internal static void CheckCancel()
        {
            bool stop = false;
            try { stop = HostApplicationServices.Current.UserBreak(); } catch { }
            if (stop) throw new OperationCanceledException("Построение подсистемы отменено.");
        }

        internal void Add(Transaction tr, string category, int index, Entity entity)
        {
            Poll();
            if (unavailable != null) return;
            long start = Stopwatch.GetTimestamp();
            try
            {
                var element = elements[category][index];
                var source = (Dictionary<string, object>)inputs[category][index];
                var block = entity as BlockReference;
                if (category == "rails" && block != null && !RailDimensionMatches(tr, block, source))
                {
                    Unavailable("Длина или положение направляющей в блоке не подтверждены геометрией образца. " +
                        "Подсистема построена, но ведомость недоступна; используйте проверенный образец либо прямоугольные направляющие.");
                    return;
                }
                if (category == "rails" && block != null && !RailProfileMatches(block, element.mark))
                {
                    Unavailable("Марка направляющей не подтверждена текущим состоянием блока. " +
                        "Подсистема построена, но ведомость недоступна; проверьте состояния видимости образца и повторите ATFRAME.");
                    return;
                }
                element.cad_entities.Add(FacadeQuantityStore.CaptureEntity(tr, entity,
                    entity is BlockReference ? "block" : "outer"));
                element.color = DrawingColor(tr, entity);
            }
            catch (OperationCanceledException) { throw; }
            catch (System.Exception ex)
            { Unavailable("Нельзя подтвердить состав подсистемы для ведомости: " + ex.Message); }
            finally { captureTicks += Stopwatch.GetTimestamp() - start; }
        }

        internal void Store(Transaction tr, Database db, IEnumerable<Entity> carriers,
            FacadeQuantityStore.FrameSources sources, ISet<string> erasedHandles)
        {
            CheckCancel();
            if (unavailable == null)
            {
                var provenance = new List<object>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var retainedZones = new HashSet<string>(report.zone_ids, StringComparer.Ordinal);
                foreach (var piece in report.elements) seen.Add(piece.element_id);
                if (retained.Count > 0) report.connection_passports = new List<QuantityConnectionPassport>();
                foreach (var old in retained)
                {
                    var keptIds = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var piece in old.elements)
                    {
                        Poll();
                        bool erased = false, kept = false;
                        foreach (var link in piece.cad_entities)
                            if (erasedHandles.Contains(link.handle)) erased = true; else kept = true;
                        if (erased && kept)
                        { Unavailable("Сохранена только часть физического элемента прежней подсистемы. Перестройте подсистему целиком."); break; }
                        if (!erased && seen.Add(piece.element_id))
                        {
                            report.elements.Add(piece);
                            keptIds.Add(piece.element_id);
                            foreach (string zone in piece.zone_ids)
                                if (retainedZones.Add(zone)) report.zone_ids.Add(zone);
                        }
                    }
                    AppendRetainedProvenance(old, keptIds, provenance);
                    AppendRetainedConnections(old, keptIds, report.connection_passports);
                }
                if (provenance.Count > 0)
                {
                    report.parameters["retained_sources"] = provenance;
                    report.parameters["summary_scope"] = "new_clamps_operation_only";
                    report.engineering_coverage = "clamps_geometry_only_with_retained_frame";
                }
                foreach (var piece in report.elements)
                    if (piece.cad_entities == null || piece.cad_entities.Count != 1)
                    { Unavailable("Неполная связь физического элемента подсистемы с чертежом. Перестройте подсистему целиком."); break; }
            }
            if (sources == null) Unavailable("Не сохранены исходные контуры и направляющие для проверки ведомости подсистемы.");
            if (unavailable != null)
                FacadeQuantityStore.MarkFrameUnavailable(tr, db, carriers, unavailable);
            else
                FacadeQuantityStore.StoreFrame(tr, db, carriers, report, sources);
        }

        // Expected visibility mapping is resolved once per base definition and
        // nominal profile. Only the current value is read on subsequent inserts.
        // This repeats no setter or block evaluation.
        private bool RailProfileMatches(BlockReference block, string profile)
        {
            if (string.IsNullOrEmpty(profile)) return true; // unknown remains unknown
            if (!block.IsDynamicBlock) return false;
            string want = ProfileKey(profile);
            string key = block.DynamicBlockTableRecord.Handle.ToString() + "|" + want;
            string[] expected;
            if (!profileStates.TryGetValue(key, out expected))
            {
                expected = null;
                foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                {
                    if (property.ReadOnly) continue;
                    object[] allowed;
                    try { allowed = property.GetAllowedValues(); } catch { continue; }
                    if (allowed == null) continue;
                    foreach (object value in allowed)
                    {
                        string normalized = ProfileKey(Text(value));
                        // A short visibility family such as "ГП" does not prove
                        // a complete section mark such as "ГП-40-40-1,2".
                        if (normalized.Length == 0 || normalized != want) continue;
                        expected = new[] { property.PropertyName, normalized };
                        break;
                    }
                    if (expected != null) break;
                }
                profileStates[key] = expected;
            }
            if (expected == null) return false;
            foreach (DynamicBlockReferenceProperty property in block.DynamicBlockReferencePropertyCollection)
                if (property.PropertyName == expected[0]) return ProfileKey(Text(property.Value)) == expected[1];
            return false;
        }

        private bool RailDimensionMatches(Transaction tr, BlockReference block, Dictionary<string, object> source)
        {
            var bounds = BlockBounds(tr, block.BlockTableRecord, new HashSet<ObjectId>());
            if (!bounds.HasValue) return false;
            var world = bounds.Value; world.TransformBy(block.BlockTransform);
            return Math.Abs(world.MinPoint.Y - Number(Get(source, "y0"))) <= 0.5 &&
                Math.Abs(world.MaxPoint.Y - Number(Get(source, "y1"))) <= 0.5;
        }
        private Extents3d? BlockBounds(Transaction tr, ObjectId id, HashSet<ObjectId> visiting)
        {
            Extents3d? cached;
            if (blockBounds.TryGetValue(id, out cached)) return cached;
            if (!visiting.Add(id)) return null;
            var definition = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
            Extents3d? result = null;
            if (definition != null)
                foreach (ObjectId childId in definition)
                {
                    var child = tr.GetObject(childId, OpenMode.ForRead) as Entity;
                    Extents3d? box = null;
                    var nested = child as BlockReference;
                    if (nested != null)
                    {
                        box = BlockBounds(tr, nested.BlockTableRecord, visiting);
                        if (box.HasValue) { var transformed = box.Value; transformed.TransformBy(nested.BlockTransform); box = transformed; }
                    }
                    else if (child is Curve) box = child.GeometricExtents;
                    if (box.HasValue)
                    {
                        if (!result.HasValue) result = box;
                        else { var combined = result.Value; combined.AddExtents(box.Value); result = combined; }
                    }
                }
            visiting.Remove(id); blockBounds[id] = result; return result;
        }
        private string DrawingColor(Transaction tr, Entity entity)
        {
            var color = entity.Color;
            if (color.IsByLayer)
            {
                string cached;
                if (layerColors.TryGetValue(entity.LayerId, out cached)) return cached;
                var layer = tr.GetObject(entity.LayerId, OpenMode.ForRead) as LayerTableRecord;
                if (layer != null) color = layer.Color;
                string value = color.IsByAci ? "CAD ACI " + color.ColorIndex : "CAD RGB " + color.Red + "," + color.Green + "," + color.Blue;
                layerColors[entity.LayerId] = value; return value;
            }
            return color.IsByAci ? "CAD ACI " + color.ColorIndex : "CAD RGB " + color.Red + "," + color.Green + "," + color.Blue;
        }

        internal static QuantityReport BuildReport(Dictionary<string, object> response,
            Dictionary<string, object> request, Dictionary<string, string> partToRoot)
        {
            if (response == null || !object.Equals(Get(response, "ok"), true))
                throw new InvalidOperationException("Движок не подтвердил успешный результат подсистемы.");
            string run = Guid.NewGuid().ToString("N");
            var summary = Get(response, "summary") as Dictionary<string, object> ?? new Dictionary<string, object>();
            bool clamps = Text(Get(request, "parts")) == "clamps";
            bool calc = Get(response, "calc_report") != null;
            var result = new QuantityReport { kind = "frame", report_id = run, run_id = run,
                scope = "whole_frame_run", algorithm = "ATFRAME/facade_quantities/1",
                completeness = "partial", engineering_coverage = clamps ? "clamps_geometry_only" : calc ? "limited_static_chain" : "geometry_only",
                engine_summary = new Dictionary<string, object>(summary) };
            foreach (var parameter in request)
                if (parameter.Key != "zones" && parameter.Key != "contours" && parameter.Key != "rails_fixed")
                    result.parameters[parameter.Key] = parameter.Value;
            result.parameters["coordinate_unit"] = "mm";
            result.parameters["length_basis"] = "engine_axis_segments_with_verified_drawing_identity";
            result.parameters["profile_source"] = "engine_or_user_label_not_catalogue_approval";
            result.parameters["fittings_scope"] = "conditional_symbols_not_confirmed_hardware_bom";
            foreach (string key in new[] { "design_scope", "calc_report", "calc_reports", "notes" })
                if (response.ContainsKey(key)) result.engine_summary[key] = response[key];
            var solutionReport = Get(response, "solution_report") as Dictionary<string, object>;
            if (solutionReport != null)
            {
                // The exact declaration already exists once in parameters.
                // Store the engine's limits/status without duplicating selection.
                var declarationStatus = new Dictionary<string, object>(solutionReport);
                declarationStatus.Remove("selection");
                result.engine_summary["solution_report"] = declarationStatus;
            }
            result.engine_summary["geometry_basis"] = "frame_axes_and_conventional_symbols";
            var systemUsed = Get(response, "system_used") as Dictionary<string, object>;
            string system = Text(Get(systemUsed, "_name"));
            if (system.Length == 0) system = Text(Get(request, "system"));
            var scope = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in Array(Get(response, "per_zone")))
            {
                var zone = item as Dictionary<string, object>;
                string id = Root(Text(Get(zone, "zone_id")), partToRoot);
                if (id.Length == 0) throw new InvalidOperationException("У результата отсутствует зона.");
                scope.Add(id);
            }
            if (scope.Count == 0) throw new InvalidOperationException("Не определена полная область результата подсистемы.");
            result.zone_ids.AddRange(scope); result.zone_ids.Sort(StringComparer.Ordinal);
            foreach (string category in Categories)
            {
                object[] source = Array(Get(response, category));
                for (int i = 0; i < source.Length; i++)
                {
                    var item = source[i] as Dictionary<string, object>;
                    string zone = Root(Text(Get(item, "zone")), partToRoot), type = Text(Get(item, "kind"));
                    if (item == null || !scope.Contains(zone))
                        throw new InvalidOperationException("Элемент подсистемы не связан с обработанной зоной.");
                    string role = category == "rails" ? "rail" : category == "hrails" ?
                        (type.StartsWith("шина", StringComparison.Ordinal) ? "shina" : "hrail") :
                        category == "brackets" ? "bracket" : category == "clamps" ? "clamp" : "fitting";
                    bool linear = category == "rails" || category == "hrails";
                    double? length = null;
                    if (linear)
                    {
                        length = category == "rails" ? Number(Get(item, "y1")) - Number(Get(item, "y0")) :
                            Number(Get(item, "x1")) - Number(Get(item, "x0"));
                        if (length <= 0) throw new InvalidOperationException("Длина физического отрезка направляющей неположительна.");
                        if (Get(item, "len") != null && Math.Abs(Number(Get(item, "len")) - length.Value) > 0.01)
                            throw new InvalidOperationException("Длина направляющей расходится с её конечными точками.");
                    }
                    string profile = linear ? Text(Get(item, "profile")) : null;
                    if (profile == "направляющая" || profile == "Z-профиль" || profile == "НГП" ||
                        (profile == type && type.StartsWith("шина", StringComparison.Ordinal))) profile = null;
                    string orientation = category == "rails" ? "vertical" : category == "hrails" ? "horizontal" :
                        category == "clamps" && type == "боковой" && Text(Get(item, "orient")) != "h" ? "vertical" : "horizontal";
                    result.elements.Add(new QuantityElement {
                        element_id = ElementId(run, category, i),
                        zone_id = zone, zone_ids = new List<string> { zone }, system = system,
                        role = role, type = type, mark = string.IsNullOrEmpty(profile) ? null : profile,
                        product_id = null, material = null, coating = null, color = null,
                        orientation = orientation, piece_kind = linear ? "segment" : "symbol",
                        length_mm = length, origin = "generated:ATFRAME:" + category
                    });
                }
            }
            if (response.ContainsKey("connection_passport"))
                result.connection_passports = new List<QuantityConnectionPassport> {
                    BuildConnectionPassport(Object(response["connection_passport"]), result, response, partToRoot) };
            result.issues.Add(new QuantityIssue { code = "Q_FRAME_ENGINEERING_LIMIT", report_id = result.report_id,
                message = "Ведомость учитывает построенные элементы. Полная статическая модель, неподвижные/подвижные соединения, стыки и комплектность узлов не подтверждены." });
            if (Array(Get(response, "fittings")).Length > 0)
                result.issues.Add(new QuantityIssue { code = "Q_FRAME_FITTINGS_CONDITIONAL", report_id = result.report_id,
                    message = "Вставки и скобы — условные элементы схемы; они не задают подтверждённый комплект болтов, шайб и других метизов." });
            object estimate = Get(summary, "rail_stock_est");
            if (estimate != null && !clamps)
            {
                double count = Number(estimate);
                if (count < 0 || count != Math.Floor(count)) throw new InvalidOperationException("Неверная оценка числа хлыстов.");
                var group = new QuantityEstimateGroup { group_id = run + ":rail_stock_est", scope_zone_ids = new List<string>(result.zone_ids) };
                group.parameters["method"] = "sum_length_divided_by_stock_rounded_up";
                group.parameters["basis"] = "engine_summary_estimate_not_cutting_or_procurement";
                double? stockLength = null;
                if (Get(systemUsed, "rail_stock") != null)
                {
                    double explicitStock = Number(Get(systemUsed, "rail_stock"));
                    if (explicitStock > 0) { stockLength = explicitStock; group.parameters["stock_length_mm"] = explicitStock; }
                }
                group.rows.Add(new QuantityRow { basis = "estimate", role = "rail_stock_est", system = system,
                    zone_id = string.Join("+", result.zone_ids.ToArray()), zone_ids = new List<string>(result.zone_ids),
                    unit = "шт.", quantity = count, length_mm = stockLength,
                    note = "Оценка по сумме длин; раскрой и закупка не рассчитаны." });
                result.estimates.Add(group);
            }
            if (clamps)
                result.issues.Add(new QuantityIssue { code = "Q_FRAME_ESTIMATE_NOT_RECALCULATED", report_id = result.report_id,
                    message = "При обновлении только кляммеров оценка числа хлыстов сохранённого каркаса не пересчитывалась и не выводится." });
            return result;
        }

        private static bool IsManualContribution(QuantityReport value)
        {
            if (value == null || value.kind != "frame" || value.scope != "manual_zone_contribution" ||
                value.algorithm != "manual-import/1" || value.elements == null) return false;
            foreach (var element in value.elements)
                if (element == null || element.origin == null || !element.origin.StartsWith("manual:", StringComparison.Ordinal)) return false;
            return true;
        }
        private static bool IsGeneratedFrameReport(QuantityReport value)
        {
            if (value == null || value.kind != "frame" || value.scope != "whole_frame_run" ||
                value.algorithm != "ATFRAME/facade_quantities/1" || value.elements == null) return false;
            foreach (var element in value.elements)
                if (element == null || element.origin == null || !element.origin.StartsWith("generated:ATFRAME:", StringComparison.Ordinal)) return false;
            return true;
        }

        private static QuantityConnectionPassport BuildConnectionPassport(Dictionary<string, object> source,
            QuantityReport report, Dictionary<string, object> response, Dictionary<string, string> partToRoot)
        {
            var p = new QuantityConnectionPassport {
                schema = Text(Get(source, "schema")), passport_id = report.run_id + ":connections", source_run_id = report.run_id,
                zone_ids = new List<string>(report.zone_ids), status = Text(Get(source, "status")),
                reason = Get(source, "reason") == null ? null : Text(Get(source, "reason")),
                scheme = Text(Get(source, "scheme")), catalog_revision = Text(Get(source, "catalog_revision")),
                catalog_scope = Text(Get(source, "catalog_scope")) };
            foreach (object value in Array(Get(source, "sources")))
            {
                var item = Object(value);
                p.sources.Add(new QuantityConnectionSource { source_id = Text(Get(item, "source_id")),
                    sha256 = Text(Get(item, "sha256")), kind = Text(Get(item, "kind")),
                    edition = Text(Get(item, "edition")), locator = Text(Get(item, "locator")) });
            }
            foreach (object value in Array(Get(source, "references")))
            {
                var item = Object(value);
                var reference = new QuantityConnectionReference { reference_id = Text(Get(item, "reference_id")),
                    kind = Text(Get(item, "kind")), designation = Text(Get(item, "designation")), role = Text(Get(item, "role")),
                    source_id = Text(Get(item, "source_id")), properties_status = Text(Get(item, "properties_status")) };
                foreach (object page in Array(Get(item, "pdf_pages"))) reference.pdf_pages.Add(Integer(page));
                p.references.Add(reference);
            }
            object[] rails = Array(Get(response, "rails")), brackets = Array(Get(response, "brackets"));
            foreach (object value in Array(Get(source, "members")))
            {
                var item = Object(value);
                int index = Index(Get(item, "rail_index"), rails.Length);
                var rail = Object(rails[index]);
                string zone = Text(Get(item, "zone_id"));
                if (zone != Text(Get(rail, "zone"))) throw new InvalidOperationException("Паспорт соединений ссылается на направляющую другой зоны.");
                var member = new QuantityConnectionMember {
                    rail_element_id = ElementId(report.run_id, "rails", index), zone_id = Root(zone, partToRoot),
                    support_count = Integer(Get(item, "support_count")), span_count = Integer(Get(item, "span_count")),
                    bottom_free_mm = Number(Get(item, "bottom_free_mm")), top_free_mm = Number(Get(item, "top_free_mm")) };
                foreach (object interval in Array(Get(item, "intervals_mm"))) member.intervals_mm.Add(Number(interval));
                foreach (object supportValue in Array(Get(item, "supports")))
                {
                    var supportItem = Object(supportValue);
                    var support = new QuantityConnectionSupport { offset_mm = Number(Get(supportItem, "offset_mm")) };
                    foreach (object bracketValue in Array(Get(supportItem, "bracket_indices")))
                    {
                        int bracketIndex = Index(bracketValue, brackets.Length);
                        if (zone != Text(Get(Object(brackets[bracketIndex]), "zone")))
                            throw new InvalidOperationException("Кандидат опоры принадлежит другой исходной зоне.");
                        support.bracket_element_ids.Add(ElementId(report.run_id, "brackets", bracketIndex));
                    }
                    member.supports.Add(support);
                }
                var match = Object(Get(item, "profile_match"));
                member.profile_match.status = Text(Get(match, "status"));
                foreach (object id in Array(Get(match, "reference_ids"))) member.profile_match.reference_ids.Add(Text(id));
                p.members.Add(member);
            }
            foreach (object value in Array(Get(source, "joints")))
            {
                var item = Object(value);
                int first = Index(Get(item, "first_rail_index"), rails.Length), second = Index(Get(item, "second_rail_index"), rails.Length);
                if (Text(Get(Object(rails[first]), "zone")) != Text(Get(Object(rails[second]), "zone")))
                    throw new InvalidOperationException("Геометрическая смежность направляющих пересекает исходные зоны.");
                p.joints.Add(new QuantityConnectionJoint {
                    first_rail_element_id = ElementId(report.run_id, "rails", first),
                    second_rail_element_id = ElementId(report.run_id, "rails", second),
                    gap_mm = Number(Get(item, "gap_mm")), status = Text(Get(item, "status")) });
            }
            var coverage = Object(Get(source, "coverage"));
            p.coverage = new QuantityConnectionCoverage { fixed_sliding = Text(Get(coverage, "fixed_sliding")),
                splice_continuity = Text(Get(coverage, "splice_continuity")), gravity_load_distribution = Text(Get(coverage, "gravity_load_distribution")),
                strength = Text(Get(coverage, "strength")) };
            var summary = Object(Get(source, "summary"));
            p.summary = new QuantityConnectionSummary { members = Integer(Get(summary, "members")),
                support_positions = Integer(Get(summary, "support_positions")), support_links = Integer(Get(summary, "support_links")),
                geometric_joints = Integer(Get(summary, "geometric_joints")), matched_profile_references = Integer(Get(summary, "matched_profile_references")),
                unresolved_profile_references = Integer(Get(summary, "unresolved_profile_references")) };
            foreach (object value in Array(Get(source, "issues")))
            {
                var item = Object(value);
                p.issues.Add(new QuantityConnectionIssue { code = Text(Get(item, "code")), message = Text(Get(item, "message")), count = Integer(Get(item, "count")) });
            }
            return p;
        }

        // Retain the original inventory and IDs as a single typed object. Updating
        // clamps does not recalculate support candidates or grow a provenance chain.
        internal static void AppendRetainedConnections(QuantityReport old, ISet<string> keptIds,
            List<QuantityConnectionPassport> into)
        {
            if (keptIds.Count == 0) return;
            var scope = new HashSet<string>(StringComparer.Ordinal);
            foreach (var element in old.elements)
                if (keptIds.Contains(element.element_id))
                    foreach (string zone in element.zone_ids) scope.Add(zone);
            if (scope.Count == 0) return;
            if (old.connection_passports == null || old.connection_passports.Count == 0)
            {
                into.Add(UnavailableConnections(old.run_id + ":connections:legacy", old.run_id, scope,
                    "У сохранённого каркаса отсутствует паспорт соединений. Для его получения перестройте подсистему целиком."));
                return;
            }
            foreach (var passport in old.connection_passports)
            {
                bool complete = true;
                foreach (string zone in passport.zone_ids) if (!scope.Contains(zone)) complete = false;
                foreach (var member in passport.members)
                {
                    if (!keptIds.Contains(member.rail_element_id)) complete = false;
                    foreach (var support in member.supports)
                        foreach (string id in support.bracket_element_ids) if (!keptIds.Contains(id)) complete = false;
                }
                if (complete) into.Add(passport);
                else into.Add(UnavailableConnections(passport.passport_id, passport.source_run_id, scope,
                    "Сохранена только часть каркаса прежнего паспорта соединений. Для нового паспорта перестройте подсистему целиком."));
            }
        }

        private static QuantityConnectionPassport UnavailableConnections(string id, string sourceRun, IEnumerable<string> zones, string reason)
        {
            var p = new QuantityConnectionPassport { passport_id = id, source_run_id = sourceRun, status = "unavailable", reason = reason,
                scheme = "vertical", zone_ids = new List<string>(zones) };
            p.zone_ids.Sort(StringComparer.Ordinal);
            return p;
        }
        private static string ElementId(string run, string category, int index)
        { return run + ":" + category + ":" + index.ToString(CultureInfo.InvariantCulture); }
        private static Dictionary<string, object> Object(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new InvalidOperationException("Некорректный объект паспорта соединений.");
            return result;
        }
        private static int Integer(object value)
        {
            if (value is bool || value is string) throw new InvalidOperationException("Индекс паспорта соединений должен быть числом.");
            double number = Number(value);
            if (number < int.MinValue || number > int.MaxValue || number != Math.Floor(number))
                throw new InvalidOperationException("Некорректный целочисленный индекс или счётчик паспорта соединений.");
            return (int)number;
        }
        private static int Index(object value, int count)
        {
            int index = Integer(value);
            if (index < 0 || index >= count) throw new InvalidOperationException("Ссылка паспорта соединений выходит за состав каркаса.");
            return index;
        }

        internal static void AppendRetainedProvenance(QuantityReport old, ISet<string> keptIds, List<object> into)
        {
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var inherited = Get(old.parameters, "retained_sources") as IEnumerable;
            if (inherited != null && !(inherited is string))
                foreach (object value in inherited)
                {
                    var source = value as Dictionary<string, object>;
                    var ids = Get(source, "element_ids") as IEnumerable;
                    if (source == null || ids == null) continue;
                    var active = new List<string>();
                    foreach (object idValue in ids)
                    {
                        string id = Text(idValue);
                        if (keptIds.Contains(id) && claimed.Add(id)) active.Add(id);
                    }
                    if (active.Count == 0) continue;
                    var copy = new Dictionary<string, object>(source);
                    copy["element_ids"] = active;
                    into.Add(copy);
                }
            var own = new List<string>();
            foreach (string id in keptIds) if (!claimed.Contains(id)) own.Add(id);
            if (own.Count == 0) return;
            own.Sort(StringComparer.Ordinal);
            var parameters = new Dictionary<string, object>();
            foreach (var parameter in old.parameters)
                if (parameter.Key != "retained_sources") parameters[parameter.Key] = parameter.Value;
            into.Add(new Dictionary<string, object> {
                { "report_id", old.report_id }, { "run_id", old.run_id }, { "element_ids", own },
                { "algorithm", old.algorithm }, { "engineering_coverage", old.engineering_coverage },
                { "source_revisions", old.source_revisions }, { "parameters", parameters },
                { "design_scope", Get(old.engine_summary, "design_scope") },
                { "calc_report", Get(old.engine_summary, "calc_report") },
                { "calc_reports", Get(old.engine_summary, "calc_reports") } });
        }

        private static string ProfileKey(string value)
        {
            const string latin = "ABCEHKMOPTX", cyrillic = "АВСЕНКМОРТХ";
            var result = new System.Text.StringBuilder();
            foreach (char original in (value ?? "").ToUpperInvariant())
            {
                if (char.IsWhiteSpace(original)) continue;
                int at = latin.IndexOf(original);
                char c = at < 0 ? original : cyrillic[at];
                if (c == '\u2010' || c == '\u2011' || c == '\u2012' || c == '\u2013' ||
                    c == '\u2014' || c == '\u2212') c = '-';
                result.Append(c);
            }
            // Keep dimension delimiters: 1,2 is not 12, and 4-040 is not
            // 40-40. Only a decimal comma between digits equals a decimal dot.
            for (int i = 1; i + 1 < result.Length; i++)
                if (result[i] == ',' && char.IsDigit(result[i - 1]) && char.IsDigit(result[i + 1])) result[i] = '.';
            return result.ToString();
        }

        private static object Get(Dictionary<string, object> source, string key)
        { object value; return source != null && source.TryGetValue(key, out value) ? value : null; }
        private static object[] Array(object value)
        {
            var array = value as object[];
            if (array == null) throw new InvalidOperationException("Отсутствует явный массив состава подсистемы.");
            return array;
        }
        private static string Text(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
        private static string Root(string id, Dictionary<string, string> mapping)
        { string root; return mapping.TryGetValue(id, out root) ? root : id; }
        private static double Number(object value)
        {
            if (value == null) throw new InvalidOperationException("Отсутствует числовое свойство элемента.");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("Некорректное числовое свойство элемента.");
            return number;
        }
        private static string FileHash(string path)
        {
            if (!File.Exists(path)) return "unavailable";
            using (var file = File.OpenRead(path)) using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
    }
}
