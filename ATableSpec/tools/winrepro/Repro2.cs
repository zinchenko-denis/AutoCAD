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

        // ═══ «Взять с таблицы» (фидбэк Алексея 08.07): def -> секции раскроя ═══
        var takeM = form.GetType().GetMethod("BuildCutSeedsFromDef",
                        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Func<Dictionary<string, object>, object[]> take = d0x =>
        {
            var args = new object[] { d0x, null, null };
            var res = takeM.Invoke(null, args);
            return new object[] { res, args[1], args[2] };
        };

        var defSpec = new Dictionary<string, object>
        {
            { "sections", new object[] {
                new Dictionary<string, object> {
                    { "section_title", "Крышки" },
                    { "header", new object[] { "№ п/п", "Артикул", "Длина, мм", "Колич." } },
                    { "columns", new object[] { "=row", "=«17.01.01»", "=Object.«Длина»-150", "=Count" } },
                    { "filter", new object[] {
                        new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", "RF-крышки" } },
                        new Dictionary<string, object> { { "field", "МАРКИРОВКА" }, { "op", "=" },
                            { "value", "Сп1;Вр1" }, { "values", new object[] { "Сп1", "Вр1" } } } } } },
                new Dictionary<string, object> {
                    { "section_title", "Стойки" },
                    { "header", new object[] { "Артикул", "Длина, мм" } },
                    { "columns", new object[] { "=Object.«ПРОФ»", "=Object.«Длина»" } },
                    { "filter", new object[] {
                        new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", "RF-стойки" } } } } } } }
        };
        var t1 = take(defSpec);
        var sd1 = (IList)t1[0];
        var nl1 = (List<string>)t1[2];
        Check(sd1.Count == 2, "взять: 2 секции источника -> 2 отчёта раскроя", "n=" + sd1.Count);
        if (sd1.Count == 2)
        {
            var a = sd1[0]; var rowsA = (List<string[]>)F(a, "FullRows") ?? (List<string[]>)a.GetType().GetField("FullRows").GetValue(a);
            Check(rowsA[0][0] == "17.01.01 8 6000" && rowsA[0][1] == "=Object.«Длина»-150" &&
                  rowsA[0][4] == "по возрастанию",
                  "взять: литерал-артикул в шапке, ПРАВЛЕНАЯ длина, группа по длине",
                  rowsA[0][0] + " | " + rowsA[0][1] + " | " + rowsA[0][4]);
            Check(rowsA[1][1] == "=Count", "взять: строка =Count", rowsA[1][1]);
            string sl = Convert.ToString(a.GetType().GetField("SeedLayer").GetValue(a));
            Check(sl == "RF-крышки", "взять: источник секции перенесён", sl);
            bool fOk = false;
            foreach (var rr in rowsA)
                if ((rr[1] ?? "").Contains("МАРКИРОВКА") && rr[2] == "=" && rr[3] == "Сп1;Вр1") fOk = true;
            Check(fOk, "взять: values-фильтр источника перенесён строкой «Сп1;Вр1»");
            var mg = (List<int[]>)a.GetType().GetField("SeedMerges").GetValue(a);
            Check(mg != null && mg.Count == 1 && mg[0][0] == 0 && mg[0][1] == 1,
                  "взять: контракт-шапка объединена [0,1]");
            var b = sd1[1]; var rowsB = (List<string[]>)b.GetType().GetField("FullRows").GetValue(b);
            Check(rowsB[0][0] == "Артикул 8 6000", "взять: выражение-артикул -> плейсхолдер в шапке", rowsB[0][0]);
            Check(nl1.Exists(z => z.Contains("ПРОФ")), "взять: заметка про артикул-выражение");
        }

        var defBeads = new Dictionary<string, object>
        {
            { "beads", new Dictionary<string, object> { { "layer", "RF-стойки" }, { "source", "RF-заполнения" } } },
            { "sections", new object[] {
                new Dictionary<string, object> {
                    { "section_title", "Вертикальный штапик в зонах с терморазрывом" },
                    { "header", new object[] { "Наименование", "Размер, мм" } },
                    { "columns", new object[] { "=Object.«ИМЯ»", "=Object.«ШТ_РАЗМЕР»" } },
                    { "filter", new object[] {
                        new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", "ШТАПИК-РАЗРЕЗ" } } } } },
                new Dictionary<string, object> {
                    { "section_title", "Без длины" },
                    { "header", new object[] { "Марка" } },
                    { "columns", new object[] { "=Object.«МАРКИРОВКА»" } } } } }
        };
        var t2 = take(defBeads);
        var sd2x = (IList)t2[0];
        string bl2 = Convert.ToString(t2[1]);
        var nl2 = (List<string>)t2[2];
        Check(sd2x.Count == 1, "взять(штапики): секция без длины пропущена", "n=" + sd2x.Count);
        Check(bl2 == "RF-стойки", "взять(штапики): beads.layer вытащен наружу", bl2);
        Check(nl2.Exists(z => z.Contains("Без длины")), "взять: пропуск отражён в заметках");
        if (sd2x.Count == 1)
        {
            var rowsC = (List<string[]>)sd2x[0].GetType().GetField("FullRows").GetValue(sd2x[0]);
            Check(rowsC[0][1] == "=Object.«ШТ_РАЗМЕР»", "взять(штапики): длина = ШТ_РАЗМЕР", rowsC[0][1]);
        }

        var t3 = take(null);
        Check(((IList)t3[0]).Count == 0 && ((List<string>)t3[2]).Count > 0,
              "взять: null-def -> пусто с заметкой, без падения");

        string dump = Path.Combine(Path.GetTempPath(), "ATableSpec_last_def.json");
        Check(File.Exists(dump), "дамп def в TEMP записан", dump);

        Console.WriteLine(fails == 0 ? "\n=== ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ ===" : "\n=== ПРОВАЛОВ: " + fails + " ===");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
