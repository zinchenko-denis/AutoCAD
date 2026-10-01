// Compatibility oracle: the FrameSourceMetadata implementation and SourceKeys
// from commit 375f082887a052bcba3d0379bd4946ef39b25604, before project parameters.
// Only the method/field names and visibility are changed for same-assembly use.
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
namespace FacadeSafety
{
    internal static partial class FacadeQuantityStore
    {
        private static readonly string[] LegacySourceKeys = { "ATFZONE", "ATFZONE_GEOMETRY", "ATCLAD", "ATTILE",
            "ATCLAD_GEOMETRY", "ATTILE_GEOMETRY", "ATLAYOUT_CURRENT", "ATFRAME_RAIL" };
        internal static string LegacyFrameSourceMetadata(Transaction tr, Entity e, Queue<ObjectId> sources)
        {
            var text = new StringBuilder();
            DBDictionary ext = e.ExtensionDictionary.IsNull ? null : (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead);
            foreach (string key in LegacySourceKeys)
            {
                var refs = new List<ObjectId>(); string value = ReadRecord(tr, ext, key, refs);
                Add(text, key); Add(text, value);
                foreach (ObjectId id in refs)
                {
                    Add(text, id.IsNull ? "null" : id.Handle.ToString());
                    if (sources != null && key.EndsWith("_GEOMETRY", System.StringComparison.Ordinal)) sources.Enqueue(id);
                }
            }
            return Hash(text.ToString());
        }
    }
}
