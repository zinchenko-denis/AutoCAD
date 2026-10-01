using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AFramePlugin
{
    public sealed class FrameNodeGeometryException : InvalidOperationException
    {
        public string Code { get; private set; }
        public FrameNodeGeometryException(string code, string message) : base(message) { Code = code; }
    }

    internal static class FrameNodeJson
    {
        internal static void Fail(string code, string message) { throw new FrameNodeGeometryException(code, message); }
        internal static Dictionary<string, object> Object(object value, params string[] keys)
        {
            var d = value as Dictionary<string, object>;
            if (d == null || d.Count != keys.Length || keys.Any(k => !d.ContainsKey(k)))
                Fail("E_NODE_SCHEMA", "Неполный или неизвестный состав полей схемы узла.");
            return d;
        }
        internal static void Equal(object value, string expected)
        { if (!(value is string) || (string)value != expected) Fail("E_NODE_SCHEMA", "Неизвестная схема, единицы или база узла."); }
        internal static double? Coordinate(object value, bool zero)
        {
            if (value == null) return null;
            if (!FrameParameterJson.Numeric(value)) Fail("E_NODE_INPUT", "Координата должна быть явно заданным конечным числом.");
            // Mono's decimal->double conversion can land one ULP away from
            // parsing the same exact decimal text. Preserve JSON roundtrips.
            double n = value is decimal ? double.Parse(((decimal)value).ToString("G29", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture) : Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsInfinity(n) || double.IsNaN(n) || n < 0 || (!zero && n == 0))
                Fail("E_NODE_INPUT", "Координата должна быть конечной и " + (zero ? "неотрицательной." : "положительной."));
            if (!(value is double) && !(value is float))
            {
                string original = value is decimal ? ((decimal)value).ToString("G29", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture);
                if (Normalized(original) != Normalized(n.ToString("R", CultureInfo.InvariantCulture)))
                    Fail("E_NODE_NUMERIC_PRECISION", "Исходное число " + original + " не сохраняется точно в координате схемы; скрытое округление запрещено.");
            }
            return n;
        }
        internal static string Text(decimal value) { return value.ToString("G29", CultureInfo.InvariantCulture); }
        // CLR decimal casts can differ by one ULP from parsing the exact text.
        // Every numeric export and collision check must use this same boundary.
        internal static double CadNumber(decimal value)
        { return double.Parse(Text(value), NumberStyles.Float, CultureInfo.InvariantCulture); }
        // Compare decimal values as normalized digit/exponent strings. Decimal
        // TryParse alone may silently round; a double roundtrip alone misses it.
        internal static string Normalized(string text)
        {
            text = text.ToUpperInvariant(); int e = text.IndexOf('E');
            int exponent = e < 0 ? 0 : int.Parse(text.Substring(e + 1), CultureInfo.InvariantCulture);
            string mantissa = e < 0 ? text : text.Substring(0, e); bool negative = mantissa.StartsWith("-", StringComparison.Ordinal);
            mantissa = mantissa.TrimStart('+', '-'); int point = mantissa.IndexOf('.');
            int scale = (point < 0 ? 0 : mantissa.Length - point - 1) - exponent;
            string digits = mantissa.Replace(".", "").TrimStart('0');
            if (digits.Length == 0) return "0";
            int end = digits.Length; while (end > 1 && digits[end - 1] == '0') { end--; scale--; }
            return (negative ? "-" : "") + digits.Substring(0, end) + "@" + scale.ToString(CultureInfo.InvariantCulture);
        }
        internal static decimal Exact(double value)
        {
            string text = value.ToString("R", CultureInfo.InvariantCulture); double back;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out back) || back != value)
                Fail("E_NODE_NUMERIC_PRECISION", "Платформа не сохраняет исходное число при десятичном преобразовании: " + text + ".");
            decimal parsed;
            if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                Fail("E_NODE_NUMERIC_RANGE", "Число " + text + " вне технического диапазона точной десятичной схемы; это не конструктивное ограничение.");
            if (value != 0 && parsed == 0)
                Fail("E_NODE_NUMERIC_RANGE", "Число " + text + " меньше ненулевого технического диапазона десятичной схемы; округление до нуля не выполняется.");
            if (Normalized(text) != Normalized(Text(parsed)))
                Fail("E_NODE_NUMERIC_PRECISION", "Число " + text + " невозможно сохранить точно в десятичной схеме; округление не выполняется.");
            return parsed;
        }
        internal static FrameSolutionSelection CopySelection(FrameSolutionSelection selection)
        {
            FrameParameterJson.Selection(selection);
            // Copy typed values directly: legacy JSON cloning can round tiny
            // numeric tokens during deserialization before we examine them.
            foreach (double layer in selection.geometry.insulation_layers_mm) Exact(layer);
            if (selection.geometry.cladding_front_offset_mm.HasValue) Exact(selection.geometry.cladding_front_offset_mm.Value);
            return new FrameSolutionSelection { schema=selection.schema, catalog_id=selection.catalog_id, catalog_revision=selection.catalog_revision,
                solution_id=selection.solution_id, source_id=selection.source_id, source_sha256=selection.source_sha256,
                node=new FrameSolutionNode { pdf_page=selection.node.pdf_page, sheet=selection.node.sheet },
                bracket=new FrameSolutionBracket { family_id=selection.bracket.family_id, execution=selection.bracket.execution, nominal_width_mm=selection.bracket.nominal_width_mm, L_mm=selection.bracket.L_mm },
                extender=new FrameSolutionExtender { family_id=selection.extender.family_id, execution=selection.extender.execution, nominal_width_mm=selection.extender.nominal_width_mm, L_mm=selection.extender.L_mm, thickness_mm=selection.extender.thickness_mm },
                profile=new FrameSolutionProfile { family_id=selection.profile.family_id, execution=selection.profile.execution, a_mm=selection.profile.a_mm, b_mm=selection.profile.b_mm, thickness_mm=selection.profile.thickness_mm, b_basis=selection.profile.b_basis, thickness_basis=selection.profile.thickness_basis },
                geometry=new FrameSolutionGeometry { cladding_front_offset_mm=selection.geometry.cladding_front_offset_mm, cladding_offset_basis=selection.geometry.cladding_offset_basis, insulation_layers_mm=new List<double>(selection.geometry.insulation_layers_mm) } };
        }
        internal static FrameSolutionSelection ReadSelection(object value)
        {
            // Reuse the unchanged strict legacy field/source validator, then
            // preserve numeric tokens through the node-specific exact parser.
            var result = FrameSolutionSelection.FromDict(value);
            if (result == null) Fail("E_NODE_SCHEMA", "Отсутствует выбранное решение узла.");
            var d = (Dictionary<string, object>)value;
            var b = (Dictionary<string, object>)d["bracket"]; result.bracket.nominal_width_mm = Coordinate(b["nominal_width_mm"], false).Value; result.bracket.L_mm = Coordinate(b["L_mm"], false).Value;
            var e = (Dictionary<string, object>)d["extender"]; result.extender.nominal_width_mm = Coordinate(e["nominal_width_mm"], false).Value; result.extender.L_mm = Coordinate(e["L_mm"], false).Value; result.extender.thickness_mm = Coordinate(e["thickness_mm"], false).Value;
            var p = (Dictionary<string, object>)d["profile"]; result.profile.a_mm = Coordinate(p["a_mm"], false).Value; result.profile.b_mm = Coordinate(p["b_mm"], false); result.profile.thickness_mm = Coordinate(p["thickness_mm"], false);
            var g = (Dictionary<string, object>)d["geometry"]; result.geometry.cladding_front_offset_mm = Coordinate(g["cladding_front_offset_mm"], false);
            result.geometry.insulation_layers_mm = new List<double>(); foreach (object layer in (IEnumerable)g["insulation_layers_mm"]) result.geometry.insulation_layers_mm.Add(Coordinate(layer, false).Value);
            return CopySelection(result);
        }
    }

    public sealed class FrameNodeClearanceSurface
    {
        public const string Unknown = "unknown", Layers = "insulation_outer_declared", Membrane = "membrane_outer_declared";
        public string kind = Unknown;
        public double? membrane_outer_x_mm;
    }
    public sealed class FrameNodeGeometryInput
    {
        public string schema = "aframe_node_geometry_input/1", unit = "mm", basis = "wall_structural_face_x0_outward_positive";
        public double? profile_near_face_x_mm;
        public FrameNodeClearanceSurface clearance_surface = new FrameNodeClearanceSurface();
        public static FrameNodeGeometryInput CreateDefault() { return new FrameNodeGeometryInput(); }
        public Dictionary<string, object> ToDict()
        {
            FrameNodeJson.Equal(schema, "aframe_node_geometry_input/1"); FrameNodeJson.Equal(unit, "mm");
            FrameNodeJson.Equal(basis, "wall_structural_face_x0_outward_positive");
            FrameNodeJson.Coordinate(profile_near_face_x_mm, false);
            if (clearance_surface == null) FrameNodeJson.Fail("E_NODE_INPUT", "Не указан режим поверхности просвета.");
            if (clearance_surface.kind != FrameNodeClearanceSurface.Unknown && clearance_surface.kind != FrameNodeClearanceSurface.Layers && clearance_surface.kind != FrameNodeClearanceSurface.Membrane)
                FrameNodeJson.Fail("E_NODE_INPUT", "Неизвестная поверхность проверки просвета.");
            if (clearance_surface.kind == FrameNodeClearanceSurface.Membrane)
            { if (!FrameNodeJson.Coordinate(clearance_surface.membrane_outer_x_mm, true).HasValue) FrameNodeJson.Fail("E_NODE_INPUT", "Задайте наружную координату мембраны."); }
            else if (clearance_surface.membrane_outer_x_mm.HasValue)
                FrameNodeJson.Fail("E_NODE_INPUT", "Координата мембраны допустима только при явном выборе её поверхности.");
            return new Dictionary<string, object> { { "schema", schema }, { "unit", unit }, { "basis", basis }, { "profile_near_face_x_mm", profile_near_face_x_mm },
                { "clearance_surface", new Dictionary<string, object> { { "kind", clearance_surface.kind }, { "membrane_outer_x_mm", clearance_surface.membrane_outer_x_mm } } } };
        }
        public static FrameNodeGeometryInput FromDict(object value)
        {
            var d = FrameNodeJson.Object(value, "schema", "unit", "basis", "profile_near_face_x_mm", "clearance_surface");
            var surface = FrameNodeJson.Object(d["clearance_surface"], "kind", "membrane_outer_x_mm");
            var result = new FrameNodeGeometryInput { schema = d["schema"] as string, unit = d["unit"] as string, basis = d["basis"] as string,
                profile_near_face_x_mm = FrameNodeJson.Coordinate(d["profile_near_face_x_mm"], false),
                clearance_surface = new FrameNodeClearanceSurface { kind = surface["kind"] as string, membrane_outer_x_mm = FrameNodeJson.Coordinate(surface["membrane_outer_x_mm"], true) } };
            result.ToDict(); return result;
        }
        public FrameNodeGeometryInput Clone() { return FromDict(ToDict()); }
    }

    public sealed class FrameNodePlane
    {
        public string Id { get; private set; } public string Title { get; private set; } public double XMm { get; private set; } public string Basis { get; private set; }
        internal FrameNodePlane(string id, string title, decimal x, string basis) { Id = id; Title = title; XMm = FrameNodeJson.CadNumber(x); Basis = basis; }
        internal Dictionary<string, object> ToDict() { return new Dictionary<string, object> { { "id", Id }, { "title", Title }, { "x_mm", XMm }, { "basis", Basis } }; }
    }
    public sealed class FrameNodeLayer
    {
        public int Index { get; private set; } public double StartXMm { get; private set; } public double EndXMm { get; private set; } public double ThicknessMm { get; private set; }
        internal FrameNodeLayer(int index, decimal start, decimal end, decimal thickness) { Index = index; StartXMm = FrameNodeJson.CadNumber(start); EndXMm = FrameNodeJson.CadNumber(end); ThicknessMm = FrameNodeJson.CadNumber(thickness); }
        internal Dictionary<string, object> ToDict() { return new Dictionary<string, object> { { "index", Index }, { "start_x_mm", StartXMm }, { "end_x_mm", EndXMm }, { "thickness_mm", ThicknessMm } }; }
    }
    public sealed class FrameNodeDimension
    {
        public string Id { get; private set; } public string Title { get; private set; } public double FromXMm { get; private set; } public double ToXMm { get; private set; }
        public double ValueMm { get; private set; } public string ValueText { get; private set; } public string Basis { get; private set; }
        internal FrameNodeDimension(string id, string title, decimal from, decimal to, decimal value, string basis)
        { Id = id; Title = title; FromXMm = FrameNodeJson.CadNumber(from); ToXMm = FrameNodeJson.CadNumber(to); ValueMm = FrameNodeJson.CadNumber(value); ValueText = FrameNodeJson.Text(value); Basis = basis; }
        internal Dictionary<string, object> ToDict() { return new Dictionary<string, object> { { "id", Id }, { "title", Title }, { "from_x_mm", FromXMm }, { "to_x_mm", ToXMm }, { "value_mm", ValueMm }, { "value_text", ValueText }, { "basis", Basis } }; }
    }
    public sealed class FrameNodeIssue
    {
        public string Code { get; private set; } public string Message { get; private set; } public string Severity { get; private set; }
        internal FrameNodeIssue(string code, string message, string severity) { Code = code; Message = message; Severity = severity; }
        internal Dictionary<string, object> ToDict() { return new Dictionary<string, object> { { "code", Code }, { "message", Message }, { "severity", Severity } }; }
    }
    public sealed class FrameNodeClearanceRule
    {
        public string SourceId { get; private set; } public string SourceSha256 { get; private set; } public int PdfPage { get; private set; }
        public string Sheet { get; private set; } public string From { get; private set; } public string To { get; private set; } public double MinimumMm { get; private set; }
        internal FrameNodeClearanceRule()
        {
            var catalog = FrameSolutionSelection.CatalogSnapshot(); var source = (Dictionary<string, object>)catalog["source"];
            var rule = (Dictionary<string, object>)((Dictionary<string, object>)catalog["solution"])["local_clearance"];
            SourceId = (string)source["source_id"]; SourceSha256 = (string)source["sha256"]; PdfPage = Convert.ToInt32(rule["pdf_page"]);
            Sheet = (string)rule["sheet"]; From = (string)rule["from"]; To = (string)rule["to"]; MinimumMm = Convert.ToDouble(rule["minimum_mm"], CultureInfo.InvariantCulture);
        }
        internal Dictionary<string, object> ToDict() { return new Dictionary<string, object> { { "source_id", SourceId }, { "source_sha256", SourceSha256 }, { "pdf_page", PdfPage }, { "sheet", Sheet }, { "from", From }, { "to", To }, { "minimum_mm", MinimumMm } }; }
    }
    public sealed class FrameNodeGeometryResult
    {
        public string Schema { get { return "aframe_node_geometry_result/1"; } }
        public string AlgorithmRevision { get { return FrameNodeGeometry.AlgorithmRevision; } }
        public string SelectionDigest { get; private set; } public string InputDigest { get; private set; } public string ResultDigest { get; private set; }
        public bool CanInsert { get; private set; } public string Status { get; private set; } public string ClearanceStatus { get; private set; }
        public double? GapMm { get; private set; } public string GapText { get; private set; }
        public double? InsulationOuterXMm { get; private set; } public double? CladdingFrontXMm { get; private set; }
        public double? ProfileNearFaceXMm { get; private set; } public double? ClearanceSurfaceXMm { get; private set; }
        public FrameNodeClearanceRule ClearanceRule { get; private set; }
        public ReadOnlyCollection<FrameNodePlane> Planes { get; private set; } public ReadOnlyCollection<FrameNodeLayer> Layers { get; private set; }
        public ReadOnlyCollection<FrameNodeDimension> Dimensions { get; private set; } public ReadOnlyCollection<FrameNodeIssue> Issues { get; private set; }
        public ReadOnlyCollection<string> Missing { get; private set; } public ReadOnlyCollection<string> Limitations { get; private set; }
        internal FrameNodeGeometryResult(string selectionDigest, string inputDigest, string clearanceStatus, decimal? gap,
            decimal? insulation, decimal? cladding, decimal? profile, decimal? surface, FrameNodeClearanceRule rule,
            List<FrameNodePlane> planes, List<FrameNodeLayer> layers, List<FrameNodeDimension> dimensions, List<FrameNodeIssue> issues, List<string> missing)
        {
            SelectionDigest = selectionDigest; InputDigest = inputDigest; ClearanceStatus = clearanceStatus;
            GapMm = Number(gap); GapText = gap.HasValue ? FrameNodeJson.Text(gap.Value) : null;
            InsulationOuterXMm = Number(insulation); CladdingFrontXMm = Number(cladding); ProfileNearFaceXMm = Number(profile); ClearanceSurfaceXMm = Number(surface); ClearanceRule = rule;
            Planes = planes.AsReadOnly(); Layers = layers.AsReadOnly(); Dimensions = dimensions.AsReadOnly(); Issues = issues.AsReadOnly(); Missing = missing.AsReadOnly();
            Limitations = Array.AsReadOnly(new[] { "Размерная схема заявленных плоскостей; не рабочий узел.", "Совместимость КР2–УК–ГП, регулировка и посадки не подтверждены.",
                "Автоподбор кронштейна, статический расчёт и спецификация крепежа не выполняются.", "Толщина облицовки, кляммер, анкеровка и положение полных тел изделий не определены." });
            CanInsert = !issues.Any(i => i.Severity == "error"); Status = !CanInsert ? "rejected" : clearanceStatus == "pass" ? "clearance_pass" : "partial";
            ResultDigest = FrameParameterJson.Hash(Content());
        }
        private static double? Number(decimal? n) { return n.HasValue ? (double?)FrameNodeJson.CadNumber(n.Value) : null; }
        private Dictionary<string, object> Content()
        { return new Dictionary<string, object> { { "schema", Schema }, { "algorithm_revision", AlgorithmRevision }, { "selection_digest", SelectionDigest }, { "input_digest", InputDigest },
            { "can_insert", CanInsert }, { "status", Status }, { "clearance_status", ClearanceStatus }, { "gap_mm", GapMm }, { "gap_text", GapText },
            { "insulation_outer_x_mm", InsulationOuterXMm }, { "cladding_front_x_mm", CladdingFrontXMm }, { "profile_near_face_x_mm", ProfileNearFaceXMm }, { "clearance_surface_x_mm", ClearanceSurfaceXMm },
            { "clearance_rule", ClearanceRule.ToDict() }, { "planes", Planes.Select(p => p.ToDict()).ToArray() }, { "layers", Layers.Select(p => p.ToDict()).ToArray() },
            { "dimensions", Dimensions.Select(p => p.ToDict()).ToArray() }, { "issues", Issues.Select(p => p.ToDict()).ToArray() }, { "missing", Missing.ToArray() }, { "limitations", Limitations.ToArray() } }; }
        public Dictionary<string, object> ToDict() { var d = Content(); d["result_digest"] = ResultDigest; return d; }
        public string ReviewText()
        {
            var sb = new StringBuilder("Размерная схема Вектор-1 2015, узел 4.2.1 / PDF 20.\nОснование x = 0; наружу — положительное направление. Размеры в мм.\n");
            foreach (var d in Dimensions) sb.AppendLine(d.Title + ": " + d.ValueText + " мм.");
            sb.AppendLine(ClearanceStatus == "not_evaluated" ? "Проверка локального просвета не выполнена." :
                "Локальный просвет: " + GapText + " мм; минимум " + FrameNodeGeometry.FormatMm(ClearanceRule.MinimumMm) + " мм — " + (ClearanceStatus == "pass" ? "соблюдён только по заявленным плоскостям." : "не соблюдён."));
            foreach (var issue in Issues) sb.AppendLine(issue.Message);
            foreach (string item in Missing) sb.AppendLine("Не задано: " + item);
            foreach (string item in Limitations) sb.AppendLine(item);
            if (!CanInsert) sb.AppendLine("Вставка схемы запрещена; данные DWG не изменяются.");
            return sb.ToString();
        }
    }

    public static class FrameNodeGeometry
    {
        public const string AlgorithmRevision = "vector1_2015_4_2_1_planes/2";
        private static readonly FrameNodeClearanceRule Rule = new FrameNodeClearanceRule();
        private static readonly decimal[] Powers = Enumerable.Range(0, 29).Select(Power).ToArray();
        private static decimal Power(int n) { decimal value = 1; for (int i = 0; i < n; i++) value *= 10; return value; }
        public static string FormatMm(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        public static FrameSolutionSelection CloneSelection(FrameSolutionSelection selection) { return FrameNodeJson.CopySelection(selection); }
        public static void ValidateBoundSelection(FrameSolutionSelection selection, FrameParameterContext context, object projectPayload, object zonePayload)
        {
            var project = FrameProjectParameters.FromDict(projectPayload); var zone = FrameZoneParameters.FromDict(zonePayload);
            if (project == null || zone == null || context == null || project.project_id != zone.project_id)
                FrameNodeJson.Fail("E_NODE_CONTEXT", "Требуется явная привязка к тому же проекту.");
            context.ToDict(); FrameParameterJson.Selection(selection);
            if (context.project.project_id != project.project_id || context.project.revision != project.revision || context.project.content_digest != project.content_digest ||
                context.zone.revision != zone.revision || context.zone.content_digest != zone.content_digest)
                FrameNodeJson.Fail("E_NODE_CONTEXT", "Происхождение узла не соответствует текущим параметрам проекта/зоны.");
            var p = (Dictionary<string, object>)projectPayload; var z = (Dictionary<string, object>)zonePayload;
            var exact = FrameNodeJson.ReadSelection(p["defaults"]).ToDict();
            foreach (var pair in (Dictionary<string, object>)z["overrides"])
            {
                var operation = (Dictionary<string, object>)pair.Value; string[] path = pair.Key.Split('.');
                ((Dictionary<string, object>)exact[path[0]])[path[1]] = (string)operation["state"] == "clear" ?
                    (pair.Key == "geometry.insulation_layers_mm" ? (object)new object[0] : null) : operation["value"];
            }
            var declared = FrameNodeJson.ReadSelection(exact);
            if (!FrameSolutionSelection.Same(declared, selection))
                FrameNodeJson.Fail("E_NODE_SOURCE_PRECISION", "Разрешение параметров изменило точное численное заявление проекта/зоны. Узел не создаётся; округлённые значения не принимаются.");
            context.ValidateSelection(selection);
        }
        private sealed class Scale
        {
            internal readonly int Digits; internal readonly decimal Divisor;
            internal Scale(IEnumerable<decimal> values) { Digits = values.Select(v => (decimal.GetBits(v)[3] >> 16) & 255).DefaultIfEmpty(0).Max(); Divisor = Powers[Digits]; }
            internal decimal Units(decimal value)
            { int[] b = decimal.GetBits(value); return checked(new decimal(b[0], b[1], b[2], false, 0) * Powers[Digits - ((b[3] >> 16) & 255)]); }
            internal decimal Value(decimal units) { return units / Divisor; }
        }
        public static FrameNodeGeometryResult Evaluate(FrameSolutionSelection selection, FrameNodeGeometryInput input)
        {
            var selected = FrameNodeJson.CopySelection(selection);
            if (input == null) FrameNodeJson.Fail("E_NODE_INPUT", "Не заданы локальные вводы узла.");
            var local = input.Clone(); var g = selected.geometry;
            var exactLayers = g.insulation_layers_mm.Select(FrameNodeJson.Exact).ToList();
            decimal? front = g.cladding_front_offset_mm.HasValue ? (decimal?)FrameNodeJson.Exact(g.cladding_front_offset_mm.Value) : null;
            decimal? profile = local.profile_near_face_x_mm.HasValue ? (decimal?)FrameNodeJson.Exact(local.profile_near_face_x_mm.Value) : null;
            decimal? membrane = local.clearance_surface.membrane_outer_x_mm.HasValue ? (decimal?)FrameNodeJson.Exact(local.clearance_surface.membrane_outer_x_mm.Value) : null;
            var values = new List<decimal>(exactLayers); if (front.HasValue) values.Add(front.Value); if (profile.HasValue) values.Add(profile.Value); if (membrane.HasValue) values.Add(membrane.Value);
            var scale = new Scale(values); var planes = new List<FrameNodePlane>(); var layers = new List<FrameNodeLayer>(); var dimensions = new List<FrameNodeDimension>();
            var issues = new List<FrameNodeIssue>(); var missing = new List<string>(); var cadCoordinates = new Dictionary<double, decimal>();
            Action<string, string, decimal, string> plane = (id, title, x, basis) => {
                double cad = FrameNodeJson.CadNumber(x); decimal prior;
                if (cadCoordinates.TryGetValue(cad, out prior) && prior != x) FrameNodeJson.Fail("E_NODE_CAD_PRECISION", "Различные плоскости " + FrameNodeJson.Text(prior) + " и " + FrameNodeJson.Text(x) + " мм совпадают в CAD double; точная вставка невозможна.");
                cadCoordinates[cad] = x; planes.Add(new FrameNodePlane(id, title, x, basis)); };
            Action<string, string> error = (code, text) => issues.Add(new FrameNodeIssue(code, text, "error"));
            decimal? insulation = null, surface = null, gap = null; string clearance = "not_evaluated";
            try
            {
                // Validate the complete common scale once, before any arithmetic.
                foreach (decimal value in values) scale.Units(value);
                plane("wall", "Поверхность строительного основания", 0, "structural_origin");
                decimal total = 0;
                for (int i = 0; i < exactLayers.Count; i++)
                {
                    decimal start = scale.Value(total); total = checked(total + scale.Units(exactLayers[i])); decimal end = scale.Value(total);
                    layers.Add(new FrameNodeLayer(i + 1, start, end, exactLayers[i]));
                    plane("insulation_" + (i + 1), "Наружная граница слоя " + (i + 1), end, "derived_from_declared_layers");
                    dimensions.Add(new FrameNodeDimension("layer_" + (i + 1), "Слой утеплителя " + (i + 1), start, end, exactLayers[i], "project_declared"));
                }
                if (exactLayers.Count != 0) insulation = scale.Value(total); else missing.Add("слои утеплителя (пустой список не означает нулевую толщину)");
                if (front.HasValue) { plane("cladding_front", "Наружная поверхность облицовки", front.Value, "project_declared"); dimensions.Add(new FrameNodeDimension("cladding_offset", "Вынос наружной облицовки", 0, front.Value, front.Value, "project_declared")); }
                else missing.Add("наружная плоскость облицовки");
                if (profile.HasValue) { plane("profile_near", "Ближайшая к стене поверхность ГП", profile.Value, "local_declared"); dimensions.Add(new FrameNodeDimension("profile_offset", "Координата поверхности ГП", 0, profile.Value, profile.Value, "local_declared")); }
                else missing.Add("координата ближайшей поверхности ГП");
                if (membrane.HasValue) { plane("membrane_outer", "Наружная поверхность мембраны", membrane.Value, "local_declared"); dimensions.Add(new FrameNodeDimension("membrane_offset", "Координата мембраны", 0, membrane.Value, membrane.Value, "local_declared")); }
                if (local.clearance_surface.kind == FrameNodeClearanceSurface.Layers)
                { surface = insulation; if (!surface.HasValue) error("E_NODE_SURFACE_MISSING", "Выбрана наружная граница слоёв, но сами слои не заявлены. Задайте слои или выберите неизвестную поверхность."); }
                else if (local.clearance_surface.kind == FrameNodeClearanceSurface.Membrane) surface = membrane;
                else missing.Add("поверхность проверки просвета с учётом мембраны");
                if (insulation.HasValue && front.HasValue && insulation.Value >= front.Value)
                    error("E_NODE_INSULATION_FRONT", "Утеплитель заканчивается на x=" + FrameNodeJson.Text(insulation.Value) + " мм, наружная облицовка на x=" + FrameNodeJson.Text(front.Value) + " мм: плоскости противоречат узлу.");
                if (membrane.HasValue && insulation.HasValue && membrane.Value < insulation.Value)
                    error("E_NODE_MEMBRANE_INSIDE", "Мембрана x=" + FrameNodeJson.Text(membrane.Value) + " мм находится внутри заявленных слоёв до x=" + FrameNodeJson.Text(insulation.Value) + " мм.");
                if (membrane.HasValue && front.HasValue && membrane.Value >= front.Value)
                    error("E_NODE_MEMBRANE_FRONT", "Наружная мембрана должна находиться перед наружной облицовкой: " + FrameNodeJson.Text(membrane.Value) + " / " + FrameNodeJson.Text(front.Value) + " мм.");
                if (profile.HasValue && front.HasValue && profile.Value >= front.Value)
                    error("E_NODE_PROFILE_FRONT", "Ближайшая поверхность ГП должна находиться перед наружной облицовкой: " + FrameNodeJson.Text(profile.Value) + " / " + FrameNodeJson.Text(front.Value) + " мм.");
                // Unknown clearance basis cannot conceal an already known
                // intersection. This checks ordering, never infers the min20 base.
                if (profile.HasValue && insulation.HasValue && profile.Value <= insulation.Value)
                    error("E_NODE_PROFILE_INSULATION", "Поверхность ГП x=" + FrameNodeJson.Text(profile.Value) + " мм находится внутри утеплителя или совпадает с его наружной границей x=" + FrameNodeJson.Text(insulation.Value) + " мм.");
                if (profile.HasValue && surface.HasValue)
                {
                    gap = scale.Value(checked(scale.Units(profile.Value) - scale.Units(surface.Value)));
                    clearance = gap.Value >= FrameNodeJson.Exact(Rule.MinimumMm) ? "pass" : "fail";
                    dimensions.Add(new FrameNodeDimension("local_clearance", "Локальный просвет", surface.Value, profile.Value, gap.Value, "derived_from_declared_planes"));
                    if (clearance == "fail") error("E_NODE_CLEARANCE", "Локальный просвет " + FrameNodeJson.Text(gap.Value) + " мм меньше " + FormatMm(Rule.MinimumMm) + " мм (лист 4.2.1, PDF 20).");
                }
                if (planes.All(p => p.XMm == 0)) error("E_NODE_EMPTY", "Не заданы размерные данные кроме основания; пустая схема не вставляется.");
            }
            catch (OverflowException) { FrameNodeJson.Fail("E_NODE_NUMERIC_PRECISION", "Совместная десятичная точность или сумма размеров не представима численным типом схемы. Округление не выполняется; это технический предел, не инженерная норма."); }
            string selectionDigest = FrameParameterResolver.SelectionDigest(selected);
            string inputDigest = FrameParameterJson.Hash(new Dictionary<string, object> { { "algorithm_revision", AlgorithmRevision }, { "selection", selected.ToDict() }, { "input", local.ToDict() } });
            return new FrameNodeGeometryResult(selectionDigest, inputDigest, clearance, gap, insulation, front, profile, surface, Rule, planes, layers, dimensions, issues, missing);
        }
    }

    public sealed class FrameNodeSnapshot
    {
        private readonly FrameSolutionSelection selection; private readonly FrameParameterContext context; private readonly FrameNodeGeometryInput input;
        public string Schema { get { return "aframe_node_snapshot/1"; } } public string CreatedUtc { get; private set; }
        public FrameSolutionSelection Selection { get { return FrameNodeJson.CopySelection(selection); } } public FrameParameterContext Context { get { return context.Clone(); } }
        public FrameNodeGeometryInput Input { get { return input.Clone(); } } public FrameNodeGeometryResult Result { get; private set; } public string SnapshotDigest { get; private set; }
        private FrameNodeSnapshot(FrameSolutionSelection selection, FrameParameterContext context, FrameNodeGeometryInput input, string createdUtc)
        {
            if (context == null) FrameNodeJson.Fail("E_NODE_CONTEXT", "Требуется происхождение привязанной зоны проекта.");
            context.ValidateSelection(selection); DateTime time;
            if (!DateTime.TryParseExact(createdUtc, "o", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out time) || time.Kind != DateTimeKind.Utc || time.ToString("o", CultureInfo.InvariantCulture) != createdUtc)
                FrameNodeJson.Fail("E_NODE_TIMESTAMP", "Требуется точная UTC-метка снимка.");
            this.selection = FrameNodeJson.CopySelection(selection); this.context = context.Clone(); this.input = input == null ? null : input.Clone(); CreatedUtc = createdUtc;
            Result = FrameNodeGeometry.Evaluate(this.selection, this.input);
            if (!Result.CanInsert) FrameNodeJson.Fail("E_NODE_REJECTED", Result.ReviewText());
            SnapshotDigest = FrameParameterJson.Hash(Content());
        }
        public static FrameNodeSnapshot Create(FrameSolutionSelection selection, FrameParameterContext context, FrameNodeGeometryInput input, string createdUtc)
        { return new FrameNodeSnapshot(selection, context, input, createdUtc); }
        private Dictionary<string, object> Content()
        { return new Dictionary<string, object> { { "schema", Schema }, { "algorithm_revision", Result.AlgorithmRevision }, { "created_utc", CreatedUtc }, { "selection", selection.ToDict() },
            { "context", context.ToDict() }, { "input", input.ToDict() }, { "input_digest", Result.InputDigest }, { "result_digest", Result.ResultDigest } }; }
        public Dictionary<string, object> ToDict() { var d = Content(); d["snapshot_digest"] = SnapshotDigest; return d; }
        public static FrameNodeSnapshot FromDict(object value)
        {
            var d = FrameNodeJson.Object(value, "schema", "algorithm_revision", "created_utc", "selection", "context", "input", "input_digest", "result_digest", "snapshot_digest");
            FrameNodeJson.Equal(d["schema"], "aframe_node_snapshot/1");
            if (!(d["algorithm_revision"] is string) || (string)d["algorithm_revision"] != FrameNodeGeometry.AlgorithmRevision)
                FrameNodeJson.Fail("E_NODE_ALGORITHM", "Снимок узла создан прежним или неизвестным алгоритмом. Создайте новый снимок через ATFNODE; автоматическая миграция не выполняется.");
            var result = Create(FrameNodeJson.ReadSelection(d["selection"]), FrameParameterContext.FromDict(d["context"]), FrameNodeGeometryInput.FromDict(d["input"]), d["created_utc"] as string);
            if (!(d["input_digest"] is string) || (string)d["input_digest"] != result.Result.InputDigest || !(d["result_digest"] is string) || (string)d["result_digest"] != result.Result.ResultDigest || !(d["snapshot_digest"] is string) || (string)d["snapshot_digest"] != result.SnapshotDigest)
                FrameNodeJson.Fail("E_NODE_DIGEST", "Снимок узла изменён или не соответствует пересчитанному результату.");
            return result;
        }
        public ReadOnlyCollection<string> CaptionLines()
        { return Array.AsReadOnly(new[] { "Размерная схема Вектор-1 2015, узел 4.2.1 / PDF 20.", "Снимок параметров: " + CreatedUtc + ". Актуальность — ATFNODE Проверить.",
            "Зона " + context.zone.zone_id + "; проект ред. " + context.project.revision + "; привязка ред. " + context.zone.revision + ".",
            Result.ClearanceStatus == "pass" ? "Локальный просвет " + Result.GapText + " мм: min20 соблюдён по заявленным плоскостям." : "Проверка локального просвета не выполнена; данные неполны.",
            "Не рабочий узел. Посадки, регулировка, совместимость, автоподбор и статика не подтверждены." }); }
    }
}
