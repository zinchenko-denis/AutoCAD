namespace AFacadesPlugin
{
    // Compatibility entry point; both element schedules use one workflow.
    internal static class CladdingTableCommand
    {
        internal static void Run(Autodesk.AutoCAD.ApplicationServices.Document doc)
        { FacadeQuantityTableCommand.Run(doc, "cladding"); }
    }
}
