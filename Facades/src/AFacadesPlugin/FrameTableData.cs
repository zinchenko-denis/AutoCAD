using System;
using System.Collections.Generic;
using FacadeSafety;

namespace AFacadesPlugin
{
    // Actual generated components and length-based stock estimates remain
    // separate blocks. No quantities or catalogue properties are inferred here.
    internal sealed class FrameTableData
    {
        internal const string Title = "Ведомость подсистемы — элементы схемы";
        internal static readonly string[] Headers = { "Зона", "Система", "Категория", "Марка / тип", "Материал",
            "Покрытие", "Цвет", "Длина, мм", "Количество, шт.", "Погонаж, м", "Примечание" };
        internal readonly List<object[]> Installed = new List<object[]>();
        internal readonly List<object[]> Estimates = new List<object[]>();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> Information = new List<string>();
        internal readonly List<string> EstimateDetails = new List<string>();
        internal string Scope;
        internal string Coverage = "Учтены элементы схемы; статическая модель и комплектность узлов не подтверждены. " +
            "Условные соединения не являются комплектом метизов. Это не закупочная ведомость.";
        internal bool Complete;

        private sealed class CategoryTotal
        {
            internal string Role;
            internal double Count, Length;
            internal bool LengthKnown = true;
        }

        internal static FrameTableData Build(QuantityResult result, IEnumerable<QuantityReport> reports,
            IEnumerable<string> zoneIds, IEnumerable<string> warnings, bool byZone)
        {
            if (result == null || !result.ok) throw new InvalidOperationException("Нельзя вывести неподтверждённую ведомость.");
            var data = new FrameTableData();
            var zones = new SortedSet<string>(zoneIds ?? new string[0], StringComparer.Ordinal);
            data.Scope = "Область: зоны «" + string.Join("», «", new List<string>(zones).ToArray()) + "». " +
                (byZone ? "Группировка по зонам." : "Общая группировка.") + " Выбор элемента включает его зону целиком.";
            data.Complete = result.completeness == "complete";
            foreach (string warning in warnings ?? new string[0]) Add(data.Warnings, warning);
            foreach (var issue in result.issues)
                Add(issue.severity == "info" ? data.Information : data.Warnings, issue.message);
            data.Complete &= data.Warnings.Count == 0;
            foreach (string coverage in result.source_engineering_coverage ?? new List<string>())
                Add(data.Information, EngineeringScope(coverage));
            var totals = new SortedDictionary<string, CategoryTotal>(StringComparer.Ordinal);
            foreach (var row in result.rows)
            {
                if (row.basis == "estimate") { data.Estimates.Add(Row(row)); continue; }
                if (row.basis != "installed") throw new InvalidOperationException("Неизвестное основание строки подсистемы.");
                data.Installed.Add(Row(row));
                CategoryTotal total;
                if (!totals.TryGetValue(row.role, out total))
                    totals[row.role] = total = new CategoryTotal { Role = row.role };
                total.Count += row.quantity;
                if (Linear(row.role))
                {
                    if (row.total_length_m.HasValue) total.Length += row.total_length_m.Value;
                    else total.LengthKnown = false;
                }
            }
            if (data.Installed.Count == 0)
            {
                Add(data.Information, "В выбранной области нет созданных элементов подсистемы. Количество: 0 шт.");
                data.Installed.Add(new object[] { "ИТОГО", "", "созданные элементы", "", "", "", "", "—", 0.0, 0.0, "" });
            }
            else
                foreach (var total in totals.Values)
                    data.Installed.Add(new object[] { "ИТОГО", "", Category(total.Role), "", "", "", "", "—", total.Count,
                        Linear(total.Role) ? total.LengthKnown ? (object)total.Length : "не определён" : "—", "" });
            var selected = new HashSet<string>(zones, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var report in reports ?? new QuantityReport[0])
                foreach (var group in report.estimates ?? new List<QuantityEstimateGroup>())
                {
                    var scope = new HashSet<string>(group.scope_zone_ids, StringComparer.Ordinal);
                    if (!scope.Overlaps(selected) || !scope.IsSubsetOf(selected) || !seen.Add(report.run_id + "|" + group.group_id)) continue;
                    var names = new List<string>(scope); names.Sort(StringComparer.Ordinal);
                    Add(data.EstimateDetails, "Область оценки: зоны «" + string.Join("», «", names.ToArray()) + "».");
                }
            if (data.Estimates.Count > 0)
                Add(data.EstimateDetails, "Оценка хлыстов получена по сумме длин. Она не учитывает реальный раскрой и не является количеством для закупки.");
            return data;
        }

        internal List<object[]> Rows(string note)
        {
            var rows = new List<object[]> { new object[] { Title }, new object[] { Scope }, new object[] { Coverage },
                new object[] { Complete ? "Полнота: учтён выбранный состав." :
                    "Полнота: неполная ведомость. Неопределённые позиции и причины приведены ниже." }, Headers };
            rows.AddRange(Installed);
            if (Estimates.Count > 0)
            {
                rows.Add(new object[] { "Оценка хлыстов по сумме длин — не раскрой и не закупка" });
                foreach (string detail in EstimateDetails) rows.Add(new object[] { detail });
                rows.Add(Headers); rows.AddRange(Estimates);
            }
            foreach (string warning in Warnings) rows.Add(new object[] { "Неопределённость: " + warning });
            foreach (string info in Information) rows.Add(new object[] { info });
            rows.Add(new object[] { "Примечание пользователя: " + (note ?? "") });
            return rows;
        }

        private static object[] Row(QuantityRow row)
        {
            bool estimate = row.basis == "estimate";
            string mark = Value(row.mark);
            if (!string.IsNullOrWhiteSpace(row.type) && row.type != row.mark)
                mark += " / " + row.type;
            if (!string.IsNullOrEmpty(row.product_id)) mark += " [" + row.product_id + "]";
            string note = row.note ?? "";
            if (!string.IsNullOrEmpty(row.orientation))
                note = "Ориентация на схеме: " + (row.orientation == "vertical" ? "вертикальная" :
                    row.orientation == "horizontal" ? "горизонтальная" : row.orientation) + ". " + note;
            if (row.role == "fitting") note = "Условное соединение; комплект метизов не подтверждён. " + note;
            if (estimate) note = "Длина относится к исходному хлысту. Оценка по сумме длин; не раскрой и не закупка. " + note;
            return new object[] {
                string.IsNullOrEmpty(row.zone_id) ? "Общая" : row.zone_id, Value(row.system),
                estimate ? "Оценка хлыстов" : Category(row.role), mark, Value(row.material), Value(row.coating), Value(row.color),
                row.length_mm.HasValue ? (object)row.length_mm.Value : Linear(row.role) || estimate ? "не определена" : "—",
                row.quantity, row.total_length_m.HasValue ? (object)row.total_length_m.Value :
                    Linear(row.role) ? "не определён" : "—", note };
        }

        private static string EngineeringScope(string value)
        {
            switch (value)
            {
                case "geometry_only": return "Источник: геометрическая расстановка без статического расчёта.";
                case "limited_static_chain": return "Источник: ограниченная расчётная цепочка; полная статическая модель не подтверждена.";
                case "clamps_geometry_only": return "Источник: режим только кляммеров. Существующие направляющие не включены в созданный состав.";
                case "clamps_geometry_only_with_retained_frame": return "Источник: новые кляммеры и сохранённые элементы предыдущего построения подсистемы. " +
                    "Его инженерные ограничения сохраняются; новый статический расчёт не выполнен.";
                default: return "Инженерное покрытие источника не определено; расчётный допуск ведомостью не подтверждается.";
            }
        }
        private static bool Linear(string role) { return role == "rail" || role == "hrail" || role == "shina"; }
        private static string Category(string role)
        {
            switch (role)
            {
                case "rail": return "Направляющая";
                case "hrail": return "Горизонтальная направляющая";
                case "shina": return "Шина";
                case "bracket": return "Кронштейн";
                case "clamp": return "Кляммер";
                case "fitting": return "Условное соединение";
                default: return "Категория не определена";
            }
        }
        private static string Value(string value) { return string.IsNullOrWhiteSpace(value) ? "не указано" : value; }
        private static void Add(List<string> list, string value)
        { if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value)) list.Add(value); }
    }
}
