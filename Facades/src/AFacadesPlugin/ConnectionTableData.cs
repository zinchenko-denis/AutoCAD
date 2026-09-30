using System;
using System.Collections.Generic;
using System.Globalization;
using FacadeSafety;

namespace AFacadesPlugin
{
    // Read-only presentation of the stored, freshness-checked geometry snapshot.
    // No CAD access, candidate search, catalogue choice or engineering inference.
    internal sealed class ConnectionTableData
    {
        internal const string Title = "Паспорт соединений — геометрия схемы";
        internal static readonly string[] Headers = { "Зона", "Элемент / марка", "Кандидаты опор, поз.",
            "Интервалы, шт.", "Интервалы, мм", "Свободный участок в начале, мм", "Свободный участок в конце, мм",
            "Закрепление", "Соседние куски / непрерывность", "Справочная идентификация", "Примечание" };
        internal readonly List<object[]> Members = new List<object[]>();
        internal readonly List<string> Messages = new List<string>();
        internal string Scope;
        internal const string Coverage = "Показаны геометрические кандидаты опор и интервалы между ними, а не подтверждённые расчётные пролёты. " +
            "Неподвижные и подвижные закрепления, непрерывность стыков, распределение собственного веса и прочность не подтверждены. " +
            "Совпадение марки с источником не является подбором изделия или допуском к монтажу.";
        internal bool Complete;
        private bool missingPassports;

        internal static ConnectionTableData Build(QuantityResult result, IEnumerable<QuantityReport> reports,
            IEnumerable<string> zoneIds, IEnumerable<string> warnings, bool byZone)
        {
            if (result == null || !result.ok) throw new InvalidOperationException("Нельзя вывести неподтверждённый источник паспорта.");
            var data = new ConnectionTableData { Complete = result.completeness == "complete" };
            var zones = new SortedSet<string>(zoneIds ?? new string[0], StringComparer.Ordinal);
            data.Scope = "Область: зоны «" + string.Join("», «", new List<string>(zones).ToArray()) + "». " +
                (byZone ? "Порядок по зонам. " : "Общий список. ") +
                "Каждая физическая направляющая показана отдельно; выбор элемента включает его зону целиком.";
            var messageSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (var warning in warnings ?? new string[0]) data.Message(messageSet, warning);
            foreach (var issue in result.issues ?? new List<QuantityIssue>()) data.Message(messageSet, issue.message);
            if (result.completeness != "complete")
                data.Message(messageSet, "Исходная ведомость имеет ограничения полноты или номенклатуры. Наличие геометрического паспорта указано отдельно.");
            var rowsByZone = new SortedDictionary<string, List<object[]>>(StringComparer.Ordinal);
            var seenMembers = new HashSet<string>(StringComparer.Ordinal);
            var seenPassportZones = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var seenReports = new HashSet<QuantityReport>();
            var covered = new HashSet<string>(StringComparer.Ordinal);
            foreach (var report in reports ?? new QuantityReport[0])
            {
                if (report == null || report.kind != "frame" || !seenReports.Add(report)) continue;
                var reportZones = new SortedSet<string>(report.zone_ids ?? new List<string>(), StringComparer.Ordinal);
                reportZones.IntersectWith(zones);
                if (reportZones.Count == 0) continue;
                covered.UnionWith(reportZones);
                var elements = new Dictionary<string, QuantityElement>(StringComparer.Ordinal);
                foreach (var element in report.elements ?? new List<QuantityElement>())
                    if (element != null && !string.IsNullOrEmpty(element.element_id)) elements[element.element_id] = element;
                var passportZones = new HashSet<string>(StringComparer.Ordinal);
                foreach (var passport in report.connection_passports ?? new List<QuantityConnectionPassport>())
                {
                    if (passport == null) continue;
                    var selected = new SortedSet<string>(passport.zone_ids ?? new List<string>(), StringComparer.Ordinal);
                    selected.IntersectWith(reportZones);
                    if (selected.Count == 0) continue;
                    passportZones.UnionWith(selected);
                    // Retained passports can occur in several reports with different
                    // scopes. Deduplicate physical members, never drop a whole passport
                    // just because another selected zone has used the same identity.
                    HashSet<string> earlierZones;
                    string passportId = passport.passport_id ?? report.run_id ?? "";
                    if (!seenPassportZones.TryGetValue(passportId, out earlierZones))
                        seenPassportZones[passportId] = earlierZones = new HashSet<string>(StringComparer.Ordinal);
                    if (passport.schema != "aframe_connection_passport/1" || passport.status != "inventory_only")
                    {
                        foreach (var zone in selected)
                            if (earlierZones.Add(zone))
                            data.Missing(rowsByZone, zone, "Паспорт не сформирован. " +
                                (string.IsNullOrWhiteSpace(passport.reason) ? "Эта схема не поддержана геометрическим паспортом." : passport.reason));
                        continue;
                    }
                    data.Message(messageSet, "Геометрический паспорт: пресет Вектор-1, вертикальная схема, керамогранит. " +
                        "Соответствие всей схемы типу 1 альбома не утверждается.");
                    data.Message(messageSet, "Редакция справочного реестра: " + Value(passport.catalog_revision) + ".");
                    var references = new Dictionary<string, QuantityConnectionReference>(StringComparer.Ordinal);
                    foreach (var reference in passport.references ?? new List<QuantityConnectionReference>())
                        if (reference != null && !string.IsNullOrEmpty(reference.reference_id)) references[reference.reference_id] = reference;
                    var sources = new Dictionary<string, QuantityConnectionSource>(StringComparer.Ordinal);
                    foreach (var source in passport.sources ?? new List<QuantityConnectionSource>())
                        if (source != null && !string.IsNullOrEmpty(source.source_id))
                        {
                            sources[source.source_id] = source;
                            data.Message(messageSet, "Справочный источник: " + source.source_id + "; редакция " + Value(source.edition) +
                                "; " + Value(source.locator) + "; SHA-256 " + Value(source.sha256) + ".");
                        }
                    var joints = JointIndex(passport.joints);
                    var memberZones = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var member in passport.members ?? new List<QuantityConnectionMember>())
                    {
                        if (member == null || !selected.Contains(member.zone_id)) continue;
                        memberZones.Add(member.zone_id);
                        if (!seenMembers.Add(member.rail_element_id ?? "")) continue;
                        QuantityElement element;
                        elements.TryGetValue(member.rail_element_id ?? "", out element);
                        string mark = element == null || string.IsNullOrWhiteSpace(element.mark) ? "марка не указана" : element.mark;
                        var intervals = new List<string>();
                        foreach (double interval in member.intervals_mm ?? new List<double>()) intervals.Add(Number(interval));
                        List<string> memberJoints;
                        string jointText = joints.TryGetValue(member.rail_element_id ?? "", out memberJoints) ?
                            string.Join("; ", memberJoints.ToArray()) : "Соседние куски на той же оси не найдены";
                        var handles = new List<string>();
                        if (element != null)
                            foreach (var entity in element.cad_entities ?? new List<QuantityCadEntity>())
                                if (entity != null && !string.IsNullOrWhiteSpace(entity.handle)) handles.Add(entity.handle);
                        AddRow(rowsByZone, member.zone_id, new object[] { member.zone_id,
                            mark + (handles.Count == 0 ? "" : " / CAD " + string.Join(", ", handles.ToArray())),
                            member.support_count, member.span_count, intervals.Count == 0 ? "—" : string.Join("; ", intervals.ToArray()),
                            member.support_count == 0 ? (object)"Опор-кандидатов нет" : member.bottom_free_mm,
                            member.support_count == 0 ? (object)"Опор-кандидатов нет" : member.top_free_mm,
                            "Не определено: неподвижное / подвижное",
                            jointText + ". Непрерывность не подтверждена", Match(member.profile_match, references, sources),
                            "Элемент: " + (member.rail_element_id ?? "идентификатор не указан") + ". " + SupportNote(member) });
                    }
                    foreach (var zone in selected)
                        if (earlierZones.Add(zone) && !memberZones.Contains(zone))
                            AddRow(rowsByZone, zone, new object[] { zone, "Паспорт сформирован: направляющих в этой зоне нет", 0, "—", "—", "—", "—",
                                "Не определено", "Не определено", "—", "Это ноль направляющих в геометрическом паспорте; прочие элементы учитываются в виде «Элементы»." });
                    foreach (var issue in passport.issues ?? new List<QuantityConnectionIssue>())
                        if (issue != null) data.Message(messageSet, issue.message + (issue.count > 0 ? " Количество: " + issue.count + "." : ""));
                }
                foreach (var zone in reportZones)
                    if (!passportZones.Contains(zone))
                        data.Missing(rowsByZone, zone, "Паспорт не сформирован для этого источника: ручная регистрация или прежняя генерация. " +
                            "Количество элементов сохранено в виде «Элементы»; отсутствие паспорта не означает нулевой состав.");
            }
            foreach (var zone in zones)
                if (!covered.Contains(zone)) data.Missing(rowsByZone, zone, "Паспорт не сформирован: нет подтверждённого источника геометрии соединений.");
            foreach (var pair in rowsByZone) data.Members.AddRange(pair.Value);
            if (data.Members.Count == 0)
            {
                data.Complete = false;
                data.missingPassports = true;
                data.Members.Add(new object[] { "—", "Паспорт не сформирован", "не определено", "не определено", "—", "—", "—",
                    "Не определено", "Не определено", "Не определено", "Нет подтверждённого источника геометрии соединений. Это не нулевой результат." });
            }
            return data;
        }

        internal List<object[]> Rows(string note)
        {
            var rows = new List<object[]> { new object[] { Title }, new object[] { Scope }, new object[] { Coverage },
                new object[] { missingPassports ? "Наличие паспорта: для части выбранных источников он не сформирован; причины указаны ниже." :
                    "Наличие паспорта: паспорта выбранных зарегистрированных источников представлены. Ограничения и неучтённые объекты указаны отдельно." }, Headers };
            rows.AddRange(Members);
            foreach (var message in Messages) rows.Add(new object[] { message });
            rows.Add(new object[] { "Примечание пользователя: " + (note ?? "") });
            return rows;
        }

        private void Missing(SortedDictionary<string, List<object[]>> rows, string zone, string reason)
        {
            Complete = false;
            missingPassports = true;
            AddRow(rows, zone, new object[] { zone, "Паспорт не сформирован", "не определено", "не определено", "—", "—", "—",
                "Не определено", "Не определено", "Не определено", reason });
        }
        private static void AddRow(SortedDictionary<string, List<object[]>> rows, string zone, object[] row)
        {
            List<object[]> list;
            if (!rows.TryGetValue(zone, out list)) rows[zone] = list = new List<object[]>();
            list.Add(row);
        }
        private void Message(HashSet<string> seen, string value)
        { if (!string.IsNullOrWhiteSpace(value) && seen.Add(value)) Messages.Add(value); }

        private static Dictionary<string, List<string>> JointIndex(IEnumerable<QuantityConnectionJoint> joints)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var joint in joints ?? new QuantityConnectionJoint[0])
            {
                if (joint == null) continue;
                string geometry = joint.gap_mm < 0 ? "перекрытие проекций " + Number(-joint.gap_mm) + " мм" : "зазор " + Number(joint.gap_mm) + " мм";
                AddJoint(result, joint.first_rail_element_id, joint.second_rail_element_id, geometry);
                AddJoint(result, joint.second_rail_element_id, joint.first_rail_element_id, geometry);
            }
            return result;
        }
        private static void AddJoint(Dictionary<string, List<string>> result, string id, string other, string geometry)
        {
            if (string.IsNullOrEmpty(id)) return;
            List<string> list;
            if (!result.TryGetValue(id, out list)) result[id] = list = new List<string>();
            list.Add((other ?? "элемент не определён") + ": " + geometry);
        }
        private static string SupportNote(QuantityConnectionMember member)
        {
            var positions = new List<string>();
            foreach (var support in member.supports ?? new List<QuantityConnectionSupport>())
                if (support != null) positions.Add(Number(support.offset_mm) + " мм: " +
                    string.Join(", ", (support.bracket_element_ids ?? new List<string>()).ToArray()));
            return (positions.Count == 0 ? "Геометрические кандидаты опор отсутствуют. " :
                "Кандидаты от начала направляющей: " + string.Join("; ", positions.ToArray()) + ". ") +
                "Совпадение положения не подтверждает крепление. Свободные участки — от низа/верха направляющей до крайнего кандидата опоры; это не вынос фасада.";
        }
        private static string Match(QuantityConnectionProfileMatch match,
            Dictionary<string, QuantityConnectionReference> references, Dictionary<string, QuantityConnectionSource> sources)
        {
            if (match == null || match.status != "matched_source_identity" || match.reference_ids == null || match.reference_ids.Count == 0)
                return "Марка не сопоставлена с источником; изделие не подобрано";
            var descriptions = new List<string>();
            foreach (var id in match.reference_ids)
            {
                QuantityConnectionReference reference;
                if (!references.TryGetValue(id, out reference)) { descriptions.Add("Источник марки не определён"); continue; }
                QuantityConnectionSource source;
                sources.TryGetValue(reference.source_id ?? "", out source);
                var pages = new List<string>();
                foreach (int page in reference.pdf_pages ?? new List<int>()) pages.Add(page.ToString(CultureInfo.InvariantCulture));
                descriptions.Add((reference.designation ?? "марка не указана") + " — " +
                    (source == null || string.IsNullOrWhiteSpace(source.locator) ? "источник не указан" : source.locator) +
                    (pages.Count == 0 ? "" : ", PDF стр. " + string.Join(", ", pages.ToArray())));
            }
            return "Справочное совпадение марки: " + string.Join("; ", descriptions.ToArray()) +
                ". Характеристики не импортированы; изделие не подобрано";
        }
        private static string Number(double value) { return value.ToString("R", CultureInfo.InvariantCulture); }
        private static string Value(string value) { return string.IsNullOrWhiteSpace(value) ? "не указан" : value; }
    }
}
