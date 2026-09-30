using System.Collections.Generic;

namespace AFacadesPlugin
{
    internal sealed partial class QuantityTableView
    {
        internal static QuantityTableView FromConnections(ConnectionTableData data)
        {
            var view = new QuantityTableView { Title = ConnectionTableData.Title, Scope = data.Scope,
                Coverage = ConnectionTableData.Coverage, Complete = data.Complete, Headers = ConnectionTableData.Headers,
                Widths = new double[] { 7, 24, 9, 9, 15, 10, 10, 20, 28, 35, 45 },
                FormShapes = new Dictionary<string, string>(), _rows = data.Rows };
            view.PreviewRows.AddRange(data.Members);
            view.Messages.AddRange(data.Messages);
            return view;
        }
    }
}
