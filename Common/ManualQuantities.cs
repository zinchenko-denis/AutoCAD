using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FacadeSafety
{
    public sealed class ManualQuantityField
    {
        public string source { get; set; } // literal | attribute | dynamic
        public string name { get; set; }
        public string value { get; set; }
    }

    public sealed class ManualQuantityRule
    {
        public string schema { get; set; } = "facade_manual_rule/1";
        public string rule_id { get; set; }
        public string revision { get; set; }
        public string kind { get; set; }
        public string role { get; set; }
        public string adapter { get; set; } // boundary | linear | symbol
        public string axis { get; set; } // X | Y, only for linear blocks
        public string geometry_unit { get; set; } = "mm";
        public bool one_physical_part { get; set; }
        public string sample_key { get; set; }
        public Dictionary<string, ManualQuantityField> fields { get; set; } = new Dictionary<string, ManualQuantityField>();
    }

    public sealed class ManualQuantityObservation
    {
        public string handle { get; set; }
        public string entity_type { get; set; }
        public string layer { get; set; }
        public string sample_key { get; set; }
        public string effective_block_name { get; set; }
        public string symbol_key { get; set; }
        public string fingerprint { get; set; }
        public string cad_fingerprint { get; set; }
        public string color { get; set; }
        public bool is_block { get; set; }
        public bool generated_conflict { get; set; }
        public Dictionary<string, string> attributes { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, object> dynamic_properties { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> dynamic_units { get; set; } = new Dictionary<string, string>();
        public double[][] boundary_points { get; set; }
        public double[] line_start { get; set; }
        public double[] line_end { get; set; }
        public double[] axis_x_start { get; set; }
        public double[] axis_x_end { get; set; }
        public double[] axis_y_start { get; set; }
        public double[] axis_y_end { get; set; }
        public double? scale_x { get; set; }
        public double? scale_y { get; set; }
        public double? scale_z { get; set; }
        public string boundary_reason { get; set; }
        public string linear_reason { get; set; }
        public string symbol_reason { get; set; }
        public string extraction_reason { get; set; }
        public double[] position { get; set; }
        public double? rotation { get; set; }
    }

    public sealed class ManualQuantityEvaluation
    {
        public string handle { get; set; }
        public string status { get; set; } // accepted | unrecognized | rejected | conflict
        public string reason { get; set; }
        public string duplicate_key { get; set; }
        public QuantityElement element { get; set; }
        public ManualQuantityObservation observation { get; set; }
        public List<QuantityIssue> issues { get; set; } = new List<QuantityIssue>();
    }

    public sealed class ManualQuantityPreview
    {
        public ManualQuantityRule rule { get; set; }
        public string zone_id { get; set; }
        public List<ManualQuantityEvaluation> items { get; set; } = new List<ManualQuantityEvaluation>();
        public List<QuantityIssue> issues { get; set; } = new List<QuantityIssue>();
        public int accepted_count { get; set; }
        public int rejected_count { get; set; }
        public int repeated_count { get; set; }
        public string fingerprint { get; set; }
        public ManualQuantityDiagnostics diagnostics { get; set; } = new ManualQuantityDiagnostics();
    }

    public sealed class ManualQuantityDiagnostics
    {
        public long observations_seen { get; set; }
        public long observations_evaluated { get; set; }
        public long fields_read { get; set; }
        public long shapes_validated { get; set; }
        public long duplicate_keys_built { get; set; }
        public long duplicate_buckets { get; set; }
    }

    /// <summary>Prepared immutable rule snapshot. Reuse only inside one read operation; no CAD or global cache.</summary>
    public sealed class ManualQuantityEvaluationContext
    {
        internal readonly ManualQuantityRule Rule;
        internal readonly string Reason, IdentityGroup;
        internal readonly Dictionary<string, string> WarningMessages = new Dictionary<string, string>(StringComparer.Ordinal);
        public string rule_fingerprint { get; private set; }
        internal ManualQuantityEvaluationContext(ManualQuantityRule rule, string reason, string identityGroup, string fingerprint)
        { Rule = rule; Reason = reason; IdentityGroup = identityGroup; rule_fingerprint = fingerprint; }
    }

    public static class ManualQuantitiesCore
    {
        private static readonly HashSet<string> TextFields = new HashSet<string>(new[] {
            "mark", "type", "material", "coating", "system", "color", "orientation", "piece_kind" }, StringComparer.Ordinal);
        private static readonly HashSet<string> NumberFields = new HashSet<string>(new[] { "width", "height", "length" }, StringComparer.Ordinal);

        public static ManualQuantityEvaluationContext Prepare(ManualQuantityRule rule)
        {
            var snapshot = CopyRule(rule);
            return new ManualQuantityEvaluationContext(snapshot, RuleReason(snapshot), "manual1:" + Hash(RuleKey(snapshot, false)), RuleFingerprint(snapshot));
        }

        public static ManualQuantityEvaluation Evaluate(ManualQuantityRule rule, ManualQuantityObservation observation, string zoneId)
        { return Evaluate(rule, observation, zoneId, new ManualQuantityDiagnostics()); }
        public static ManualQuantityEvaluation Evaluate(ManualQuantityRule rule, ManualQuantityObservation observation, string zoneId,
            ManualQuantityDiagnostics diagnostics)
        { return Evaluate(Prepare(rule), observation, zoneId, diagnostics); }
        public static ManualQuantityEvaluation Evaluate(ManualQuantityEvaluationContext context, ManualQuantityObservation observation, string zoneId)
        { return Evaluate(context, observation, zoneId, new ManualQuantityDiagnostics()); }
        public static ManualQuantityEvaluation Evaluate(ManualQuantityEvaluationContext context, ManualQuantityObservation observation, string zoneId,
            ManualQuantityDiagnostics diagnostics)
        {
            context = context ?? Prepare(null);
            return EvaluateCore(context.Rule, observation, zoneId, diagnostics ?? new ManualQuantityDiagnostics(), context.Reason,
                context.IdentityGroup, context.WarningMessages);
        }

        private static ManualQuantityEvaluation EvaluateCore(ManualQuantityRule rule, ManualQuantityObservation observation, string zoneId,
            ManualQuantityDiagnostics diagnostics, string ruleReason, string identityGroup, Dictionary<string, string> warningMessages)
        {
            diagnostics.observations_evaluated++;
            var result = new ManualQuantityEvaluation { handle = observation == null ? null : observation.handle, observation = observation };
            if (observation == null) return Reject(result, "rejected", "Нет проверяемых данных объекта.");
            if (observation.generated_conflict)
                return Reject(result, "conflict", "Объект содержит метку результата программы. Его нельзя переоформить как ручной для обхода проверки.");
            string reason = ruleReason;
            if (reason != null) return Reject(result, "rejected", reason);
            if (Empty(zoneId)) return Reject(result, "rejected", "Не указана проверенная зона ручной детали.");
            if (!Empty(observation.extraction_reason)) return Reject(result, "rejected", observation.extraction_reason);
            if (Empty(observation.handle) || Empty(observation.fingerprint) || Empty(observation.cad_fingerprint))
                return Reject(result, "rejected", "У объекта нет проверяемой идентичности и отпечатка чертежа.");
            if (rule.sample_key != observation.sample_key)
                return Reject(result, "unrecognized", "Объект не соответствует выбранному образцу. Назначьте ему отдельную схему свойств.");
            if (observation.is_block && (!ValidScale(observation.scale_x) || !ValidScale(observation.scale_y) || !ValidScale(observation.scale_z)))
                return Reject(result, "rejected", "Масштаб блока отсутствует, равен нулю или не является конечным числом.");

            var e = new QuantityElement { element_id = "manual:" + observation.handle.ToUpperInvariant(),
                zone_id = zoneId, zone_ids = new List<string> { zoneId }, role = rule.role,
                origin = "manual:" + rule.adapter, identity_group = identityGroup,
                color = observation.color, product_id = null,
                cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity { handle = observation.handle,
                    role = "primary", fingerprint = observation.cad_fingerprint } } };
            var numbers = new Dictionary<string, double>(StringComparer.Ordinal);
            var missingFields = new List<string>();
            foreach (var field in rule.fields ?? new Dictionary<string, ManualQuantityField>())
            {
                diagnostics.fields_read++;
                object raw;
                if (!Read(field.Value, observation, out raw))
                {
                    if (NumberFields.Contains(field.Key))
                        return Reject(result, "rejected", "Не найдено числовое свойство «" + field.Value.name + "» для сверки «" + field.Key + "».");
                    SetText(e, field.Key, null);
                    missingFields.Add("«" + field.Key + "» (свойство «" + field.Value.name + "»)");
                    continue;
                }
                if (NumberFields.Contains(field.Key))
                {
                    string unit;
                    if (field.Value.source == "dynamic" && (observation.dynamic_units == null ||
                        !observation.dynamic_units.TryGetValue(field.Value.name, out unit) || unit != "Distance"))
                        return Reject(result, "rejected", "Динамическое свойство «" + field.Value.name + "» не подтверждено как линейный размер Distance.");
                    double n;
                    if (!TryNumber(raw, out n) || n <= 0)
                        return Reject(result, "rejected", "Свойство «" + field.Key + "» должно содержать положительный размер в миллиметрах.");
                    numbers.Add(field.Key, n);
                }
                else
                {
                    string text;
                    if (!TryText(raw, out text))
                        return Reject(result, "rejected", "Свойство «" + field.Key + "» имеет неподдержанный составной тип значения.");
                    SetText(e, field.Key, text);
                    if (text == null && field.Value.source != "literal")
                        missingFields.Add("«" + field.Key + "» (пустое свойство «" + field.Value.name + "»)");
                }
            }
            if (rule.adapter == "boundary")
            {
                if (!Empty(observation.boundary_reason)) return Reject(result, "rejected", observation.boundary_reason);
                QuantityShape shape;
                var rings = new[] { new QuantityRing { role = "outer", points = observation.boundary_points } };
                diagnostics.shapes_validated++;
                if (!FacadeQuantitiesCore.TryShape(rings, out shape, out reason))
                    return Reject(result, "rejected", "Форма облицовки не подтверждена: " + reason + ".");
                e.rings.Add(rings[0]); e.shape_id = shape.shape_id; e.area_mm2 = shape.area_mm2;
                e.width_mm = shape.width_mm; e.height_mm = shape.height_mm;
                result.duplicate_key = BoundaryDuplicateKey(rule.kind, rule.role, observation);
                double width = shape.width_mm, height = shape.height_mm;
                if (observation.is_block)
                {
                    if (numbers.ContainsKey("width") && !Distance(observation.axis_x_start, observation.axis_x_end, out width))
                        return Reject(result, "rejected", "Нет проверенной геометрической оси X для сверки ширины блока.");
                    if (numbers.ContainsKey("height") && !Distance(observation.axis_y_start, observation.axis_y_end, out height))
                        return Reject(result, "rejected", "Нет проверенной геометрической оси Y для сверки высоты блока.");
                }
                if (!Matches(numbers, "width", width, observation.is_block ? Math.Abs(observation.scale_x.Value) : 1) ||
                    !Matches(numbers, "height", height, observation.is_block ? Math.Abs(observation.scale_y.Value) : 1))
                    return Reject(result, "rejected", "Явно выбранная ширина или высота не соответствует фактической геометрии облицовки.");
            }
            else if (rule.adapter == "linear")
            {
                if (!Empty(observation.linear_reason)) return Reject(result, "rejected", observation.linear_reason);
                if (observation.is_block && rule.axis != "X" && rule.axis != "Y")
                    return Reject(result, "rejected", "Для линейного блока явно выберите геометрическую ось X или Y.");
                if (observation.is_block && !numbers.ContainsKey("length"))
                    return Reject(result, "rejected", "Для линейного блока явно укажите свойство или значение длины и ось X/Y.");
                double[] start, end; Endpoints(observation, rule.axis, out start, out end);
                double length;
                if (!Distance(start, end, out length))
                    return Reject(result, "rejected", "Не подтверждены два конца физического линейного элемента.");
                double scale = !observation.is_block ? 1 : Math.Abs(rule.axis == "X" ? observation.scale_x.Value : observation.scale_y.Value);
                if (!Matches(numbers, "length", length, scale))
                    return Reject(result, "rejected", "Явно выбранная длина не соответствует фактической геометрии элемента.");
                e.length_mm = length;
            }
            else
            {
                if (!observation.is_block || !rule.one_physical_part)
                    return Reject(result, "rejected", "Для штучного образца явно подтвердите: один блок обозначает одну физическую деталь, а не узел.");
                if (!Empty(observation.symbol_reason)) return Reject(result, "rejected", observation.symbol_reason);
            }
            result.element = e; result.status = "accepted";
            result.reason = "Ручная деталь; геометрический учёт, без подтверждения каталога и инженерного расчёта.";
            if (missingFields.Count > 0)
            {
                missingFields.Sort(StringComparer.Ordinal);
                string pattern = Tokens(missingFields.ToArray()), message;
                if (warningMessages == null || !warningMessages.TryGetValue(pattern, out message))
                {
                    message = "Не прочитаны поля: " + string.Join(", ", missingFields.ToArray()) + ". Значения оставлены неизвестными.";
                    if (warningMessages != null) warningMessages.Add(pattern, message);
                }
                result.issues.Add(new QuantityIssue { code = "MQ_FIELD_UNKNOWN", element_id = e.element_id, element_count = 1, message = message });
            }
            diagnostics.duplicate_keys_built++;
            if (rule.adapter != "boundary") result.duplicate_key = DuplicateKey(rule.kind, rule.role, rule.adapter, rule.axis, observation);
            return result;
        }

        public static ManualQuantityPreview BuildPreview(ManualQuantityRule rule, IEnumerable<ManualQuantityObservation> observations, string zoneId)
        {
            var prepared = Prepare(rule);
            var result = new ManualQuantityPreview { rule = CopyRule(prepared.Rule), zone_id = zoneId };
            var seen = new Dictionary<string, ManualQuantityEvaluation>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in observations ?? new ManualQuantityObservation[0])
            {
                result.diagnostics.observations_seen++;
                ManualQuantityEvaluation previous;
                if (o != null && !Empty(o.handle) && seen.TryGetValue(o.handle, out previous))
                {
                    result.repeated_count++;
                    if (!ReferenceEquals(previous.observation, o) && ObservationKey(previous.observation) != ObservationKey(o))
                    {
                        Reject(previous, "conflict", "Один объект получен с разными данными. Повторите чтение чертежа.");
                        previous.duplicate_key = null; previous.issues.Clear();
                    }
                    continue;
                }
                var item = Evaluate(prepared, o, zoneId, result.diagnostics);
                result.items.Add(item);
                if (o != null && !Empty(o.handle)) seen.Add(o.handle, item);
            }
            var groups = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var warnings = new Dictionary<string, QuantityIssue>(StringComparer.Ordinal);
            var snapshot = new List<string>();
            foreach (var item in result.items)
            {
                if (item.status == "accepted") result.accepted_count++; else result.rejected_count++;
                foreach (var issue in item.issues)
                {
                    string key = Tokens(issue.code, issue.message, issue.severity);
                    QuantityIssue aggregate;
                    if (!warnings.TryGetValue(key, out aggregate))
                    {
                        aggregate = new QuantityIssue { code = issue.code, message = issue.message, severity = issue.severity };
                        warnings.Add(key, aggregate); result.issues.Add(aggregate);
                    }
                    aggregate.element_count += issue.element_count;
                    if (issue.element_id != null) aggregate.element_ids.Add(issue.element_id);
                    aggregate.element_id = aggregate.element_count == 1 ? issue.element_id : null;
                }
                snapshot.Add(Tokens(item.handle, item.status, item.reason, item.observation == null ? null : item.observation.fingerprint,
                    item.observation == null ? null : item.observation.cad_fingerprint, item.duplicate_key));
                if (item.status != "accepted" || Empty(item.duplicate_key)) continue;
                List<string> ids;
                if (!groups.TryGetValue(item.duplicate_key, out ids)) { ids = new List<string>(); groups.Add(item.duplicate_key, ids); }
                ids.Add(item.element.element_id);
            }
            result.diagnostics.duplicate_buckets = groups.Count;
            foreach (var warning in warnings.Values) warning.element_ids.Sort(StringComparer.Ordinal);
            foreach (var group in groups.Values)
                if (group.Count > 1)
                {
                    group.Sort(StringComparer.Ordinal);
                    result.issues.Add(new QuantityIssue { code = "MQ_DUPLICATE_POSSIBLE", element_ids = group,
                        element_count = group.Count, message = "Возможное совпадение ручных деталей: " + group.Count.ToString(CultureInfo.InvariantCulture) +
                            ". Все отдельные объекты учтены; автоматическое удаление не выполнялось." });
                }
            if (result.repeated_count > 0)
                result.issues.Add(new QuantityIssue { code = "MQ_SELECTION_REPEATED", severity = "info", element_count = result.repeated_count,
                    message = "Повторные ссылки на те же объекты обработаны один раз." });
            snapshot.Sort(StringComparer.Ordinal);
            snapshot.Insert(0, Tokens(prepared.rule_fingerprint, zoneId));
            result.fingerprint = HashParts(snapshot);
            return result;
        }

        public static string RuleFingerprint(ManualQuantityRule rule)
        { return Hash(RuleKey(rule, true)); }

        public static string DuplicateKey(string kind, string role, ManualQuantityObservation observation)
        { return DuplicateKey(kind, role, kind == "cladding" ? "boundary" : IsLinear(role) ? "linear" : "symbol", null, observation); }

        public static string DuplicateKey(string kind, string role, string adapter, string axis, ManualQuantityObservation o)
        {
            if (o == null || (kind != "cladding" && kind != "frame") || Empty(role)) return null;
            if (adapter == "boundary")
            {
                QuantityShape shape; string reason;
                if (!FacadeQuantitiesCore.TryShape(new[] { new QuantityRing { points = o.boundary_points } }, out shape, out reason)) return null;
                return BoundaryDuplicateKey(kind, role, o);
            }
            if (adapter == "linear")
            {
                double[] start, end; Endpoints(o, axis, out start, out end);
                double length; if (!Distance(start, end, out length)) return null;
                string a = DuplicatePointKey(start), b = DuplicatePointKey(end);
                if (string.CompareOrdinal(a, b) > 0) { string t = a; a = b; b = t; }
                return "manual-duplicate1:" + Hash(Tokens(kind, role, "linear", a, b));
            }
            if (adapter == "symbol" && o.is_block && ValidPoint(o.position) && o.rotation.HasValue && Finite(o.rotation.Value) &&
                ValidScale(o.scale_x) && ValidScale(o.scale_y) && ValidScale(o.scale_z) && !Empty(o.sample_key))
                return "manual-duplicate1:" + Hash(Tokens(kind, role, "symbol", Empty(o.symbol_key) ? o.sample_key : o.symbol_key, DuplicatePointKey(o.position), Number(o.rotation.Value),
                    Number(o.scale_x.Value), Number(o.scale_y.Value), Number(o.scale_z.Value)));
            return null;
        }

        // Warning buckets only: snapping suppresses transform noise, but is not a
        // complete collision/proximity test. It never changes physical counts,
        // exact quantity grouping, declared-size verification, or stored geometry.
        private const double DuplicateCoordinateStepMm = 1e-6;
        private static string BoundaryDuplicateKey(string kind, string role, ManualQuantityObservation o)
        {
            if (o.boundary_points == null) return null;
            var points = new List<double[]>();
            foreach (var p in o.boundary_points)
            {
                if (p == null || p.Length != 2 || !Finite(p[0]) || !Finite(p[1])) return null;
                var point = new[] { CoordinateBucket(p[0]), CoordinateBucket(p[1]) };
                if (points.Count > 0 && SamePoint(points[points.Count - 1], point)) continue;
                while (points.Count > 1 && RedundantPoint(points[points.Count - 2], points[points.Count - 1], point))
                    points.RemoveAt(points.Count - 1);
                points.Add(point);
            }
            if (points.Count > 1 && SamePoint(points[0], points[points.Count - 1])) points.RemoveAt(points.Count - 1);
            bool changed = true;
            while (changed && points.Count > 2)
            {
                changed = false;
                if (RedundantPoint(points[points.Count - 1], points[0], points[1])) { points.RemoveAt(0); changed = true; }
                if (points.Count > 2 && RedundantPoint(points[points.Count - 2], points[points.Count - 1], points[0]))
                { points.RemoveAt(points.Count - 1); changed = true; }
            }
            var vertices = new List<string>();
            foreach (var p in points) vertices.Add(Tokens(Number(p[0]), Number(p[1])));
            // If snapping collapses an outline, omit its warning key. Exact
            // physical geometry above remains valid and its quantity is kept.
            if (vertices.Count < 3 || new HashSet<string>(vertices, StringComparer.Ordinal).Count != vertices.Count) return null;
            int start = 0;
            for (int i = 1; i < vertices.Count; i++) if (string.CompareOrdinal(vertices[i], vertices[start]) < 0) start = i;
            var forward = new StringBuilder(); var reverse = new StringBuilder();
            for (int i = 0; i < vertices.Count; i++)
            {
                forward.Append(vertices[(start + i) % vertices.Count]);
                reverse.Append(vertices[(start - i + vertices.Count) % vertices.Count]);
            }
            string a = forward.ToString(), b = reverse.ToString();
            return "manual-duplicate1:" + Hash(Tokens(kind, role, "boundary", string.CompareOrdinal(a, b) <= 0 ? a : b));
        }

        private static string RuleReason(ManualQuantityRule r)
        {
            if (r == null || r.schema != "facade_manual_rule/1" || Empty(r.rule_id) || Empty(r.revision) || Empty(r.sample_key))
                return "Схема ручного образца отсутствует, повреждена или имеет неподдержанную версию.";
            if (r.geometry_unit != "mm") return "Ручной импорт поддерживает геометрию в миллиметрах; неизвестные единицы не преобразуются автоматически.";
            if (r.kind == "cladding")
            { if (r.role != "cladding" || r.adapter != "boundary") return "Для облицовки требуется явная проверяемая граница детали."; }
            else if (r.kind == "frame")
            {
                if (IsLinear(r.role)) { if (r.adapter != "linear") return "Для направляющей или шины требуется линейная геометрия."; }
                else if (r.role == "bracket" || r.role == "clamp" || r.role == "fitting")
                { if (r.adapter != "symbol") return "Для выбранной штучной категории требуется образец одной детали."; }
                else return "Не распознана категория ручной детали подсистемы.";
            }
            else return "Не распознан вид ручной ведомости.";
            if (!Empty(r.axis) && r.axis != "X" && r.axis != "Y") return "Ось блока должна быть указана точно как X или Y.";
            if (r.fields != null)
                foreach (var f in r.fields)
                {
                    if (!TextFields.Contains(f.Key) && !NumberFields.Contains(f.Key)) return "Поле «" + f.Key + "» не поддержано; каталог и комплектация не назначаются автоматически.";
                    if (f.Value == null || (f.Value.source != "literal" && f.Value.source != "attribute" && f.Value.source != "dynamic") ||
                        (f.Value.source != "literal" && Empty(f.Value.name))) return "У поля «" + f.Key + "» нет точного источника значения.";
                    if ((f.Key == "length" && r.adapter != "linear") || ((f.Key == "width" || f.Key == "height") && r.adapter != "boundary"))
                        return "Размер «" + f.Key + "» не относится к выбранному способу измерения.";
                }
            return null;
        }

        private static bool Read(ManualQuantityField f, ManualQuantityObservation o, out object value)
        {
            value = null;
            if (f.source == "literal") { value = f.value; return true; }
            if (f.source == "attribute") { string s; if (o.attributes != null && o.attributes.TryGetValue(f.name, out s)) { value = s; return true; } return false; }
            return o.dynamic_properties != null && o.dynamic_properties.TryGetValue(f.name, out value);
        }
        private static bool TryText(object value, out string text)
        {
            text = null; if (value == null) return true;
            if (!(value is string) && !(value is bool) && !(value is byte) && !(value is short) && !(value is int) && !(value is long) &&
                !(value is float) && !(value is double) && !(value is decimal)) return false;
            text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text)) text = null;
            return true;
        }
        private static bool TryNumber(object raw, out double value)
        {
            value = 0; string s;
            if (raw is bool || !TryText(raw, out s) || Empty(s)) return false;
            if (s.IndexOf(',') >= 0) { if (s.IndexOf('.') >= 0) return false; s = s.Replace(',', '.'); }
            return double.TryParse(s, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign |
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out value) && Finite(value);
        }
        private static void SetText(QuantityElement e, string key, string value)
        {
            switch (key) {
                case "mark": e.mark = value; break; case "type": e.type = value; break; case "material": e.material = value; break;
                case "coating": e.coating = value; break; case "system": e.system = value; break; case "color": e.color = value; break;
                case "orientation": e.orientation = value; break; case "piece_kind": e.piece_kind = value; break;
            }
        }
        private static ManualQuantityEvaluation Reject(ManualQuantityEvaluation r, string status, string reason)
        { r.status = status; r.reason = reason; r.element = null; r.duplicate_key = null; return r; }
        private static bool IsLinear(string role) { return role == "rail" || role == "hrail" || role == "shina"; }
        private static bool Empty(string value) { return string.IsNullOrWhiteSpace(value); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static bool ValidScale(double? value) { return value.HasValue && Finite(value.Value) && value.Value != 0; }
        private static bool ValidPoint(double[] p)
        { return p != null && (p.Length == 2 || p.Length == 3) && Finite(p[0]) && Finite(p[1]) && (p.Length == 2 || Finite(p[2])); }
        private static bool Distance(double[] a, double[] b, out double length)
        {
            length = 0; if (!ValidPoint(a) || !ValidPoint(b)) return false;
            double x = b[0] - a[0], y = b[1] - a[1], z = (b.Length == 3 ? b[2] : 0) - (a.Length == 3 ? a[2] : 0);
            length = Math.Sqrt(x * x + y * y + z * z); return Finite(length) && length > 0;
        }
        private static void Endpoints(ManualQuantityObservation o, string axis, out double[] start, out double[] end)
        {
            if (!o.is_block || Empty(axis)) { start = o.line_start; end = o.line_end; }
            else if (axis == "X") { start = o.axis_x_start; end = o.axis_x_end; }
            else { start = o.axis_y_start; end = o.axis_y_end; }
        }
        private static bool Matches(Dictionary<string, double> numbers, string key, double geometry, double scale)
        { double value; return !numbers.TryGetValue(key, out value) || Math.Abs(value * scale - geometry) <= Math.Max(0.001, Math.Abs(geometry) * 1e-9); }
        private static string Number(double n) { return (n == 0 ? 0 : n).ToString("R", CultureInfo.InvariantCulture); }
        private static double CoordinateBucket(double n) { return Math.Round(n / DuplicateCoordinateStepMm, MidpointRounding.AwayFromZero); }
        private static double SnapCoordinate(double n) { return CoordinateBucket(n) * DuplicateCoordinateStepMm; }
        private static bool SamePoint(double[] a, double[] b) { return a[0] == b[0] && a[1] == b[1]; }
        private static bool RedundantPoint(double[] a, double[] b, double[] c)
        {
            return (b[0] - a[0]) * (c[1] - a[1]) == (b[1] - a[1]) * (c[0] - a[0]) &&
                b[0] >= Math.Min(a[0], c[0]) && b[0] <= Math.Max(a[0], c[0]) &&
                b[1] >= Math.Min(a[1], c[1]) && b[1] <= Math.Max(a[1], c[1]);
        }
        private static string DuplicatePointKey(double[] p)
        { return Tokens(Number(SnapCoordinate(p[0])), Number(SnapCoordinate(p[1])), Number(p.Length == 3 ? SnapCoordinate(p[2]) : 0)); }
        private static string RuleKey(ManualQuantityRule r, bool ids)
        {
            if (r == null) return "null";
            var parts = new List<string>();
            if (r.fields != null) foreach (var f in r.fields)
                parts.Add(Tokens(f.Key, f.Value == null ? null : f.Value.source, f.Value == null ? null : f.Value.name, f.Value == null ? null : f.Value.value));
            parts.Sort(StringComparer.Ordinal);
            return Tokens(r.schema, ids ? r.rule_id : null, ids ? r.revision : null, r.kind, r.role, r.adapter, r.axis,
                r.geometry_unit, r.one_physical_part.ToString(), r.sample_key, Tokens(parts.ToArray()));
        }
        private static ManualQuantityRule CopyRule(ManualQuantityRule r)
        {
            if (r == null) return null;
            var copy = new ManualQuantityRule { schema = r.schema, rule_id = r.rule_id, revision = r.revision, kind = r.kind, role = r.role,
                adapter = r.adapter, axis = r.axis, geometry_unit = r.geometry_unit, one_physical_part = r.one_physical_part, sample_key = r.sample_key,
                fields = r.fields == null ? null : new Dictionary<string, ManualQuantityField>(StringComparer.Ordinal) };
            if (r.fields != null) foreach (var field in r.fields) copy.fields.Add(field.Key, field.Value == null ? null :
                new ManualQuantityField { source = field.Value.source, name = field.Value.name, value = field.Value.value });
            return copy;
        }
        private static string ObservationKey(ManualQuantityObservation o)
        {
            if (o == null) return "null";
            var parts = new List<string>();
            if (o.attributes != null) foreach (var p in o.attributes) parts.Add(Tokens("attribute", p.Key, p.Value));
            if (o.dynamic_properties != null) foreach (var p in o.dynamic_properties) parts.Add(Tokens("dynamic", p.Key, Convert.ToString(p.Value, CultureInfo.InvariantCulture)));
            if (o.dynamic_units != null) foreach (var p in o.dynamic_units) parts.Add(Tokens("units", p.Key, p.Value));
            parts.Sort(StringComparer.Ordinal);
            if (o.boundary_points != null) foreach (var p in o.boundary_points) parts.Add(ArrayKey(p));
            return Tokens(o.handle, o.entity_type, o.layer, o.sample_key, o.effective_block_name, o.symbol_key, o.fingerprint, o.cad_fingerprint, o.color,
                o.is_block.ToString(), o.generated_conflict.ToString(), o.extraction_reason, o.boundary_reason, o.linear_reason, o.symbol_reason,
                ArrayKey(o.line_start), ArrayKey(o.line_end), ArrayKey(o.axis_x_start), ArrayKey(o.axis_x_end), ArrayKey(o.axis_y_start), ArrayKey(o.axis_y_end),
                o.scale_x.HasValue ? Number(o.scale_x.Value) : null, o.scale_y.HasValue ? Number(o.scale_y.Value) : null,
                o.scale_z.HasValue ? Number(o.scale_z.Value) : null, ArrayKey(o.position), o.rotation.HasValue ? Number(o.rotation.Value) : null, Tokens(parts.ToArray()));
        }
        private static string ArrayKey(double[] values) { return values == null ? null : Tokens(Array.ConvertAll(values, Number)); }
        private static string Tokens(params string[] values)
        { var s = new StringBuilder(); foreach (var v in values) { if (v == null) s.Append("-1:"); else s.Append(v.Length).Append(':').Append(v); } return s.ToString(); }
        private static string Hash(string value)
        { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        private static string HashParts(IEnumerable<string> parts)
        {
            using (var sha = SHA256.Create())
            {
                foreach (var part in parts)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(Tokens(part));
                    sha.TransformBlock(bytes, 0, bytes.Length, bytes, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
