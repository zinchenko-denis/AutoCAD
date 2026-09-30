// Actual pure presentation/export code. No AutoCAD table entity or live host is exercised.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using AFacadesPlugin;
using FacadeSafety;

namespace ManualQuantitiesChecks
{
    internal static class ManualOutputProbe
    {
        private static readonly List<object> Cases = new List<object>();
        private static int Failed;
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static void Need(bool value, string reason) { if (!value) throw new Exception(reason); }
        private static void Check(string name, Action action)
        {
            try { action(); Cases.Add(new { name = name, status = "PASS" }); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { Failed++; Cases.Add(new { name = name, status = "FAIL", reason = ex.Message }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        private static ManualQuantityRule Rule()
        {
            return new ManualQuantityRule { rule_id = "explicit-user-rule", revision = "1", kind = "cladding", role = "cladding", adapter = "boundary",
                sample_key = "closed-polyline", fields = new Dictionary<string, ManualQuantityField> {
                    { "mark", new ManualQuantityField { source = "attribute", name = "МАРКА" } },
                    { "type", new ManualQuantityField { source = "literal", value = "ручная плита" } } } };
        }
        private static ManualQuantityObservation Piece(string handle, double x = 0)
        {
            return new ManualQuantityObservation { handle = handle, entity_type = "Polyline", layer = "Фасад & <основной>", sample_key = "closed-polyline",
                fingerprint = "manual:" + handle, cad_fingerprint = "cad:" + handle,
                boundary_points = new[] { new[] { x, 0.0 }, new[] { x + 100, 0.0 }, new[] { x + 100, 200.0 }, new[] { x, 200.0 } },
                attributes = new Dictionary<string, string> { { "МАРКА", "001-К/1,2" } } };
        }
        private static List<ManualQuantityObservation> Inventory()
        {
            var good = Piece("A1");
            var other = Piece("A2"); other.sample_key = "another-explicit-family";
            var generated = Piece("A3"); generated.generated_conflict = true;
            var unsupported = Piece("A4"); unsupported.fingerprint = null; unsupported.cad_fingerprint = null;
            unsupported.extraction_reason = "Дуговой контур не поддержан для измерения.";
            var unknownMark = Piece("A5", 500); unknownMark.attributes.Clear();
            return new List<ManualQuantityObservation> { good, other, generated, unsupported, unknownMark };
        }
        private static QuantityReport Report(string id, string kind, params QuantityElement[] elements)
        { return new QuantityReport { report_id = id, run_id = id, document_id = "one-drawing", algorithm = "output-oracle/1", kind = kind,
            completeness = "partial", engineering_coverage = "geometry_only", zone_ids = new List<string> { "Зона А" }, elements = elements.ToList() }; }
        private static void Export(string directory, string stem, string sheet, QuantityTableView view, string note)
        {
            var rows = view.Rows(note);
            File.WriteAllText(Path.Combine(directory, stem + "_rows.json"), Json.Serialize(rows));
            using (var staged = new QuantityXlsxFile(Path.Combine(directory, stem + ".xlsx"), sheet, rows)) { staged.Publish(); staged.Complete(); }
        }
        private static string Flatten(IEnumerable<object[]> rows)
        { return string.Join("\n", rows.Select(row => string.Join("|", row.Select(value => Convert.ToString(value, CultureInfo.InvariantCulture)).ToArray())).ToArray()); }

        public static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
            string directory = args[0]; Directory.CreateDirectory(directory);
            var observations = Inventory();
            var preview = ManualQuantitiesCore.BuildPreview(Rule(), observations, "Зона А");
            Check("initial inventory preserves every selected object without claiming quantities", delegate {
                var rows = ManualQuantityView.Rows(observations, null);
                Need(rows.Count == 5 && rows.All(row => Convert.ToString(row[4]) == "Не проверен"), "Unverified objects vanished or became accepted");
                Need(rows.Select(row => Convert.ToString(row[1])).SequenceEqual(new[] { "A1", "A2", "A3", "A4", "A5" }), "Selection provenance reordered or omitted");
                Need(Convert.ToString(rows[3][7]).Contains("Дуговой контур"), "Existing extraction reason is hidden before mapping");
            });
            Check("evaluated inventory retains accepted unknown unsupported and generated conflict reasons", delegate {
                var rows = ManualQuantityView.Rows(observations, preview);
                Need(rows.Count == 5 && preview.accepted_count == 2 && preview.rejected_count == 3, "Inventory or accepted physical count differs");
                Need(rows.Select(row => Convert.ToString(row[4])).SequenceEqual(new[] { "Учтён", "Не распознан", "Конфликт", "Не принят", "Учтён" }), "Statuses collapsed or fabricated");
                Need(Convert.ToString(rows[1][7]).Contains("образцу") && Convert.ToString(rows[2][7]).Contains("метку") &&
                    Convert.ToString(rows[3][7]).Contains("Дуговой контур"), "Exact exclusion reasons lost");
                Need(Convert.ToString(rows[4][5]).Contains("не задано") && Convert.ToString(rows[4][7]).Contains("МАРКА"), "Missing source property disappeared");
                Need(rows.All(row => row.Length == ManualQuantityView.Headers.Length), "Inventory columns are inconsistent");
            });
            Check("manual mark punctuation and source layer survive presentation", delegate {
                var row = ManualQuantityView.Rows(observations, preview)[0];
                Need(Convert.ToString(row[5]).Contains("001-К/1,2") && Convert.ToString(row[3]) == "Фасад & <основной>", "Mark precision or layer text damaged");
                Need(Convert.ToString(row[6]).Contains("100") && Convert.ToString(row[6]).Contains("200"), "Actual outline dimensions absent");
                Need(!Convert.ToString(row[7]).Contains("static_verified"), "Manual observation became engineering approval");
            });
            Check("repeated selected handle remains visible while physical preview counts once", delegate {
                var twice = new List<ManualQuantityObservation> { observations[0], observations[0] };
                var checkedTwice = ManualQuantitiesCore.BuildPreview(Rule(), twice, "Зона А");
                Need(ManualQuantityView.Rows(twice, checkedTwice).Count == 2 && checkedTwice.accepted_count == 1, "Selection inventory confused with physical quantity");
            });
            Check("block label and piece-only dimensions do not invent profile length", delegate {
                var symbol = new ManualQuantityObservation { handle = "B1", entity_type = "BlockReference", is_block = true,
                    effective_block_name = "условный кронштейн", layer = "Кронштейны" };
                var symbolPreview = new ManualQuantityPreview { items = new List<ManualQuantityEvaluation> {
                    new ManualQuantityEvaluation { handle = "B1", status = "accepted", element = new QuantityElement { role = "bracket" } } } };
                var row = ManualQuantityView.Rows(new List<ManualQuantityObservation> { symbol }, symbolPreview).Single();
                Need(Convert.ToString(row[2]).Contains("условный кронштейн") && Convert.ToString(row[6]) == "не определены; учёт в штуках", "Symbol became measured/certified product");
            });

            var manual = preview.items.Where(item => item.status == "accepted").Select(item => item.element).ToArray();
            var manualReport = Report("manual-report", "cladding", manual);
            var generated = ManualQuantitiesCore.Evaluate(Rule(), Piece("G1", 1000), "Зона А").element;
            generated.element_id = "generated:one"; generated.origin = "generated:ATTILE"; generated.identity_group = null;
            var generatedReport = Report("generated-report", "cladding", generated);
            var quantity = FacadeQuantitiesCore.BuildRows(new[] { generatedReport, manualReport }, new[] { "Зона А" }, true, false);
            var data = CladdingTableData.Build(quantity, new[] { generatedReport, manualReport }, new[] { "Зона А" }, null, true, false);
            var cladding = QuantityTableView.FromCladding(data);
            cladding.UnaccountedRows.Add(new object[] { 1, "A2", "BlockReference", "Нераспознано", "Нет явного сопоставления образца" });
            cladding.UnaccountedRows.Add(new object[] { 2, "A4", "Polyline", "Дуги", "=1+1 <не поддержано>" });
            Check("mixed generated and manual quantities include three physical pieces without excluded objects", delegate {
                Need(quantity.ok && quantity.rows.Sum(row => row.quantity) == 3, "Unaccounted objects included or manual/generated lost");
                Need(data.Installed.Count == 4 && (double)data.Installed.Last()[7] == 3 && (double)data.Installed.Last()[8] == 0.06,
                    "Three source rows and physical subtotal differ");
                Need(!cladding.Complete && cladding.PreviewRows.Count == data.Installed.Count, "Partial manual report promoted to complete or preview differs");
                Need(!cladding.PreviewRows.Any(row => row.Any(value => Convert.ToString(value) == "A4")), "Excluded object became quantity row");
            });
            Check("unaccounted appendix is structured and repeat rendering cannot append it twice", delegate {
                string first = Json.Serialize(cladding.Rows("Проверено")), second = Json.Serialize(cladding.Rows("Проверено"));
                Need(first == second, "Rendering mutates source data or duplicates inventory");
                var rows = cladding.Rows("Проверено");
                Need(rows.Count(row => Convert.ToString(row[0]).StartsWith("НЕУЧТЁННЫЕ ОБЪЕКТЫ")) == 1, "Unaccounted inventory has no separate heading");
                Need(Convert.ToString(rows[rows.Count - 2][1]) == "A2" && Convert.ToString(rows.Last()[4]) == "=1+1 <не поддержано>", "Structured source/reason lost");
                Need((double)data.Installed.Last()[7] == 3 && (double)data.Installed.Last()[8] == 0.06, "Appendix changed computed totals");
            });
            Check("cladding export contains the same typed quantities and unaccounted appendix", delegate {
                Export(directory, "manual_cladding", "Облицовка", cladding, "  Примечание & <проверка>  ");
            });
            Check("unknown-only refusal has no invented subtotal or accepted zero", delegate {
                var refused = QuantityTableView.Refusal("Облицовка", "Нет подтверждённых деталей в выборке.");
                refused.UnaccountedRows.Add(new object[] { 1, "U1", "BlockReference", "Сборки", "Составной узел не является одной деталью" });
                Need(!refused.Complete && refused.PreviewRows.Count == 0 && refused.Messages.Any(message => message.Contains("не подтверждённый нулевой")), "Refusal looks like known zero");
                Need(!Flatten(refused.Rows("")).Contains("ИТОГО") && !Flatten(refused.Rows("")).Contains("0 шт"), "Refusal invented zero quantities");
                // The command does not offer export on refusal. This inspects only the model.
                File.WriteAllText(Path.Combine(directory, "refusal_rows.json"), Json.Serialize(refused.Rows("")));
            });
            Check("known empty valid scope remains distinct from unknown-only refusal", delegate {
                var empty = QuantityTableView.FromCladding(CladdingTableData.Build(new QuantityResult { ok = true, completeness = "complete" },
                    new QuantityReport[0], new[] { "Пустая зона" }, null, true, false));
                Need(empty.Complete && empty.PreviewRows.Count == 1 && (double)empty.PreviewRows[0][7] == 0, "Known zero lost its existing meaning");
                Need(empty.Messages.Any(message => message.Contains("нет деталей облицовки")), "Known zero missing explanation");
            });
            Check("frame appendix preserves exact metres and keeps unknown symbols out of length totals", delegate {
                var result = new QuantityResult { ok = true, completeness = "partial" };
                result.rows.Add(new QuantityRow { zone_id = "Зона А", role = "rail", mark = "001-НСП/1,2", quantity = 2,
                    length_mm = 2500.1234567, total_length_m = 5.0002469134 });
                result.rows.Add(new QuantityRow { zone_id = "Зона А", role = "bracket", quantity = 3 });
                var frame = QuantityTableView.FromFrame(FrameTableData.Build(result, new QuantityReport[0], new[] { "Зона А" }, null, true));
                frame.UnaccountedRows.Add(new object[] { 1, "F1", "BlockReference", "Неизвестно", "Длина не подтверждена" });
                Need((double)frame.PreviewRows[0][7] == 2500.1234567 && (double)frame.PreviewRows[0][9] == 5.0002469134,
                    "Manual presentation rounded source metres");
                Need(Convert.ToString(frame.PreviewRows[1][7]) == "—" && Convert.ToString(frame.PreviewRows[1][9]) == "—", "Bracket acquired invented metres");
                Export(directory, "manual_frame", "Подсистема", frame, "Проверены только количества");
            });
            File.WriteAllText(Path.Combine(directory, "cases.json"), Json.Serialize(new { total = Cases.Count, passed = Cases.Count - Failed, cases = Cases }));
            Console.WriteLine("Manual output: " + (Cases.Count - Failed) + "/" + Cases.Count + " PASS");
            return Failed == 0 ? 0 : 1;
        }
    }
}
