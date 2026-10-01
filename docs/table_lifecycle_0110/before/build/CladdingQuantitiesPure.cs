using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using FacadeSafety;
namespace ACladPlugin { internal sealed class CladdingQuantities {

        internal static QuantityReport BuildReport(string key, Dictionary<string, object> response,
            Dictionary<string, object> request, Dictionary<string, string> partToRoot,
            string material, double tileWidth, double tileHeight)
        {
            bool tile = key == "ATTILE";
            string run = Guid.NewGuid().ToString("N");
            var result = new QuantityReport
            {
                report_id = run, run_id = run, scope = "whole_layout_run",
                algorithm = key + "/facade_quantities/1", completeness = "complete",
                engine_summary = new Dictionary<string, object>(Get(response, "summary") as Dictionary<string, object>
                    ?? new Dictionary<string, object>())
            };
            result.engine_summary["geometry_basis"] = "engine_layout_after_placement_guards";
            foreach (var parameter in request)
                if (parameter.Key != "zones" && parameter.Key != "contours")
                    result.parameters[parameter.Key] = parameter.Value;
            result.parameters["coordinate_unit"] = "mm";
            result.parameters["material_source"] = "user_label";
            result.parameters["catalogue_mapping"] = "not_available";
            var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (object item in Items(Get(response, "per_zone")))
            {
                var zone = item as Dictionary<string, object>;
                string id = Text(Get(zone, "zone_id"));
                var roots = Roots(Get(zone, "members"), id, partToRoot);
                if (id.Length == 0 || members.ContainsKey(id))
                    throw new InvalidOperationException("Повреждён состав зон результата раскладки.");
                members[id] = roots;
                foreach (var root in roots) if (!result.zone_ids.Contains(root)) result.zone_ids.Add(root);
                if (tile) AddCutting(result, zone, roots, request, material, tileWidth, tileHeight);
            }
            object[] pieces = Get(response, tile ? "pieces" : "inserts") as object[];
            if (pieces == null || members.Count == 0)
                throw new InvalidOperationException("Нет полного состава результата для ведомости облицовки.");
            for (int i = 0; i < pieces.Length; i++)
            {
                var piece = pieces[i] as Dictionary<string, object>;
                string zid = Text(Get(piece, "zone"));
                List<string> roots;
                if (piece == null || !members.TryGetValue(zid, out roots))
                    throw new InvalidOperationException("У детали раскладки нет подтверждённой зоны.");
                var rings = PieceRings(piece);
                QuantityShape shape; string reason;
                if (!FacadeQuantitiesCore.TryShape(rings, out shape, out reason))
                    throw new InvalidOperationException("Нельзя сохранить форму детали: " + reason);
                if (tile && Math.Abs(Number(Get(piece, "area")) - shape.area_mm2) >
                    Math.Max(0.1, shape.area_mm2 * 0.000001))
                    throw new InvalidOperationException("Площадь детали расходится с её контурами.");
                bool full = tile ? Bool(Get(piece, "full")) : Get(piece, "pts") == null &&
                    Math.Abs(shape.width_mm - tileWidth) < 0.001 && Math.Abs(shape.height_mm - tileHeight) < 0.001;
                bool shaped = tile ? !Bool(Get(piece, "rect")) : Get(piece, "pts") != null;
                result.elements.Add(new QuantityElement
                {
                    element_id = run + ":" + i.ToString(CultureInfo.InvariantCulture),
                    zone_id = string.Join("+", roots.ToArray()), zone_ids = new List<string>(roots),
                    material = material, type = tile ? Text(Get(piece, "type")) : material,
                    // No inferred catalogue product, mark or coating.
                    product_id = null, mark = null, color = null,
                    orientation = "XY; rotation=0; mirror=false", origin = "generated:" + key,
                    piece_kind = full ? "full" : shaped ? "shaped" : "cut",
                    width_mm = shape.width_mm, height_mm = shape.height_mm, area_mm2 = shape.area_mm2,
                    shape_id = shape.shape_id, rings = rings
                });
            }
            result.zone_ids.Sort(StringComparer.Ordinal);
            return result;
        }

        private static List<QuantityRing> PieceRings(Dictionary<string, object> piece)
        {
            var result = new List<QuantityRing>();
            object rings = Get(piece, "rings");
            if (rings != null)
            {
                int i = 0;
                foreach (object ring in Items(rings))
                    result.Add(new QuantityRing { role = i++ == 0 ? "outer" : "hole", points = Points(ring) });
            }
            else if (Get(piece, "pts") != null)
                result.Add(new QuantityRing { role = "outer", points = Points(Get(piece, "pts")) });
            else
            {
                double x = Number(Get(piece, "x")), y = Number(Get(piece, "y"));
                double w = Number(Get(piece, "w")), h = Number(Get(piece, "h"));
                result.Add(new QuantityRing { points = new[] {
                    new[] { x, y }, new[] { x + w, y }, new[] { x + w, y + h }, new[] { x, y + h } } });
            }
            return result;
        }

        private static double[][] Points(object value)
        {
            var result = new List<double[]>();
            foreach (object item in Items(value))
            {
                var numbers = new List<double>();
                foreach (object number in Items(item)) numbers.Add(Number(number));
                if (numbers.Count != 2) throw new InvalidOperationException("Повреждена точка контура детали.");
                result.Add(numbers.ToArray());
            }
            return result.ToArray();
        }

        private static List<string> Roots(object memberValues, string fallback, Dictionary<string, string> mapping)
        {
            var result = new List<string>();
            foreach (object member in memberValues == null ? new object[] { fallback } : Items(memberValues))
            {
                string value = Text(member), root;
                if (mapping.TryGetValue(value, out root)) value = root;
                if (value.Length == 0) throw new InvalidOperationException("Участник общей раскладки не определён.");
                if (!result.Contains(value)) result.Add(value);
            }
            if (result.Count == 0) throw new InvalidOperationException("Пустая область общей раскладки.");
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static void AddCutting(QuantityReport result, Dictionary<string, object> zone,
            List<string> roots, Dictionary<string, object> request, string material, double w, double h)
        {
            var byType = Get(zone, "by_type") as Dictionary<string, object>;
            if (byType == null) throw new InvalidOperationException("Не сохранён раскрой группы ATTILE.");
            var group = new QuantityCuttingGroup
            {
                group_id = result.run_id + ":cutting:" + Text(Get(zone, "zone_id")),
                scope_zone_ids = new List<string>(roots)
            };
            group.parameters["by_type"] = byType;
            group.parameters["tile_w_mm"] = w; group.parameters["tile_h_mm"] = h;
            var summary = result.engine_summary;
            foreach (string name in new[] { "kerf", "min_piece", "tiny_mode", "axis", "shaped", "warn_cut", "gap_around" })
                if (summary.ContainsKey(name)) group.parameters[name] = summary[name];
            group.parameters["scope_basis"] = "whole_merged_zone_group";
            group.parameters["extra_breakage_allowance"] = "not_included";
            foreach (var pair in byType)
            {
                var data = pair.Value as Dictionary<string, object>;
                if (data == null) throw new InvalidOperationException("Повреждён состав раскроя ATTILE.");
                foreach (string field in new[] { "blanks_cut", "tiles_total", "waste_area" })
                {
                    if (!data.ContainsKey(field)) throw new InvalidOperationException("Неполные данные раскроя ATTILE.");
                    bool waste = field == "waste_area";
                    var row = new QuantityRow
                    {
                        basis = "cutting", zone_id = string.Join("+", roots.ToArray()), zone_ids = new List<string>(roots),
                        role = field == "tiles_total" ? "blanks_total" : waste ? "waste" : "blanks_cut",
                        type = pair.Key, material = material, width_mm = waste ? (double?)null : w,
                        height_mm = waste ? (double?)null : h, unit = waste ? "м²" : "шт.",
                        quantity = Number(data[field]) / (waste ? 1000000.0 : 1.0),
                        piece_kind = waste ? "отходы" : field == "tiles_total" ? "заготовки всего" : "заготовки для подрезки",
                        note = "Общая группа: " + string.Join(", ", roots.ToArray()) + ". Без запаса на бой; не закупка."
                    };
                    group.rows.Add(row);
                }
            }
            result.cutting.Add(group);
        }

        private static IEnumerable Items(object value)
        {
            var result = value as IEnumerable;
            if (result == null || value is string) throw new InvalidOperationException("Повреждён список результата раскладки.");
            return result;
        }
        private static object Get(Dictionary<string, object> d, string key)
        { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
        private static string Text(object value) { return Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
        private static bool Bool(object value) { return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        private static double Number(object value)
        {
            if (value == null) throw new InvalidOperationException("Отсутствует числовое свойство детали.");
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("Некорректное числовое свойство детали.");
            return number;
        }
        private static string Hash(string text)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant();
        }
        private static string FileHash(string path)
        {
            if (!File.Exists(path)) return "unavailable";
            using (var input = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
        }
    }
}
