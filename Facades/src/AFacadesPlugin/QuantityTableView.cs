using System;
using System.Collections.Generic;

namespace AFacadesPlugin
{
    // Presentation contract shared by the table command, preview and exports.
    // The existing cladding row builder remains the authoritative cladding API.
    internal sealed class QuantityTableView
    {
        internal string Title, Scope, Coverage;
        internal bool Complete;
        internal string[] Headers;
        internal double[] Widths;
        internal readonly List<object[]> PreviewRows = new List<object[]>();
        internal readonly List<string> Messages = new List<string>();
        internal static readonly string[] UnaccountedHeaders = { "№", "Handle", "Тип объекта", "Слой", "Почему не учтён" };
        internal readonly List<object[]> UnaccountedRows = new List<object[]>();
        internal IDictionary<string, string> FormShapes;
        private Func<string, List<object[]>> _rows;

        internal List<object[]> Rows(string note)
        {
            var rows = _rows(note);
            if (UnaccountedRows.Count > 0)
            {
                rows.Add(new object[] { "НЕУЧТЁННЫЕ ОБЪЕКТЫ — в количества выше не включены" });
                rows.Add(UnaccountedHeaders);
                rows.AddRange(UnaccountedRows);
            }
            return rows;
        }

        internal static QuantityTableView Refusal(string title, string reason)
        {
            var view = new QuantityTableView { Title = title, Scope = "Ведомость не создана.", Coverage = reason,
                Complete = false, Headers = UnaccountedHeaders, Widths = new double[] { 5, 10, 20, 20, 65 },
                FormShapes = new Dictionary<string, string>(), _rows = note => new List<object[]>() };
            view.Messages.Add("Числа не рассчитаны. Это не подтверждённый нулевой результат.");
            return view;
        }

        internal static QuantityTableView FromCladding(CladdingTableData data)
        {
            var view = new QuantityTableView { Title = CladdingTableData.Title, Scope = data.Scope,
                Coverage = data.Coverage, Complete = data.Complete, Headers = CladdingTableData.Headers,
                Widths = new double[] { 7, 12, 12, 8, 24, 10, 4, 7, 8, 24 },
                FormShapes = data.FormShapes, _rows = data.Rows };
            view.PreviewRows.AddRange(data.Installed);
            if (data.Cutting.Count > 0)
            {
                view.PreviewRows.Add(new object[] { "РАСКРОЙ", "вся группа" });
                view.PreviewRows.AddRange(data.Cutting);
            }
            view.Messages.AddRange(data.Information);
            view.Messages.AddRange(data.Warnings);
            view.Messages.AddRange(data.CuttingDetails);
            return view;
        }

        internal static QuantityTableView FromFrame(FrameTableData data)
        {
            var view = new QuantityTableView { Title = FrameTableData.Title, Scope = data.Scope,
                Coverage = data.Coverage, Complete = data.Complete, Headers = FrameTableData.Headers,
                Widths = new double[] { 7, 10, 13, 13, 10, 10, 7, 8, 7, 10, 24 },
                FormShapes = new Dictionary<string, string>(), _rows = data.Rows };
            view.PreviewRows.AddRange(data.Installed);
            if (data.Estimates.Count > 0)
            {
                view.PreviewRows.Add(new object[] { "ОЦЕНКА", "не закупка" });
                view.PreviewRows.AddRange(data.Estimates);
            }
            view.Messages.AddRange(data.Information);
            view.Messages.AddRange(data.Warnings);
            view.Messages.AddRange(data.EstimateDetails);
            return view;
        }
    }
}
