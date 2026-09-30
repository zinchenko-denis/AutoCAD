using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using ACladPlugin;
using FacadeSafety;

internal static class CladdingProducerProbe
{
    public static int Main(string[] args)
    {
        var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var payload = (Dictionary<string, object>)json.DeserializeObject(File.ReadAllText(args[0]));
        try
        {
            var request = (Dictionary<string, object>)payload["request"];
            var response = (Dictionary<string, object>)payload["response"];
            var mapping = new Dictionary<string, string>();
            foreach (var item in (Dictionary<string, object>)payload["mapping"])
                mapping[item.Key] = (string)item.Value;
            var report = CladdingQuantities.BuildReport((string)payload["key"], response, request,
                mapping, "Керамогранит", 600, 600);
            // Synthetic unique CAD links permit testing aggregation; they do not
            // claim that AutoCAD geometry or transactions were executed.
            report.document_id = "synthetic-document";
            for (int i = 0; i < report.elements.Count; i++)
                report.elements[i].cad_entities.Add(new QuantityCadEntity {
                    handle = (i + 1).ToString("X"), role = "outer", fingerprint = "synthetic:" + i });
            var result = FacadeQuantitiesCore.BuildRows(new[] { report }, report.zone_ids, true, true);
            QuantityResult subset = null;
            if (report.zone_ids.Count > 1)
                subset = FacadeQuantitiesCore.BuildRows(new[] { report }, new[] { report.zone_ids[0] }, true, true);
            File.WriteAllText(args[1], json.Serialize(new { ok = true, report, result, subset }));
        }
        catch (InvalidOperationException error)
        {
            File.WriteAllText(args[1], json.Serialize(new { ok = false, error = error.Message }));
        }
        return 0;
    }
}
