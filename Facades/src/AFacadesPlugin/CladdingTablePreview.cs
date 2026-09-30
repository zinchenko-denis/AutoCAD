namespace AFacadesPlugin
{
    internal sealed class CladdingTablePreview : QuantityTablePreview
    {
        internal CladdingTablePreview(CladdingTableData data, string note)
            : base(QuantityTableView.FromCladding(data), note) { }
    }
}
