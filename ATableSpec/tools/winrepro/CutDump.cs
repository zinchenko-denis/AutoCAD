// e2e-драйвер 10.07: путь Алексея целиком в живой форме (headless):
//  шаблон «Штапики» -> артикул-литерал 17_01_04 во все секции -> BuildDef (spec)
//  -> шаблон «Раскрой» -> TakeFromTable(spec) -> BuildDef (cut).
// Оба def уходят в /tmp/atspec_{spec,cut}_def.json — их движок гоняет на живом DXF.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AtSpecPlugin;

static class CutDump
{
    static object F(object o, string n)
    {
        var f = o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return f == null ? null : f.GetValue(o);
    }
    static void Pump(int ms)
    {
        var u = DateTime.Now.AddMilliseconds(ms);
        while (DateTime.Now < u) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
    }
    [STAThread]
    static void Main()
    {
        var layers = new List<string> { "0", "RF-доборники", "RF-заполнения", "RF-кронштейны",
                                        "RF-ригеля", "RF-створки", "RF-стойки", "_АР_ВИТРАЖ_БЛОКИ" };
        var fields = new List<string> { "ИМЯ", "МАРКИРОВКА", "ПРОФ", "Длина", "Ширина", "Высота" };
        var vals = new Dictionary<string, Dictionary<string, List<string>>>();
        var form = new ReportBuilderForm(layers, fields, vals, new List<string>());
        form.Show(); Pump(300);

        var cbTitle = (ComboBox)F(form, "cbTitle");
        var cards = (List<SectionCard>)F(form, "_cards");
        var ser = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        var buildDef = form.GetType().GetMethod("BuildDef", BindingFlags.Instance | BindingFlags.NonPublic);

        // (10.07-2) геометрия: стойки с зазором 10, заполнение поперёк зазора = стык ЕСТЬ
        //  (пресет сеет все 4 секции — как на эталоне Проба_штапики_2)
        var boxJoint = new Dictionary<string, List<double[]>> {
            { "RF-стойки", new List<double[]> {
                new double[] { 0.0, 0.0, 60.0, 2000.0 }, new double[] { 0.0, 2010.0, 60.0, 4000.0 } } },
            { "RF-заполнения", new List<double[]> { new double[] { 60.0, 1900.0, 1060.0, 2110.0 } } } };
        // заполнение целиком ниже зазора = стыка НЕТ (кейс Алексея «терморазрыва нет»)
        var boxNo = new Dictionary<string, List<double[]>> {
            { "RF-стойки", new List<double[]> {
                new double[] { 0.0, 0.0, 60.0, 2000.0 }, new double[] { 0.0, 2010.0, 60.0, 4000.0 } } },
            { "RF-заполнения", new List<double[]> { new double[] { 60.0, 100.0, 1060.0, 1900.0 } } } };
        form.BoxesByLayer = boxJoint;

        // 1) пресет «Штапики» (авто-Yes на подтверждение пересева)
        cbTitle.SelectedIndex = cbTitle.Items.IndexOf("Штапики");
        Pump(500);
        Console.WriteLine("stage1 cards=" + cards.Count);

        // 2) артикул-литерал 17_01_04 вместо плейсхолдера «Артикул» — во всех секциях (как Алексей)
        int fixedArts = 0;
        foreach (var c in cards)
        {
            var g = (DataGridView)F(c, "grid");
            foreach (DataGridViewRow r in g.Rows)
            {
                if (r.IsNewRow) continue;
                if (Convert.ToString(r.Cells["expr"].Value) == "Артикул")
                { r.Cells["expr"].Value = "17_01_04"; fixedArts++; }
            }
        }
        Console.WriteLine("arts=" + fixedArts);

        // 3) def спецификации штапиков
        buildDef.Invoke(form, null);
        string specJson = ser.Serialize(form.ReportDef);
        File.WriteAllText("/tmp/atspec_spec_def.json", specJson);

        // 4) «Раскрой» (пересев, 1 карточка) -> «Взять с табл.» из спецификации
        cbTitle.SelectedIndex = cbTitle.Items.IndexOf("Раскрой");
        Pump(500);
        Console.WriteLine("stage4 cards=" + cards.Count);
        form.GetType().GetMethod("TakeFromTable", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(form, new object[] { cards[0], specJson });
        Pump(400);
        Console.WriteLine("stage5 cards=" + cards.Count);

        // 5) def раскроя
        buildDef.Invoke(form, null);
        File.WriteAllText("/tmp/atspec_cut_def.json", ser.Serialize(form.ReportDef));

        // ═══ (10.07-2) сценарий Алексея «терморазрыва нет» — вторая пара def'ов ═══
        // 6) пресет «Штапики» при геометрии БЕЗ стыков: секции 3–4 не сеются
        form.BoxesByLayer = boxNo;
        cbTitle.SelectedIndex = cbTitle.Items.IndexOf("Штапики");
        Pump(500);
        Console.WriteLine("stage6 cards=" + cards.Count);
        int arts2 = 0;
        foreach (var c in cards)
        {
            var g = (DataGridView)F(c, "grid");
            foreach (DataGridViewRow r in g.Rows)
            {
                if (r.IsNewRow) continue;
                if (Convert.ToString(r.Cells["expr"].Value) == "Артикул")
                { r.Cells["expr"].Value = "17_01_04"; arts2++; }
            }
        }
        buildDef.Invoke(form, null);
        File.WriteAllText("/tmp/atspec_spec_nobrk_def.json", ser.Serialize(form.ReportDef));

        // 7) раскрой «Взять с табл.» из ПОЛНОЙ спецификации (4 секции) при том же
        //    отсутствии стыков: разрезные секции должны быть пропущены
        cbTitle.SelectedIndex = cbTitle.Items.IndexOf("Раскрой");
        Pump(500);
        form.GetType().GetMethod("TakeFromTable", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(form, new object[] { cards[0], specJson });
        Pump(400);
        Console.WriteLine("stage7 cards=" + cards.Count);
        buildDef.Invoke(form, null);
        File.WriteAllText("/tmp/atspec_cut_nobrk_def.json", ser.Serialize(form.ReportDef));
        Console.WriteLine("DUMPED spec+cut+nobrk");
        Environment.Exit(0);
    }
}
