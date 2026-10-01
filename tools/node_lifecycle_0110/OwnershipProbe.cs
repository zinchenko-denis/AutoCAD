using System;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;

// Adapter reproduction only. This is not evidence of an AutoCAD/product bug.
internal static class OwnershipProbe
{
    public static void Main()
    {
        var db = new Database();
        var table = (BlockTable)db.BlockTableId.Item;
        var model = (BlockTableRecord)table[BlockTableRecord.ModelSpace].Item;
        var definition = db.Add(new BlockTableRecord { Name = "AFNODE_SCHEMA_fixture" });
        var internalLine = new Line();
        definition.AppendEntity(internalLine);
        var reference = new BlockReference { BlockTableRecord = definition.ObjectId };
        model.AppendEntity(reference);
        var lazyDb = new Database();
        var lazyDefinition = lazyDb.Add(new BlockTableRecord { Name = "AFNODE_SCHEMA_lazy" });
        var lazyLine = new Line(); lazyDefinition.AppendEntity(lazyLine);
        var lazyModel = (BlockTableRecord)((BlockTable)lazyDb.BlockTableId.Item)[BlockTableRecord.ModelSpace].Item;
        Console.WriteLine("{\"model_count\":" + model.Children.Count +
            ",\"definition_count\":" + definition.Children.Count +
            ",\"internal_primitive_leaked\":" + model.Children.Contains(internalLine.ObjectId).ToString().ToLowerInvariant() +
            ",\"reference_membership_count\":" + model.Children.Count(id => id == reference.ObjectId) + ",\"lazy_internal_primitive_leaked\":" + lazyModel.Children.Contains(lazyLine.ObjectId).ToString().ToLowerInvariant() + "}");
    }
}
