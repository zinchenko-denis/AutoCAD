// Репро/регресс мёртвого выбора «Штапики»: до фикса ApplyTemplate(4) отсекался
// гардом «tpl > 3» — выбор пункта не делал НИЧЕГО (видео Алексея 07–08.07).
// Прогон: xvfb-run -a mono repro2.exe
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using AtSpecPlugin;

static class Repro2
{
    static int fails = 0;
    static void Check(bool ok, string what, string detail = "")
    {
        Console.WriteLine((ok ? "  OK  " : "  FAIL") + " " + what + (ok || detail == "" ? "" : "  [" + detail + "]"));
        if (!ok) fails++;
    }
    static object F(object o, string name)   // приватное поле
    {
        var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return f == null ? null : f.GetValue(o);
    }
    static void Pump(int ms)
    {
        var until = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < until) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
    }

    [STAThread]
    static void Main()
    {
        var layers = new List<string> { "0", "RF-доборники", "RF-заполнения", "RF-кронштейны",
                                        "RF-ригеля", "RF-створки", "RF-стойки", "_АР_ВИТРАЖ_БЛОКИ" };
        var fields = new List<string> { "ИМЯ", "МАРКИРОВКА", "ПРОФ", "Длина", "Ширина", "Высота" };
        var vals = new Dictionary<string, Dictionary<string, List<string>>>();

        var form = new ReportBuilderForm(layers, fields, vals, new List<string>());
        form.Show();
        Pump(300);

        var cbTitle = (ComboBox)F(form, "cbTitle");
        var cmbStands = (ComboBox)F(form, "cmbStands");
        var cards = (IList)F(form, "_cards");

        Check(cards.Count == 1, "старт: одна дефолтная секция", "cards=" + cards.Count);
        int idx = cbTitle.Items.IndexOf("Штапики");
        Check(idx >= 0, "пункт «Штапики» в выпадушке", "idx=" + idx);

        // === действие Алексея: выбрать «Штапики» в выпадушке заголовка ===
        cbTitle.SelectedIndex = idx;
        Pump(500);   // BeginInvoke-сеттер заголовка + пересев

        Check(cards.Count == 4, "пересев: 4 секции", "cards=" + cards.Count);
        Check(cbTitle.Text == "СПЕЦИФИКАЦИЯ ШТАПИКОВ", "заголовок таблицы", "«" + cbTitle.Text + "»");
        Check(Convert.ToString(cmbStands.SelectedItem) == "RF-стойки",
              "«Стойки:» = RF-стойки (дефолт)", "«" + Convert.ToString(cmbStands.SelectedItem) + "»");
        Check(ReproMsg.Log.Any(m => m.Contains("Заменить все секции")),
              "диалог подтверждения пересева показан");

        string[] wantStyk = { "0", "0", "1", null };   // фильтр ШТ_СТЫК по секциям; 4-я — без
        string[] wantSrc  = { "RF-заполнения", "RF-заполнения", "RF-заполнения", "ШТАПИК-РАЗРЕЗ" };
        for (int i = 0; i < 4 && i < cards.Count; i++)
        {
            var card = cards[i];
            var lblNum = (Label)F(card, "lblNum");
            var grid = (DataGridView)F(card, "grid");
            var src = (ComboBox)F(card, "cmbSource");
            Check(lblNum.Text == "Отчёт " + (i + 1) + " из 4",
                  "секция " + (i + 1) + ": подпись", "«" + lblNum.Text + "»");
            Check(Convert.ToString(src.Text) == wantSrc[i],
                  "секция " + (i + 1) + ": источник " + wantSrc[i], "«" + src.Text + "»");
            string got = null;
            foreach (DataGridViewRow r in grid.Rows)
            {
                if (r.IsNewRow) continue;
                string ex = Convert.ToString(r.Cells["expr"].Value) ?? "";
                if (ex.Contains("ШТ_СТЫК"))
                    got = Convert.ToString(r.Cells["cond"].Value) + "|" + Convert.ToString(r.Cells["val"].Value);
            }
            if (wantStyk[i] == null)
                Check(got == null, "секция " + (i + 1) + ": без фильтра ШТ_СТЫК", "got=" + got);
            else
                Check(got == "=|" + wantStyk[i],
                      "секция " + (i + 1) + ": фильтр-строка ШТ_СТЫК=" + wantStyk[i], "got=" + got);
        }

        // === BuildDef: грид → def (то, чего Алексей так и не достиг) ===
        int msgBefore = ReproMsg.Log.Count;
        form.GetType().GetMethod("BuildDef", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(form, null);
        var def = form.ReportDef;
        Check(def != null, "BuildDef вернул определение");
        bool diag = ReproMsg.Log.Skip(msgBefore).Any(m => m.Contains("не попал в определение"));
        Check(!diag, "самопроверка ШТ_СТЫК грид↔def: расхождений нет");

        var sections = def != null && def.ContainsKey("sections") ? def["sections"] as IList : null;
        Check(sections != null && sections.Count == 4, "def: 4 секции",
              "n=" + (sections == null ? -1 : sections.Count));
        if (sections != null && sections.Count == 4)
            for (int i = 0; i < 4; i++)
            {
                var sd = (Dictionary<string, object>)sections[i];
                var fl = sd.ContainsKey("filter") ? sd["filter"] as IList : null;
                string styk = null, lay = null;
                if (fl != null)
                    foreach (Dictionary<string, object> fd in fl)
                    {
                        string fld = Convert.ToString(fd["field"]);
                        if (fld.IndexOf("ШТ_СТЫК", StringComparison.OrdinalIgnoreCase) >= 0)
                            styk = Convert.ToString(fd["value"]);
                        if (fld == "Слой") lay = Convert.ToString(fd["value"]);
                    }
                Check(styk == wantStyk[i], "def секция " + (i + 1) + ": ШТ_СТЫК=" +
                      (wantStyk[i] ?? "(нет)"), "got=" + styk);
                Check(lay == wantSrc[i], "def секция " + (i + 1) + ": Слой=" + wantSrc[i], "got=" + lay);
            }
        var beads = def != null && def.ContainsKey("beads") ? def["beads"] as Dictionary<string, object> : null;
        Check(beads != null && Convert.ToString(beads["layer"]) == "RF-стойки",
              "def.beads.layer = RF-стойки", beads == null ? "beads=null" : Convert.ToString(beads["layer"]));
        Check(beads != null && beads.ContainsKey("source") &&
              Convert.ToString(beads["source"]) == "RF-заполнения",
              "def.beads.source = RF-заполнения",
              beads == null ? "beads=null" : Convert.ToString(beads.ContainsKey("source") ? beads["source"] : "(нет)"));

        // ═══ ТЗ 08.07: Стойки-видимость + многослойные фильтры ═══
        var lblStands = (Label)F(form, "lblStands");

        // после Штапиков пара видима
        Check(cmbStands.Visible && lblStands.Visible, "Стойки: видимы в шаблоне «Штапики»");

        // мультиисточник на карточке 1 + условие-список + диапазон-защита
        var c0 = cards[0];
        var src0 = (ComboBox)F(c0, "cmbSource");
        var g0 = (DataGridView)F(c0, "grid");
        src0.Text = "RF-стойки; RF-ригеля";
        c0.GetType().GetMethod("OnSourceChanged", BindingFlags.Instance | BindingFlags.NonPublic)
          .Invoke(c0, null);
        {   // строка-условие: МАРКИРОВКА = «Сп1; Вр1 ;;Сп1» (трим/дедуп) — «только-фильтр»
            int ri = g0.Rows.Add();
            g0.Rows[ri].Cells["expr"].Value = "=Object.«МАРКИРОВКА»";
            g0.Rows[ri].Cells["cond"].Value = "=";
            g0.Rows[ri].Cells["val"].Value = "Сп1; Вр1 ;;Сп1";
            int r2 = g0.Rows.Add();      // диапазон + список -> защитно первое (в def)
            g0.Rows[r2].Cells["expr"].Value = "=Object.«Длина»";
            g0.Rows[r2].Cells["cond"].Value = ">";
            g0.Rows[r2].Cells["val"].Value = "1000;1";
        }
        var d0 = (Dictionary<string, object>)c0.GetType().GetMethod("ToDef").Invoke(c0, null);
        var fl0 = (IList)d0["filter"];
        var f0 = (Dictionary<string, object>)fl0[0];
        var f0v = f0.ContainsKey("values") ? f0["values"] as List<string> : null;
        Check(Convert.ToString(f0["field"]) == "Слой" && f0v != null &&
              f0v.Count == 2 && f0v[0] == "RF-стойки" && f0v[1] == "RF-ригеля" &&
              Convert.ToString(f0["value"]) == "RF-стойки;RF-ригеля",
              "ToDef: мультиисточник -> Слой values[2] + value-склейка",
              f0v == null ? "values=null" : string.Join("|", f0v.ToArray()));
        Dictionary<string, object> fM = null, fR = null;
        foreach (Dictionary<string, object> ff in fl0)
        {
            if (Convert.ToString(ff["field"]) == "МАРКИРОВКА") fM = ff;
            if (Convert.ToString(ff["field"]) == "Длина") fR = ff;
        }
        var fMv = fM != null && fM.ContainsKey("values") ? fM["values"] as List<string> : null;
        Check(fMv != null && fMv.Count == 2 && fMv[0] == "Сп1" && fMv[1] == "Вр1",
              "ToDef: значение-список -> values (трим+дедуп)",
              fMv == null ? "null" : string.Join("|", fMv.ToArray()));
        Check(fR != null && !fR.ContainsKey("values") && Convert.ToString(fR["value"]) == "1000",
              "ToDef: диапазон+список -> защитно первое, без values",
              fR == null ? "null" : Convert.ToString(fR["value"]));

        // реверс: def-фильтр с values -> ячейка «Сп1;Вр1», источник-список -> в Источник
        var seedM = form.GetType().GetMethod("SeedFromSection", BindingFlags.Static | BindingFlags.NonPublic);
        var sd2 = new Dictionary<string, object>
        {
            { "section_title", "T" }, { "header", new object[] { "М" } },
            { "columns", new object[] { "=Object.«МАРКИРОВКА»" } },
            { "filter", new object[] {
                new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" },
                    { "value", "RF-стойки;RF-ригеля" },
                    { "values", new object[] { "RF-стойки", "RF-ригеля" } } },
                new Dictionary<string, object> { { "field", "МАРКИРОВКА" }, { "op", "=" },
                    { "value", "Сп1;Вр1" },
                    { "values", new object[] { "Сп1", "Вр1" } } } } }
        };
        var seed2 = seedM.Invoke(null, new object[] { sd2 });
        var sLayer = Convert.ToString(seed2.GetType().GetField("SeedLayer").GetValue(seed2));
        var sRows = (List<string[]>)seed2.GetType().GetField("FullRows").GetValue(seed2);
        Check(sLayer == "RF-стойки;RF-ригеля", "реверс: источник-список вернулся", sLayer);
        Check(sRows.Count >= 1 && sRows[0][2] == "=" && sRows[0][3] == "Сп1;Вр1",
              "реверс: values -> ячейка «Сп1;Вр1»", sRows[0][3]);

        // смена шаблона со Штапиков -> пара скрыта и сброшена, beads из def ушёл
        cbTitle.SelectedIndex = cbTitle.Items.IndexOf("Спецификация");
        Pump(500);
        Check(!cmbStands.Visible && !lblStands.Visible && cmbStands.SelectedIndex == 0,
              "Стойки: скрыты и сброшены вне штапиков",
              "vis=" + cmbStands.Visible + " idx=" + cmbStands.SelectedIndex);
        form.GetType().GetMethod("BuildDef", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(form, null);
        Check(form.ReportDef != null && !form.ReportDef.ContainsKey("beads"),
              "def после смены шаблона: без beads");

        string dump = Path.Combine(Path.GetTempPath(), "ATableSpec_last_def.json");
        Check(File.Exists(dump), "дамп def в TEMP записан", dump);

        Console.WriteLine(fails == 0 ? "\n=== ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ ===" : "\n=== ПРОВАЛОВ: " + fails + " ===");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
