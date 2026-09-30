using System;
using System.Collections.Generic;
using System.Globalization;

namespace AFacadesPlugin
{
    // A connection passport and an element bill must never overwrite each other.
    // Published tables without a view discriminator are element bills.
    internal static class QuantityTableIdentity
    {
        internal const string Elements = "elements";
        internal const string Connections = "connections";

        internal static bool Matches(IDictionary<string, object> data, string schema, string owner, string kind, string view)
        {
            if (data == null || (view != Elements && view != Connections) ||
                (view == Connections && kind != "frame")) return false;
            return Text(data, "schema") == schema && Text(data, "owner") == owner &&
                (Text(data, "kind") == kind || (kind == "cladding" && !data.ContainsKey("kind"))) &&
                (data.ContainsKey("view") ? Text(data, "view") : Elements) == view &&
                data.ContainsKey("user_note") && data.ContainsKey("text_height");
        }

        private static string Text(IDictionary<string, object> data, string key)
        { object value; return data.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
    }
}
