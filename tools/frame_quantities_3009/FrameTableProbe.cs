using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AFacadesPlugin;
using FacadeSafety;

internal static class FrameTableProbe
{
    private static int checks;
    private static void Check(bool value, string message)
    { checks++; if (!value) throw new Exception(message); }

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        var result = new QuantityResult { ok = true, completeness = "complete" };
        result.source_engineering_coverage.AddRange(new[] { "geometry_only", "limited_static_chain",
            "clamps_geometry_only_with_retained_frame" });
        result.rows.Add(new QuantityRow { zone_id = "А", role = "rail", system = "Вектор-1", mark = "ШП-60-20", type = "направляющая",
            length_mm = 2500.1234567, quantity = 2, total_length_m = 5.0002469134, material = null, coating = null, color = "CAD ACI 7" });
        result.rows.Add(new QuantityRow { zone_id = "Б", role = "rail", quantity = 1, length_mm = null, total_length_m = null });
        result.rows.Add(new QuantityRow { zone_id = "А", role = "hrail", length_mm = 1000.55555, quantity = 1, total_length_m = 1.00055555 });
        result.rows.Add(new QuantityRow { zone_id = "Б", role = "shina", mark = "ШК-1", length_mm = 1500.25, quantity = 4, total_length_m = 6.001 });
        result.rows.Add(new QuantityRow { zone_id = "А", role = "bracket", type = "несущий", quantity = 6 });
        result.rows.Add(new QuantityRow { zone_id = "А", role = "clamp", type = "рядовой", quantity = 12 });
        result.rows.Add(new QuantityRow { zone_id = "Б", role = "fitting", type = "вставка", quantity = 2 });
        result.rows.Add(new QuantityRow { basis = "estimate", role = "rail_stock_est", quantity = 2, unit = "шт.", length_mm = 6000 });
        result.issues.Add(new QuantityIssue { severity = "warning", message = "Каталожное изделие не определено" });
        result.issues.Add(new QuantityIssue { severity = "info", message = "Режим источника сохранён" });
        var report = new QuantityReport { kind = "frame", run_id = "run-1" };
        report.estimates.Add(new QuantityEstimateGroup { group_id = "estimate-1", scope_zone_ids = new List<string> { "А", "Б" } });
        var data = FrameTableData.Build(result, new[] { report, report }, new[] { "Б", "А", "А" },
            new[] { "Материал не указан", "Материал не указан" }, true);
        Check(data.Installed.Count == 13, "Seven installed rows and six category totals required");
        Check(data.Estimates.Count == 1 && (double)data.Estimates[0][8] == 2, "Stock estimate kept separate");
        Check((double)data.Installed.Take(7).Sum(row => (double)row[8]) == 28, "Installed count includes an estimate or loses objects");
        Check((double)data.Installed[0][7] == 2500.1234567 && (double)data.Installed[0][9] == 5.0002469134,
            "Member or metre length rounded before export");
        Check((string)data.Installed[1][7] == "не определена" && (string)data.Installed[1][9] == "не определён", "Unknown linear length is not explicit");
        Check((string)data.Installed[4][7] == "—" && (string)data.Installed[4][9] == "—", "Bracket acquired an invented length");
        Check((string)data.Installed[0][4] == "не указано" && (string)data.Installed[0][5] == "не указано", "Material or coating guessed");
        Check(Convert.ToString(data.Installed[6][10]).Contains("комплект метизов не подтверждён"), "Conditional fitting became a hardware bill");
        Check(Convert.ToString(data.Estimates[0][10]).Contains("не раскрой и не закупка"), "Stock estimate became procurement");
        Check((double)data.Estimates[0][7] == 6000 && Convert.ToString(data.Estimates[0][10]).Contains("исходному хлысту"),
            "Explicit stock length confused with installed member length");
        var totals = data.Installed.Where(row => Convert.ToString(row[0]) == "ИТОГО").ToList();
        Check(totals.Count == 6 && totals.Sum(row => (double)row[8]) == 28, "Category totals mix units or double count estimates");
        Check(Convert.ToString(totals.Single(row => Convert.ToString(row[2]) == "Направляющая")[9]) == "не определён",
            "Mixed known/unknown rail total became a known value");
        Check((double)totals.Single(row => Convert.ToString(row[2]) == "Горизонтальная направляющая")[9] == 1.00055555,
            "Horizontal metres mixed with vertical rails");
        Check((double)totals.Single(row => Convert.ToString(row[2]) == "Шина")[9] == 6.001, "Shina metres lost");
        Check(!data.Complete && data.Warnings.Count == 2, "Unknown quantities disappear or duplicate warnings expand");
        Check(data.Information.Any(text => text.Contains("Режим источника")), "Information treated as an error");
        Check(data.Information.Any(text => text.Contains("сохранённые элементы")), "Retained-frame engineering scope lost");
        Check(data.Information.All(text => !text.Contains("clamps_geometry")), "Raw engineering enum exposed");
        Check(data.Scope.Contains("«А», «Б»") && data.Scope.Contains("целиком"), "Selection scope missing or duplicate");
        Check(data.EstimateDetails.Count(text => text.StartsWith("Область оценки")) == 1, "Repeated report duplicates estimate scope");
        var rows = data.Rows("  Проверено & <пример> =1+1  ");
        Check(rows.Any(row => Convert.ToString(row[0]).StartsWith("Оценка хлыстов по сумме длин")), "No distinct estimate heading");
        Check(rows.Any(row => Convert.ToString(row[0]).Contains("статическая модель")), "Engineering limits absent from export");
        using (var staged = new QuantityXlsxFile(Path.Combine(args[0], "frame_exact.xlsx"), "Подсистема", rows))
        { staged.Publish(); staged.Complete(); }
        var empty = FrameTableData.Build(new QuantityResult { ok = true, completeness = "partial" }, null, new[] { "Нулевая зона" }, null, false);
        Check(empty.Installed.Count == 1 && (double)empty.Installed[0][8] == 0 && (double)empty.Installed[0][9] == 0,
            "Explicit known zero table does not show zero");
        Check(empty.Scope.Contains("Нулевая зона") && empty.Information.Any(text => text.Contains("нет созданных элементов")),
            "Zero lost source scope or explanation");
        Check(!empty.Complete, "Partial engineering coverage promoted to complete");
        var unknownStock = new QuantityResult { ok = true, completeness = "partial" };
        unknownStock.rows.Add(new QuantityRow { basis = "estimate", role = "rail_stock_est", quantity = 2 });
        Check(Convert.ToString(FrameTableData.Build(unknownStock, null, new[] { "А" }, null, true).Estimates[0][7]) == "не определена",
            "Unknown stock length became zero or inapplicable");
        bool refused = false;
        try { FrameTableData.Build(new QuantityResult { ok = false }, null, new[] { "А" }, null, true); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "Failed source report rendered");
        Console.WriteLine("Frame table C# checks: " + checks);
        return 0;
    }
}
