using System;using System.Collections.Generic;using System.Globalization;using System.Text;using Autodesk.AutoCAD.DatabaseServices;
namespace AFacadesPlugin {internal static class ZoneCommand {
        internal static void StoreZoneData(Transaction tr, Entity ent,
                                           string json, string key, IEnumerable<ObjectId> refs)
        {
            if (ent.ExtensionDictionary.IsNull)
                ent.CreateExtensionDictionary();
            var ext = (DBDictionary)tr.GetObject(ent.ExtensionDictionary,
                                                 OpenMode.ForWrite);
            var rb = new ResultBuffer();
            for (int i = 0; i < json.Length; i += 250)
                rb.Add(new TypedValue((int)DxfCode.Text,
                    json.Substring(i, Math.Min(250, json.Length - i))));
            if (refs != null)
                foreach (var id in refs)
                    if (!id.IsNull && !id.IsErased)
                        rb.Add(new TypedValue((int)DxfCode.SoftPointerId, id));
            Xrecord xr;
            if (ext.Contains(key))
            {
                xr = (Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForWrite);
                xr.Data = rb;
            }
            else
            {
                xr = new Xrecord { Data = rb };
                ext.SetAt(key, xr);
                tr.AddNewlyCreatedDBObject(xr, true);
            }
            xr.XlateReferences = true;
        }

        internal static string ReadZoneData(Transaction tr, Entity ent,
                                            string key)
        {
            if (ent.ExtensionDictionary.IsNull) return null;
            var ext = (DBDictionary)tr.GetObject(ent.ExtensionDictionary,
                                                 OpenMode.ForRead);
            if (!ext.Contains(key)) return null;
            var xr = (Xrecord)tr.GetObject(ext.GetAt(key), OpenMode.ForRead);
            if (xr.Data == null) return null;
            var sb = new StringBuilder();
            foreach (TypedValue tv in xr.Data)
                if (tv.TypeCode == (int)DxfCode.Text)
                    sb.Append(SafeStr(tv.Value));
            return sb.Length > 0 ? sb.ToString() : null;
        }

        internal static string SafeStr(object o)
        { return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture); }
}}
