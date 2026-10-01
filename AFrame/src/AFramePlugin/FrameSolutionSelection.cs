using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace AFramePlugin
{
    public sealed class FrameSolutionSelectionException : InvalidOperationException
    { public FrameSolutionSelectionException(string message) : base(message) { } }

    public sealed class FrameSolutionNode
    { public int pdf_page { get; set; } public string sheet { get; set; } }
    public sealed class FrameSolutionBracket
    {
        public string family_id { get; set; }
        public string execution { get; set; }
        public double nominal_width_mm { get; set; }
        public double L_mm { get; set; }
    }
    public sealed class FrameSolutionExtender
    {
        public string family_id { get; set; }
        public string execution { get; set; }
        public double nominal_width_mm { get; set; }
        public double L_mm { get; set; }
        public double thickness_mm { get; set; }
    }
    public sealed class FrameSolutionProfile
    {
        public string family_id { get; set; }
        public string execution { get; set; }
        public double a_mm { get; set; }
        public double? b_mm { get; set; }
        public double? thickness_mm { get; set; }
        public string b_basis { get; set; }
        public string thickness_basis { get; set; }
    }
    public sealed class FrameSolutionGeometry
    {
        public double? cladding_front_offset_mm { get; set; }
        public string cladding_offset_basis { get; set; }
        public List<double> insulation_layers_mm { get; set; } = new List<double>();
    }

    // Pure project declaration. Historical nomenclature membership is independent
    // of structural approval, modern SKU identity and the reference calculation.
    public sealed class FrameSolutionSelection
    {
        public string schema { get; set; }
        public string catalog_id { get; set; }
        public string catalog_revision { get; set; }
        public string solution_id { get; set; }
        public string source_id { get; set; }
        public string source_sha256 { get; set; }
        public FrameSolutionNode node { get; set; }
        public FrameSolutionBracket bracket { get; set; }
        public FrameSolutionExtender extender { get; set; }
        public FrameSolutionProfile profile { get; set; }
        public FrameSolutionGeometry geometry { get; set; }

        // Generated from the canonical Python export; checked byte-for-byte as a
        // JSON value in the native contract gate. No runtime file or CAD access.
        private const string CatalogJson = @"{""catalog_id"":""vector1_2015_type1_historical"",""executions"":[{""coating_thickness"":null,""id"":""galvanized_painted"",""mark_token"":null,""pdf_pages"":[3,6,8,9],""steel_grade"":null,""title"":""Оцинкованная сталь с порошковой окраской""},{""coating_thickness"":null,""id"":""corrosion_resistant"",""mark_token"":""КС"",""pdf_pages"":[3,6,8,9],""steel_grade"":null,""title"":""Коррозионностойкая сталь (КС)""}],""families"":{""gp"":{""dimensions"":{""a_mm"":{""values"":[40,50,60,80]},""b_mm"":{""basis"":""project_and_strength_calculation_required"",""values"":null},""supply_length_mm"":{""basis"":""not_stated"",""values"":null},""thickness_mm"":{""basis"":""project_and_strength_calculation_required"",""values"":null}},""family_id"":""gp"",""printed_pattern"":""ГП-(КС)-a-b-c"",""role"":""profile"",""source"":{""pdf_page"":9,""position"":""4.4"",""sheet"":""3.4""}},""kr2"":{""dimensions"":{""L_mm"":{""maximum"":350,""minimum"":50,""step"":10},""nominal_width_mm"":{""values"":[50,60,70,85]},""thickness_mm"":{""basis"":""sketch_dimension"",""value"":2}},""family_id"":""kr2"",""printed_pattern"":""КР2-(КС)-50(60,70,85)-L"",""role"":""bracket"",""source"":{""pdf_page"":6,""position"":""2.2"",""sheet"":""3.2.1""}},""uk"":{""dimensions"":{""L_mm"":{""values"":[100,150]},""nominal_width_mm"":{""values"":[50,60,70,85]},""sketch_width_mm_by_nominal"":{""50"":54,""60"":64,""70"":74,""85"":89},""thickness_mm"":{""values"":[1,1.2,1.5]}},""family_id"":""uk"",""printed_pattern"":""УК-(КС)-50(60,70,85)-L-c"",""role"":""extender"",""source"":{""pdf_page"":8,""position"":""3.2"",""sheet"":""3.3""}}},""geometry_parameters"":{""automatic_bracket_length_equation"":""not_confirmed"",""cladding_offset_basis"":""wall_structural_face_to_cladding_exterior"",""cladding_offset_meaning"":""Заявленное пользователем расстояние от поверхности строительного основания до наружной поверхности облицовки."",""insulation_layers_basis"":""project_declared_layer_thicknesses"",""legacy_calculation_offset_is_separate"":true,""unit"":""mm""},""missing"":[""current_sku"",""approved_section_properties"",""steel_grade"",""manufacturing_tolerances"",""hole_coordinates"",""node_adjustment_interval"",""fixed_sliding_contract"",""splice_continuity"",""anchor_substrate_design"",""complete_fastener_bom"",""automatic_bracket_selection""],""revision"":""6bb12795979248fd51f0d9384c8bb0f91b7c45165734ef2ce7d04fe2b6877c24"",""schema"":""aframe_solution_catalog/1"",""scope"":""historical_nomenclature_membership_only"",""solution"":{""adjustment_interval_mm"":null,""calculation_binding"":""not_confirmed"",""cladding"":""porcelain"",""combination_compatibility"":""not_confirmed"",""families"":{""bracket"":""kr2"",""extender"":""uk"",""profile"":""gp""},""geometry_preset"":""Вектор-1"",""local_clearance"":{""checked_by_this_increment"":false,""from"":""outer_insulation_membrane_surface"",""minimum_mm"":20,""not"":""distance_to_outer_cladding_surface"",""pdf_page"":20,""sheet"":""4.2.1"",""to"":""nearest_profile_surface""},""node"":{""pdf_page"":20,""sheet"":""4.2.1""},""note"":""Минимум 30 мм узла 4.1 относится к пластинам У2/У и не переносится на выбранный узел с УК."",""overlap_minimum_mm"":null,""solution_id"":""vector1_2015_type1_4_2_1"",""sub_type"":""vertical"",""title"":""Тип 1, Г-профиль с КР2 и УК; узел 4.2.1""},""source"":{""current_manufacturer_approval"":""not_confirmed"",""current_product_availability"":""not_confirmed"",""edition_label"":null,""edition_note"":""3.15 есть только в имени файла; самостоятельный номер редакции не подтверждён."",""edition_year"":2015,""issuer_as_printed"":""ООО «Вектор групп»"",""kind"":""historical_manufacturer_album"",""pdf_pages"":109,""sha256"":""7386f4152de455a5e2622704d51a98d8f05afd20050807f634eea4572de37142"",""source_id"":""vector1_2015"",""title"":""АТР Вектор-1, 2015""}}";
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly Dictionary<string, object> Catalog = (Dictionary<string, object>)Json.DeserializeObject(CatalogJson);
        public static Dictionary<string, object> CatalogSnapshot()
        { return (Dictionary<string, object>)Json.DeserializeObject(CatalogJson); }

        public static FrameSolutionSelection CreateDefault()
        {
            var solution = (Dictionary<string, object>)Catalog["solution"];
            var source = (Dictionary<string, object>)Catalog["source"];
            var families = (Dictionary<string, object>)solution["families"];
            var locator = (Dictionary<string, object>)solution["node"];
            var geometry = (Dictionary<string, object>)Catalog["geometry_parameters"];
            return new FrameSolutionSelection {
                schema = "aframe_solution_selection/1", catalog_id = (string)Catalog["catalog_id"],
                catalog_revision = (string)Catalog["revision"], solution_id = (string)solution["solution_id"],
                source_id = (string)source["source_id"], source_sha256 = (string)source["sha256"],
                node = new FrameSolutionNode { pdf_page = Convert.ToInt32(locator["pdf_page"], CultureInfo.InvariantCulture), sheet = (string)locator["sheet"] },
                bracket = new FrameSolutionBracket { family_id = (string)families["bracket"] },
                extender = new FrameSolutionExtender { family_id = (string)families["extender"] },
                profile = new FrameSolutionProfile { family_id = (string)families["profile"], b_basis = "project_declared", thickness_basis = "project_declared" },
                geometry = new FrameSolutionGeometry { cladding_offset_basis = (string)geometry["cladding_offset_basis"] }
            };
        }
        public FrameSolutionSelection Clone()
        { return Json.Deserialize<FrameSolutionSelection>(Json.Serialize(this)); }
        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object> {
                { "schema", schema }, { "catalog_id", catalog_id }, { "catalog_revision", catalog_revision },
                { "solution_id", solution_id }, { "source_id", source_id }, { "source_sha256", source_sha256 },
                { "node", node == null ? null : new Dictionary<string, object> { { "pdf_page", node.pdf_page }, { "sheet", node.sheet } } },
                { "bracket", bracket == null ? null : new Dictionary<string, object> {
                    { "family_id", bracket.family_id }, { "execution", bracket.execution }, { "nominal_width_mm", bracket.nominal_width_mm }, { "L_mm", bracket.L_mm } } },
                { "extender", extender == null ? null : new Dictionary<string, object> {
                    { "family_id", extender.family_id }, { "execution", extender.execution }, { "nominal_width_mm", extender.nominal_width_mm },
                    { "L_mm", extender.L_mm }, { "thickness_mm", extender.thickness_mm } } },
                { "profile", profile == null ? null : new Dictionary<string, object> {
                    { "family_id", profile.family_id }, { "execution", profile.execution }, { "a_mm", profile.a_mm }, { "b_mm", profile.b_mm },
                    { "thickness_mm", profile.thickness_mm }, { "b_basis", profile.b_basis }, { "thickness_basis", profile.thickness_basis } } },
                { "geometry", geometry == null ? null : new Dictionary<string, object> {
                    { "cladding_front_offset_mm", geometry.cladding_front_offset_mm }, { "cladding_offset_basis", geometry.cladding_offset_basis },
                    { "insulation_layers_mm", geometry.insulation_layers_mm == null ? null : new List<double>(geometry.insulation_layers_mm) } } }
            };
        }
        public static bool Same(FrameSolutionSelection first, FrameSolutionSelection second)
        {
            if (first == null || second == null) return first == null && second == null;
            return Json.Serialize(first) == Json.Serialize(second);
        }
        public string Validate()
        {
            try { ValidateDictionary(ToDict()); return null; }
            catch (FrameSolutionSelectionException error) { return error.Message; }
        }
        public static FrameSolutionSelection FromDict(object value)
        {
            if (value == null) return null;
            var d = RequireObject(value, "solution_selection");
            ValidateDictionary(d);
            return Json.Deserialize<FrameSolutionSelection>(Json.Serialize(d));
        }
        public void ValidateEngineReport(object value, bool clampsOnly)
        {
            var d = RequireObject(value, "solution_report");
            Keys(d, "schema", "status", "selection", "geometry_preset", "geometry_effect", "catalogue_dimensions", "project_dimensions",
                "assembly_compatibility", "calculation_binding", "physical_assignment", "product_id", "retention_validation", "limitations");
            Equal(d, "schema", "aframe_solution_report/1");
            Equal(d, "status", clampsOnly ? "retained_identity_only" : "declared_selection");
            Equal(d, "geometry_preset", "Вектор-1");
            Equal(d, "geometry_effect", clampsOnly ? "no_new_frame" : "existing_preset_only");
            Equal(d, "catalogue_dimensions", "validated_against_historical_source");
            Equal(d, "project_dimensions", "declared_not_verified");
            Equal(d, "assembly_compatibility", "not_verified"); Equal(d, "calculation_binding", "not_confirmed");
            Equal(d, "physical_assignment", "not_asserted");
            Equal(d, "retention_validation", clampsOnly ? "caller_must_verify_saved_selection" : "not_applicable");
            if (d["product_id"] != null || !Same(this, FromDict(d["selection"]))) Fail("Движок изменил явный выбор решения или назначил неподтверждённое изделие.");
            var limitations = d["limitations"] as IEnumerable;
            if (limitations == null || d["limitations"] is string || d["limitations"] is IDictionary) Fail("Не сохранены ограничения выбранного решения.");
            var required = new HashSet<string>(new[] { "historical_source_only", "project_profile_dimensions_unverified",
                "assembly_not_verified", "geometry_preset_not_catalogue_dimensions", "offset_not_calculation_lever_arm",
                "no_automatic_bracket_selection", "no_installed_product_assignment", "calculation_binding_unconfirmed" }, StringComparer.Ordinal);
            var actual = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in limitations)
                if (!(item is string) || !actual.Add((string)item)) Fail("Не читаются ограничения выбранного решения.");
            if (!actual.SetEquals(required)) Fail("Движок не сохранил полный набор ограничений выбранного решения.");
        }

        private static void ValidateDictionary(Dictionary<string, object> d)
        {
            Keys(d, "schema", "catalog_id", "catalog_revision", "solution_id", "source_id", "source_sha256", "node", "bracket", "extender", "profile", "geometry");
            var expected = CreateDefault();
            Equal(d, "schema", expected.schema); Equal(d, "catalog_id", expected.catalog_id);
            Equal(d, "catalog_revision", expected.catalog_revision); Equal(d, "solution_id", expected.solution_id);
            Equal(d, "source_id", expected.source_id); Equal(d, "source_sha256", expected.source_sha256);
            var n = RequireObject(d["node"], "node"); Keys(n, "pdf_page", "sheet");
            if (Number(n["pdf_page"], "node.pdf_page") != expected.node.pdf_page) Fail("Не подтверждена страница выбранного узла.");
            Equal(n, "sheet", expected.node.sheet);
            var b = RequireObject(d["bracket"], "bracket"); Keys(b, "family_id", "execution", "nominal_width_mm", "L_mm"); Equal(b, "family_id", expected.bracket.family_id);
            var e = RequireObject(d["extender"], "extender"); Keys(e, "family_id", "execution", "nominal_width_mm", "L_mm", "thickness_mm"); Equal(e, "family_id", expected.extender.family_id);
            var p = RequireObject(d["profile"], "profile"); Keys(p, "family_id", "execution", "a_mm", "b_mm", "thickness_mm", "b_basis", "thickness_basis"); Equal(p, "family_id", expected.profile.family_id);
            Execution(b); Execution(e); Execution(p);
            Dimension(b, "kr2", "nominal_width_mm"); Dimension(b, "kr2", "L_mm");
            Dimension(e, "uk", "nominal_width_mm"); Dimension(e, "uk", "L_mm"); Dimension(e, "uk", "thickness_mm");
            Dimension(p, "gp", "a_mm");
            Positive(p["b_mm"], "Проектная полка b профиля"); Positive(p["thickness_mm"], "Проектная толщина профиля");
            Equal(p, "b_basis", "project_declared"); Equal(p, "thickness_basis", "project_declared");
            var g = RequireObject(d["geometry"], "geometry"); Keys(g, "cladding_front_offset_mm", "cladding_offset_basis", "insulation_layers_mm");
            Equal(g, "cladding_offset_basis", expected.geometry.cladding_offset_basis);
            if (g["cladding_front_offset_mm"] != null) Positive(g["cladding_front_offset_mm"], "Расстояние от поверхности основания до наружной поверхности облицовки");
            var layers = g["insulation_layers_mm"] as IEnumerable;
            if (layers == null || g["insulation_layers_mm"] is string || g["insulation_layers_mm"] is IDictionary) Fail("Слои утепления должны быть явным массивом толщин.");
            foreach (object layer in layers) Positive(layer, "Толщина слоя утепления");
        }
        private static void Execution(Dictionary<string, object> d)
        {
            string value = d["execution"] as string;
            foreach (object item in (object[])Catalog["executions"])
                if (value == (string)((Dictionary<string, object>)item)["id"]) return;
            Fail("Выберите исполнение изделия из исторического альбома.");
        }
        private static void Dimension(Dictionary<string, object> selected, string family, string key)
        {
            double number = Number(selected[key], key);
            var families = (Dictionary<string, object>)Catalog["families"];
            var dimensions = (Dictionary<string, object>)((Dictionary<string, object>)families[family])["dimensions"];
            var rule = (Dictionary<string, object>)dimensions[key];
            object values;
            if (rule.TryGetValue("values", out values))
            {
                foreach (object allowed in (object[])values) if (number == Number(allowed, key)) return;
            }
            else
            {
                double minimum = Number(rule["minimum"], key), maximum = Number(rule["maximum"], key), step = Number(rule["step"], key);
                if (number >= minimum && number <= maximum && (number - minimum) % step == 0) return;
            }
            Fail("Размер «" + key + "» изделия " + family + " отсутствует в номенклатуре выбранного исторического альбома.");
        }
        private static void Equal(Dictionary<string, object> d, string key, string expected)
        { if (!(d[key] is string) || (string)d[key] != expected) Fail("Не поддерживается значение «" + key + "» выбранного решения. Требуется явный повторный выбор по текущему каталогу."); }
        private static void Keys(Dictionary<string, object> d, params string[] keys)
        {
            var required = new HashSet<string>(keys, StringComparer.Ordinal);
            if (d.Count != required.Count) Fail("Неполный или неизвестный состав полей выбранного решения.");
            foreach (string key in d.Keys) if (!required.Contains(key)) Fail("Неизвестное поле выбранного решения: " + key + ".");
        }
        private static Dictionary<string, object> RequireObject(object value, string name)
        {
            var d = value as Dictionary<string, object>;
            if (d == null) Fail("Поле «" + name + "» должно содержать полный объект выбора решения.");
            return d;
        }
        private static double Number(object value, string name)
        {
            if (!(value is double || value is float || value is decimal || value is byte || value is sbyte ||
                value is short || value is ushort || value is int || value is uint || value is long || value is ulong))
                Fail("Поле «" + name + "» должно быть явно заданным числом.");
            double result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(result) || double.IsInfinity(result)) Fail("Поле «" + name + "» должно быть конечным числом.");
            return result;
        }
        private static double Positive(object value, string name)
        { double n = Number(value, name); if (n <= 0) Fail("Поле «" + name + "» должно быть больше нуля."); return n; }
        private static void Fail(string reason) { throw new FrameSolutionSelectionException(reason); }
    }

    public sealed class FrameSolutionSelectionScope
    {
        private readonly Dictionary<string, Dictionary<string, object>> byRoot = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        private readonly FrameSolutionSelectionContext preliminary = new FrameSolutionSelectionContext();
        private readonly bool deferGroupValidation;
        private readonly HashSet<string> preliminaryRoots = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, FrameSolutionSelection> aliases = new Dictionary<string, FrameSolutionSelection>(StringComparer.Ordinal);
        public int Observations { get; private set; }
        public int AliasComparisons { get; private set; }
        public FrameSolutionSelectionScope(bool deferGroupValidation = false)
        { this.deferGroupValidation = deferGroupValidation; }
        public FrameSolutionSelection Baseline { get {
            if (!deferGroupValidation) return preliminary.Baseline;
            var current = new FrameSolutionSelectionContext();
            foreach (string root in preliminaryRoots) current.Add(byRoot[root]);
            return current.Baseline;
        } }
        public void Add(string root, Dictionary<string, object> settings, bool rawContour)
        {
            if (string.IsNullOrEmpty(root)) throw new FrameSolutionSelectionException("Не определён исходный корень выбора решения.");
            object raw = null; if (settings != null) settings.TryGetValue("solution_selection", out raw);
            var selection = FrameSolutionSelection.FromDict(raw);
            Dictionary<string, object> old;
            if (byRoot.TryGetValue(root, out old))
            {
                var pair = new FrameSolutionSelectionContext(); pair.Add(old); pair.Add(settings);
            }
            else byRoot.Add(root, selection == null ? null : new Dictionary<string, object> { { "solution_selection", selection.ToDict() } });
            Observations++;
            // A raw null contour may be an opening. Its actual owning root is
            // resolved by the existing engine, not by a second geometry pass.
            if (!rawContour || selection != null)
            {
                preliminaryRoots.Add(root);
                if (!deferGroupValidation) preliminary.Add(settings);
            }
        }
        // A drawing mark is an alias of the verified zone hatch. An absent
        // declaration on that alias does not make a second, legacy zone.
        public void AddAlias(string root, Dictionary<string, object> settings)
        {
            if (string.IsNullOrEmpty(root)) throw new FrameSolutionSelectionException("Не определён исходный корень марки зоны.");
            object raw = null; if (settings != null) settings.TryGetValue("solution_selection", out raw);
            var selection = FrameSolutionSelection.FromDict(raw);
            if (selection != null)
            {
                FrameSolutionSelection prior;
                if (aliases.TryGetValue(root, out prior))
                {
                    AliasComparisons++;
                    if (!FrameSolutionSelection.Same(prior, selection))
                        throw new FrameSolutionSelectionException("Метки одной зоны содержат разные явные выборы решения. Проверьте исходные метки.");
                }
                else aliases.Add(root, selection);
                Dictionary<string, object> canonical;
                if (byRoot.TryGetValue(root, out canonical)) { AliasComparisons++; CheckAlias(selection, canonical); }
            }
            Observations++;
        }
        public void AddCanonical(string root, Dictionary<string, object> settings)
        {
            // Read even a null declaration as authoritative: it is a genuine
            // new/legacy root, never an invitation to inherit from a neighbour.
            Add(root, settings, false);
            FrameSolutionSelection alias;
            if (aliases.TryGetValue(root, out alias)) { AliasComparisons++; CheckAlias(alias, settings); }
        }
        private static void CheckAlias(FrameSolutionSelection alias, Dictionary<string, object> canonical)
        {
            if (alias == null) return;
            object raw = null; if (canonical != null) canonical.TryGetValue("solution_selection", out raw);
            if (!FrameSolutionSelection.Same(alias, FrameSolutionSelection.FromDict(raw)))
                throw new FrameSolutionSelectionException("Явный выбор решения на марке расходится с подтверждённой штриховкой той же зоны. Обновление остановлено; проверьте исходные метки.");
        }
        public static Dictionary<string, object> PreferCanonicalWindowSettings(Dictionary<string, object> current,
            Dictionary<string, object> canonical, ref bool explicitCanonicalChosen)
        {
            object raw = null; if (canonical != null) canonical.TryGetValue("solution_selection", out raw);
            var selection = FrameSolutionSelection.FromDict(raw);
            if (selection != null && !explicitCanonicalChosen)
            {
                explicitCanonicalChosen = true;
                return canonical;
            }
            return current ?? canonical;
        }
        public static void ValidateClampsRoots(IEnumerable<string> actualRoots, IEnumerable<string> previousRoots)
        {
            var actual = new HashSet<string>(actualRoots, StringComparer.Ordinal);
            foreach (string root in previousRoots)
                if (!actual.Contains(root))
                    throw new FrameSolutionSelectionException("Режим «только кляммеры»: прежний каркас «" + root +
                        "» не входит в результат выбранных зон. Обновление остановлено до изменения чертежа; выполните полное перестроение либо выберите только согласованную область.");
        }
        public static List<string> EngineRoots(object value, Dictionary<string, string> partToRoot)
        {
            var rows = value as IEnumerable;
            if (rows == null || value is string || value is IDictionary) throw new FrameSolutionSelectionException("Движок не вернул область зон для проверки выбора решения.");
            var roots = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in rows)
            {
                var row = item as Dictionary<string, object>; object idValue;
                if (row == null || !row.TryGetValue("zone_id", out idValue) || !(idValue is string) || string.IsNullOrWhiteSpace((string)idValue))
                    throw new FrameSolutionSelectionException("В результате отсутствует исходная зона выбора решения.");
                string id = (string)idValue, root;
                if (!partToRoot.TryGetValue(id, out root)) root = id;
                if (seen.Add(root)) roots.Add(root);
            }
            return roots;
        }
        public FrameSolutionSelectionContext Resolve(IEnumerable<string> actualRoots)
        {
            var result = new FrameSolutionSelectionContext();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string root in actualRoots)
            {
                if (!seen.Add(root)) continue;
                Dictionary<string, object> settings;
                if (!byRoot.TryGetValue(root, out settings)) throw new FrameSolutionSelectionException("Не найден исходный выбор решения зоны «" + root + "».");
                result.Add(settings);
            }
            if (seen.Count == 0) throw new FrameSolutionSelectionException("Движок не подтвердил область выбора решения.");
            return result;
        }
    }

    // One pass over already selected owners, before the dialog. Null is an
    // explicit legacy/new-zone state and cannot inherit a neighbour's catalog.
    public sealed class FrameSolutionSelectionContext
    {
        private bool observed;
        private FrameSolutionSelection baseline;
        public int Observations { get; private set; }
        public FrameSolutionSelection Baseline { get { return baseline == null ? null : baseline.Clone(); } }
        public void Add(Dictionary<string, object> settings)
        {
            object raw = null;
            if (settings != null) settings.TryGetValue("solution_selection", out raw);
            var selection = FrameSolutionSelection.FromDict(raw);
            Observations++;
            if (observed && !FrameSolutionSelection.Same(baseline, selection))
                throw new FrameSolutionSelectionException("Выбранные зоны имеют разные каталожные решения либо смешивают новый/старый каркас с явным выбором. Выберите однородную группу зон.");
            if (!observed) { baseline = selection; observed = true; }
        }
        public string ValidateClamps(FrameSolutionSelection selection)
        {
            if (selection != null) { string invalid = selection.Validate(); if (invalid != null) return invalid; }
            return FrameSolutionSelection.Same(baseline, selection) ? null :
                "Режим «только кляммеры» не назначает и не меняет каталожное решение сохранённого каркаса. Выберите полное построение подсистемы.";
        }
    }
}
