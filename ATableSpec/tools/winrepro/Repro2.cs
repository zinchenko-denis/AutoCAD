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
        Func<Dictionary<string, object>, Func<string, string, List<string>>, object[]> take =
        (d0x, resolver) =>
        {
            var args = new object[] { d0x, resolver, null, null };
            var res = takeM.Invoke(null, args);
            return new object[] { res, args[2], args[3] };
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
        var t1 = take(defSpec, null);
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
            bool fRow = false;
            foreach (var rr in rowsA)
                if ((rr[1] ?? "").Contains("МАРКИРОВКА")) fRow = true;
            var afA = (List<Dictionary<string, object>>)a.GetType().GetField("AutoFilters").GetValue(a);
            bool afOk = afA != null && afA.Count == 1 &&
                        Convert.ToString(afA[0]["field"]) == "МАРКИРОВКА" &&
                        Convert.ToString(afA[0]["value"]) == "Сп1;Вр1" &&
                        afA[0].ContainsKey("values");
            Check(!fRow && afOk, "взять: фильтр источника СКРЫТ из грида, живёт в AutoFilters (values целы)",
                  "row=" + fRow + " af=" + (afA == null ? "null" : afA.Count.ToString()));
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
        var t2 = take(defBeads, null);
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

        var t3 = take(null, null);
        Check(((IList)t3[0]).Count == 0 && ((List<string>)t3[2]).Count > 0,
              "взять: null-def -> пусто с заметкой, без падения");

        // ═══ 08.07-4: развёртка артикула-выражения по значениям с чертежа ═══
        Func<string, string, List<string>> res45 = (lay, fld) =>
            (lay == "RF-стойки" && fld == "ПРОФ")
                ? new List<string> { "КП50", "КП45" } : new List<string>();
        var t4 = take(defSpec, res45);
        var sd4 = (IList)t4[0];
        var nl4 = (List<string>)t4[2];
        Check(sd4.Count == 3, "развёртка: литерал(1) + выражение(2 артикула) = 3 отчёта", "n=" + sd4.Count);
        if (sd4.Count == 3)
        {
            var rB = (List<string[]>)sd4[1].GetType().GetField("FullRows").GetValue(sd4[1]);
            var rC = (List<string[]>)sd4[2].GetType().GetField("FullRows").GetValue(sd4[2]);
            Check(rB[0][0] == "КП45 8 6000" && rC[0][0] == "КП50 8 6000",
                  "развёртка: шапки по артикулам, сортировка", rB[0][0] + " / " + rC[0][0]);
            bool profRow = false;
            foreach (var rr in rB)
                if ((rr[1] ?? "").Contains("ПРОФ")) profRow = true;
            var afB = (List<Dictionary<string, object>>)sd4[1].GetType().GetField("AutoFilters").GetValue(sd4[1]);
            bool profAf = false;
            if (afB != null)
                foreach (var ff in afB)
                    if (Convert.ToString(ff["field"]) == "ПРОФ" && Convert.ToString(ff["value"]) == "КП45")
                        profAf = true;
            Check(!profRow && profAf, "развёртка: доп-фильтр «ПРОФ = КП45» скрыт в AutoFilters, не в гриде");
            Check(nl4.Exists(z => z.Contains("развёрнут")), "развёртка: заметка о N отчётах");
        }

        // ═══ 10.07: литерал-артикул + слияние по артикулу + скрытые auto-фильтры ═══
        var defLit = new Dictionary<string, object>
        {
            { "beads", new Dictionary<string, object> { { "layer", "RF-стойки" }, { "source", "RF-заполнения" } } },
            { "sections", new object[] {
                new Dictionary<string, object> {
                    { "section_title", "Горизонтальный штапик в зонах без терморазрыва" },
                    { "header", new object[] { "№", "Наименование", "Артикул", "Длина, мм", "Колич." } },
                    { "columns", new object[] { "=row", "=Object.«МАРКИРОВКА»", "17_01_04",
                                                "=Object.«Ширина»+20", "=Count*2" } },
                    { "filter", new object[] {
                        new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", "RF-заполнения" } },
                        new Dictionary<string, object> { { "field", "МАРКИРОВКА" }, { "op", "не содержит" }, { "value", "вр" } } } } },
                new Dictionary<string, object> {
                    { "section_title", "Вертикальный штапик в зонах без терморазрыва" },
                    { "header", new object[] { "№", "Наименование", "Артикул", "Длина, мм", "Колич." } },
                    { "columns", new object[] { "=row", "=Object.«МАРКИРОВКА»", "17_01_04",
                                                "=Object.«Высота»-1", "=Count" } },
                    { "filter", new object[] {
                        new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", "RF-заполнения" } },
                        new Dictionary<string, object> { { "field", "ШТ_СТЫК" }, { "op", "=" }, { "value", "0" } } } } },
                new Dictionary<string, object> {
                    { "section_title", "Гориз. с терморазрывом" },
                    { "header", new object[] { "Артикул", "Длина, мм" } },
                    { "columns", new object[] { "Артикул", "=Object.«ШТ_РАЗМЕР»" } },
                    { "filter", new object[] {
                        new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", "ШТАПИК-РАЗРЕЗ" } } } } },
                new Dictionary<string, object> {
                    { "section_title", "Верт. с терморазрывом" },
                    { "header", new object[] { "Артикул", "Длина, мм" } },
                    { "columns", new object[] { "Артикул", "=Object.«ШТ_РАЗМЕР»" } },
                    { "filter", new object[] {
                        new Dictionary<string, object> { { "field", "Слой" }, { "op", "=" }, { "value", "ШТАПИК-РАЗРЕЗ" } } } } } } }
        };
        var t5 = take(defLit, null);
        var sd5 = (IList)t5[0];
        var nl5 = (List<string>)t5[2];
        Check(sd5.Count == 4, "литерал: 4 секции -> 4 отчёта", "n=" + sd5.Count);
        if (sd5.Count == 4)
        {
            Func<object, List<string[]>> rowsOf = o => (List<string[]>)o.GetType().GetField("FullRows").GetValue(o);
            Func<object, bool> hidOf = o => (bool)o.GetType().GetField("HideHeader").GetValue(o);
            Check(rowsOf(sd5[0])[0][0] == "17_01_04 8 6000" && !hidOf(sd5[0]),
                  "литерал: артикул из столбца в шапке первого, шапка видима", rowsOf(sd5[0])[0][0]);
            Check(rowsOf(sd5[1])[0][0] == "17_01_04 8 6000" && hidOf(sd5[1]),
                  "слияние: у второго тот же артикул, шапка столбцов СКРЫТА");
            Check(rowsOf(sd5[1])[0][1] == "=Object.«Высота»-1",
                  "слияние: длина второго — СВОЁ выражение (верт)", rowsOf(sd5[1])[0][1]);
            Check(rowsOf(sd5[0])[1][1] == "=Count*2",
                  "количество раскроя = выражение ИСТОЧНИКА (множитель не теряется)",
                  rowsOf(sd5[0])[1][1]);
            Check(!hidOf(sd5[2]) && !hidOf(sd5[3]),
                  "плейсхолдеры «Артикул» НЕ слиты (обе шапки видимы)");
            Check(nl5.Exists(z => z.Contains("взят из столбца")) &&
                  nl5.Exists(z => z.Contains("объединены под одной шапкой")),
                  "литерал/слияние: заметки на месте");
        }

        // e2e через живую форму: TakeFromTable -> карточки; авто-фильтры скрыты из грида,
        // видны в сводке; def несёт auto; реверс возвращает их мимо грида
        var serE = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        form.GetType().GetMethod("TakeFromTable", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(form, new object[] { cards[0], serE.Serialize(defLit) });
        Pump(300);
        Check(cards.Count == 4, "e2e Take: карточки заменены (4)", "n=" + cards.Count);
        var gE = (DataGridView)F(cards[0], "grid");
        bool gridFlt = false;
        foreach (DataGridViewRow rr in gE.Rows)
        {
            if (rr.IsNewRow) continue;
            string exx = Convert.ToString(rr.Cells["expr"].Value) ?? "";
            if (exx.Contains("МАРКИРОВКА")) gridFlt = true;
        }
        var afE = (List<Dictionary<string, object>>)F(cards[0], "_autoFilters");
        Check(!gridFlt && afE != null && afE.Count == 1,
              "e2e: фильтр источника не в гриде, в _autoFilters", "grid=" + gridFlt +
              " af=" + (afE == null ? "null" : afE.Count.ToString()));
        var lblS = (Label)F(cards[0], "lblSummary");
        Check(lblS != null && (lblS.Text ?? "").Contains("МАРКИРОВКА"),
              "e2e: сводка показывает скрытый фильтр", lblS == null ? "null" : lblS.Text);
        var chkHH = (CheckBox)F(cards[1], "chkHideHeader");
        Check(chkHH != null && chkHH.Checked, "e2e: у второй карточки «Скрыть шапку столбцов» взведена");
        var dE = (Dictionary<string, object>)cards[0].GetType().GetMethod("ToDef").Invoke(cards[0], null);
        bool autoInDef = false;
        foreach (Dictionary<string, object> ff in (IList)dE["filter"])
            if (ff.ContainsKey("auto") && Convert.ToBoolean(ff["auto"])) autoInDef = true;
        Check(autoInDef, "e2e: ToDef несёт авто-фильтр с флагом auto");
        // контракт SeedFromSection — десериализованный def (object[]), как в CopyCard/ATSPECEDIT
        var dE2 = serE.DeserializeObject(serE.Serialize(dE)) as Dictionary<string, object>;
        var seedE = seedM.Invoke(null, new object[] { dE2 });
        var afE2 = (List<Dictionary<string, object>>)seedE.GetType().GetField("AutoFilters").GetValue(seedE);
        var rowsE2 = (List<string[]>)seedE.GetType().GetField("FullRows").GetValue(seedE);
        bool rowFlt2 = false;
        foreach (var rr in rowsE2) if ((rr[1] ?? "").Contains("МАРКИРОВКА")) rowFlt2 = true;
        Check(afE2 != null && afE2.Count == 1 && !rowFlt2,
              "e2e: round-trip def -> AutoFilters вернулись мимо грида");

        // регресс 10.07 (пойман e2e на живом пресете): гард ШТ_СТЫК считает грид+авто,
        // BuildDef после «Взять с табл.» не рубится «самопроверкой» и отдаёт РАСКРОЙ
        form.GetType().GetMethod("BuildDef", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(form, null);
        Check(!ReproMsg.Log.Any(m => m.Contains("не попал в определение")),
              "гард ШТ_СТЫК: авто-фильтры не считаются «потерей»");
        var rdE = form.ReportDef;
        var rsecs = (rdE != null && rdE.ContainsKey("sections")) ? rdE["sections"] as IList : null;
        bool cutOk = false, hid2 = false, autoDef = false;
        if (rsecs != null && rsecs.Count == 4)
        {
            var s0 = rsecs[0] as Dictionary<string, object>;
            var s1 = rsecs[1] as Dictionary<string, object>;
            var h0 = (s0 != null && s0.ContainsKey("header")) ? s0["header"] as IList : null;
            cutOk = h0 != null && Convert.ToString(h0[0]) == "17_01_04 8 6000";
            hid2 = s1 != null && Convert.ToBoolean(s1["hide_header"]);
            var fl1 = (s1 != null && s1.ContainsKey("filter")) ? s1["filter"] as IList : null;
            if (fl1 != null)
                foreach (Dictionary<string, object> ff in fl1)
                    if (ff.ContainsKey("auto") && Convert.ToBoolean(ff["auto"])) autoDef = true;
        }
        Check(cutOk, "BuildDef после Take: шапка раскроя «17_01_04 8 6000»",
              rsecs == null ? "secs=null" : "n=" + rsecs.Count);
        Check(hid2 && autoDef, "BuildDef после Take: продолжение с hide_header, авто-фильтр в def",
              "hid=" + hid2 + " auto=" + autoDef);

        // ═══ 08.07-4: «Взять с табл.» — только в «Раскрое»; резина карточек ═══
        var takeVis = new Func<object, bool>(c =>
        {
            var b = (Button)F(c, "_btnTake");
            return b != null && b.Visible;
        });
        Check(!takeVis(cards[0]), "Take: скрыта в текущем шаблоне (Спецификация)");
        cbTitle.SelectedIndex = cbTitle.Items.IndexOf("Раскрой");
        Pump(400);
        Check(cards.Count >= 1 && takeVis(cards[0]), "Take: видима в «Раскрое»");
        int w0 = ((Control)cards[0]).Width;
        form.Width += 240; Pump(200);
        int w1 = ((Control)cards[0]).Width;
        Check(w1 >= w0 + 200, "резина: карточка тянется за формой", w0 + " -> " + w1);

        // раскладка грида: смоук Save/Apply через реестр
        var g0x = (DataGridView)F(cards[0], "grid");
        g0x.Columns["val"].Width = 234;
        using (var rk = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\ATableSpec"))
            cards[0].GetType().GetMethod("SaveGridLayout").Invoke(cards[0], new object[] { rk });
        int saved = 0;
        using (var rk = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\ATableSpec"))
            saved = Convert.ToInt32(rk.GetValue("ColW_val", 0));
        Check(saved == 234, "раскладка: ширина колонки ушла в реестр", "ColW_val=" + saved);

        string dump = Path.Combine(Path.GetTempPath(), "ATableSpec_last_def.json");
        Check(File.Exists(dump), "дамп def в TEMP записан", dump);

        Console.WriteLine(fails == 0 ? "\n=== ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ ===" : "\n=== ПРОВАЛОВ: " + fails + " ===");
        Environment.Exit(fails == 0 ? 0 : 1);
    }
}
