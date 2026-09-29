using System;
using System.Collections.Generic;
using System.Globalization;

namespace AFacadesPlugin
{
    /// <summary>
    /// Ведомость зон облицовки — строки и шапка без AutoCAD (одна логика для таблицы в
    /// чертеже ATFZONE/ATFTABLE и для выгрузки в Excel; проверяется под mono —
    /// Facades/tools/zone_ui). 29.09 (Герман: «все площади — в типовую таблицу, пока в
    /// свободной и удобной форме»): проёмы по типам (окна / витражи / двери — штуки и
    /// площадь), нетто, погонаж: отливы окон, откосы окон, откосы дверей, примыкания
    /// витражей бок / верх / низ; парапеты — отдельный блок (площадь, длина по верху).
    /// Зоны старых сборок (до 29.09 все проёмы были окнами) — в колонки окон.
    /// </summary>
    internal static class ZoneTable
    {
        internal const int Cols = 16;

        internal static readonly string[] Group =
        {
            "Марка", "Облицовка", "S участка, м²", "Проёмы, шт / м²", "", "", "", "", "",
            "S облицовки, м²", "Погонаж, м.п.", "", "", "", "", ""
        };

        internal static readonly string[] Sub =
        {
            "", "", "", "окна, шт", "окна, м²", "витражи, шт", "витражи, м²", "двери, шт",
            "двери, м²", "", "отливы окон", "откосы окон", "откосы дверей",
            "витражи: бок", "витражи: верх", "витражи: низ"
        };

        /// <summary>Слияния шапки: (строка0, колонка0, строка1, колонка1) от начала шапки.</summary>
        internal static readonly int[][] HeaderMerges =
        {
            new[] { 0, 0, 1, 0 }, new[] { 0, 1, 1, 1 }, new[] { 0, 2, 1, 2 },
            new[] { 0, 3, 0, 8 }, new[] { 0, 9, 1, 9 }, new[] { 0, 10, 0, 15 },
        };

        internal static readonly string[] ParapetHead = { "Парапет", "S, м²", "Длина по верху, м.п.", "Ширина, м" };

        private static double D(Dictionary<string, object> d, string k, double def)
        {
            object o;
            if (d == null || !d.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }
            catch { return def; }
        }

        private static bool Has(Dictionary<string, object> d, string k)
        {
            return d != null && d.ContainsKey(k) && d[k] != null;
        }

        internal static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        private static string N0(double v) { return ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture); }

        /// <summary>Числа строки зоны в порядке колонок 2..15 (у старых зон — проёмы как окна).</summary>
        internal static double[] Numbers(Dictionary<string, object> report)
        {
            var r = report;
            bool fresh = Has(r, "window_count");
            double ops = D(r, "openings_count", 0), opsA = D(r, "openings_total_m2", 0);
            return new[]
            {
                D(r, "area_outer_m2", 0),
                fresh ? D(r, "window_count", 0) : ops, fresh ? D(r, "window_area_m2", 0) : opsA,
                D(r, "vitrage_count", 0), D(r, "vitrage_area_m2", 0),
                D(r, "door_count", 0), D(r, "door_area_m2", 0),
                D(r, "area_net_m2", 0),
                fresh ? D(r, "window_sills_m", 0) : D(r, "sills_total_m", 0),
                fresh ? D(r, "window_slopes_m", 0) : D(r, "jambs_total_m", 0),
                D(r, "door_slopes_m", 0), D(r, "vitrage_side_m", 0), D(r, "vitrage_top_m", 0),
                D(r, "vitrage_bottom_m", 0),
            };
        }

        private static readonly bool[] IsCount = { false, true, false, true, false, true, false, false,
                                                   false, false, false, false, false, false };

        internal static string[] Row(string mark, string cladding, double[] n)
        {
            var row = new string[Cols];
            row[0] = mark ?? "";
            row[1] = cladding ?? "";
            for (int i = 0; i < n.Length; i++) row[2 + i] = IsCount[i] ? N0(n[i]) : F3(n[i]);
            return row;
        }

        /// <summary>Строки данных зон + «ИТОГО» (последняя).</summary>
        internal static List<string[]> ZoneRows(List<Dictionary<string, object>> zones,
                                               Func<Dictionary<string, object>, string> id,
                                               Func<Dictionary<string, object>, string> cladding)
        {
            var rows = new List<string[]>();
            var tot = new double[14];
            foreach (var z in zones)
            {
                object ro;
                var rep = z != null && z.TryGetValue("report", out ro) ? ro as Dictionary<string, object> : null;
                var n = Numbers(rep);
                for (int i = 0; i < n.Length; i++) tot[i] += n[i];
                rows.Add(Row(id(z), cladding(z), n));
            }
            rows.Add(Row("ИТОГО", "", tot));
            return rows;
        }

        /// <summary>Строки парапетов (Марка, S, длина по верху, ширина) + «ИТОГО».</summary>
        internal static List<string[]> ParapetRows(List<Dictionary<string, object>> parapets)
        {
            var rows = new List<string[]>();
            double ta = 0, tt = 0;
            int k = 0;
            foreach (var p in parapets ?? new List<Dictionary<string, object>>())
            {
                k++;
                double a = D(p, "area_m2", 0), t = D(p, "top_m", 0), w = D(p, "width_m", 0);
                ta += a; tt += t;
                object mo;
                string mark = p.TryGetValue("mark", out mo) && mo != null ? Convert.ToString(mo, CultureInfo.InvariantCulture)
                                                                         : "П-" + k;
                rows.Add(new[] { mark, F3(a), F3(t), F3(w) });
            }
            if (rows.Count > 0) rows.Add(new[] { "ИТОГО", F3(ta), F3(tt), "" });
            return rows;
        }
    }
}
