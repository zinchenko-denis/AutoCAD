using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace AFacadesPlugin
{
    // Pure data preparation for explicit legacy adoption. CAD identity is read once
    // per operation by the command; no old report or layout becomes a fresh result.
    internal static class ZoneMigration
    {
        internal sealed class Input
        {
            internal string ZoneId, Cladding;
            internal readonly List<Dictionary<string, object>> PreviousParts = new List<Dictionary<string, object>>();
            internal readonly Dictionary<string, string> Roles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, object> Kinds = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        internal static Input ReadIdentity(Dictionary<string, object> data, object[] sidecar,
            int hatchCount, int markCount)
        {
            string id = Text(data, "zone_id"), cladding = Text(data, "cladding");
            if (id.Length == 0 || cladding.Length == 0)
                throw new InvalidOperationException("В старой зоне отсутствует марка или название облицовки.");
            if (hatchCount != 1 || markCount != 1)
                throw new InvalidOperationException("Марка «" + id + "» неоднозначна: штриховок " + hatchCount +
                    ", текстовых марок " + markCount + ". Проверьте копии и повторные зоны; исходные объекты не изменены.");
            var input = new Input { ZoneId = id, Cladding = cladding };
            var partIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in sidecar ?? new object[0])
            {
                var part = item as Dictionary<string, object>;
                var meta = Get(part, "meta") as Dictionary<string, object>;
                string partId = Text(part, "id"), group = Text(meta, "group");
                if (partId != id && group != id && !partId.StartsWith(id + ".", StringComparison.Ordinal)) continue;
                if (Text(part, "schema") != "facade_zone/1" || !partIds.Add(partId) ||
                    Text(part, "cladding") != cladding || (group.Length > 0 && group != id))
                    throw new InvalidOperationException("Неоднозначные или повреждённые части зоны в _fzones.json.");
                AddSource(input, Text(meta, "outer_contour_id"), "outer");
                foreach (var openingValue in Items(Get(part, "openings")))
                {
                    var opening = openingValue as Dictionary<string, object>;
                    string source = Text(opening, "id"), kind = Text(opening, "kind");
                    if (kind != "window" && kind != "door" && kind != "vitrage")
                        throw new InvalidOperationException("Не определён тип проёма " + source + " в _fzones.json.");
                    AddSource(input, source, "hole"); input.Kinds.Add(source, kind);
                }
                input.PreviousParts.Add(part);
            }
            if (input.PreviousParts.Count == 0)
                throw new InvalidOperationException("В _fzones.json нет исходных контуров зоны «" + id +
                    "». Восстановите файл рядом с DWG или создайте только эту зону через ATFZONE.");
            return input;
        }

        private static void AddSource(Input input, string handle, string role)
        {
            long parsed;
            if (handle.Length == 0 || !long.TryParse(handle, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed) ||
                parsed <= 0 || input.Roles.ContainsKey(handle))
                throw new InvalidOperationException("Повторённая или повреждённая ссылка исходного контура в _fzones.json.");
            input.Roles.Add(handle, role);
        }

        internal static Dictionary<string, object> Request(Input input,
            IList<Dictionary<string, object>> contours, IDictionary<string, string> actualRoles)
        {
            if (actualRoles.Count != input.Roles.Count || contours.Count != input.Roles.Count)
                throw new InvalidOperationException("Состав связанных контуров не совпадает с исходной зоной. Проверьте COPY и связи штриховки.");
            foreach (var source in input.Roles)
            {
                string role;
                if (!actualRoles.TryGetValue(source.Key, out role) || role != source.Value)
                    throw new InvalidOperationException("Связь контура " + source.Key +
                        " не принадлежит исходной зоне. Копию нельзя принять за оригинал; создайте для неё отдельную зону.");
            }
            return new Dictionary<string, object> {
                { "op", "zones" }, { "units", "mm" }, { "contours", contours },
                { "cladding", input.Cladding }, { "zone_prefix", "migration-" }, { "start_index", 1 },
                { "merge", input.PreviousParts.Count > 1 }, { "opening_kinds", input.Kinds }
            };
        }

        internal static Dictionary<string, object> Result(Input input, Dictionary<string, object> result,
            out List<Dictionary<string, object>> parts)
        {
            parts = new List<Dictionary<string, object>>();
            if (Get(result, "ok") == null || !Convert.ToBoolean(Get(result, "ok"), CultureInfo.InvariantCulture) ||
                Items(Get(result, "failed")).Count != 0)
                throw new InvalidOperationException("Текущие контуры не прошли расчёт зоны: " + Text(result, "error"));
            foreach (var issueValue in Items(Get(result, "issues")))
                if (Text(issueValue as Dictionary<string, object>, "level") == "error")
                    throw new InvalidOperationException("Движок обнаружил ошибку контуров: " + Text(issueValue as Dictionary<string, object>, "msg"));
            var zones = Items(Get(result, "zones"));
            if (zones.Count != 1) throw new InvalidOperationException("Изменилось число зон; выполните ATFZONE по текущим контурам.");
            var zone = zones[0] as Dictionary<string, object>;
            if (Get(zone, "report") == null) throw new InvalidOperationException("Движок не вернул новый отчёт зоны.");
            var previous = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (var old in input.PreviousParts) previous.Add(Text(Get(old, "meta") as Dictionary<string, object>, "outer_contour_id"), old);
            foreach (var value in Items(Get(result, "zones_full")))
            {
                var part = value as Dictionary<string, object>;
                var meta = Get(part, "meta") as Dictionary<string, object>;
                string source = Text(meta, "outer_contour_id"); Dictionary<string, object> old;
                if (!previous.TryGetValue(source, out old)) throw new InvalidOperationException("Движок изменил принадлежность внешнего контура.");
                previous.Remove(source);
                part["id"] = old["id"];
                var oldMeta = Get(old, "meta") as Dictionary<string, object>;
                if (Text(oldMeta, "group").Length > 0) meta["group"] = input.ZoneId;
                else meta.Remove("group");
                parts.Add(part);
            }
            if (previous.Count != 0) throw new InvalidOperationException("Движок пропустил часть зоны.");
            var report = new Dictionary<string, object>((Dictionary<string, object>)zone["report"]);
            report["zone_id"] = input.ZoneId;
            return new Dictionary<string, object> { { "zone_id", input.ZoneId }, { "cladding", input.Cladding }, { "report", report } };
        }

        internal static object Get(Dictionary<string, object> d, string key)
        { object value; return d != null && d.TryGetValue(key, out value) ? value : null; }
        internal static string Text(Dictionary<string, object> d, string key)
        { return Convert.ToString(Get(d, key), CultureInfo.InvariantCulture) ?? ""; }
        internal static List<object> Items(object value)
        {
            var result = new List<object>(); var items = value as IEnumerable;
            if (items != null && !(value is string) && !(value is IDictionary)) foreach (var item in items) result.Add(item);
            return result;
        }
    }
}
