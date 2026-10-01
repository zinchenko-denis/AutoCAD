using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AFramePlugin
{
    // Pure intent and provenance. No CAD objects, file access or engineering
    // inference: resolve one snapshot per zone, never inside a member loop.
    internal static class FrameParameterJson
    {
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }
        internal static void Fail(string reason) { throw new FrameSolutionSelectionException(reason); }
        internal static Dictionary<string, object> Object(object value)
        { var d = value as Dictionary<string, object>; if (d == null) Fail("Повреждён объект параметров проекта или зоны."); return d; }
        internal static void Keys(Dictionary<string, object> d, params string[] keys)
        { if (d.Count != keys.Length || keys.Any(k => !d.ContainsKey(k))) Fail("Неполный или неизвестный состав полей параметров проекта/зоны."); }
        internal static string Text(object value)
        { var s = value as string; if (string.IsNullOrWhiteSpace(s)) Fail("Не задана идентичность параметров проекта/зоны."); return s; }
        internal static void Equal(object value, string expected)
        { if (!(value is string) || (string)value != expected) Fail("Неизвестная схема или значение параметров проекта/зоны."); }
        internal static string Id(object value)
        { string s = Text(value); Guid id; if (!Guid.TryParseExact(s, "N", out id) || id.ToString("N") != s || id == Guid.Empty) Fail("Неверный ID проекта."); return s; }
        internal static string Digest(object value)
        { string s = Text(value); if (s.Length != 64 || s.Any(c => !Uri.IsHexDigit(c))) Fail("Неверный отпечаток параметров проекта/зоны."); return s; }
        internal static bool Numeric(object v)
        { return v is double || v is float || v is decimal || v is byte || v is sbyte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong; }
        internal static double Positive(object v)
        { if (!Numeric(v)) Fail("Параметр должен быть явно заданным числом."); double n = Convert.ToDouble(v, CultureInfo.InvariantCulture); if (double.IsNaN(n) || double.IsInfinity(n) || n <= 0) Fail("Параметр должен быть конечным числом больше нуля."); return n; }
        internal static long Revision(object v)
        {
            if (!Numeric(v)) Fail("Не задана положительная целая ревизия параметров.");
            try { decimal n = Convert.ToDecimal(v, CultureInfo.InvariantCulture); if (n <= 0 || n != decimal.Truncate(n) || n > long.MaxValue) Fail("Неверная ревизия параметров."); return (long)n; }
            catch (OverflowException) { Fail("Неверная ревизия параметров."); return 0; }
        }
        internal static long Next(long revision)
        { if (revision == long.MaxValue) Fail("Исчерпан диапазон ревизий параметров."); return revision + 1; }
        internal static object Copy(object value)
        {
            try { return Json().DeserializeObject(Json().Serialize(value)); }
            catch (ArgumentException) { Fail("Параметр содержит неконечное число или неподдерживаемое JSON-значение."); return null; }
            catch (InvalidOperationException) { Fail("Не удалось прочитать значение параметра проекта/зоны."); return null; }
        }
        private static object Ordered(object value)
        {
            var map = value as IDictionary<string, object>;
            if (map != null) { var sorted = new SortedDictionary<string, object>(StringComparer.Ordinal); foreach (var pair in map) sorted.Add(pair.Key, Ordered(pair.Value)); return sorted; }
            if (value is IEnumerable && !(value is string)) { var list = new List<object>(); foreach (object item in (IEnumerable)value) list.Add(Ordered(item)); return list; }
            return value;
        }
        internal static string Canonical(object value) { return Json().Serialize(Ordered(value)); }
        internal static string Hash(object value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(Canonical(value)))).Replace("-", "").ToLowerInvariant(); }
        internal static void Selection(FrameSolutionSelection value)
        { if (value == null) Fail("Требуется полный явный выбор решения проекта."); string reason = value.Validate(); if (reason != null) Fail(reason); }
    }

    public sealed class FrameProjectParameters
    {
        public string schema = "aframe_project_parameters/1", project_id, content_digest;
        public long revision;
        public FrameSolutionSelection defaults;
        private Dictionary<string, object> Content()
        { return new Dictionary<string, object> { { "schema", schema }, { "project_id", project_id }, { "defaults", defaults.ToDict() } }; }
        public static FrameProjectParameters CreateNext(FrameProjectParameters previous, FrameSolutionSelection defaults, string projectId = null)
        {
            FrameParameterJson.Selection(defaults);
            if (previous != null) { previous.Validate(); if (projectId != null && projectId != previous.project_id) FrameParameterJson.Fail("Нельзя незаметно заменить ID проекта."); }
            var result = new FrameProjectParameters { project_id = previous == null ? projectId ?? Guid.NewGuid().ToString("N") : previous.project_id,
                revision = previous == null ? 1 : previous.revision, defaults = defaults.Clone() };
            FrameParameterJson.Id(result.project_id); result.content_digest = FrameParameterJson.Hash(result.Content());
            if (previous != null && result.content_digest != previous.content_digest) result.revision = FrameParameterJson.Next(previous.revision);
            return result;
        }
        private void Validate()
        {
            FrameParameterJson.Equal(schema, "aframe_project_parameters/1"); FrameParameterJson.Id(project_id); FrameParameterJson.Revision(revision);
            FrameParameterJson.Selection(defaults); FrameParameterJson.Digest(content_digest);
            if (content_digest != FrameParameterJson.Hash(Content())) FrameParameterJson.Fail("Содержимое параметров проекта не соответствует отпечатку.");
        }
        public Dictionary<string, object> ToDict()
        { Validate(); var d = Content(); d["revision"] = revision; d["content_digest"] = content_digest; return d; }
        public static FrameProjectParameters FromDict(object value)
        {
            if (value == null) return null; var d = FrameParameterJson.Object(value);
            FrameParameterJson.Keys(d, "schema", "project_id", "revision", "content_digest", "defaults");
            var result = new FrameProjectParameters { schema = FrameParameterJson.Text(d["schema"]), project_id = FrameParameterJson.Id(d["project_id"]),
                revision = FrameParameterJson.Revision(d["revision"]), content_digest = FrameParameterJson.Digest(d["content_digest"]), defaults = FrameSolutionSelection.FromDict(d["defaults"]) };
            result.Validate(); return result;
        }
        public FrameProjectParameters Clone() { return FromDict(ToDict()); }
    }

    public sealed class FrameParameterOverride
    {
        public string state;
        public object value;
        public static FrameParameterOverride Set(object value) { return new FrameParameterOverride { state = "set", value = FrameParameterJson.Copy(value) }; }
        public static FrameParameterOverride Clear() { return new FrameParameterOverride { state = "clear" }; }
        public Dictionary<string, object> ToDict()
        {
            if (state == "clear") { if (value != null) FrameParameterJson.Fail("Очистка поля не может содержать значение."); return new Dictionary<string, object> { { "state", state } }; }
            if (state != "set") FrameParameterJson.Fail("Неизвестный режим локального исключения.");
            return new Dictionary<string, object> { { "state", state }, { "value", FrameParameterJson.Copy(value) } };
        }
        public static FrameParameterOverride FromDict(object value)
        {
            var d = FrameParameterJson.Object(value); object state;
            if (!d.TryGetValue("state", out state)) FrameParameterJson.Fail("Не задан режим локального исключения.");
            if ((state as string) == "clear") { FrameParameterJson.Keys(d, "state"); return Clear(); }
            FrameParameterJson.Keys(d, "state", "value"); FrameParameterJson.Equal(state, "set"); return Set(d["value"]);
        }
    }

    public sealed class FrameZoneParameters
    {
        public string schema = "aframe_zone_parameters/1", project_id, content_digest;
        public long revision;
        public Dictionary<string, FrameParameterOverride> overrides = new Dictionary<string, FrameParameterOverride>(StringComparer.Ordinal);
        private Dictionary<string, object> Content()
        {
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            if (overrides == null) FrameParameterJson.Fail("Отсутствует набор исключений зоны.");
            foreach (var pair in overrides) { FrameParameterResolver.ValidateOverride(pair.Key, pair.Value); fields.Add(pair.Key, pair.Value.ToDict()); }
            return new Dictionary<string, object> { { "schema", schema }, { "project_id", project_id }, { "overrides", fields } };
        }
        public static FrameZoneParameters CreateNext(FrameZoneParameters previous, FrameProjectParameters project, Dictionary<string, FrameParameterOverride> overrides)
        {
            if (project == null) FrameParameterJson.Fail("Сначала явно задайте параметры проекта."); project.ToDict();
            if (previous != null) { previous.Validate(); if (previous.project_id != project.project_id) FrameParameterJson.Fail("Привязка зоны относится к другому проекту."); }
            if (overrides == null) FrameParameterJson.Fail("Отсутствует набор исключений зоны.");
            var result = new FrameZoneParameters { project_id = project.project_id, revision = previous == null ? 1 : previous.revision };
            foreach (var pair in overrides) result.overrides.Add(pair.Key, FrameParameterOverride.FromDict(pair.Value == null ? null : pair.Value.ToDict()));
            result.content_digest = FrameParameterJson.Hash(result.Content());
            if (previous != null && result.content_digest != previous.content_digest) result.revision = FrameParameterJson.Next(previous.revision);
            FrameParameterResolver.Effective(project, result); return result;
        }
        private void Validate()
        {
            FrameParameterJson.Equal(schema, "aframe_zone_parameters/1"); FrameParameterJson.Id(project_id); FrameParameterJson.Revision(revision); FrameParameterJson.Digest(content_digest);
            if (content_digest != FrameParameterJson.Hash(Content())) FrameParameterJson.Fail("Содержимое исключений зоны не соответствует отпечатку.");
        }
        public Dictionary<string, object> ToDict()
        { Validate(); var d = Content(); d["revision"] = revision; d["content_digest"] = content_digest; return d; }
        public static FrameZoneParameters FromDict(object value)
        {
            if (value == null) return null; var d = FrameParameterJson.Object(value);
            FrameParameterJson.Keys(d, "schema", "project_id", "revision", "content_digest", "overrides");
            var result = new FrameZoneParameters { schema = FrameParameterJson.Text(d["schema"]), project_id = FrameParameterJson.Id(d["project_id"]),
                revision = FrameParameterJson.Revision(d["revision"]), content_digest = FrameParameterJson.Digest(d["content_digest"]) };
            foreach (var pair in FrameParameterJson.Object(d["overrides"])) result.overrides.Add(pair.Key, FrameParameterOverride.FromDict(pair.Value));
            result.Validate(); return result;
        }
        public FrameZoneParameters Clone() { return FromDict(ToDict()); }
    }

    public sealed class FrameProjectReference
    {
        public string project_id, content_digest, record_digest;
        public long revision;
        public Dictionary<string, object> ToDict()
        { FrameParameterJson.Id(project_id); FrameParameterJson.Revision(revision); FrameParameterJson.Digest(content_digest); FrameParameterJson.Digest(record_digest);
            return new Dictionary<string, object> { { "project_id", project_id }, { "revision", revision }, { "content_digest", content_digest }, { "record_digest", record_digest } }; }
        public static FrameProjectReference FromDict(object value)
        { var d = FrameParameterJson.Object(value); FrameParameterJson.Keys(d, "project_id", "revision", "content_digest", "record_digest");
            return new FrameProjectReference { project_id = FrameParameterJson.Id(d["project_id"]), revision = FrameParameterJson.Revision(d["revision"]),
                content_digest = FrameParameterJson.Digest(d["content_digest"]), record_digest = FrameParameterJson.Digest(d["record_digest"]) }; }
    }

    public sealed class FrameZoneReference
    {
        public string owner_handle, zone_id, content_digest, record_digest;
        public long revision;
        public Dictionary<string, object> ToDict()
        { FrameParameterJson.Text(owner_handle); FrameParameterJson.Text(zone_id); FrameParameterJson.Revision(revision); FrameParameterJson.Digest(content_digest); FrameParameterJson.Digest(record_digest);
            return new Dictionary<string, object> { { "owner_handle", owner_handle }, { "zone_id", zone_id }, { "revision", revision }, { "content_digest", content_digest }, { "record_digest", record_digest } }; }
        public static FrameZoneReference FromDict(object value)
        { var d = FrameParameterJson.Object(value); FrameParameterJson.Keys(d, "owner_handle", "zone_id", "revision", "content_digest", "record_digest");
            return new FrameZoneReference { owner_handle = FrameParameterJson.Text(d["owner_handle"]), zone_id = FrameParameterJson.Text(d["zone_id"]),
                revision = FrameParameterJson.Revision(d["revision"]), content_digest = FrameParameterJson.Digest(d["content_digest"]), record_digest = FrameParameterJson.Digest(d["record_digest"]) }; }
    }

    public sealed class FrameParameterContext
    {
        public string schema = "aframe_parameter_context/1", effective_digest;
        public FrameProjectReference project;
        public FrameZoneReference zone;
        public Dictionary<string, string> origins = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, object> ToDict()
        {
            FrameParameterJson.Equal(schema, "aframe_parameter_context/1"); FrameParameterJson.Digest(effective_digest);
            if (project == null || zone == null || origins == null || origins.Count != FrameParameterResolver.Fields.Count) FrameParameterJson.Fail("Неполное происхождение параметров зоны.");
            var fields = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var field in FrameParameterResolver.Fields) { string origin; if (!origins.TryGetValue(field.Path, out origin) ||
                (origin != "project_default" && origin != "zone_override" && origin != "zone_clear") || (origin == "zone_clear" && !field.CanClear)) FrameParameterJson.Fail("Неверное происхождение поля «" + field.Path + "»."); fields.Add(field.Path, origin); }
            return new Dictionary<string, object> { { "schema", schema }, { "project", project.ToDict() }, { "zone", zone.ToDict() }, { "effective_digest", effective_digest }, { "origins", fields } };
        }
        public static FrameParameterContext FromDict(object value)
        {
            if (value == null) return null; var d = FrameParameterJson.Object(value);
            FrameParameterJson.Keys(d, "schema", "project", "zone", "effective_digest", "origins");
            var result = new FrameParameterContext { schema = FrameParameterJson.Text(d["schema"]), project = FrameProjectReference.FromDict(d["project"]),
                zone = FrameZoneReference.FromDict(d["zone"]), effective_digest = FrameParameterJson.Digest(d["effective_digest"]) };
            foreach (var pair in FrameParameterJson.Object(d["origins"])) result.origins.Add(pair.Key, FrameParameterJson.Text(pair.Value));
            result.ToDict(); return result;
        }
        public void ValidateSelection(FrameSolutionSelection selection)
        { ToDict(); if (effective_digest != FrameParameterResolver.SelectionDigest(selection)) FrameParameterJson.Fail("Сохранённое итоговое решение не соответствует проектному снимку."); }
        public FrameParameterContext Clone() { return FromDict(ToDict()); }
        public static bool Same(FrameParameterContext a, FrameParameterContext b)
        { return a == null || b == null ? a == null && b == null : FrameParameterJson.Canonical(a.ToDict()) == FrameParameterJson.Canonical(b.ToDict()); }
    }

    public sealed class FrameParameterResolution
    {
        private readonly Dictionary<string, object> selection, context;
        internal FrameParameterResolution(FrameSolutionSelection selection, FrameParameterContext context)
        { this.selection = selection.ToDict(); this.context = context.ToDict(); }
        public FrameSolutionSelection Selection { get { return FrameSolutionSelection.FromDict(FrameParameterJson.Copy(selection)); } }
        public FrameParameterContext Context { get { return FrameParameterContext.FromDict(FrameParameterJson.Copy(context)); } }
    }

    public sealed class FrameParameterField
    {
        public string Path { get; private set; }
        public string Title { get; private set; }
        public string Kind { get; private set; }
        public bool CanClear { get; private set; }
        internal FrameParameterField(string path, string title, string kind, bool clear = false) { Path = path; Title = title; Kind = kind; CanClear = clear; }
    }

    public static class FrameParameterResolver
    {
        public static readonly ReadOnlyCollection<FrameParameterField> Fields = Array.AsReadOnly(new[] {
            new FrameParameterField("bracket.execution", "КР2: исполнение", "choice"), new FrameParameterField("bracket.nominal_width_mm", "КР2: номинальная ширина", "choice"), new FrameParameterField("bracket.L_mm", "КР2: длина L", "choice"),
            new FrameParameterField("extender.execution", "УК: исполнение", "choice"), new FrameParameterField("extender.nominal_width_mm", "УК: номинальная ширина", "choice"), new FrameParameterField("extender.L_mm", "УК: длина L", "choice"), new FrameParameterField("extender.thickness_mm", "УК: толщина", "choice"),
            new FrameParameterField("profile.execution", "ГП: исполнение", "choice"), new FrameParameterField("profile.a_mm", "ГП: полка a", "choice"), new FrameParameterField("profile.b_mm", "ГП: проектная полка b", "number"), new FrameParameterField("profile.thickness_mm", "ГП: проектная толщина", "number"),
            new FrameParameterField("geometry.cladding_front_offset_mm", "Вынос наружной поверхности облицовки", "number", true), new FrameParameterField("geometry.insulation_layers_mm", "Слои утеплителя", "layers", true) });
        private static readonly Dictionary<string, FrameParameterField> ByPath = Fields.ToDictionary(f => f.Path, StringComparer.Ordinal);
        private static readonly Dictionary<string, object> Catalog = FrameSolutionSelection.CatalogSnapshot();
        public static object GetValue(FrameSolutionSelection selection, string path)
        { if (!ByPath.ContainsKey(path)) FrameParameterJson.Fail("Неизвестное поле исключения «" + path + "»."); string[] keys = path.Split('.'); return FrameParameterJson.Copy(FrameParameterJson.Object(selection.ToDict()[keys[0]])[keys[1]]); }
        public static string SelectionDigest(FrameSolutionSelection selection)
        { FrameParameterJson.Selection(selection); return FrameParameterJson.Hash(selection.ToDict()); }
        internal static void ValidateOverride(string path, FrameParameterOverride value)
        {
            FrameParameterField field; if (!ByPath.TryGetValue(path, out field) || value == null) FrameParameterJson.Fail("Неизвестное или пустое исключение поля «" + path + "».");
            value.ToDict(); if (value.state == "clear") { if (!field.CanClear) FrameParameterJson.Fail("Обязательное поле «" + path + "» нельзя очистить."); return; }
            if (path.EndsWith(".execution", StringComparison.Ordinal))
            { string execution = value.value as string; foreach (object item in (IEnumerable)Catalog["executions"]) if ((string)FrameParameterJson.Object(item)["id"] == execution) return; FrameParameterJson.Fail("Неизвестное исполнение изделия."); }
            if (field.Kind == "layers")
            { var layers = value.value as IEnumerable; if (layers == null || value.value is string || value.value is IDictionary) FrameParameterJson.Fail("Слои утеплителя задаются явным массивом толщин."); foreach (object item in layers) FrameParameterJson.Positive(item); return; }
            double number = FrameParameterJson.Positive(value.value);
            if (field.Kind == "number") return;
            string[] keys = path.Split('.'); string family = keys[0] == "bracket" ? "kr2" : keys[0] == "extender" ? "uk" : "gp";
            var rule = FrameParameterJson.Object(FrameParameterJson.Object(FrameParameterJson.Object(FrameParameterJson.Object(Catalog["families"])[family])["dimensions"])[keys[1]]);
            object allowed;
            if (rule.TryGetValue("values", out allowed)) { foreach (object n in (IEnumerable)allowed) if (number == Convert.ToDouble(n, CultureInfo.InvariantCulture)) return; }
            else { double minimum = Convert.ToDouble(rule["minimum"], CultureInfo.InvariantCulture), maximum = Convert.ToDouble(rule["maximum"], CultureInfo.InvariantCulture), step = Convert.ToDouble(rule["step"], CultureInfo.InvariantCulture);
                if (number >= minimum && number <= maximum && (number - minimum) % step == 0) return; }
            FrameParameterJson.Fail("Размер поля «" + path + "» отсутствует в исторической номенклатуре.");
        }
        internal static FrameSolutionSelection Effective(FrameProjectParameters project, FrameZoneParameters zone)
        {
            if (project == null || zone == null) FrameParameterJson.Fail("Для наследования требуются явный проект и привязка зоны.");
            project.ToDict(); zone.ToDict(); if (project.project_id != zone.project_id) FrameParameterJson.Fail("Привязка зоны относится к другому проекту.");
            var result = project.defaults.ToDict();
            foreach (var pair in zone.overrides) { ValidateOverride(pair.Key, pair.Value); string[] keys = pair.Key.Split('.'); var member = FrameParameterJson.Object(result[keys[0]]);
                member[keys[1]] = pair.Value.state == "set" ? FrameParameterJson.Copy(pair.Value.value) : pair.Key == "geometry.insulation_layers_mm" ? (object)new object[0] : null; }
            return FrameSolutionSelection.FromDict(result);
        }
        public static FrameParameterResolution Resolve(FrameProjectParameters project, string projectRecordDigest, FrameZoneParameters zone, string zoneRecordDigest, string ownerHandle, string zoneId)
        {
            var effective = Effective(project, zone);
            var context = new FrameParameterContext { effective_digest = SelectionDigest(effective),
                project = new FrameProjectReference { project_id = project.project_id, revision = project.revision, content_digest = project.content_digest, record_digest = projectRecordDigest },
                zone = new FrameZoneReference { owner_handle = ownerHandle, zone_id = zoneId, revision = zone.revision, content_digest = zone.content_digest, record_digest = zoneRecordDigest } };
            foreach (var field in Fields) { FrameParameterOverride item; context.origins.Add(field.Path, zone.overrides.TryGetValue(field.Path, out item) ? (item.state == "clear" ? "zone_clear" : "zone_override") : "project_default"); }
            return new FrameParameterResolution(effective, context);
        }
        public static string ValidateGroup(IDictionary<string, FrameParameterResolution> bound, IEnumerable<string> unboundZoneIds)
        {
            var unbound = (unboundZoneIds ?? new string[0]).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (bound == null) FrameParameterJson.Fail("Не задан снимок параметров выбранных зон.");
            if (bound.Count == 0) return null;
            string names = string.Join(", ", bound.Keys.Concat(unbound).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray());
            if (unbound.Count != 0) return "Выбраны зоны с привязкой к проекту и без неё: " + names + ". Выполните отдельные операции.";
            string digest = null, project = null;
            foreach (var pair in bound) { if (pair.Value == null) FrameParameterJson.Fail("Не разрешены параметры зоны «" + pair.Key + "»."); var current = pair.Value.Context;
                string reference = FrameParameterJson.Canonical(current.project.ToDict());
                if (digest != null && (digest != current.effective_digest || project != reference)) return "Выбранные зоны имеют разные итоговые параметры: " + names + ". Перестройте отдельные или однородные группы зон.";
                digest = current.effective_digest; project = reference; }
            return null;
        }
        public static Dictionary<string, object> RunSnapshot(IEnumerable<FrameParameterContext> contexts)
        {
            var zones = new SortedDictionary<string, object>(StringComparer.Ordinal); FrameParameterContext first = null;
            foreach (var item in contexts) { if (item == null) FrameParameterJson.Fail("Неполный проектный снимок операции."); item.ToDict();
                if (first == null) first = item;
                else if (first.effective_digest != item.effective_digest || FrameParameterJson.Canonical(first.project.ToDict()) != FrameParameterJson.Canonical(item.project.ToDict())) FrameParameterJson.Fail("Снимок операции смешивает разные параметры проекта.");
                var part = new Dictionary<string, object> { { "zone", item.zone.ToDict() }, { "origins", item.origins.ToDictionary(p => p.Key, p => (object)p.Value, StringComparer.Ordinal) } };
                object existing; if (zones.TryGetValue(item.zone.owner_handle, out existing) && FrameParameterJson.Canonical(existing) != FrameParameterJson.Canonical(part)) FrameParameterJson.Fail("Один владелец зоны имеет разные источники параметров.");
                zones[item.zone.owner_handle] = part; }
            if (first == null) FrameParameterJson.Fail("Нет проектного снимка зон операции.");
            return new Dictionary<string, object> { { "schema", "aframe_parameter_run/1" }, { "project", first.project.ToDict() }, { "effective_digest", first.effective_digest }, { "zones", zones.Values.ToArray() } };
        }
    }
}
