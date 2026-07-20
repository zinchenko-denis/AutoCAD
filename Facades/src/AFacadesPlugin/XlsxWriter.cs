using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AFacadesPlugin
{
    /// <summary>
    /// Минимальный генератор .xlsx без внешних библиотек (фидбэк Германа
    /// №2 п.2: выгрузка ведомости в Excel). Одна книга, один лист, строки
    /// из string (inlineStr) и double (number). Открывается Excel/LibreOffice.
    /// </summary>
    internal static class XlsxWriter
    {
        internal static void Write(string path, string sheetName,
                                   List<object[]> rows)
        {
            using (var fs = new FileStream(path, FileMode.Create,
                                           FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Put(zip, "[Content_Types].xml",
"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
"<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
"<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
"<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
"<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
"<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
"</Types>");
                Put(zip, "_rels/.rels",
"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
"</Relationships>");
                Put(zip, "xl/workbook.xml",
"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
"<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
"xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
"<sheets><sheet name=\"" + Esc(sheetName) +
"\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Put(zip, "xl/_rels/workbook.xml.rels",
"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
"<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
"</Relationships>");
                Put(zip, "xl/worksheets/sheet1.xml", Sheet(rows));
            }
        }

        private static string Sheet(List<object[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
            for (int r = 0; r < rows.Count; r++)
            {
                sb.Append("<row r=\"").Append(r + 1).Append("\">");
                var row = rows[r];
                for (int c = 0; c < row.Length; c++)
                {
                    string cell = Col(c) + (r + 1);
                    object v = row[c];
                    if (v is double || v is float || v is int || v is long)
                    {
                        double d = Convert.ToDouble(
                            v, CultureInfo.InvariantCulture);
                        sb.Append("<c r=\"").Append(cell).Append("\"><v>")
                          .Append(d.ToString("0.###",
                                  CultureInfo.InvariantCulture))
                          .Append("</v></c>");
                    }
                    else if (v != null)
                    {
                        sb.Append("<c r=\"").Append(cell)
                          .Append("\" t=\"inlineStr\"><is><t xml:space=\"preserve\">")
                          .Append(Esc(Convert.ToString(
                                  v, CultureInfo.InvariantCulture)))
                          .Append("</t></is></c>");
                    }
                }
                sb.Append("</row>");
            }
            sb.Append("</sheetData></worksheet>");
            return sb.ToString();
        }

        private static string Col(int i)
        {
            string s = "";
            i++;
            while (i > 0)
            {
                int m = (i - 1) % 26;
                s = (char)('A' + m) + s;
                i = (i - 1) / 26;
            }
            return s;
        }

        private static string Esc(string s)
        {
            return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;")
                            .Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        private static void Put(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name);
            using (var w = new StreamWriter(e.Open(),
                                            new UTF8Encoding(false)))
                w.Write(content);
        }
    }
}
