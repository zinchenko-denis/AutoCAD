// Actual production encoding helpers against whole-string .NET reference operations.
// CAD doubles are compile dependencies only; this probe does not open or mutate a DWG.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using FacadeSafety;

namespace FrameQuantitiesChecks
{
    internal static class StoreEncodingProbe
    {
        private static readonly Type Store = typeof(FacadeQuantityStore);

        private static string Invoke(string method, string value)
        {
            var callable = Store.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static);
            if (callable == null) throw new InvalidOperationException("Production helper missing: " + method);
            return (string)callable.Invoke(null, new object[] { value });
        }

        private static string LegacyCompress(byte[] bytes)
        {
            // Equivalent to the pre-Q05 whole-buffer writer. Compression bytes need not
            // be equal; old DWG payloads must still decode to the same UTF-8 string.
            using (var stream = new MemoryStream())
            {
                using (var zip = new GZipStream(stream, CompressionMode.Compress, true))
                    zip.Write(bytes, 0, bytes.Length);
                return "gzip1:" + Convert.ToBase64String(stream.ToArray());
            }
        }

        private static string RepeatedEmoji(int count)
        {
            var text = new StringBuilder();
            for (int i = 0; i < count; i++) text.Append("\uD83D\uDE00");
            return text.ToString();
        }

        public static int Main(string[] args)
        {
            if (args.Length != 2 || args[0] != "--report")
            {
                Console.Error.WriteLine("Usage: StoreEncodingProbe.exe --report cases.json");
                return 2;
            }
            var values = new List<KeyValuePair<string, string>> {
                new KeyValuePair<string, string>("empty", ""),
                new KeyValuePair<string, string>("short_cyrillic", "Профиль, марка С1 — фактические элементы")
            };
            foreach (int length in new[] { 1, 4095, 4096, 4097, 8191, 8192, 8193, 65535 })
                values.Add(new KeyValuePair<string, string>("ascii_" + length, new string('x', length)));
            values.Add(new KeyValuePair<string, string>("surrogate_pair_cross_4096",
                new string('x', 4095) + "\uD83D\uDE00" + new string('я', 8500)));
            values.Add(new KeyValuePair<string, string>("surrogate_pair_cross_8192",
                new string('я', 8191) + "\uD83D\uDE00" + "xyz"));
            values.Add(new KeyValuePair<string, string>("lonely_high_boundary",
                new string('x', 4095) + "\uD83D" + "x" + new string('б', 4097)));
            values.Add(new KeyValuePair<string, string>("lonely_low_boundary",
                new string('я', 4096) + "\uDE00" + new string('x', 4096)));
            values.Add(new KeyValuePair<string, string>("lonely_high_final", new string('x', 8191) + "\uD83D"));
            values.Add(new KeyValuePair<string, string>("repeated_non_bmp", RepeatedEmoji(8193)));

            var cases = new List<object>();
            int failed = 0;
            foreach (var test in values)
            {
                string error = null;
                int byteCount = 0;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(test.Value);
                    byteCount = bytes.Length;
                    string expectedHash;
                    using (var sha = SHA256.Create())
                        expectedHash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                    if (Invoke("Hash", test.Value) != expectedHash)
                        throw new InvalidOperationException("SHA-256 differs from the legacy whole-string UTF-8 hash.");
                    // Isolated surrogates intentionally compare against .NET's standard
                    // UTF-8 fallback, not against an unencodable original UTF-16 value.
                    string expectedText = Encoding.UTF8.GetString(bytes);
                    if (Invoke("Decompress", Invoke("Compress", test.Value)) != expectedText)
                        throw new InvalidOperationException("Chunked compression changed text or retained unused buffer capacity.");
                    if (Invoke("Decompress", LegacyCompress(bytes)) != expectedText)
                        throw new InvalidOperationException("Legacy whole-buffer gzip payload is no longer readable.");
                }
                catch (Exception ex) { failed++; error = ex.GetBaseException().Message; }
                cases.Add(new { name = test.Key, chars = test.Value.Length, utf8_bytes = byteCount,
                    status = error == null ? "PASS" : "FAIL", error = error });
                Console.WriteLine((error == null ? "PASS " : "FAIL ") + test.Key + (error == null ? "" : ": " + error));
            }
            File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(new {
                scope = "Production Hash/Compress/Decompress only; no AutoCAD host, database, UI or transaction behavior.",
                total = cases.Count, passed = cases.Count - failed, failed = failed, cases = cases
            }));
            Console.WriteLine("Quantity store UTF-8: " + (cases.Count - failed) + "/" + cases.Count + " PASS");
            return failed == 0 ? 0 : 1;
        }
    }
}
