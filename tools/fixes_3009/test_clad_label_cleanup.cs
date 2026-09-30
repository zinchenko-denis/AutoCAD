// Executes the extracted production RemoveData, using existing CAD API doubles.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using ACladPlugin;

internal static class CladLabelCleanupCheck
{
    private static int checks, failures;

    private static void Check(string name, bool passed)
    {
        checks++;
        if (!passed) { failures++; Console.Error.WriteLine("FAIL " + name); }
    }

    private sealed class Fixture
    {
        public readonly Database Db = new Database();
        public readonly Entity Carrier;
        public readonly Transaction Tr;
        public readonly DBDictionary Ext;
        public readonly Dictionary<string, Xrecord> Records = new Dictionary<string, Xrecord>();
        private readonly Dictionary<string, string> contents = new Dictionary<string, string>();

        public Fixture()
        {
            Carrier = Db.Add(new Entity());
            Carrier.CreateExtensionDictionary();
            Tr = new Transaction(Db);
            Ext = (DBDictionary)Tr.GetObject(Carrier.ExtensionDictionary, OpenMode.ForRead);
        }

        public void Add(string key)
        {
            var record = new Xrecord { Data = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, "original:" + key),
                new TypedValue((int)DxfCode.SoftPointerId, Carrier.ObjectId)) };
            Ext.SetAt(key, record);
            Records[key] = record;
            contents[key] = Content(record);
        }

        public bool Untouched(string key)
        {
            return Ext.Contains(key) && Ext.GetAt(key) == Records[key].ObjectId &&
                   !Records[key].IsErased && Content(Records[key]) == contents[key];
        }

        private static string Content(Xrecord record)
        {
            var text = new StringBuilder();
            foreach (TypedValue value in record.Data)
                text.Append(value.TypeCode).Append(':')
                    .Append(value.Value is ObjectId ? ((ObjectId)value.Value).Handle.ToString() :
                            Convert.ToString(value.Value, CultureInfo.InvariantCulture)).Append(';');
            return text.ToString();
        }
    }

    private static Fixture WithBothLayouts()
    {
        var fixture = new Fixture();
        foreach (string key in new[] { CladCommand.XKeyClad, TilePatternCommand.XKeyTile,
                 CladCommand.XKeyClad + "_GEOMETRY", TilePatternCommand.XKeyTile + "_GEOMETRY",
                 "ATLAYOUT_CURRENT", "OTHER", "OTHER_GEOMETRY" }) fixture.Add(key);
        return fixture;
    }

    private static void ReplacementDirection(string previous, string current)
    {
        var f = WithBothLayouts();
        string proof = previous + "_GEOMETRY";
        CladCommand.RemoveData(f.Tr, f.Carrier, previous);
        Check(previous + ":metadata_removed", !f.Ext.Contains(previous));
        Check(previous + ":metadata_erased", f.Records[previous].IsErased);
        Check(previous + ":proof_removed", !f.Ext.Contains(proof));
        Check(previous + ":proof_erased", f.Records[proof].IsErased);
        Check(previous + ":current_metadata_identity", f.Ext.Contains(current) &&
              f.Ext.GetAt(current) == f.Records[current].ObjectId);
        Check(previous + ":current_metadata_unchanged", f.Untouched(current));
        Check(previous + ":current_proof_identity", f.Ext.Contains(current + "_GEOMETRY") &&
              f.Ext.GetAt(current + "_GEOMETRY") == f.Records[current + "_GEOMETRY"].ObjectId);
        Check(previous + ":current_proof_unchanged", f.Untouched(current + "_GEOMETRY"));
        Check(previous + ":canonical_stamp_unchanged", f.Untouched("ATLAYOUT_CURRENT"));
        Check(previous + ":unrelated_records_unchanged", f.Untouched("OTHER") && f.Untouched("OTHER_GEOMETRY"));
        int count = f.Ext.Items.Count;
        CladCommand.RemoveData(f.Tr, f.Carrier, previous);
        Check(previous + ":repeat_safe", f.Ext.Items.Count == count && f.Untouched(current) &&
              f.Untouched(current + "_GEOMETRY") && f.Untouched("ATLAYOUT_CURRENT"));
    }

    private static void OrphanProof(string previous, string current)
    {
        var f = new Fixture();
        string proof = previous + "_GEOMETRY";
        foreach (string key in new[] { proof, current, current + "_GEOMETRY", "ATLAYOUT_CURRENT" }) f.Add(key);
        CladCommand.RemoveData(f.Tr, f.Carrier, previous);
        Check(previous + ":orphan_proof_removed", !f.Ext.Contains(proof));
        Check(previous + ":orphan_proof_erased", f.Records[proof].IsErased);
        Check(previous + ":orphan_cleanup_preserves_current", f.Untouched(current) &&
              f.Untouched(current + "_GEOMETRY") && f.Untouched("ATLAYOUT_CURRENT"));
        int count = f.Ext.Items.Count;
        CladCommand.RemoveData(f.Tr, f.Carrier, previous);
        Check(previous + ":orphan_repeat_safe", f.Ext.Items.Count == count);
    }

    private static void UnrelatedKeys()
    {
        var f = WithBothLayouts();
        CladCommand.RemoveData(f.Tr, f.Carrier, "OTHER");
        Check("unrelated:requested_key_removed", !f.Ext.Contains("OTHER"));
        Check("unrelated:requested_record_erased", f.Records["OTHER"].IsErased);
        Check("unrelated:proof_identity_preserved", f.Ext.Contains("OTHER_GEOMETRY") &&
              f.Ext.GetAt("OTHER_GEOMETRY") == f.Records["OTHER_GEOMETRY"].ObjectId);
        Check("unrelated:proof_content_preserved", f.Untouched("OTHER_GEOMETRY"));
        Check("unrelated:clad_preserved", f.Untouched(CladCommand.XKeyClad) &&
              f.Untouched(CladCommand.XKeyClad + "_GEOMETRY"));
        Check("unrelated:tile_preserved", f.Untouched(TilePatternCommand.XKeyTile) &&
              f.Untouched(TilePatternCommand.XKeyTile + "_GEOMETRY"));
        CladCommand.RemoveData(f.Tr, f.Carrier, "OTHER");
        Check("unrelated:repeat_preserves_proof_and_stamp", f.Untouched("OTHER_GEOMETRY") &&
              f.Untouched("ATLAYOUT_CURRENT"));
        f.Add("MISSING_GEOMETRY");
        CladCommand.RemoveData(f.Tr, f.Carrier, "MISSING");
        Check("unrelated:orphan_proof_preserved", f.Untouched("MISSING_GEOMETRY"));
        Check("unrelated:missing_key_does_not_touch_current", f.Untouched(CladCommand.XKeyClad) &&
              f.Untouched(TilePatternCommand.XKeyTile) && f.Untouched("ATLAYOUT_CURRENT"));
    }

    private static void EmptyExtension()
    {
        var db = new Database();
        var carrier = db.Add(new Entity());
        var tr = new Transaction(db);
        CladCommand.RemoveData(tr, carrier, CladCommand.XKeyClad);
        Check("empty_extension:no_dictionary_created", carrier.ExtensionDictionary.IsNull);
        CladCommand.RemoveData(tr, carrier, TilePatternCommand.XKeyTile);
        CladCommand.RemoveData(tr, carrier, "OTHER");
        Check("empty_extension:repeat_safe", carrier.ExtensionDictionary.IsNull);
    }

    public static int Main()
    {
        try
        {
            ReplacementDirection(CladCommand.XKeyClad, TilePatternCommand.XKeyTile);
            ReplacementDirection(TilePatternCommand.XKeyTile, CladCommand.XKeyClad);
            OrphanProof(CladCommand.XKeyClad, TilePatternCommand.XKeyTile);
            OrphanProof(TilePatternCommand.XKeyTile, CladCommand.XKeyClad);
            UnrelatedKeys();
            EmptyExtension();
        }
        catch (Exception error) { Check("unexpected_exception", false); Console.Error.WriteLine(error); }
        Console.WriteLine("CladLabelCleanupCheck: checks=" + checks + ", failures=" + failures +
                          "; actual RemoveData with CAD doubles, not AutoCAD");
        return failures == 0 ? 0 : 1;
    }
}
