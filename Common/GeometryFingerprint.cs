using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FacadeSafety
{
    /// <summary>Pure geometry DTO. Coordinates are world XY in millimetres, bulges are outgoing.</summary>
    public sealed class GeometryLoop
    {
        public string Role;
        public double[][] Points;
        public double[] Bulges;
        public bool Closed = true;
    }

    /// <summary>Geometry equality is numerical; a hash alone is never evidence of equality.</summary>
    public static class GeometryFingerprint
    {
        public const double CoordinateTolerance = 0.001;
        private const double ClosingTolerance = 0.5; // same accepted trailing gap as facade_zones
        private const double BulgeTolerance = 1e-12;

        public static bool TryFingerprint(IList<GeometryLoop> loops, out string fingerprint, out string reason)
        {
            fingerprint = null;
            List<GeometryLoop> normalized;
            if (!NormalizeAll(loops, out normalized, out reason)) return false;
            var records = new List<string>();
            foreach (var loop in normalized) records.Add(Canonical(loop));
            records.Sort(StringComparer.Ordinal);
            fingerprint = Hash(string.Join("\n", records.ToArray()));
            return true;
        }

        public static bool Equivalent(IList<GeometryLoop> expected, IList<GeometryLoop> actual, out string reason)
        {
            List<GeometryLoop> a, b;
            if (!NormalizeAll(expected, out a, out reason) || !NormalizeAll(actual, out b, out reason)) return false;
            if (a.Count != b.Count) { reason = "изменилось количество контуров"; return false; }
            var used = new bool[b.Count];
            foreach (var loop in a)
            {
                int match = -1;
                for (int i = 0; i < b.Count; i++)
                    if (!used[i] && loop.Role == b[i].Role && EqualLoop(loop, b[i])) { match = i; break; }
                if (match < 0) { reason = "изменились вершины, дуги или замкнутость контура"; return false; }
                used[match] = true;
            }
            reason = null;
            return true;
        }

        public static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? ""));
                var sb = new StringBuilder();
                foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        private static bool NormalizeAll(IList<GeometryLoop> loops, out List<GeometryLoop> output, out string reason)
        {
            output = new List<GeometryLoop>();
            reason = null;
            if (loops == null || loops.Count == 0) { reason = "нет геометрии для проверки"; return false; }
            foreach (var loop in loops)
            {
                GeometryLoop normalized;
                if (!Normalize(loop, out normalized, out reason)) return false;
                output.Add(normalized);
            }
            return true;
        }

        private static bool Normalize(GeometryLoop loop, out GeometryLoop output, out string reason)
        {
            output = null;
            reason = null;
            if (loop == null || string.IsNullOrEmpty(loop.Role) || loop.Points == null ||
                loop.Points.Length < (loop.Closed ? 3 : 2))
            { reason = "контур не содержит роль и достаточное число вершин"; return false; }
            if (loop.Bulges != null && loop.Bulges.Length != loop.Points.Length)
            { reason = "число дуг не соответствует вершинам"; return false; }
            var points = new List<double[]>();
            var bulges = new List<double>();
            for (int i = 0; i < loop.Points.Length; i++)
            {
                var p = loop.Points[i];
                double bulge = loop.Bulges == null ? 0 : loop.Bulges[i];
                if (p == null || p.Length != 2 || !Finite(p[0]) || !Finite(p[1]) || !Finite(bulge))
                { reason = "геометрия содержит неконечные числа или неверные координаты"; return false; }
                points.Add(new[] { p[0] == 0 ? 0 : p[0], p[1] == 0 ? 0 : p[1] });
                bulges.Add(bulge == 0 ? 0 : bulge);
            }
            if (loop.Closed && points.Count > 2 && Distance(points[0], points[points.Count - 1]) <= ClosingTolerance &&
                Math.Abs(bulges[bulges.Count - 1]) <= BulgeTolerance &&
                (Distance(points[0], points[points.Count - 1]) <= 1e-6 || Math.Abs(bulges[bulges.Count - 2]) <= BulgeTolerance))
            { points.RemoveAt(points.Count - 1); bulges.RemoveAt(bulges.Count - 1); }
            // Consecutive exact duplicate vertices carry the following outgoing segment.
            for (int i = points.Count - 1; i > 0; i--)
                if (Distance(points[i], points[i - 1]) <= 1e-6)
                {
                    if (Math.Abs(bulges[i - 1]) > BulgeTolerance)
                    { reason = "дублированная вершина с неоднозначными дугами"; return false; }
                    if (Math.Abs(bulges[i - 1]) <= BulgeTolerance) bulges[i - 1] = bulges[i];
                    points.RemoveAt(i); bulges.RemoveAt(i);
                }
            bool changed = true;
            while (changed && points.Count > (loop.Closed ? 3 : 2))
            {
                changed = false;
                for (int i = loop.Closed ? 0 : 1; i < (loop.Closed ? points.Count : points.Count - 1); i++)
                {
                    int previous = (i + points.Count - 1) % points.Count, next = (i + 1) % points.Count;
                    if (Math.Abs(bulges[previous]) > BulgeTolerance || Math.Abs(bulges[i]) > BulgeTolerance) continue;
                    if (!BetweenOnLine(points[previous], points[i], points[next])) continue;
                    points.RemoveAt(i); bulges.RemoveAt(i); changed = true; break;
                }
            }
            if (points.Count < (loop.Closed ? 3 : 2))
            { reason = "вырожденный контур после нормализации"; return false; }
            output = new GeometryLoop { Role = loop.Role, Closed = loop.Closed, Points = points.ToArray(), Bulges = bulges.ToArray() };
            return true;
        }

        private static bool BetweenOnLine(double[] a, double[] b, double[] c)
        {
            double dx = c[0] - a[0], dy = c[1] - a[1], length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= CoordinateTolerance) return false;
            double cross = Math.Abs((b[0] - a[0]) * dy - (b[1] - a[1]) * dx) / length;
            double along = ((b[0] - a[0]) * dx + (b[1] - a[1]) * dy) / length;
            return cross <= CoordinateTolerance && along > 0 && along < length;
        }

        private static bool EqualLoop(GeometryLoop a, GeometryLoop b)
        {
            if (a.Closed != b.Closed || a.Points.Length != b.Points.Length) return false;
            int n = a.Points.Length;
            if (!a.Closed) return EqualOrder(a, b, 0, false);
            for (int start = 0; start < n; start++)
                if (Distance(a.Points[0], b.Points[start]) <= CoordinateTolerance &&
                    (EqualOrder(a, b, start, false) || EqualOrder(a, b, start, true))) return true;
            return false;
        }

        private static bool EqualOrder(GeometryLoop a, GeometryLoop b, int start, bool reverse)
        {
            int n = a.Points.Length;
            for (int i = 0; i < n; i++)
            {
                int j = reverse ? (start - i + n) % n : (start + i) % n;
                double bulge = reverse ? -b.Bulges[(j - 1 + n) % n] : b.Bulges[j];
                if (Distance(a.Points[i], b.Points[j]) > CoordinateTolerance) return false;
                if ((a.Closed || i < n - 1) && Math.Abs(a.Bulges[i] - bulge) > BulgeTolerance) return false;
            }
            return true;
        }

        private static string Canonical(GeometryLoop loop)
        {
            string best = null;
            int n = loop.Points.Length;
            for (int direction = 0; direction < (loop.Closed ? 2 : 1); direction++)
                for (int start = 0; start < (loop.Closed ? n : 1); start++)
                {
                    // Only a lexicographically minimal vertex can begin the minimal rotation.
                    bool minimum = true;
                    if (loop.Closed)
                        for (int k = 0; k < n; k++)
                            if (loop.Points[k][0] < loop.Points[start][0] ||
                                (loop.Points[k][0] == loop.Points[start][0] && loop.Points[k][1] < loop.Points[start][1]))
                            { minimum = false; break; }
                    if (!minimum) continue;
                    var sb = new StringBuilder();
                    sb.Append(loop.Role.Length).Append(':').Append(loop.Role).Append(loop.Closed ? "|C|" : "|O|");
                    for (int i = 0; i < n; i++)
                    {
                        int j = direction == 1 ? (start - i + n) % n : (start + i) % n;
                        double bulge = direction == 1 ? -loop.Bulges[(j - 1 + n) % n] : loop.Bulges[j];
                        if (!loop.Closed && i == n - 1) bulge = 0;
                        sb.Append(F(loop.Points[j][0])).Append(',').Append(F(loop.Points[j][1])).Append(',').Append(F(bulge)).Append(';');
                    }
                    string record = sb.ToString();
                    if (best == null || string.CompareOrdinal(record, best) < 0) best = record;
                }
            return best;
        }

        private static string F(double value) { return (value == 0 ? 0 : value).ToString("R", CultureInfo.InvariantCulture); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static double Distance(double[] a, double[] b)
        { double x = a[0] - b[0], y = a[1] - b[1]; return Math.Sqrt(x * x + y * y); }
    }
}
