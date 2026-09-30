using System;
using System.Collections.Generic;
using FacadeSafety;

namespace AFacadesPlugin
{
    // A read-only presentation of every selected object. No CAD calls or inference here.
    internal static class ManualQuantityView
    {
        internal static readonly string[] Headers = { "№", "Handle", "Объект / образец", "Слой", "Состояние", "Марка / тип", "Габариты X/Y либо L, мм", "Причина / ограничения" };
        internal static readonly int[] Widths = { 45, 85, 200, 140, 130, 170, 180, 460 };

        internal static List<object[]> Rows(IList<ManualQuantityObservation> observations, ManualQuantityPreview preview)
        {
            var rows = new List<object[]>();
            var evaluations = new Dictionary<string, ManualQuantityEvaluation>(StringComparer.OrdinalIgnoreCase);
            if (preview != null)
                foreach (var item in preview.items)
                    if (item != null && item.handle != null) evaluations[item.handle] = item;
            for (int i = 0; i < observations.Count; i++)
            {
                var source = observations[i];
                ManualQuantityEvaluation item = null;
                if (source.handle != null) evaluations.TryGetValue(source.handle, out item);
                var element = item == null ? null : item.element;
                var notes = new List<string>();
                if (item != null)
                {
                    if (!string.IsNullOrEmpty(item.reason)) notes.Add(item.reason);
                    foreach (var issue in item.issues)
                        if (issue != null && !string.IsNullOrEmpty(issue.message) && !notes.Contains(issue.message)) notes.Add(issue.message);
                }
                else if (!string.IsNullOrEmpty(source.extraction_reason)) notes.Add(source.extraction_reason);
                rows.Add(new object[] { i + 1, source.handle ?? "", Label(source), source.layer ?? "", Status(item),
                    element == null ? "" : Text(element.mark) + " / " + Text(element.type),
                    element == null ? "" : Dimensions(element), string.Join("; ", notes.ToArray()) });
            }
            return rows;
        }

        internal static string Label(ManualQuantityObservation source)
        { return source.is_block ? "Блок «" + (source.effective_block_name ?? "без имени") + "»" : source.entity_type ?? "Объект"; }
        private static string Text(string value) { return string.IsNullOrWhiteSpace(value) ? "не задано" : value; }
        private static string Status(ManualQuantityEvaluation item)
        {
            if (item == null) return "Не проверен";
            switch (item.status)
            {
                case "accepted": return "Учтён";
                case "unrecognized": return "Не распознан";
                case "conflict": return "Конфликт";
                default: return "Не принят";
            }
        }
        private static string Dimensions(QuantityElement element)
        {
            if (element.length_mm.HasValue) return "L = " + CladdingTableData.Display(element.length_mm.Value);
            if (element.width_mm.HasValue && element.height_mm.HasValue)
                return "X/Y = " + CladdingTableData.Display(element.width_mm.Value) + " × " + CladdingTableData.Display(element.height_mm.Value);
            return "не определены; учёт в штуках";
        }
    }
}
