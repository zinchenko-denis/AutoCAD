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
    // Nominal source-derived datums are not physical installation approval.
    // No CAD, process, file or mutable catalogue escapes.
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
        public string State { get; private set; }
        public string ConfirmedPart { get; private set; }
        public string NeededEvidence { get; private set; }
        public string Consequence { get; private set; }
        public ReadOnlyCollection<FrameMountingSource> Sources { get; private set; }
        internal FrameMountingDependency(string id, string state, string title, string confirmed, string needed, string consequence, params FrameMountingSource[] sources)
        { Id = id; State = state; Title = title; ConfirmedPart = confirmed; NeededEvidence = needed; Consequence = consequence; Sources = Array.AsReadOnly((FrameMountingSource[])sources.Clone()); }
    }

    public sealed class FrameMountingNominalDatum
    {
        public string Id { get; private set; }
        public string Title { get; private set; }
        public double ValueMm { get; private set; }
        public string ValueText { get; private set; }
        public string Formula { get; private set; }
        public ReadOnlyCollection<FrameMountingSource> Sources { get; private set; }
        internal FrameMountingNominalDatum(string id, string title, double value, string formula, params FrameMountingSource[] sources)
        { Id = id; Title = title; ValueMm = value; ValueText = FrameNodeGeometry.FormatMm(value); Formula = formula; Sources = Array.AsReadOnly((FrameMountingSource[])sources.Clone()); }
    }

    public sealed class FrameMountingNominalChain
    {
        public string Scope { get { return "historical_nominal_undeformed_geometry"; } }
        public string UkPlacementStatus { get { return "underdetermined"; } }
        public ReadOnlyCollection<string> Conditions { get; private set; }
        public ReadOnlyCollection<FrameMountingNominalDatum> Datums { get; private set; }
        public double UkLengthMm { get; private set; }
        public string UkLengthText { get; private set; }
        public string UkLengthDatumsText { get; private set; }
        public ReadOnlyCollection<FrameMountingSource> UkLengthSources { get; private set; }
        public double UkHeelConstantMm { get; private set; }
        public string UkHeelConstantText { get; private set; }
        public string UkHeelIdentity { get; private set; }
        public string OverlapDefinition { get; private set; }
        internal FrameMountingNominalChain(double krLength, double ukLength, FrameMountingSource kr2,
            FrameMountingSource pad, FrameMountingSource uk, FrameMountingSource node)
        {
            // Validated catalogue lengths are small exact integers. Do not use
            // project GP dimensions or their numeric envelope in this chain.
            double contact = 2, tip = contact + krLength;
            Conditions = Array.AsReadOnly(new[] {
                "Только номинальная плоская недеформированная схема Вектор-1 2015, узел 4.2.1; x = 0 на основании, направление наружу.",
                "КР2 прилегает к одной показанной ПП номинальной толщиной 2 мм; сжатие, дополнительная подкладка и монтажный зазор не вводятся.",
                "Направление изделий соответствует узлу; неровность основания, допуски, радиусы гибов и деформации не вычисляются.",
                "Толщина ПП не назначает её типоразмер, контур или отверстие и не подтверждает совместимость с выбранным КР2, в том числе номиналом 85." });
            Datums = Array.AsReadOnly(new[] {
                new FrameMountingNominalDatum("wall", "Основание", 0, "x = 0", node),
                new FrameMountingNominalDatum("pad_outer", "Наружная поверхность ПП", contact, "x = s ПП = 2", pad, node),
                new FrameMountingNominalDatum("kr2_contact", "Опорная поверхность КР2", contact, "x = s ПП = 2", kr2, pad, node),
                new FrameMountingNominalDatum("kr2_free_tip", "Свободный торец КР2", tip, "x = s ПП + L КР2 = 2 + " + FrameNodeGeometry.FormatMm(krLength), kr2, pad, node) });
            UkLengthMm = ukLength; UkLengthText = FrameNodeGeometry.FormatMm(ukLength);
            UkLengthDatumsText = "L УК измерена от свободного торца продольной части до наружной стороны отогнутой лапки.";
            UkLengthSources = Array.AsReadOnly(new[] { uk, node });
            UkHeelConstantMm = tip + ukLength; UkHeelConstantText = FrameNodeGeometry.FormatMm(UkHeelConstantMm);
            UkHeelIdentity = "x пятки УК = 2 + L КР2 + L УК − h = " + UkHeelConstantText + " мм − h";
            OverlapDefinition = "h = x свободного торца КР2 − x свободного торца УК: знаковая разность координат торцов. При положительном частичном вложении это перекрытие продольных габаритов, не допустимая рабочая длина соединения.";
        }
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
        public FrameMountingNominalChain NominalChain { get; private set; }
        public ReadOnlyCollection<FrameMountingDependency> Dependencies { get; private set; }
        public ReadOnlyCollection<string> OutsideCurrentAssessment { get; private set; }
        internal FrameMountingAssessmentResult(string selectionDigest, FrameNodeGeometryResult geometry,
            FrameMountingMember[] members, ReadOnlyCollection<FrameMountingDependency> dependencies, FrameMountingNominalChain nominalChain)
        {
            SelectionDigest = selectionDigest; LocalGeometryStatus = geometry.Status;
            LocalClearanceStatus = geometry.ClearanceStatus; LocalGapText = geometry.GapText;
            CanInsertDimensionalScheme = geometry.CanInsert;
            DeclaredMembers = Array.AsReadOnly((FrameMountingMember[])members.Clone()); Dependencies = dependencies;
            NominalChain = nominalChain;
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
            text.AppendLine("Номинальные базы по размерным линиям альбома — при показанном контактном прилегании:");
            foreach (var datum in NominalChain.Datums)
                text.AppendLine("  " + datum.Title + ": x = " + datum.ValueText + " мм; " + datum.Formula + "; " + string.Join("; ", datum.Sources.Select(s => s.DisplayText())) + ".");
            text.AppendLine("УК: L = " + NominalChain.UkLengthText + " мм. " + NominalChain.UkLengthDatumsText + " " + string.Join("; ", NominalChain.UkLengthSources.Select(s => s.DisplayText())) + ".");
            text.AppendLine("Размерное тождество: " + NominalChain.UkHeelIdentity + ". " + NominalChain.OverlapDefinition);
            text.AppendLine("Число h, положение УК и допустимый ход не определены. Заявленная координата ГП не заменяет базу пятки УК; постоянная часть тождества не является выносом сборки.");
            foreach (string condition in NominalChain.Conditions) text.AppendLine("Условие номинальной схемы: " + condition);
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
                text.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture) + ". " + row.Title + " — " +
                    (row.State == "partially_confirmed" ? "частично подтверждено" : "не подтверждено для пилота") + ".");
                text.AppendLine("  Известно: " + row.ConfirmedPart);
                text.AppendLine("  Требуется: " + row.NeededEvidence);
                text.AppendLine("  Без этого: " + row.Consequence);
                text.AppendLine("  Проверенные листы: " + string.Join("; ", row.Sources.Select(s => s.DisplayText())) + ".");
            }
            text.AppendLine("Минимум 30 мм узла 4.1 / PDF 19 относится к пластинам У2/У. Перехлёст min30 для КР2–УК показан в других узлах типов 2/3: листы 5.5, 5.7, 6.5, 6.7 / PDF 34, 37, 44, 46. Его перенос на 4.2.1 не подтверждён; эти размеры не задают допустимый ход пилота. Числа отдельного расчёта не являются общими монтажными нормами.");
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
            Node = new FrameMountingSource(20, "4.2.1"),
            OtherType2a = new FrameMountingSource(34, "5.5"), OtherType2b = new FrameMountingSource(37, "5.7"),
            OtherType3a = new FrameMountingSource(44, "6.5"), OtherType3b = new FrameMountingSource(46, "6.7");
        private static readonly ReadOnlyCollection<FrameMountingDependency> Required = Array.AsReadOnly(new[] {
            new FrameMountingDependency("wall_to_kr2_datum", "partially_confirmed", "Основание — монтажная база КР2",
                "ПП имеет номинальную толщину 2 мм. При показанном контактном прилегании опорная поверхность КР2 находится на x = 2 мм; условия приведены в номинальной цепи.",
                "Применимость прокладки и показанного контактного прилегания к фактической установке, без скрытого назначения подкладок, зазоров или допусков.",
                "Номинальная база определена; фактическая установка и монтажная пригодность всей сборки не подтверждены.", Kr2, Pad, Node),
            new FrameMountingDependency("kr2_length_datums", "partially_confirmed", "Номенклатурная длина КР2 — размер сборки",
                "Размер L КР2 идёт от свободного торца до наружной опорной поверхности лапки; номинальная координата торца x = 2 + L КР2. Толщина металла повторно не прибавляется.",
                "Связь подтверждённого номинального торца КР2 с положением УК и ближайшей поверхностью ГП, допустимые положения соединения.",
                "Номинальный торец КР2 определён, но его координата не является выносом ГП или облицовки.", Kr2, Pad, Node),
            new FrameMountingDependency("kr2_uk_engagement", "not_confirmed", "Перехлёст и регулировка КР2–УК",
                "Узел 4.2.1 изображает КР2 с УК; известны отдельные длины и номинальная база КР2. В узлах 5.5, 5.7, 6.5, 6.7 других типов 2/3 для КР2–УК размерными линиями показан перехлёст min30 мм; это сведения об этих узлах.",
                "Для 4.2.1 — допустимые минимум и максимум перехлёста с точными базами и пределы регулировки. Общего разрешения переноса min30 из узлов типов 2/3 на этот пилот в проверенном альбоме нет.",
                "Допустимый вынос пары и выбор длины кронштейна не определяются суммой L КР2 и L УК; min30 других узлов не становится нижней границей или ходом пилота.", Kr2, Uk, Node, OtherType2a, OtherType2b, OtherType3a, OtherType3b),
            new FrameMountingDependency("kr2_uk_connection", "not_confirmed", "Отверстия и закрепление КР2–УК",
                "Крепление показано в составе узла; это не полный размерный паспорт соединения.",
                "Центры и размеры отверстий/пазов, расстояния до кромок, допуски и схема крепежа выбранного соединения.",
                "Посадка, монтажная совместимость и полный состав крепежа не подтверждены.", Kr2, Uk, Node),
            new FrameMountingDependency("uk_gp_datum", "not_confirmed", "Посадка УК–ГП и ближайшая поверхность ГП",
                "Узел включает УК и ГП; профиль имеет отдельные номенклатурные и проектные размеры.",
                "Ориентация, монтажные базы, размеры посадки и схема отверстий УК–ГП, связь с ближайшей поверхностью ГП.",
                "Введённая координата ГП не доказывает, что выбранные изделия устанавливаются в этом положении.", Uk, Gp, Node),
            new FrameMountingDependency("gp_to_cladding", "not_confirmed", "ГП — кляммер — наружная поверхность облицовки",
                "ГП, кляммер и облицовка изображены; полка b и толщина c ГП зависят от проекта и расчёта.",
                "Фактическая ориентация и толщина ГП, положение и толщина кляммера, толщина и тыльная плоскость облицовки с общей размерной цепочкой.",
                "Разность двух заявленных плоскостей не подтверждает полную монтажную цепочку до облицовки.", Gp, Node),
            new FrameMountingDependency("assembly_scope", "not_confirmed", "Допустимые сочетания изделий",
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
            var nominal = new FrameMountingNominalChain(selected.bracket.L_mm, selected.extender.L_mm, Kr2, Pad, Uk, Node);
            return new FrameMountingAssessmentResult(digest, geometry, members, Required, nominal);
        }
        private static FrameMountingDimension Dimension(string id, string title, double? value, string basis = "historical_nomenclature")
        { return new FrameMountingDimension(id, title, value, basis); }
    }
}
