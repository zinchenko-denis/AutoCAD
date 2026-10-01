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
    private const string SharedPrefix = "Монтажная принадлежность не определена: кронштейн ";
    private const string MemberShared = "Монтажная принадлежность не определена: общих кандидатов-кронштейнов — ";
    private static T Clone<T>(T value) { return Json.Deserialize<T>(Json.Serialize(value)); }
    private static Dictionary<string, HashSet<string>> SharedOracle(IEnumerable<QuantityReport> reports, ISet<string> selected)
    {
        var links = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var report in reports)
            foreach (var passport in report.connection_passports)
                foreach (var member in passport.members)
                {
                    if (!selected.Contains(member.zone_id)) continue;
                    foreach (var support in member.supports)
                        foreach (string id in support.bracket_element_ids)
                        {
                            HashSet<string> members;
                            if (!links.TryGetValue(id, out members)) links[id] = members = new HashSet<string>(StringComparer.Ordinal);
                            members.Add(member.rail_element_id);
                        }
                }
        return links.Where(p => p.Value.Count > 1).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }
    private static object CheckSharedCase(string name, QuantityReport[] reports, string[] selected, string output = null)
    {
        string before = Json.Serialize(reports);
        var result = FacadeQuantitiesCore.BuildRows(reports, selected, true, true);
        Check(result.ok, name + ": source rejected: " + Json.Serialize(result.issues));
        var diagnostics = new ConnectionTableDiagnostics();
        var data = ConnectionTableData.Build(result, reports, selected, null, true, diagnostics);
        var selectedSet = new HashSet<string>(selected, StringComparer.Ordinal);
        var expected = SharedOracle(reports, selectedSet);
        var messages = data.Messages.Where(m => m.StartsWith(SharedPrefix, StringComparison.Ordinal)).ToList();
        var expectedMembers = reports.SelectMany(r => r.connection_passports).SelectMany(p => p.members)
            .Where(m => selectedSet.Contains(m.zone_id)).GroupBy(m => m.rail_element_id).Select(g => g.First()).ToList();
        var rows = data.Members.ToDictionary(row => Convert.ToString(row[10]).Split(new[] { ". " }, 2, StringSplitOptions.None)[0].Substring("Элемент: ".Length),
            row => row, StringComparer.Ordinal);
        Check(rows.Count == expectedMembers.Count, name + ": duplicated/lost member");
        Check(messages.All(message => message.Length <= ConnectionTableData.SharedCandidateMessageLimit),
            name + ": shared message exceeds output chunk limit");
        Check(expected.Count > 0 || messages.Count == 0, name + ": false shared bracket warning");
        // Independent expected fanout comes from physical IDs; no coordinates or labels.
        foreach (var pair in expected)
        {
            var matching = messages.Where(m => m.StartsWith(SharedPrefix + pair.Key + " —", StringComparison.Ordinal)).ToList();
            Check(matching.Count > 0 && matching[0].Contains(" — геометрический кандидат для направляющих "),
                name + ": shared bracket first message absent");
            Check(matching.Skip(1).All(message => message.Contains(" — продолжение списка направляющих ")),
                name + ": continuation lost its bracket identity/context");
            var ids = matching.SelectMany(message => message.Split(new[] {
                " — геометрический кандидат для направляющих ", " — продолжение списка направляющих " }, StringSplitOptions.None)[1]
                .Split(new[] { ". Совпадение" }, StringSplitOptions.None)[0].Split(new[] { ", " }, StringSplitOptions.None)).ToArray();
            Check(new HashSet<string>(ids, StringComparer.Ordinal).SetEquals(pair.Value) && ids.Length == pair.Value.Count,
                name + ": incorrect/repeated physical peer IDs across chunks");
        }
        int links = 0;
        foreach (var member in expectedMembers)
        {
            var row = rows[member.rail_element_id];
            var expectedBrackets = member.supports.SelectMany(support => support.bracket_element_ids).Where(expected.ContainsKey).ToList();
            string text = Convert.ToString(row[10]) + Convert.ToString(row[7]);
            Check(text.Contains(MemberShared) == (expectedBrackets.Count > 0), name + ": missing or false member warning");
            if (expectedBrackets.Count > 0)
            {
                string actual = text.Split(new[] { MemberShared }, StringSplitOptions.None)[1]
                    .Split(new[] { ". Общие" }, StringSplitOptions.None)[0];
                Check(actual == expectedBrackets.Count.ToString(CultureInfo.InvariantCulture),
                    name + ": member warning lost the shared candidate count");
                Check(text.Split(new[] { MemberShared }, StringSplitOptions.None).Length == 2,
                    name + ": member warning duplicated across cells");
            }
            Check(Convert.ToInt32(row[2]) == member.support_count && Convert.ToInt32(row[3]) == member.span_count,
                name + ": candidate/interval count changed");
            Check(Convert.ToString(row[7]).StartsWith("Не определено: неподвижное / подвижное", StringComparison.Ordinal) && Convert.ToString(row[8]).Contains("Непрерывность не подтверждена"),
                name + ": geometry promoted to engineering model");
            links += member.supports.Sum(support => support.bracket_element_ids.Count);
        }
        Check(diagnostics.MembersIndexed == expectedMembers.Count && diagnostics.SupportLinksIndexed == links &&
            diagnostics.SharedBrackets == expected.Count && diagnostics.SharedMemberLinks == expected.Sum(p => p.Value.Count),
            name + ": additional index revisited retained members or became nonlinear");
        Check(before == Json.Serialize(reports), name + ": source DTO mutated");
        if (output != null) Export(output, data);
        return new { name, members = expectedMembers.Count, links, shared_brackets = expected.Count,
            shared_member_links = diagnostics.SharedMemberLinks, messages = messages.Count,
            warning_characters = messages.Sum(m => m.Length) + data.Members.Sum(r => Convert.ToString(r[10]).Length),
            max_shared_message_characters = messages.Count == 0 ? 0 : messages.Max(m => m.Length),
            members_indexed = diagnostics.MembersIndexed, links_indexed = diagnostics.SupportLinksIndexed };
    }
    private static QuantityReport Fanout(QuantityReport source, int count)
    {
        // Synthetic validated DTO stress case, separate from the real engine fixture.
        var report = Clone(source);
        var passport = report.connection_passports[0];
        string zone = passport.members[0].zone_id;
        var originalRail = report.elements.First(e => e.element_id == passport.members[0].rail_element_id);
        string bracketId = passport.members[0].supports[0].bracket_element_ids[0];
        var bracket = report.elements.First(e => e.element_id == bracketId);
        var originalMember = passport.members[0];
        report.zone_ids = passport.zone_ids = new List<string> { zone };
        report.elements = new List<QuantityElement> { bracket };
        report.estimates.Clear(); report.cutting.Clear();
        passport.members = new List<QuantityConnectionMember>();
        passport.joints.Clear();
        for (int i = 0; i < count; i++)
        {
            var rail = Clone(originalRail);
            rail.element_id = passport.source_run_id + ":rails:" + i;
            rail.cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity { handle = "FAN" + i, role = "primary", fingerprint = "synthetic:" + i } };
            report.elements.Add(rail);
            var member = Clone(originalMember);
            member.rail_element_id = rail.element_id;
            member.support_count = 1; member.span_count = 0; member.intervals_mm.Clear();
            member.bottom_free_mm = member.top_free_mm = rail.length_mm.Value / 2;
            member.supports = new List<QuantityConnectionSupport> { new QuantityConnectionSupport {
                offset_mm = rail.length_mm.Value / 2, bracket_element_ids = new List<string> { bracketId } } };
            passport.members.Add(member);
        }
        passport.summary = new QuantityConnectionSummary { members = count, support_positions = count,
            support_links = count, matched_profile_references = count };
        return report;
    }
    private static QuantityReport ManyBrackets(QuantityReport source, int count)
    {
        // Opposite stress axis: many shared bracket IDs on just two physical rails.
        var report = Fanout(source, 2);
        var passport = report.connection_passports[0];
        var original = report.elements.First(e => e.role == "bracket");
        report.elements.Remove(original);
        var ids = new List<string>();
        for (int i = 0; i < count; i++)
        {
            var bracket = Clone(original);
            bracket.element_id = passport.source_run_id + ":brackets:" + i;
            bracket.cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity {
                handle = "MANY" + i, role = "primary", fingerprint = "synthetic:" + i } };
            report.elements.Add(bracket); ids.Add(bracket.element_id);
        }
        foreach (var member in passport.members)
            member.supports[0].bracket_element_ids = new List<string>(ids);
        passport.summary.support_links = count * 2;
        return report;
    }
    private static int SharedReport(string reportPath, string outPath)
    {
        Directory.CreateDirectory(outPath);
        var report = Json.Deserialize<QuantityReport>(File.ReadAllText(reportPath));
        var cases = new List<object>();
        cases.Add(CheckSharedCase("actual_engine_all_zones", new[] { report }, report.zone_ids.ToArray(), Path.Combine(outPath, "shared_support.xlsx")));
        foreach (string zone in report.zone_ids)
            cases.Add(CheckSharedCase("actual_engine_selected_" + zone, new[] { report }, new[] { zone }));
        var retained = Clone(report); retained.run_id = retained.report_id = "retained-report";
        cases.Add(CheckSharedCase("retained_duplicate_passports", new[] { report, retained, report }, report.zone_ids.ToArray()));
        foreach (string zone in report.zone_ids)
            cases.Add(CheckSharedCase("retained_selected_" + zone, new[] { retained, report }, new[] { zone }));
        foreach (int count in new[] { 1, 100, 1000 })
        {
            var fanout = Fanout(report, count);
            cases.Add(CheckSharedCase("synthetic_single_bracket_fanout_" + count, new[] { fanout }, fanout.zone_ids.ToArray(),
                Path.Combine(outPath, "fanout_" + count + ".xlsx")));
        }
        var many = ManyBrackets(report, 320);
        cases.Add(CheckSharedCase("synthetic_two_members_320_shared_brackets", new[] { many }, many.zone_ids.ToArray(),
            Path.Combine(outPath, "many_brackets_320.xlsx")));
        // Locate the boundary using the original note, independent of the new
        // warning. The added text must never overflow a previously valid cell.
        var originalNote = typeof(ConnectionTableData).GetMethod("SupportNote", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var noteMember = Clone(many.connection_passports[0].members[0]);
        noteMember.supports[0].bracket_element_ids.Clear();
        int baselineLength = ("Элемент: " + noteMember.rail_element_id + ". " + (string)originalNote.Invoke(null, new object[] { noteMember })).Length;
        int nearCount = 0;
        while (baselineLength <= 32680)
        {
            string id = many.connection_passports[0].source_run_id + ":brackets:" + nearCount;
            baselineLength += id.Length + (nearCount == 0 ? 0 : 2);
            nearCount++;
        }
        var near = ManyBrackets(report, nearCount);
        noteMember = near.connection_passports[0].members[0];
        Check(baselineLength == ("Элемент: " + noteMember.rail_element_id + ". " + (string)originalNote.Invoke(null, new object[] { noteMember })).Length &&
            baselineLength <= 32767, "Boundary fixture already exceeds legacy XLSX limit or independent note-length accounting disagrees");
        cases.Add(CheckSharedCase("synthetic_nearly_full_legacy_note", new[] { near }, near.zone_ids.ToArray(),
            Path.Combine(outPath, "many_brackets_near_limit.xlsx")));
        // Distinct physical bracket IDs at one position in the real fixture do not
        // become the same identity. Separate the peer IDs while preserving offsets.
        var separate = Clone(report);
        foreach (var passport in separate.connection_passports)
        {
            var bracketById = separate.elements.Where(e => e.role == "bracket").ToDictionary(e => e.element_id);
            int next = bracketById.Count;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in passport.members)
                foreach (var support in member.supports)
                    for (int i = 0; i < support.bracket_element_ids.Count; i++)
                    {
                        string id = support.bracket_element_ids[i];
                        if (seen.Add(id)) continue;
                        var other = Clone(bracketById[id]);
                        other.element_id = passport.source_run_id + ":brackets:" + next++;
                        other.cad_entities = new List<QuantityCadEntity> { new QuantityCadEntity {
                            handle = "DISTINCT" + next, role = "primary", fingerprint = "synthetic:" + next } };
                        separate.elements.Add(other);
                        support.bracket_element_ids[i] = other.element_id;
                    }
        }
        cases.Add(CheckSharedCase("distinct_ids_at_same_positions", new[] { separate }, separate.zone_ids.ToArray()));
        File.WriteAllText(Path.Combine(outPath, "shared_cases.json"), Json.Serialize(new { status = "PASS", checks,
            cases, live_autocad_checked = false, scale_origin = "synthetic validated DTO; main fixture is actual engine/producer" }));
        Console.WriteLine("Shared candidate table checks: " + checks + " PASS");
        return 0;
    }
    public static int Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        if (args[0] == "--report") return RealReport(args[1], args[2]);
        if (args[0] == "--shared-report") return SharedReport(args[1], args[2]);
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
