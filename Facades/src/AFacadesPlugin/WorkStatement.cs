using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Xml;

namespace AFacadesPlugin
{
    /// <summary>
    /// Ведомость работ по форме (29.09n, просьба Германа): штукатурный или вентилируемый фасад.
    /// Без AutoCAD — проверяется под mono (Facades/tools/vedomost). Форма — xlsx Германа (очищенная
    /// копия в Contents/templates бандла или своя в %APPDATA%\AFacades\templates), карта — json
    /// рядом: какие клетки чем заполнить (сумма полей отчёта зоны или парапет), какие — вручную,
    /// какая строка — утеплитель. Остальные строки формы считают её же формулы (=D3 и т.п.): Excel
    /// пересчитывает их при открытии.
    /// Несколько слоёв штриховок: площадь — общая; строка утеплителя делится по толщине из имени
    /// слоя («НВФ утеплитель 150 мм» → строка с «150 мм»): если в форме такая строка уже есть —
    /// площадь идёт в неё, если нет — строка утеплителя копируется с нужной толщиной.
    /// </summary>
    internal static class WorkStatement
    {
        internal const string NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        internal class Config
        {
            public string Title = "";
            public string Template = "";
            public string Sheet = "";                 // пусто — первый лист
            public Dictionary<string, object> Fill = new Dictionary<string, object>();
            public Dictionary<string, object> Manual = new Dictionary<string, object>();
            public int InsulationRow;                 // 0 — искать по словам
            public string[] InsulationWords = { "утепл", "теплоизол" };
        }

        /// <summary>Группа зон одного слоя штриховок.</summary>
        internal class Group
        {
            public string Layer = "";
            public List<Dictionary<string, object>> Reports = new List<Dictionary<string, object>>();
        }

        /// <summary>Что куда записано — для командной строки и проверки.</summary>
        internal class Result
        {
            public List<string> Lines = new List<string>();
            public Dictionary<string, double> Values = new Dictionary<string, double>();
            public List<string> Manual = new List<string>();
            public int RowsAdded;
        }

        // ── карта формы ──
        internal static Config LoadConfig(string jsonPath)
        {
            var ser = new JavaScriptSerializer();
            var d = ser.DeserializeObject(File.ReadAllText(jsonPath, Encoding.UTF8)) as Dictionary<string, object>;
            if (d == null) throw new InvalidDataException("карта формы — не объект json: " + jsonPath);
            var c = new Config();
            c.Title = Str(d, "title");
            c.Template = Str(d, "template");
            c.Sheet = Str(d, "sheet");
            c.Fill = (Get(d, "fill") as Dictionary<string, object>) ?? c.Fill;
            c.Manual = (Get(d, "manual") as Dictionary<string, object>) ?? c.Manual;
            object ir = Get(d, "insulation_row");
            if (ir != null) c.InsulationRow = Convert.ToInt32(ir, CultureInfo.InvariantCulture);
            var iw = Get(d, "insulation_words") as object[];
            if (iw != null && iw.Length > 0)
            {
                var w = new List<string>();
                foreach (var o in iw) w.Add(Convert.ToString(o, CultureInfo.InvariantCulture).ToLowerInvariant());
                c.InsulationWords = w.ToArray();
            }
            return c;
        }

        private static object Get(Dictionary<string, object> d, string k)
        {
            object o;
            return d != null && d.TryGetValue(k, out o) ? o : null;
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            var o = Get(d, k);
            return o == null ? "" : Convert.ToString(o, CultureInfo.InvariantCulture);
        }

        private static double Num(Dictionary<string, object> d, string k)
        {
            var o = Get(d, k);
            if (o == null) return 0;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        /// <summary>Поле отчёта зоны; у зон старых сборок (до типов проёмов) все проёмы — окна.</summary>
        internal static double Field(Dictionary<string, object> rep, string key)
        {
            bool fresh = rep != null && rep.ContainsKey("window_count");
            if (!fresh)
            {
                if (key == "window_slopes_m") return Num(rep, "jambs_total_m");
                if (key == "window_sills_m") return Num(rep, "sills_total_m");
                if (key.StartsWith("door_", StringComparison.Ordinal) ||
                    key.StartsWith("vitrage_", StringComparison.Ordinal)) return 0;
            }
            return Num(rep, key);
        }

        /// <summary>Толщина из имени слоя: «НВФ утеплитель– 100 мм» → 100; нет — null.</summary>
        internal static int? Thickness(string name)
        {
            var m = Regex.Match(name ?? "", @"(\d+)\s*мм", RegexOptions.IgnoreCase);
            int v;
            return m.Success && int.TryParse(m.Groups[1].Value, out v) ? (int?)v : null;
        }

        /// <summary>Текст строки утеплителя с толщиной: заменить «N мм», вставить после
        /// «толщиной» или дописать «; N мм».</summary>
        internal static string WithThickness(string text, int mm)
        {
            string t = text ?? "";
            var re = new Regex(@"\d+\s*мм", RegexOptions.IgnoreCase);
            if (re.IsMatch(t)) return re.Replace(t, mm + " мм", 1);
            var th = new Regex(@"(толщин\w*)\s*", RegexOptions.IgnoreCase);
            if (th.IsMatch(t)) return th.Replace(t, "$1 " + mm + " мм ", 1).Replace("  ", " ");
            return t.TrimEnd() + "; " + mm + " мм";
        }

        // ── сборка ──
        internal static Result Build(string templatePath, Config cfg, List<Group> groups,
                                     double parapetM, string outPath)
        {
            var res = new Result();
            File.Copy(templatePath, outPath, true);
            using (var fs = new FileStream(outPath, FileMode.Open, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                string sheetPart = SheetPart(zip, cfg.Sheet);
                var shared = ReadShared(zip);
                var doc = LoadXml(zip, sheetPart);
                var ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("m", NsMain);
                var sheetData = (XmlElement)doc.SelectSingleNode("/m:worksheet/m:sheetData", ns);
                if (sheetData == null) throw new InvalidDataException("в форме нет sheetData");

                // 1. суммы по клеткам карты
                var total = new List<Dictionary<string, object>>();
                foreach (var g in groups) total.AddRange(g.Reports);
                foreach (var kv in cfg.Fill)
                {
                    var spec = kv.Value as Dictionary<string, object>;
                    double v = 0;
                    if (spec != null && Get(spec, "parapet") is bool && (bool)Get(spec, "parapet"))
                        v = parapetM;
                    else if (spec != null)
                    {
                        var keys = Get(spec, "sum") as object[];
                        if (keys != null)
                            foreach (var rep in total)
                                foreach (var k in keys)
                                    v += Field(rep, Convert.ToString(k, CultureInfo.InvariantCulture));
                    }
                    v = Math.Round(v, 3);
                    res.Values[kv.Key.ToUpperInvariant()] = v;
                }

                // 2. утеплитель по толщинам слоёв (до записи чисел — строки могут сдвинуться)
                var byMm = new List<KeyValuePair<int?, double>>();
                string areaKey = FirstAreaKey(cfg);
                foreach (var g in groups)
                {
                    double a = 0;
                    foreach (var rep in g.Reports) a += Field(rep, areaKey);
                    int? mm = Thickness(g.Layer);
                    int i = byMm.FindIndex(p => p.Key == mm);
                    if (i < 0) byMm.Add(new KeyValuePair<int?, double>(mm, a));
                    else byMm[i] = new KeyValuePair<int?, double>(mm, byMm[i].Value + a);
                }
                int insRow = FindInsulationRow(sheetData, ns, shared, cfg);
                var addrMap = new Dictionary<int, int>();   // старая строка → новая
                if (insRow > 0 && byMm.Exists(p => p.Key.HasValue))
                {
                    string baseText = CellText(RowOf(sheetData, ns, insRow), "B", shared);
                    // строки формы, где такая толщина уже написана (вопрос Германа: «видит слой
                    // утеплитель 150 мм — площадь в ячейку, где написан утеплитель 150 мм»)
                    var existing = new List<KeyValuePair<int, KeyValuePair<int?, double>>>();
                    var free = new List<KeyValuePair<int?, double>>();
                    foreach (var p in byMm)
                    {
                        int r = p.Key.HasValue ? RowWithThickness(sheetData, ns, shared, cfg, p.Key.Value) : 0;
                        if (r > 0) existing.Add(new KeyValuePair<int, KeyValuePair<int?, double>>(r, p));
                        else free.Add(p);
                    }
                    // строка утеплителя формы, не занятая толщиной, берёт первую свободную группу;
                    // остальным свободным — копии этой строки сразу под ней
                    bool baseTaken = existing.Exists(x => x.Key == insRow);
                    var rowsFor = new List<KeyValuePair<int, KeyValuePair<int?, double>>>();
                    int added = 0;
                    foreach (var p in free)
                    {
                        if (!baseTaken)
                        {
                            baseTaken = true;
                            rowsFor.Add(new KeyValuePair<int, KeyValuePair<int?, double>>(insRow, p));
                            continue;
                        }
                        InsertCopy(doc, sheetData, ns, insRow + added, 1, addrMap);
                        added++;
                        rowsFor.Add(new KeyValuePair<int, KeyValuePair<int?, double>>(insRow + added, p));
                    }
                    res.RowsAdded = added;
                    foreach (var x in existing)
                        rowsFor.Add(new KeyValuePair<int, KeyValuePair<int?, double>>(
                            RowNum(Shift("B" + x.Key, addrMap)), x.Value));
                    bool one = byMm.Count == 1;     // один утеплитель — формула строки (=D3) остаётся
                    foreach (var x in rowsFor)
                    {
                        int r = x.Key;
                        var row = RowOf(sheetData, ns, r);
                        bool mine = r >= insRow && r <= insRow + added;
                        string t = x.Value.Key.HasValue
                                   ? WithThickness(mine ? baseText : CellText(row, "B", shared), x.Value.Key.Value)
                                   : CellText(row, "B", shared);
                        SetText(doc, row, ns, "B" + r, t);
                        if (!one) SetNumber(doc, row, ns, "D" + r, Math.Round(x.Value.Value, 3));
                        res.Lines.Add("строка " + r + " «" + t + "»: " + F3(x.Value.Value) + " м²" +
                                      (x.Value.Key.HasValue ? "" : " (слой без толщины в имени)"));
                    }
                    if (added > 0) Renumber(doc, sheetData, ns, shared, insRow, insRow + added);
                }

                // 3. числа карты и «вручную» (адреса — после сдвига строк)
                foreach (var kv in res.Values)
                {
                    string a = Shift(kv.Key, addrMap);
                    var row = RowOf(sheetData, ns, RowNum(a));
                    if (row == null) continue;
                    SetNumber(doc, row, ns, a, kv.Value);
                    var spec = cfg.Fill[FindKey(cfg.Fill, kv.Key)] as Dictionary<string, object>;
                    res.Lines.Add(a + " = " + F3(kv.Value) + " — " + Str(spec, "what"));
                }
                foreach (var kv in cfg.Manual)
                {
                    string a = Shift(kv.Key.ToUpperInvariant(), addrMap);
                    var row = RowOf(sheetData, ns, RowNum(a));
                    if (row == null) continue;
                    string note = "E" + RowNum(a);
                    if (CellText(row, "E", shared).Trim().Length == 0)
                        SetText(doc, row, ns, note, "заполнить вручную");
                    res.Manual.Add(a + " — " + Convert.ToString(kv.Value, CultureInfo.InvariantCulture));
                }

                SaveXml(zip, sheetPart, doc);
                if (addrMap.Count > 0) ShiftWorkbookNames(zip, addrMap);
                ForceRecalc(zip);
            }
            return res;
        }

        private static string FirstAreaKey(Config cfg)
        {
            foreach (var kv in cfg.Fill)
            {
                var keys = Get(kv.Value as Dictionary<string, object>, "sum") as object[];
                if (keys != null && keys.Length == 1 && Convert.ToString(keys[0], CultureInfo.InvariantCulture).EndsWith("_m2", StringComparison.Ordinal))
                    return Convert.ToString(keys[0], CultureInfo.InvariantCulture);
            }
            return "area_net_m2";
        }

        private static string FindKey(Dictionary<string, object> d, string upper)
        {
            foreach (var k in d.Keys) if (k.ToUpperInvariant() == upper) return k;
            return upper;
        }

        internal static string F3(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        // ── строки формы ──
        private static int FindInsulationRow(XmlElement sheetData, XmlNamespaceManager ns,
                                             List<string> shared, Config cfg)
        {
            if (cfg.InsulationRow > 0 && IsInsulation(CellText(RowOf(sheetData, ns, cfg.InsulationRow), "B", shared), cfg))
                return cfg.InsulationRow;
            foreach (XmlElement row in sheetData.SelectNodes("m:row", ns))
                if (IsInsulation(CellText(row, "B", shared), cfg))
                    return int.Parse(row.GetAttribute("r"), CultureInfo.InvariantCulture);
            return 0;
        }

        private static bool IsInsulation(string text, Config cfg)
        {
            string t = (text ?? "").ToLowerInvariant();
            foreach (var w in cfg.InsulationWords) if (t.Contains(w)) return true;
            return false;
        }

        private static int RowWithThickness(XmlElement sheetData, XmlNamespaceManager ns, List<string> shared,
                                            Config cfg, int mm)
        {
            foreach (XmlElement row in sheetData.SelectNodes("m:row", ns))
            {
                string t = CellText(row, "B", shared);
                if (!IsInsulation(t, cfg)) continue;
                int? have = Thickness(t);
                if (have.HasValue && have.Value == mm)
                    return int.Parse(row.GetAttribute("r"), CultureInfo.InvariantCulture);
            }
            return 0;
        }

        private static XmlElement RowOf(XmlElement sheetData, XmlNamespaceManager ns, int r)
        {
            return sheetData.SelectSingleNode("m:row[@r='" + r + "']", ns) as XmlElement;
        }

        internal static int RowNum(string addr)
        {
            var m = Regex.Match(addr ?? "", @"\d+");
            return m.Success ? int.Parse(m.Value, CultureInfo.InvariantCulture) : 0;
        }

        private static string Col(string addr) { return Regex.Match(addr ?? "", @"^[A-Z]+").Value; }

        private static string Shift(string addr, Dictionary<int, int> map)
        {
            int r = RowNum(addr), nr;
            return map.TryGetValue(r, out nr) ? Col(addr) + nr : addr;
        }

        private static string CellText(XmlElement row, string col, List<string> shared)
        {
            if (row == null) return "";
            foreach (XmlNode n in row.ChildNodes)
            {
                var c = n as XmlElement;
                if (c == null || c.LocalName != "c" || Col(c.GetAttribute("r")) != col) continue;
                string t = c.GetAttribute("t");
                if (t == "s")
                {
                    var v = FirstChild(c, "v");
                    int i;
                    return v != null && int.TryParse(v.InnerText, out i) && i >= 0 && i < shared.Count ? shared[i] : "";
                }
                if (t == "inlineStr")
                {
                    var isn = FirstChild(c, "is");
                    return isn == null ? "" : isn.InnerText;
                }
                var vv = FirstChild(c, "v");
                return vv == null ? "" : vv.InnerText;
            }
            return "";
        }

        private static XmlElement FirstChild(XmlElement e, string local)
        {
            foreach (XmlNode n in e.ChildNodes)
                if (n is XmlElement && n.LocalName == local) return (XmlElement)n;
            return null;
        }

        private static XmlElement CellOf(XmlDocument doc, XmlElement row, string addr)
        {
            foreach (XmlNode n in row.ChildNodes)
                if (n is XmlElement && n.LocalName == "c" && ((XmlElement)n).GetAttribute("r") == addr)
                    return (XmlElement)n;
            // нет клетки — вставить по порядку колонок
            var c = doc.CreateElement("c", NsMain);
            c.SetAttribute("r", addr);
            XmlNode before = null;
            foreach (XmlNode n in row.ChildNodes)
                if (n is XmlElement && n.LocalName == "c" &&
                    ColIndex(Col(((XmlElement)n).GetAttribute("r"))) > ColIndex(Col(addr))) { before = n; break; }
            if (before != null) row.InsertBefore(c, before); else row.AppendChild(c);
            return c;
        }

        private static int ColIndex(string col)
        {
            int n = 0;
            foreach (char ch in col) n = n * 26 + (ch - 'A' + 1);
            return n;
        }

        private static void Clear(XmlElement c)
        {
            string s = c.GetAttribute("s");
            string r = c.GetAttribute("r");
            c.RemoveAll();
            c.SetAttribute("r", r);
            if (s.Length > 0) c.SetAttribute("s", s);
        }

        internal static void SetNumber(XmlDocument doc, XmlElement row, XmlNamespaceManager ns, string addr, double v)
        {
            var c = CellOf(doc, row, addr);
            Clear(c);
            var ve = doc.CreateElement("v", NsMain);
            ve.InnerText = v.ToString("R", CultureInfo.InvariantCulture);
            c.AppendChild(ve);
        }

        internal static void SetText(XmlDocument doc, XmlElement row, XmlNamespaceManager ns, string addr, string text)
        {
            var c = CellOf(doc, row, addr);
            Clear(c);
            c.SetAttribute("t", "inlineStr");
            var isn = doc.CreateElement("is", NsMain);
            var t = doc.CreateElement("t", NsMain);
            t.InnerText = text ?? "";
            if (t.InnerText != t.InnerText.Trim())
                t.SetAttribute("space", "http://www.w3.org/XML/1998/namespace", "preserve");
            isn.AppendChild(t);
            c.AppendChild(isn);
        }

        // вставка count копий строки after (после неё); сдвиг нижних строк и ссылок формул
        private static void InsertCopy(XmlDocument doc, XmlElement sheetData, XmlNamespaceManager ns,
                                       int after, int count, Dictionary<int, int> map)
        {
            var src = RowOf(sheetData, ns, after);
            var rows = new List<XmlElement>();
            foreach (XmlElement row in sheetData.SelectNodes("m:row", ns)) rows.Add(row);
            // сдвиг снизу вверх
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                int r = int.Parse(rows[i].GetAttribute("r"), CultureInfo.InvariantCulture);
                if (r <= after) break;
                SetRowNum(rows[i], r + count);
            }
            // карта исходных номеров строк формы → текущие (накопительно)
            UpdateMap(map, after, count, LastRow(rows));
            XmlNode anchor = src;
            for (int k = 1; k <= count; k++)
            {
                var copy = (XmlElement)src.CloneNode(true);
                SetRowNum(copy, after + k);
                sheetData.InsertAfter(copy, anchor);
                anchor = copy;
            }
            // формулы: ссылки на строки ниже after сдвигаются
            foreach (XmlElement f in sheetData.SelectNodes("m:row/m:c/m:f", ns))
                f.InnerText = ShiftFormula(f.InnerText, after, count);
            ShiftRefs(doc, ns, after, count);
        }

        private static int LastRow(List<XmlElement> rows)
        {
            return rows.Count == 0 ? 0 : int.Parse(rows[rows.Count - 1].GetAttribute("r"), CultureInfo.InvariantCulture);
        }

        private static void UpdateMap(Dictionary<int, int> map, int after, int count, int last)
        {
            // map: исходная строка формы → текущая. Строки ниже after сдвигаются на count.
            var cur = new Dictionary<int, int>(map);
            map.Clear();
            for (int r = 1; r <= last; r++)
            {
                int now;
                if (!cur.TryGetValue(r, out now)) now = r;
                map[r] = now > after ? now + count : now;
            }
        }

        private static void SetRowNum(XmlElement row, int r)
        {
            row.SetAttribute("r", r.ToString(CultureInfo.InvariantCulture));
            foreach (XmlNode n in row.ChildNodes)
            {
                var c = n as XmlElement;
                if (c == null || c.LocalName != "c") continue;
                c.SetAttribute("r", Col(c.GetAttribute("r")) + r);
            }
        }

        /// <summary>Сдвиг ссылок A1 в формуле: строки больше after — на count.</summary>
        internal static string ShiftFormula(string f, int after, int count)
        {
            return Regex.Replace(f ?? "", @"(?<![A-Za-z_\d\]])(\$?)([A-Z]{1,3})(\$?)(\d+)(?![\d(])", m =>
            {
                int r = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                if (r > after) r += count;
                return m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + r;
            });
        }

        // dimension / autoFilter / mergeCell / conditionalFormatting — концы диапазонов
        private static void ShiftRefs(XmlDocument doc, XmlNamespaceManager ns, int after, int count)
        {
            foreach (XmlElement e in doc.SelectNodes("//m:dimension|//m:autoFilter|//m:mergeCell", ns))
                e.SetAttribute("ref", ShiftFormula(e.GetAttribute("ref"), after, count));
            foreach (XmlElement e in doc.SelectNodes("//m:conditionalFormatting|//m:dataValidation", ns))
                if (e.HasAttribute("sqref")) e.SetAttribute("sqref", ShiftFormula(e.GetAttribute("sqref"), after, count));
        }

        // «Поз.»: после вставки номера раздела идут подряд (2.1, 2.2, 2.3 …)
        private static void Renumber(XmlDocument doc, XmlElement sheetData, XmlNamespaceManager ns,
                                     List<string> shared, int from, int to)
        {
            string first = CellText(RowOf(sheetData, ns, from), "A", shared).Trim();
            var m = Regex.Match(first, @"^(\d+)\.(\d+)$");
            if (!m.Success) return;
            string sec = m.Groups[1].Value;
            int n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            foreach (XmlElement row in sheetData.SelectNodes("m:row", ns))
            {
                int r = int.Parse(row.GetAttribute("r"), CultureInfo.InvariantCulture);
                if (r <= from) continue;
                string a = CellText(row, "A", shared).Trim();
                var mm = Regex.Match(a, @"^(\d+)\.(\d+)$");
                bool isCopy = r <= to;
                if (!isCopy && (!mm.Success || mm.Groups[1].Value != sec)) { if (mm.Success) break; else continue; }
                n++;
                SetText(doc, row, ns, "A" + r, sec + "." + n);
            }
        }

        // ── части книги ──
        private static string SheetPart(ZipArchive zip, string sheetName)
        {
            var wb = LoadXml(zip, "xl/workbook.xml");
            var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");
            var ns = new XmlNamespaceManager(wb.NameTable);
            ns.AddNamespace("m", NsMain);
            ns.AddNamespace("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            XmlElement sh = null;
            foreach (XmlElement s in wb.SelectNodes("//m:sheets/m:sheet", ns))
            {
                if (sheetName.Length == 0 || s.GetAttribute("name") == sheetName) { sh = s; break; }
            }
            if (sh == null) throw new InvalidDataException("в форме нет листа «" + sheetName + "»");
            string rid = sh.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
            foreach (XmlNode n in rels.DocumentElement.ChildNodes)
            {
                var e = n as XmlElement;
                if (e != null && e.GetAttribute("Id") == rid)
                {
                    string t = e.GetAttribute("Target").TrimStart('/');
                    return t.StartsWith("xl/", StringComparison.Ordinal) ? t : "xl/" + t;
                }
            }
            throw new InvalidDataException("лист формы не найден в связях книги");
        }

        private static List<string> ReadShared(ZipArchive zip)
        {
            var list = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") == null) return list;
            var d = LoadXml(zip, "xl/sharedStrings.xml");
            foreach (XmlNode si in d.DocumentElement.ChildNodes)
            {
                if (!(si is XmlElement) || si.LocalName != "si") continue;
                var sb = new StringBuilder();
                foreach (XmlNode t in ((XmlElement)si).GetElementsByTagName("t", NsMain)) sb.Append(t.InnerText);
                list.Add(sb.ToString());
            }
            return list;
        }

        private static XmlDocument LoadXml(ZipArchive zip, string part)
        {
            var e = zip.GetEntry(part);
            if (e == null) throw new InvalidDataException("в форме нет части " + part);
            var d = new XmlDocument { PreserveWhitespace = true };
            using (var s = e.Open()) d.Load(s);
            return d;
        }

        private static void SaveXml(ZipArchive zip, string part, XmlDocument doc)
        {
            var old = zip.GetEntry(part);
            if (old != null) old.Delete();
            var e = zip.CreateEntry(part, CompressionLevel.Optimal);
            using (var s = e.Open())
            using (var w = XmlWriter.Create(s, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                doc.Save(w);
        }

        private static void ShiftWorkbookNames(ZipArchive zip, Dictionary<int, int> map)
        {
            // определённые имена листа (фильтр, область печати): концы диапазонов — по карте строк
            var wb = LoadXml(zip, "xl/workbook.xml");
            var ns = new XmlNamespaceManager(wb.NameTable);
            ns.AddNamespace("m", NsMain);
            bool changed = false;
            foreach (XmlElement dn in wb.SelectNodes("//m:definedNames/m:definedName", ns))
            {
                string t = dn.InnerText;
                string nt = Regex.Replace(t, @"(\$?)([A-Z]{1,3})(\$?)(\d+)", m =>
                {
                    int r = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture), nr;
                    if (!map.TryGetValue(r, out nr)) nr = r;
                    return m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + nr;
                });
                if (nt != t) { dn.InnerText = nt; changed = true; }
            }
            if (changed) SaveXml(zip, "xl/workbook.xml", wb);
        }

        /// <summary>Excel пересчитывает формулы при открытии; устаревшая цепочка расчёта — долой.</summary>
        private static void ForceRecalc(ZipArchive zip)
        {
            var cc = zip.GetEntry("xl/calcChain.xml");
            if (cc != null)
            {
                cc.Delete();
                var rels = LoadXml(zip, "xl/_rels/workbook.xml.rels");
                foreach (XmlNode n in new List<XmlNode>(Nodes(rels.DocumentElement)))
                {
                    var e = n as XmlElement;
                    if (e != null && e.GetAttribute("Target").EndsWith("calcChain.xml", StringComparison.Ordinal))
                        rels.DocumentElement.RemoveChild(e);
                }
                SaveXml(zip, "xl/_rels/workbook.xml.rels", rels);
                var ct = LoadXml(zip, "[Content_Types].xml");
                foreach (XmlNode n in new List<XmlNode>(Nodes(ct.DocumentElement)))
                {
                    var e = n as XmlElement;
                    if (e != null && e.GetAttribute("PartName").EndsWith("calcChain.xml", StringComparison.Ordinal))
                        ct.DocumentElement.RemoveChild(e);
                }
                SaveXml(zip, "[Content_Types].xml", ct);
            }
            var wb = LoadXml(zip, "xl/workbook.xml");
            var ns = new XmlNamespaceManager(wb.NameTable);
            ns.AddNamespace("m", NsMain);
            var calc = wb.SelectSingleNode("//m:calcPr", ns) as XmlElement;
            if (calc == null)
            {
                calc = wb.CreateElement("calcPr", NsMain);
                wb.DocumentElement.AppendChild(calc);
            }
            calc.SetAttribute("fullCalcOnLoad", "1");
            SaveXml(zip, "xl/workbook.xml", wb);
        }

        private static IEnumerable<XmlNode> Nodes(XmlElement e)
        {
            foreach (XmlNode n in e.ChildNodes) yield return n;
        }
    }
}
