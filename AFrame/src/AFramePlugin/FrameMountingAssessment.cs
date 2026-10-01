using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;

namespace AFramePlugin
{
    // Read-only engineering coverage for the same historical pilot. This is
    // deliberately separate from the persisted planes/2 result and its digest.
    // No CAD, process, file, dimension inference or mutable catalogue escapes.
    public sealed class FrameMountingSource
    {
        public string SourceId { get { return FrameMountingAssessment.SourceId; } }
        public string SourceSha256 { get { return FrameMountingAssessment.SourceSha256; } }
        public int PdfPage { get; private set; }
        public string Sheet { get; private set; }
        public string Position { get; private set; }
        internal FrameMountingSource(int page, string sheet, string position = null)
        { PdfPage = page; Sheet = sheet; Position = position; }
        public string DisplayText()
        { return "лист " + Sheet + ", PDF " + PdfPage.ToString(CultureInfo.InvariantCulture) + (Position == null ? "" : ", поз. " + Position); }
    }

    public sealed class FrameMountingDimension
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public double? ValueMm { get; private set; }
        public string ValueText { get; private set; }
        public string Basis { get; private set; }
        internal FrameMountingDimension(string id, string title, double? value, string basis)
        { Id = id; Title = title; ValueMm = value; ValueText = value.HasValue ? FrameNodeGeometry.FormatMm(value.Value) : null; Basis = basis; }
    }

    public sealed class FrameMountingMember
    {
        public string FamilyId { get; private set; }
        public string Title { get; private set; }
        public string Execution { get; private set; }
        public string ExecutionTitle { get; private set; }
        public FrameMountingSource Source { get; private set; }
        public ReadOnlyCollection<FrameMountingDimension> Dimensions { get; private set; }
        internal FrameMountingMember(string family, string title, string execution, FrameMountingSource source, params FrameMountingDimension[] dimensions)
        {
            FamilyId = family; Title = title; Execution = execution; Source = source;
            ExecutionTitle = execution == "galvanized_painted" ? "оцинкованная сталь с порошковой окраской" : "коррозионностойкая сталь (КС)";
            Dimensions = Array.AsReadOnly((FrameMountingDimension[])dimensions.Clone());
        }
    }

    public sealed class FrameMountingDependency
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public string State { get { return "not_confirmed"; } }
        public string ConfirmedPart { get; private set; }
        public string NeededEvidence { get; private set; }
        public string Consequence { get; private set; }
        public ReadOnlyCollection<FrameMountingSource> Sources { get; private set; }
        internal FrameMountingDependency(string id, string title, string confirmed, string needed, string consequence, params FrameMountingSource[] sources)
        { Id = id; Title = title; ConfirmedPart = confirmed; NeededEvidence = needed; Consequence = consequence; Sources = Array.AsReadOnly((FrameMountingSource[])sources.Clone()); }
    }

    public sealed class FrameMountingAssessmentResult
    {
        public string SourceId { get { return FrameMountingAssessment.SourceId; } }
        public string SourceSha256 { get { return FrameMountingAssessment.SourceSha256; } }
        public string SolutionId { get { return FrameMountingAssessment.SolutionId; } }
        public string SelectionDigest { get; private set; }
        public string LocalGeometryStatus { get; private set; }
        public string LocalClearanceStatus { get; private set; }
        public string LocalGapText { get; private set; }
        public bool CanInsertDimensionalScheme { get; private set; }
        public string MountingStatus { get { return "not_confirmed"; } }
        public bool AutomaticBracketSelectionAllowed { get { return false; } }
        public ReadOnlyCollection<FrameMountingMember> DeclaredMembers { get; private set; }
        public ReadOnlyCollection<FrameMountingDependency> Dependencies { get; private set; }
        public ReadOnlyCollection<string> OutsideCurrentAssessment { get; private set; }
        internal FrameMountingAssessmentResult(string selectionDigest, FrameNodeGeometryResult geometry,
            FrameMountingMember[] members, ReadOnlyCollection<FrameMountingDependency> dependencies)
        {
            SelectionDigest = selectionDigest; LocalGeometryStatus = geometry.Status;
            LocalClearanceStatus = geometry.ClearanceStatus; LocalGapText = geometry.GapText;
            CanInsertDimensionalScheme = geometry.CanInsert;
            DeclaredMembers = Array.AsReadOnly((FrameMountingMember[])members.Clone()); Dependencies = dependencies;
            OutsideCurrentAssessment = Array.AsReadOnly(new[] {
                "Неподвижные/подвижные соединения, реальные опоры и непрерывность стыков требуют отдельного монтажного контракта.",
                "Прочность, анкеровка, полный состав крепежа и допуск системы к применению здесь не проверяются.",
                "Историческая номенклатура не подтверждает актуальный артикул, доступность изделия или установленную физическую деталь." });
        }
        public string ReviewText()
        {
            var text = new StringBuilder();
            text.AppendLine("Монтажная проверка выбранного решения — Вектор-1 2015, узел 4.2.1 / PDF 20.");
            text.AppendLine("Монтажная пригодность: не подтверждена. Автоподбор длины кронштейна не выполняется.");
            if (LocalClearanceStatus == "pass")
                text.AppendLine("Локальный просвет " + LocalGapText + " мм соблюдён только по заявленным плоскостям; это не подтверждает монтажную пригодность.");
            else if (LocalClearanceStatus == "fail")
                text.AppendLine("Локальный просвет " + LocalGapText + " мм не соблюдён; монтажная пригодность также не подтверждена.");
            else text.AppendLine("Локальная проверка просвета не выполнена; причины приведены в размерной схеме.");
            if (!CanInsertDimensionalScheme) text.AppendLine("Размерная схема отклонена. Монтажная диагностика не разрешает её вставку.");
            text.AppendLine("Заявленные изделия (не назначение установленных деталей):");
            foreach (var member in DeclaredMembers)
            {
                text.AppendLine(member.Title + ": " + member.ExecutionTitle + "; " + member.Source.DisplayText() + ".");
                foreach (var dimension in member.Dimensions)
                    text.AppendLine("  " + dimension.Title + ": " + (dimension.ValueText == null ? "не заявлено." : dimension.ValueText + " мм" +
                        (dimension.Basis == "project_declared" ? " — заявлено проектом, расчётом не подтверждено." : " — историческая номенклатура.")));
            }
            text.AppendLine("Для монтажного решения нужны подтверждённые данные:");
            for (int i = 0; i < Dependencies.Count; i++)
            {
                var row = Dependencies[i];
                text.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture) + ". " + row.Title + ".");
                text.AppendLine("  Известно: " + row.ConfirmedPart);
                text.AppendLine("  Требуется: " + row.NeededEvidence);
                text.AppendLine("  Без этого: " + row.Consequence);
                text.AppendLine("  Проверенные листы: " + string.Join("; ", row.Sources.Select(s => s.DisplayText())) + ".");
            }
            text.AppendLine("Минимум 30 мм соседнего узла 4.1 / PDF 19 не задаёт регулировку или перехлёст УК узла 4.2.1. Числа отдельного расчёта не являются общими монтажными нормами.");
            foreach (string item in OutsideCurrentAssessment) text.AppendLine(item);
            return text.ToString();
        }
    }

    public static class FrameMountingAssessment
    {
        public const string SourceId = "vector1_2015";
        public const string SourceSha256 = "7386f4152de455a5e2622704d51a98d8f05afd20050807f634eea4572de37142";
        public const string SolutionId = "vector1_2015_type1_4_2_1";
        private static readonly FrameMountingSource General = new FrameMountingSource(3, "2"),
            Kr2 = new FrameMountingSource(6, "3.2.1", "2.2"), Pad = new FrameMountingSource(7, "3.2.2", "2.6"),
            Uk = new FrameMountingSource(8, "3.3", "3.2"), Gp = new FrameMountingSource(9, "3.4", "4.4"),
            Node = new FrameMountingSource(20, "4.2.1");
        private static readonly ReadOnlyCollection<FrameMountingDependency> Required = Array.AsReadOnly(new[] {
            new FrameMountingDependency("wall_to_kr2_datum", "Основание — монтажная база КР2",
                "В альбоме изображены КР2 и паронитовая прокладка толщиной 2 мм.",
                "Базы и ориентация сборки, связь поверхности основания с КР2 с учётом применимой прокладки и условий установки.",
                "Положение КР2 относительно x = 0 не подтверждено.", Kr2, Pad, Node),
            new FrameMountingDependency("kr2_length_datums", "Номенклатурная длина КР2 — размер сборки",
                "Чертёж КР2 задаёт номенклатурную длину L; отдельные размеры изделия известны.",
                "Связь баз размера L с монтажными плоскостями всей сборки и координатой ближайшей поверхности ГП.",
                "Длину L нельзя автоматически считать выносом ГП или облицовки.", Kr2, Node),
            new FrameMountingDependency("kr2_uk_engagement", "Перехлёст и регулировка КР2–УК",
                "Узел изображает КР2 с УК; известны отдельные номенклатурные длины.",
                "Допустимые минимум и максимум перехлёста, определяющие их грани и положения, пределы регулировки выбранных вариантов.",
                "Допустимый вынос пары и выбор длины кронштейна не определяются суммой L КР2 и L УК.", Kr2, Uk, Node),
            new FrameMountingDependency("kr2_uk_connection", "Отверстия и закрепление КР2–УК",
                "Крепление показано в составе узла; это не полный размерный паспорт соединения.",
                "Центры и размеры отверстий/пазов, расстояния до кромок, допуски и схема крепежа выбранного соединения.",
                "Посадка, монтажная совместимость и полный состав крепежа не подтверждены.", Kr2, Uk, Node),
            new FrameMountingDependency("uk_gp_datum", "Посадка УК–ГП и ближайшая поверхность ГП",
                "Узел включает УК и ГП; профиль имеет отдельные номенклатурные и проектные размеры.",
                "Ориентация, монтажные базы, размеры посадки и схема отверстий УК–ГП, связь с ближайшей поверхностью ГП.",
                "Введённая координата ГП не доказывает, что выбранные изделия устанавливаются в этом положении.", Uk, Gp, Node),
            new FrameMountingDependency("gp_to_cladding", "ГП — кляммер — наружная поверхность облицовки",
                "ГП, кляммер и облицовка изображены; полка b и толщина c ГП зависят от проекта и расчёта.",
                "Фактическая ориентация и толщина ГП, положение и толщина кляммера, толщина и тыльная плоскость облицовки с общей размерной цепочкой.",
                "Разность двух заявленных плоскостей не подтверждает полную монтажную цепочку до облицовки.", Gp, Node),
            new FrameMountingDependency("assembly_scope", "Допустимые сочетания изделий",
                "Альбом задаёт семейства и номенклатуру, но не подтверждает каждое произвольное сочетание выбранных размеров и исполнений.",
                "Правила совместимости номиналов и исполнений, направление вложения и применимые посадочные зазоры.",
                "Равенство номиналов не доказывает совместимость; различие номиналов само по себе не доказывает запрет. Разница размеров УК и КР2 не становится допуском.", General, Kr2, Uk, Gp, Node)
        });

        public static FrameMountingAssessmentResult Evaluate(FrameSolutionSelection selection, FrameNodeGeometryResult geometry)
        {
            if (selection == null || geometry == null)
                throw new FrameNodeGeometryException("E_MOUNT_INPUT", "Для монтажной диагностики нужны выбранное решение и результат его размерной схемы.");
            var selected = FrameNodeGeometry.CloneSelection(selection);
            if (selected.source_id != SourceId || selected.source_sha256 != SourceSha256 || selected.solution_id != SolutionId ||
                selected.node.pdf_page != 20 || selected.node.sheet != "4.2.1")
                throw new FrameNodeGeometryException("E_MOUNT_SOURCE", "Монтажная диагностика относится только к проверенному историческому источнику Вектор-1 2015, узлу 4.2.1 / PDF 20.");
            string digest = FrameParameterResolver.SelectionDigest(selected);
            if (geometry.SelectionDigest != digest)
                throw new FrameNodeGeometryException("E_MOUNT_SELECTION", "Размерная схема получена для другого выбора решения. Повторите проверку текущих параметров.");
            var members = new[] {
                new FrameMountingMember("kr2", "КР2", selected.bracket.execution, Kr2,
                    Dimension("nominal_width_mm", "Номинальная ширина", selected.bracket.nominal_width_mm), Dimension("L_mm", "Длина L", selected.bracket.L_mm)),
                new FrameMountingMember("uk", "УК", selected.extender.execution, Uk,
                    Dimension("nominal_width_mm", "Номинальная ширина", selected.extender.nominal_width_mm), Dimension("L_mm", "Длина L", selected.extender.L_mm),
                    Dimension("thickness_mm", "Толщина c", selected.extender.thickness_mm)),
                new FrameMountingMember("gp", "ГП", selected.profile.execution, Gp,
                    Dimension("a_mm", "Полка a", selected.profile.a_mm), Dimension("b_mm", "Полка b", selected.profile.b_mm, "project_declared"),
                    Dimension("thickness_mm", "Толщина c", selected.profile.thickness_mm, "project_declared"))
            };
            return new FrameMountingAssessmentResult(digest, geometry, members, Required);
        }
        private static FrameMountingDimension Dimension(string id, string title, double? value, string basis = "historical_nomenclature")
        { return new FrameMountingDimension(id, title, value, basis); }
    }
}
