using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFacadesPlugin;
using FacadeSafety;

internal static class ConnectionTableProbe
{
    private static int checks;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static string Flat(object[] row) { return string.Join(" | ", row.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)).ToArray()); }
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
    private static QuantityReport Report(string id, string zone, int supports)
    {
        var report = new QuantityReport { kind = "frame", run_id = id, report_id = id, zone_ids = new List<string> { zone } };
        report.elements.Add(new QuantityElement { element_id = id + ":rail", zone_id = zone, role = "rail", mark = "ГП-40-40-1,2",
            cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity { handle = "A9" } } });
        var passport = new QuantityConnectionPassport { passport_id = id + ":connections", status = "inventory_only",
            scheme = "vertical", catalog_scope = "reference_project_only", catalog_revision = new string('a', 64), zone_ids = new List<string> { zone } };
        passport.sources.Add(new QuantityConnectionSource { source_id = "calc", locator = "Расчётный пример", edition = "2026", sha256 = new string('b', 64) });
        passport.references.Add(new QuantityConnectionReference { reference_id = "exact", source_id = "calc", designation = "ГП-40-40-1,2",
            pdf_pages = new List<int> { 3 }, properties_status = "not_imported" });
        var member = new QuantityConnectionMember { rail_element_id = id + ":rail", zone_id = zone,
            support_count = supports, span_count = Math.Max(0, supports - 1), bottom_free_mm = supports == 0 ? 3000 : 123.4567891234567,
            top_free_mm = supports == 0 ? 3000 : 234.5678912345678,
            profile_match = new QuantityConnectionProfileMatch { status = "matched_source_identity", reference_ids = new List<string> { "exact" } } };
        for (int i = 0; i < supports; ++i)
        {
            member.supports.Add(new QuantityConnectionSupport { offset_mm = 1000.1234567 * i,
                bracket_element_ids = new List<string> { id + ":bracket:" + i, id + ":coincident:" + i } });
            if (i > 0) member.intervals_mm.Add(1000.1234567);
        }
        passport.members.Add(member);
        report.connection_passports = new List<QuantityConnectionPassport> { passport };
        return report;
    }
    private static void Export(string path, ConnectionTableData data)
    {
        var view = QuantityTableView.FromConnections(data);
        using (var file = new QuantityXlsxFile(path, "Соединения", view.Rows("  Проверено & <узел> =1+1  ")))
        { file.Publish(); file.Complete(); }
    }
    private static int RealReport(string reportPath, string outPath)
    {
        var report = Json.Deserialize<QuantityReport>(File.ReadAllText(reportPath));
        string before = Json.Serialize(report);
        var result = FacadeQuantitiesCore.BuildRows(new[] { report }, report.zone_ids, true, true);
        Check(result.ok, "Real producer report rejected by common core: " + Json.Serialize(result.issues));
        var data = ConnectionTableData.Build(result, new[] { report }, report.zone_ids, null, true);
        Check(report.connection_passports != null && report.connection_passports.Any(p => p.status == "inventory_only"),
            "Real source fixture has no supported connection passport");
        Check(data.Members.Count == report.connection_passports.Sum(p => p.members.Count), "Real members lost or duplicated in UI");
        foreach (var member in report.connection_passports.SelectMany(p => p.members))
        {
            var row = data.Members.Single(r => Convert.ToString(r[10]).StartsWith("Элемент: " + member.rail_element_id + ". "));
            Check(Convert.ToInt32(row[2]) == member.support_count && Convert.ToInt32(row[3]) == member.span_count,
                "Real member support/span counts changed");
            Check(member.support_count == 0 || (Convert.ToDouble(row[5]) == member.bottom_free_mm && Convert.ToDouble(row[6]) == member.top_free_mm),
                "Real free segment lengths changed");
            foreach (var support in member.supports)
                foreach (var id in support.bracket_element_ids)
                    Check(Convert.ToString(row[10]).Contains(id), "A real physical bracket reference disappeared");
        }
        Export(outPath, data);
        Check(before == Json.Serialize(report), "Presentation mutated real source report");
        Console.WriteLine("Connection real-engine table checks: " + checks);
        return 0;
    }
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        if (args[0] == "--report") return RealReport(args[1], args[2]);
        var result = new QuantityResult { ok = true, completeness = "partial" };
        var first = Report("physical-1", "А", 3);
        first.connection_passports[0].joints.Add(new QuantityConnectionJoint { first_rail_element_id = "physical-1:rail",
            second_rail_element_id = "physical-neighbour", gap_mm = -12.34567891234567, status = "geometric_adjacency_only" });
        var unsupported = Report("unsupported", "Б", 2);
        unsupported.connection_passports[0].status = "unavailable";
        unsupported.connection_passports[0].reason = "Ортогональная схема не поддержана паспортом";
        unsupported.connection_passports[0].members.Clear();
        var old = new QuantityReport { kind = "frame", report_id = "legacy", run_id = "legacy", zone_ids = new List<string> { "В" } };
        old.elements.Add(new QuantityElement { element_id = "manual", role = "rail", zone_id = "В", product_id = null });
        var zeroSupport = Report("unsupported-member", "Г", 0);
        zeroSupport.connection_passports[0].members[0].profile_match = new QuantityConnectionProfileMatch { status = "unknown" };
        string before = Json.Serialize(new[] { first, unsupported, old, zeroSupport });
        var data = ConnectionTableData.Build(result, new[] { first, unsupported, old, zeroSupport, first }, new[] { "Г", "В", "Б", "А", "А" },
            new[] { "Проверить спецификацию", "Проверить спецификацию" }, true);
        Check(data.Members.Count == 4, "Repeated source or selected zone duplicated rows");
        Check(data.Members.Select(r => Convert.ToString(r[0])).SequenceEqual(new[] { "А", "Б", "В", "Г" }), "Deterministic per-zone order lost");
        var row = data.Members[0];
        Check((int)row[2] == 3 && (int)row[3] == 2, "Candidate positions or geometric intervals changed");
        Check(Convert.ToString(row[4]) == "1000.1234567; 1000.1234567", "Interval precision or invariant culture lost");
        Check((double)row[5] == 123.4567891234567 && (double)row[6] == 234.5678912345678, "Free lengths rounded");
        Check(Convert.ToString(row[1]).Contains("CAD A9") && Convert.ToString(row[10]).Contains("physical-1:rail"), "CAD handle or physical identity lost");
        Check(Convert.ToString(row[7]).Contains("Не определено") && Convert.ToString(row[8]).Contains("Непрерывность не подтверждена"), "Geometry promoted to fixed/sliding or continuity");
        Check(Convert.ToString(row[8]).Contains("перекрытие проекций 12.34567891234567") && !Convert.ToString(row[8]).Contains("стык"), "Adjacent pieces promoted to a structural joint");
        Check(Convert.ToString(row[9]).Contains("Справочное совпадение") && Convert.ToString(row[9]).Contains("PDF стр. 3") &&
            Convert.ToString(row[9]).Contains("изделие не подобрано"), "Source identity became a verified product");
        Check(Convert.ToString(row[10]).Contains("physical-1:bracket:0, physical-1:coincident:0"), "Coincident physical brackets collapsed");
        Check(data.Members[1].Skip(2).Take(2).All(x => Convert.ToString(x) == "не определено"), "Unsupported passport became zero");
        Check(Convert.ToString(data.Members[1][10]).Contains("Ортогональная"), "Unavailable reason lost");
        Check(Convert.ToString(data.Members[2][10]).Contains("Количество элементов сохранено") && Convert.ToString(data.Members[2][2]) == "не определено", "Legacy/manual passport suppressed element count or became zero");
        Check((int)data.Members[3][2] == 0 && Convert.ToString(data.Members[3][5]) == "Опор-кандидатов нет" &&
            Convert.ToString(data.Members[3][6]) == "Опор-кандидатов нет", "Unsupported member generated two misleading free lengths");
        Check(Convert.ToString(data.Members[3][9]).Contains("не сопоставлена"), "Unknown profile got a source identity");
        Check(Convert.ToString(data.Members[3][8]).Contains("Соседние куски на той же оси не найдены"), "No-neighbour wording claims structural joints");
        Check(data.Messages.Count(x => x == "Проверить спецификацию") == 1, "Warnings duplicated");
        Check(data.Messages.Count(x => x.StartsWith("Справочный источник: calc; редакция 2026;")) == 1 &&
            data.Messages.Any(x => x.Contains("SHA-256 " + new string('b', 64))), "Source provenance lost or duplicated per member");
        Check(data.Messages.Count(x => x == "Редакция справочного реестра: " + new string('a', 64) + ".") == 1, "Catalogue revision not independently identifiable");
        Check(!data.Complete && data.Rows("").Any(r => Flat(r).Contains("для части выбранных источников")), "Missing passport coverage disappeared");
        Check(before == Json.Serialize(new[] { first, unsupported, old, zeroSupport }), "Presentation changed source passports or product IDs");
        Export(Path.Combine(args[0], "connection_exact.xlsx"), data);
        var allPresent = ConnectionTableData.Build(result, new[] { first }, new[] { "А" }, null, false);
        Check(!allPresent.Complete && allPresent.Rows("").Any(r => Flat(r).Contains("паспорта выбранных зарегистрированных источников представлены")),
            "Unknown nomenclature wrongly made a present geometry passport missing");
        Check(allPresent.Scope.Contains("Общий список") && allPresent.Scope.Contains("отдельно"), "General grouping merges physical members");
        var otherPart = Report("physical-2", "Б", 2);
        otherPart.connection_passports[0].passport_id = first.connection_passports[0].passport_id;
        var retained = ConnectionTableData.Build(result, new[] { first, otherPart }, new[] { "А", "Б" }, null, true);
        Check(retained.Members.Count == 2, "Shared retained passport ID discarded another selected zone");
        var noReports = ConnectionTableData.Build(result, null, new[] { "А" }, null, true);
        Check(noReports.Members.Count == 1 && Convert.ToString(noReports.Members[0][2]) == "не определено", "Absent source became zero");
        bool refused = false;
        try { ConnectionTableData.Build(new QuantityResult { ok = false }, new[] { first }, new[] { "А" }, null, true); }
        catch (InvalidOperationException) { refused = true; }
        Check(refused, "Stale/refused source reached rendering");
        var view = QuantityTableView.FromConnections(data);
        view.UnaccountedRows.Add(new object[] { 1, "FF", "Блок", "Слой", "Не зарегистрирован" });
        var outputRows = view.Rows("=1+1");
        Check(outputRows.Any(r => Flat(r).Contains("НЕУЧТЁННЫЕ")) && outputRows.Any(r => Flat(r).Contains("Не зарегистрирован")), "Unaccounted inventory lost");
        Check(view.Headers.Length == view.Widths.Length && view.PreviewRows.All(r => r.Length == view.Headers.Length), "Table column contract inconsistent");
        var table = new Dictionary<string, object> { { "schema", "facade_quantity_table/1" }, { "owner", "AA" },
            { "kind", "frame" }, { "user_note", "note" }, { "text_height", 250 } };
        Check(QuantityTableIdentity.Matches(table, "facade_quantity_table/1", "AA", "frame", "elements"), "Published pre-view frame table no longer updatable");
        Check(!QuantityTableIdentity.Matches(table, "facade_quantity_table/1", "AA", "frame", "connections"), "Legacy bill can be overwritten with a connection passport");
        table["view"] = "connections";
        Check(QuantityTableIdentity.Matches(table, "facade_quantity_table/1", "AA", "frame", "connections"), "Own connection table is not updatable");
        Check(!QuantityTableIdentity.Matches(table, "facade_quantity_table/1", "AA", "frame", "elements"), "Connection passport can be overwritten with elements");
        Check(!QuantityTableIdentity.Matches(table, "facade_quantity_table/1", "AB", "frame", "connections"), "Copied table owner stamp accepted");
        Check(!QuantityTableIdentity.Matches(table, "facade_quantity_table/1", "AA", "cladding", "connections"), "Cross-kind connection table accepted");
        table.Remove("view"); table.Remove("kind");
        Check(QuantityTableIdentity.Matches(table, "facade_quantity_table/1", "AA", "cladding", "elements"), "Original cladding table compatibility broken");
        Console.WriteLine("Connection table C# checks: " + checks);
        return 0;
    }
}
