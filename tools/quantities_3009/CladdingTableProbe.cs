using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AFacadesPlugin;
using FacadeSafety;

internal static class CladdingTableProbe
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }

    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        var report = new QuantityReport { run_id = "run-1" };
        var group = new QuantityCuttingGroup { group_id = "batch-1" };
        group.scope_zone_ids.AddRange(new[] { "А", "Б" });
        group.parameters.Add("kerf", 0.1234567891234567);
        group.parameters.Add("tile_w_mm", 600);
        report.cutting.Add(group);
        var result = new QuantityResult { ok = true, completeness = "complete" };
        result.rows.Add(new QuantityRow {
            zone_id = "А", material = "Керамогранит & <серия>", type = "Тип 1", mark = "М1",
            product_id = "P-1", color = "Серый", piece_kind = "shaped", shape_id = "shape-1",
            width_mm = 600, height_mm = 1200, orientation = "vertical", quantity = 2,
            area_m2 = 0.1234567891234567, note = "=SUM(1,2)"
        });
        result.rows.Add(new QuantityRow {
            zone_id = "Б", material = null, type = null, color = null, piece_kind = "cut",
            quantity = 3, area_m2 = null
        });
        result.rows.Add(new QuantityRow {
            basis = "cutting", role = "blanks", quantity = 17, area_m2 = 12.24,
            width_mm = 600, height_mm = 1200, note = "Вся группа А + Б"
        });
        result.issues.Add(new QuantityIssue { code = "unknown-material", message = "Материал не указан", element_id = "E-2" });
        var data = CladdingTableData.Build(result, new[] { report, report }, new[] { "Б", "А", "А" },
            new[] { "Нет подтверждения марки", "Нет подтверждения марки" }, true, true);
        Check(data.Installed.Count == 3, "Two installed rows plus one subtotal");
        Check(data.Cutting.Count == 1, "Cutting is separate from installed");
        Check((double)data.Installed[2][7] == 5, "Subtotal excludes 17 cutting blanks");
        Check((string)data.Installed[1][8] == "не определена", "Unknown area is not zero");
        Check((string)data.Installed[2][8] == "не определена", "Mixed known/unknown subtotal remains unknown");
        Check((string)data.Installed[1][1] == "не указано", "Unknown material is explicit");
        Check((string)data.Installed[1][3] == "не указано", "Unknown color is explicit");
        Check((double)data.Installed[0][8] == 0.1234567891234567, "Renderer preserves exact numeric area");
        Check((string)data.Installed[0][5] == "фигурная", "Shaped piece kind survives");
        Check(((string)data.Installed[0][4]).Contains("Ф-1") && data.FormShapes["Ф-1"] == "shape-1",
            "Readable form mark retains original shape identity");
        Check(((string)data.Installed[0][2]).Contains("М1") && ((string)data.Installed[0][2]).Contains("P-1"), "Mark and product identity survive");
        Check(!data.Complete && data.Warnings.Count == 2, "Warnings downgrade completeness and deduplicate");
        Check(data.CuttingDetails.Count(x => x.StartsWith("Группа раскроя")) == 1, "Repeated report does not duplicate cutting details");
        Check(data.CuttingDetails.Any(x => x.Contains("0.123456789")), "Cutting decimal remains precise and culture independent");
        Check(data.Scope.Contains("«А», «Б»"), "Selected zones are ordered and unique");
        Check(data.Scope.Contains("целиком"), "Detail selection means whole zone explicitly");
        var rows = data.Rows("  Примечание & <узел> =1+1  ");
        Check(rows.Any(x => x.Length == 1 && Convert.ToString(x[0]).Contains("geometry_only")) == false, "User text does not leak internal enum");
        Check(rows.Any(x => x.Length == 1 && Convert.ToString(x[0]).Contains("Неопределённость:")), "Warnings are in export rows");
        Check(rows.Any(x => x.Length == 1 && Convert.ToString(x[0]).Contains("Раскрой —")), "Cutting has its own heading");
        Check(Convert.ToString(rows[rows.Count - 1][0]).EndsWith("  "), "User note whitespace preserved");
        XlsxWriter.WriteExact(Path.Combine(args[0], "cladding_exact.xlsx"), "Облицовка", rows);

        var numeric = new List<object[]> {
            new object[] { 0.1234567891234567, 123456789.12345678, null, "=SUM(1,2)", "  A & <B>  " },
            new object[] { 0.0000000123456789, 5, "не определена" }
        };
        XlsxWriter.WriteExact(Path.Combine(args[0], "numeric_exact.xlsx"), "Числа", numeric);
        XlsxWriter.Write(Path.Combine(args[0], "numeric_legacy.xlsx"), "Числа", numeric);

        bool rejected = false;
        try { CladdingTableData.Build(new QuantityResult { ok = false }, new QuantityReport[0], new string[0], null, false, false); }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "Unconfirmed result cannot be rendered");
        var simple = new QuantityResult { ok = true, completeness = "complete" };
        simple.rows.Add(new QuantityRow { quantity = 1, area_m2 = 0.36 });
        var noCutting = CladdingTableData.Build(simple, new QuantityReport[0], new[] { "А" }, null, false, true);
        Check(noCutting.Complete && (double)noCutting.Installed[1][8] == 0.36, "Known area subtotal remains numeric");
        Check(noCutting.CuttingDetails.Any(x => x.Contains("не определена")), "Absent cutting cannot imply zero blanks");
        Check(noCutting.Scope.Contains("Общая группировка"), "Global scope is explicit");
        simple.completeness = "partial";
        Check(!CladdingTableData.Build(simple, new QuantityReport[0], new[] { "А" }, null, true, false).Complete,
            "Partial result never upgrades to complete");
        var forms = new QuantityResult { ok = true, completeness = "complete" };
        forms.rows.Add(new QuantityRow { piece_kind = "shaped", shape_id = "second", quantity = 1 });
        forms.rows.Add(new QuantityRow { piece_kind = "shaped", shape_id = "first", quantity = 1 });
        forms.rows.Add(new QuantityRow { piece_kind = "shaped", shape_id = "second", quantity = 1 });
        foreach (string role in new[] { "blanks_total", "blanks_cut", "waste" })
            forms.rows.Add(new QuantityRow { basis = "cutting", role = role, quantity = 1 });
        var marked = CladdingTableData.Build(forms, new QuantityReport[0], new[] { "А" }, null, true, true);
        Check(marked.FormShapes.Count == 2 && marked.FormShapes["Ф-1"] == "first" && marked.FormShapes["Ф-2"] == "second",
            "Different shapes have distinct deterministic display marks");
        Check(Convert.ToString(marked.Installed[0][4]) == Convert.ToString(marked.Installed[2][4]),
            "Same shape keeps the same display mark");
        Check(marked.Cutting.Select(row => Convert.ToString(row[5])).SequenceEqual(new[] { "заготовки всего", "заготовки для подрезок", "отходы" }),
            "Actual producer cutting roles have readable labels");
        Check(marked.Cutting.All(row => Convert.ToString(row[8]) == "—"),
            "Installed area column does not imply unknown area for cutting stock or waste");
        Check(Convert.ToString(marked.Cutting[2][4]) == "—", "Waste does not imply missing physical piece dimensions");
        Check(CladdingTableData.Display(0.0000000123456789) != "0", "Nonzero displayed area must not become zero");
        var empty = CladdingTableData.Build(new QuantityResult { ok = true, completeness = "complete" },
            new QuantityReport[0], new[] { "Пустая зона" }, null, true, false);
        Check(empty.Complete && empty.Installed.Count == 1 && (double)empty.Installed[0][7] == 0 &&
            (double)empty.Installed[0][8] == 0, "Known empty composition must show an exact zero subtotal");
        Check(empty.Scope.Contains("Пустая зона"), "Known empty table must retain its source zone");
        Check(empty.Rows("").Any(row => Convert.ToString(row[0]).Contains("нет деталей облицовки")),
            "Known empty table must explain the zero result");
        string stagedDirectory = Path.Combine(args[0], "staged");
        Directory.CreateDirectory(stagedDirectory);
        string existing = Path.Combine(stagedDirectory, "existing.xlsx");
        File.WriteAllText(existing, "original workbook bytes");
        using (var staged = new QuantityXlsxFile(existing, numeric))
            Check(File.ReadAllText(existing) == "original workbook bytes", "Staging changed existing target before Publish");
        Check(File.ReadAllText(existing) == "original workbook bytes" && Directory.GetFiles(stagedDirectory).Length == 1,
            "Cancellation left temporary files or damaged old workbook");
        using (var staged = new QuantityXlsxFile(existing, numeric))
        {
            staged.Publish();
            Check(File.ReadAllBytes(existing)[0] == 'P', "Publish did not create ZIP workbook");
        }
        Check(File.ReadAllText(existing) == "original workbook bytes" && Directory.GetFiles(stagedDirectory).Length == 1,
            "Rollback did not restore exact previous file");
        string fresh = Path.Combine(stagedDirectory, "fresh.xlsx");
        using (var staged = new QuantityXlsxFile(fresh, numeric)) { staged.Publish(); }
        Check(!File.Exists(fresh) && Directory.GetFiles(stagedDirectory).Length == 1,
            "Rollback left a new uncommitted workbook");
        using (var staged = new QuantityXlsxFile(existing, numeric)) { staged.Publish(); staged.Complete(); }
        Check(File.ReadAllBytes(existing)[0] == 'P' && Directory.GetFiles(stagedDirectory).Length == 1,
            "Completion failed or left temporary/backup files");
        using (var staged = new QuantityXlsxFile(fresh, numeric)) { staged.Publish(); staged.Complete(); }
        Check(File.Exists(fresh) && Directory.GetFiles(stagedDirectory).Length == 2, "New workbook completion failed");
        byte[] previous = File.ReadAllBytes(existing);
        rejected = false;
        try { using (var staged = new QuantityXlsxFile(existing, new List<object[]> { new object[] { double.NaN } })) { } }
        catch (InvalidDataException) { rejected = true; }
        Check(rejected && previous.SequenceEqual(File.ReadAllBytes(existing)) && Directory.GetFiles(stagedDirectory).Length == 2,
            "Invalid numeric export changed prior workbook or left a truncated temporary file");
        foreach (string invalid in new[] { "до\u000bпосле", "до\ud800после" })
        {
            rejected = false;
            try { using (var staged = new QuantityXlsxFile(existing, new List<object[]> { new object[] { invalid } })) { } }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected && previous.SequenceEqual(File.ReadAllBytes(existing)) && Directory.GetFiles(stagedDirectory).Length == 2,
                "Invalid XML string accepted or damaged the previous workbook");
        }
        Console.WriteLine("CladdingTable C# checks: " + checks);
        return 0;
    }
}
