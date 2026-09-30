using System;
using System.Collections.Generic;
using System.Globalization;
using FacadeSafety;

namespace AFacadesPlugin
{
    // Одна модель строк для предпросмотра, DWG и XLSX. Форматирование DWG
    // не меняет числовые значения, которые передаются в Excel.
    internal sealed class CladdingTableData
    {
        internal static readonly string[] Headers = { "Зона", "Материал", "Тип / марка", "Цвет",
            "Размер, мм / форма", "Вид", "Ед.", "Количество", "Площадь деталей по раскладке, м²", "Примечание" };
        internal const string Title = "Ведомость облицовки — фактические элементы";
        internal readonly List<object[]> Installed = new List<object[]>();
        internal readonly List<object[]> Cutting = new List<object[]>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> Information = new List<string>();
        internal readonly List<string> CuttingDetails = new List<string>();
        internal readonly Dictionary<string, string> FormShapes = new Dictionary<string, string>(StringComparer.Ordinal);
        internal string Scope;
        internal string Coverage = "Учтена геометрия раскладки; статическая схема не проверяется. Это не полная спецификация системы и не закупочная ведомость.";
        internal bool Complete;

        internal static CladdingTableData Build(QuantityResult result, IEnumerable<QuantityReport> reports,
            IEnumerable<string> zoneIds, IEnumerable<string> warnings, bool byZone, bool includeCutting)
        {
            if (result == null || !result.ok)
                throw new InvalidOperationException("Нельзя вывести неподтверждённую ведомость.");
            var data = new CladdingTableData();
            var zones = new List<string>(new SortedSet<string>(zoneIds ?? new string[0], StringComparer.Ordinal));
            data.Scope = "Область: зоны «" + string.Join("», «", zones.ToArray()) + "». " +
                (byZone ? "Группировка по зонам." : "Общая группировка.") +
                " Выбор детали включает её зону целиком.";
            data.Complete = result.completeness == "complete";
            if (warnings != null)
                foreach (var warning in warnings) data.AddWarning(warning);
            foreach (var issue in result.issues)
                if (issue.severity == "info") data.AddInformation(issue.message);
                else data.AddWarning(issue.message);
            data.Complete = data.Complete && data.Warnings.Count == 0;
            var shapeNames = new Dictionary<string, string>(StringComparer.Ordinal);
            var shapeIds = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in result.rows)
                if (IsShaped(row) && !string.IsNullOrEmpty(row.shape_id)) shapeIds.Add(row.shape_id);
            foreach (string shape in shapeIds)
            {
                string name = "Ф-" + (shapeNames.Count + 1).ToString(CultureInfo.InvariantCulture);
                shapeNames[shape] = name;
                data.FormShapes[name] = shape;
            }
            double pieces = 0, area = 0;
            bool knownArea = true;
            foreach (var row in result.rows)
            {
                var values = Row(row, shapeNames);
                if (row.basis == "cutting") data.Cutting.Add(values);
                else
                {
                    data.Installed.Add(values);
                    pieces += row.quantity;
                    if (row.area_m2.HasValue) area += row.area_m2.Value;
                    else knownArea = false;
                }
            }
            if (data.Installed.Count == 0) data.AddInformation("В выбранной области нет деталей облицовки.");
            data.Installed.Add(new object[] { "ИТОГО", "", "", "", "", "установлено", "шт.", pieces,
                knownArea ? (object)area : "не определена", "" });
            if (includeCutting)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var selected = new HashSet<string>(zones, StringComparer.Ordinal);
                foreach (var report in reports)
                    foreach (var group in report.cutting ?? new List<QuantityCuttingGroup>())
                    {
                        var groupScope = new HashSet<string>(group.scope_zone_ids, StringComparer.Ordinal);
                        if (!groupScope.Overlaps(selected) || !groupScope.IsSubsetOf(selected)) continue;
                        if (!seen.Add(report.run_id + "|" + group.group_id)) continue;
                        data.CuttingDetails.Add("Группа раскроя: зоны «" +
                            string.Join("», «", group.scope_zone_ids.ToArray()) + "».");
                        var keys = new List<string>(group.parameters.Keys);
                        keys.Sort(StringComparer.Ordinal);
                        foreach (var key in keys)
                        {
                            string label = ParameterName(key);
                            if (label != null) data.CuttingDetails.Add(label + ": " + ParameterValue(key, group.parameters[key]));
                        }
                    }
                if (data.Cutting.Count > 0)
                    data.CuttingDetails.Add("Заготовки для подрезок входят в заготовки всего; эти количества не складываются.");
                if (data.Cutting.Count == 0)
                    data.CuttingDetails.Add("Данные принятого раскроя для этой раскладки отсутствуют. Потребность в заготовках не определена.");
            }
            return data;
        }

        private void AddWarning(string text)
        {
            if (!string.IsNullOrWhiteSpace(text) && !Warnings.Contains(text)) Warnings.Add(text);
        }
        private void AddInformation(string text)
        {
            if (!string.IsNullOrWhiteSpace(text) && !Information.Contains(text)) Information.Add(text);
        }

        internal List<object[]> Rows(string userNote)
        {
            var rows = new List<object[]> { new object[] { Title }, new object[] { Scope },
                new object[] { Coverage }, new object[] { Complete ? "Полнота: учтён выбранный состав." :
                    "Полнота: неполная ведомость. Неопределённые позиции и причины приведены ниже." }, Headers };
            rows.AddRange(Installed);
            if (Cutting.Count > 0 || CuttingDetails.Count > 0)
            {
                rows.Add(new object[] { "Раскрой — вся указанная группа; запас на бой не добавлен" });
                foreach (var detail in CuttingDetails) rows.Add(new object[] { detail });
                if (Cutting.Count > 0) { rows.Add(Headers); rows.AddRange(Cutting); }
            }
            foreach (var warning in Warnings) rows.Add(new object[] { "Неопределённость: " + warning });
            foreach (var info in Information) rows.Add(new object[] { info });
            rows.Add(new object[] { "Примечание пользователя: " + (userNote ?? "") });
            return rows;
        }

        private static bool IsShaped(QuantityRow row)
        { return row.piece_kind == "shaped" || row.piece_kind == "irregular"; }

        private static object[] Row(QuantityRow row, Dictionary<string, string> shapeNames)
        {
            string size = row.width_mm.HasValue && row.height_mm.HasValue
                ? Number(row.width_mm.Value) + " × " + Number(row.height_mm.Value) : "размер не указан";
            if (row.basis == "cutting" && row.role == "waste") size = "—";
            string shapeName;
            if (IsShaped(row) && !string.IsNullOrEmpty(row.shape_id) && shapeNames.TryGetValue(row.shape_id, out shapeName))
                size += "; форма " + shapeName;
            string type = Value(row.type);
            if (!string.IsNullOrEmpty(row.mark) && row.mark != row.type) type += " / " + row.mark;
            if (!string.IsNullOrEmpty(row.product_id)) type += " [" + row.product_id + "]";
            string note = row.note ?? "";
            if (!string.IsNullOrEmpty(row.orientation))
                note = (row.orientation == "XY; rotation=0; mirror=false" ? "Без поворота и отражения." : "Ориентация: " + row.orientation + ".") + " " + note;
            return new object[] { string.IsNullOrEmpty(row.zone_id) ? "Общая" : row.zone_id,
                Value(row.material), type, Value(row.color), size, Kind(row), row.unit,
                row.quantity, row.area_m2.HasValue ? (object)row.area_m2.Value :
                    row.basis == "cutting" ? "—" : "не определена", note };
        }

        private static string Kind(QuantityRow row)
        {
            if (row.basis == "cutting")
                return row.role == "blanks" || row.role == "blank" ? "заготовки" :
                    row.role == "blanks_cut" ? "заготовки для подрезок" :
                    row.role == "blanks_total" ? "заготовки всего" :
                    row.role == "waste" ? "отходы" : Value(row.role);
            switch (row.piece_kind)
            {
                case "full": return "целая";
                case "cut": case "rectangular_cut": return "подрезная";
                case "shaped": case "irregular": return "фигурная";
                default: return Value(row.piece_kind);
            }
        }

        private static string Value(string value) { return string.IsNullOrWhiteSpace(value) ? "не указано" : value; }
        private static string ParameterName(string key)
        {
            switch (key)
            {
                case "tile_w_mm": return "Ширина заготовки, мм";
                case "tile_h_mm": return "Высота заготовки, мм";
                case "kerf": return "Пропил, мм";
                case "min_piece": return "Минимальный кусок, мм";
                case "tiny_mode": return "Режим мелких подрезок";
                case "axis": return "Ось раскроя";
                case "shaped": return "Фигурные подрезки";
                case "warn_cut": return "Предупреждение о подрезке";
                case "gap_around": return "Зазор по контуру, мм";
                case "scope_basis": return "Область раскроя";
                case "extra_breakage_allowance": return "Дополнительный запас на бой";
                default: return null;
            }
        }
        private static string ParameterValue(string key, object value)
        {
            string text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (key == "scope_basis" && text == "whole_merged_zone_group") return "вся группа объединённых зон";
            if (key == "extra_breakage_allowance" && text == "not_included") return "не добавлен";
            if (key == "tiny_mode" && text == "absorb") return "поглощение соседним элементом";
            if (value is System.Collections.IDictionary || (value is System.Collections.IEnumerable && !(value is string)))
                return "см. сохранённые данные раскроя";
            if (value is bool) return (bool)value ? "да" : "нет";
            return text ?? "не указано";
        }
        private static string Number(double value)
        {
            string text = value.ToString("0.######", CultureInfo.InvariantCulture);
            return value != 0 && text == "0" ? value.ToString("0.######E+0", CultureInfo.InvariantCulture) : text;
        }
        internal static string Display(object value)
        {
            if (value is double) return Number((double)value);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }
    }
}
