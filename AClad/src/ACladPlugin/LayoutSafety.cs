using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace ACladPlugin
{
    internal static class LayoutSafety
    {
        internal static List<string> IncompleteRoots(
            object[] perZone, Dictionary<string, string> partToRoot)
        {
            var result = new List<string>();
            if (partToRoot == null || partToRoot.Count == 0) return result;

            var successful = SuccessfulIds(perZone);

            var expected = new Dictionary<string, int>(StringComparer.Ordinal);
            var completed = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var part in partToRoot)
            {
                if (string.IsNullOrEmpty(part.Key) || string.IsNullOrEmpty(part.Value)) continue;
                int count;
                expected.TryGetValue(part.Value, out count);
                expected[part.Value] = count + 1;
                if (successful.Contains(part.Key))
                {
                    completed.TryGetValue(part.Value, out count);
                    completed[part.Value] = count + 1;
                }
            }
            foreach (var root in expected)
            {
                int count;
                completed.TryGetValue(root.Key, out count);
                // Полностью отклонённая зона сохраняет прежнюю раскладку.
                if (count > 0 && count < root.Value) result.Add(root.Key);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        internal static List<string> IncompleteSharedGroups(
            IEnumerable<IEnumerable<string>> oldGroups, object[] perZone,
            Dictionary<string, string> partToRoot)
        {
            var successful = SuccessfulIds(perZone);
            var expected = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            if (partToRoot != null)
                foreach (var part in partToRoot)
                {
                    if (string.IsNullOrEmpty(part.Key) || string.IsNullOrEmpty(part.Value)) continue;
                    HashSet<string> parts;
                    if (!expected.TryGetValue(part.Value, out parts))
                        expected[part.Value] = parts = new HashSet<string>(StringComparer.Ordinal);
                    parts.Add(part.Key);
                }

            var ready = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in successful)
            {
                string root = RootId(id, partToRoot);
                HashSet<string> parts;
                if (!expected.TryGetValue(root, out parts) || parts.IsSubsetOf(successful))
                    ready.Add(root);
            }

            var groups = new List<HashSet<string>>();
            if (oldGroups != null)
                foreach (var oldGroup in oldGroups)
                {
                    if (oldGroup == null) continue;
                    var group = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string id in oldGroup)
                        if (!string.IsNullOrEmpty(id)) group.Add(RootId(id, partToRoot));
                    if (group.Count > 0) groups.Add(group);
                }

            // Связанные прежние группы A+B и B+C заменяются только целиком.
            var affected = new HashSet<string>(ready, StringComparer.Ordinal);
            bool changed;
            do
            {
                changed = false;
                foreach (var group in groups)
                    if (group.Overlaps(affected))
                        foreach (string id in group)
                            if (affected.Add(id)) changed = true;
            } while (changed);

            var missing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in groups)
                if (group.Overlaps(affected))
                    foreach (string id in group)
                        if (!ready.Contains(id)) missing.Add(id);
            var result = new List<string>(missing);
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static string RootId(string id, Dictionary<string, string> partToRoot)
        {
            string root;
            // Неизвестный участник остаётся обязательным, даже если его геометрия пропала.
            return partToRoot != null && partToRoot.TryGetValue(id, out root) &&
                   !string.IsNullOrEmpty(root) ? root : id;
        }

        private static HashSet<string> SuccessfulIds(object[] perZone)
        {
            var successful = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in perZone ?? new object[0])
            {
                var zone = item as IDictionary<string, object>;
                if (zone == null) continue;
                object value;
                if (zone.TryGetValue("zone_id", out value)) AddId(successful, value);
                if (!zone.TryGetValue("members", out value) || value is string) continue;
                var members = value as IEnumerable;
                if (members != null)
                    foreach (object member in members) AddId(successful, member);
            }
            return successful;
        }

        private static void AddId(HashSet<string> ids, object value)
        {
            string id = value as string;
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }

        internal static bool SetNumberChecked(Func<object> read, Action<object> write,
            double requested, double tolerance = 1e-6)
        {
            if (read == null || !Finite(requested) || !Finite(tolerance) || tolerance < 0)
                return false;
            try
            {
                object previous = read();
                if (previous == null) return false;
                double before = Convert.ToDouble(previous, CultureInfo.InvariantCulture);
                if (Finite(before) && Math.Abs(before - requested) <= tolerance) return true;
                if (write == null) return false;
                write(Convert.ChangeType(requested, previous.GetType(), CultureInfo.InvariantCulture));
                // Динсвойство может молча отвергнуть или округлить новое значение.
                object stored = read();
                if (stored == null) return false;
                double actual = Convert.ToDouble(stored, CultureInfo.InvariantCulture);
                return Finite(actual) && Math.Abs(actual - requested) <= tolerance;
            }
            catch (Exception) { return false; }
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
