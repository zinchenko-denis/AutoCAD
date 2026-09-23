// Прогон окна ATTILE без AutoCAD (mono + xvfb): логика BondSettings,
// сверка сдвигов с движком (дамп → attile_ui_check.py), снимки окна.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Linq;
using ACladPlugin;

static class UiCheck
{
    static int fails = 0;
    static void Ok(bool c, string m) { if (!c) { fails++; Console.WriteLine("FAIL: " + m); } }

    static BondSettings Mk(string kind, string value, string units, string dir, string axis, string seq)
    {
        var s = new BondSettings { Kind = kind, Value = value, Units = units, Dir = dir, Axis = axis };
        if (seq != null) s.Sequence = seq;
        return s;
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "/tmp/attile_ui";
        Directory.CreateDirectory(outDir);
        var ser = new JavaScriptSerializer();
        // модальные MessageBox (отказ ОК на неверном вводе) — закрываем сами
        int boxes = 0;
        var closer = new Timer { Interval = 150 };
        closer.Tick += delegate {
            foreach (Form of in new List<Form>(Application.OpenForms.Cast<Form>()))
                if (!(of is BondForm)) { boxes++; of.Close(); }
        };
        closer.Start();
        // 1. разбор чисел и форматов
        double v, w, h;
        Ok(BondSettings.TryParseNum("1/3", out v) && Math.Abs(v - 1.0 / 3) < 1e-12, "1/3");
        Ok(BondSettings.TryParseNum("0,25", out v) && v == 0.25, "0,25");
        Ok(BondSettings.TryParseNum("200мм", out v) && v == 200, "200мм");
        Ok(!BondSettings.TryParseNum("1/0", out v), "1/0 отказ");
        Ok(BondSettings.TryParseFormat("600×1200", out w, out h) && w == 600 && h == 1200, "формат ×");
        Ok(BondSettings.TryParseFormat("290 х 82", out w, out h) && w == 290 && h == 82, "формат х кирилл.");
        // 2. проверки ввода
        var s0 = new BondSettings { Kind = "alternate", Value = "1.5", Units = "frac" };
        Ok(s0.Validate() != null, "доля ≥ 1 — ошибка");
        s0.Units = "mm"; Ok(s0.Validate() == null, "1.5 мм — допустимо");
        var s1 = new BondSettings { Element = "Кассета", Shaped = "keep" };
        Ok(s1.Validate() != null, "блок + фигурные — ошибка");
        Ok((string)s1.ToEngine()["shaped"] == "split_joint", "блок → split_joint в запросе");
        // 3. имена
        var sn = new BondSettings { Name = "Керамогранит 600×1200", W = 600, H = 1200 };
        Ok(sn.LayerFor("", false) == "Облицовка Керамогранит 600×1200", "слой: " + sn.LayerFor("", false));
        Ok(sn.BlockFor("", false) == "Керамогранит 600×1200", "блок без дубля формата: " + sn.BlockFor("", false));
        var sb = new BondSettings { Name = "Плитка", W = 290, H = 82 };
        Ok(sb.BlockFor("T1", true) == "Плитка T1 290x82", "блок с типом и форматом: " + sb.BlockFor("T1", true));
        Ok(sb.CutLayerFor("T1", true) == "Облицовка Плитка T1_ПОДРЕЗКА", "слой подрезки");
        // 4. круг ToDict/FromDict
        var r = BondSettings.FromDict(ser.DeserializeObject(ser.Serialize(sn.ToDict())) as Dictionary<string, object>);
        Ok(ser.Serialize(r.ToDict()) == ser.Serialize(sn.ToDict()), "ToDict/FromDict — без потерь");
        // 5. дамп сдвигов и запросов для сверки с движком
        var cases = new List<BondSettings> {
            Mk("none","","frac","+","rows",null), Mk("alternate","1/2","frac","+","rows",null),
            Mk("alternate","1/3","frac","-","rows",null), Mk("step","1/3","frac","+","rows",null),
            Mk("step","200","mm","-","rows",null), Mk("step","0.37","frac","+","cols",null),
            Mk("sequence","","frac","+","rows","0; 1/2; 1/4; 3/4"), Mk("sequence","","mm","-","cols","150; 400; 700"),
            Mk("alternate","3/4","frac","-","cols",null), Mk("step","1/4","frac","+","rows",null) };
        var dump = new List<object>();
        foreach (var c in cases)
        {
            var sh = new List<object>();
            for (int j = -6; j <= 12; j++) sh.Add(c.ShiftAt(j));
            dump.Add(new Dictionary<string, object> { { "engine", c.ToEngine() }, { "shifts", sh },
                                                     { "period", c.Period() }, { "text", c.Describe() } });
        }
        File.WriteAllText(Path.Combine(outDir, "shifts.json"), ser.Serialize(dump));
        // 6. окно: несколько состояний → снимки; ОК на валидных данных
        var layers = new[] { "0", "Облицовка Керамогранит 600×1200", "ФАСАД_ОКНА" };
        var blocks = new[] { "Кассета универсальная" };
        var shots = new List<KeyValuePair<string, BondSettings>>();
        shots.Add(new KeyValuePair<string, BondSettings>("form_default", new BondSettings()));
        var st = new BondSettings { Kind = "step", Value = "1/3", AnchorH = "L", AnchorV = "B" };
        shots.Add(new KeyValuePair<string, BondSettings>("form_step13", st));
        var sc = new BondSettings { Axis = "cols", Kind = "alternate", Value = "1/2", W = 1200, H = 3000,
                                    Material = "Фиброцемент / HPL на НВФ", Name = "Фиброцемент 1200×3000",
                                    AnchorH = "C", AnchorV = "B", Center = "joint" };
        shots.Add(new KeyValuePair<string, BondSettings>("form_cols", sc));
        var sbrick = new BondSettings();
        sbrick.ApplyPreset(BondSettings.FindPreset("Клинкер / кирпич / плитка"));
        sbrick.ColorsBySample = true; sbrick.Kind = "pattern";
        shots.Add(new KeyValuePair<string, BondSettings>("form_brick", sbrick));
        var sbad = new BondSettings { Kind = "sequence", Sequence = "0; x; 1/2" };
        shots.Add(new KeyValuePair<string, BondSettings>("form_error", sbad));
        foreach (var kv in shots)
        {
            using (var f = new BondForm(kv.Value, layers, blocks))
            {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0);
                f.Show(); Application.DoEvents();
                for (int t = 0; t < 20; t++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    using (var gr = Graphics.FromImage(bmp))
                        gr.CopyFromScreen(f.Location, Point.Empty, f.Size);
                    bmp.Save(Path.Combine(outDir, kv.Key + ".png"));
                }
                // пересечения контролов внутри групп (грабля ручной вёрстки)
                foreach (Control grp in f.Controls)
                {
                    var ch = new List<Control>(); foreach (Control c in grp.Controls) if (c.Visible) ch.Add(c);
                    for (int a = 0; a < ch.Count; a++)
                        for (int b = a + 1; b < ch.Count; b++)
                        {
                            var ra = ch[a].Bounds; var rb = ch[b].Bounds;
                            if (ra.IntersectsWith(rb) && !(ch[a] is GroupBox) && !(ch[b] is GroupBox))
                                Ok(false, kv.Key + ": пересекаются «" + ch[a].Text + "» и «" + ch[b].Text + "» " + ra + " / " + rb);
                        }
                    if (grp is GroupBox)
                        foreach (Control c in grp.Controls)
                            Ok(c.Right <= grp.Width - 2 && c.Bottom <= grp.Height, kv.Key + ": «" + c.Text + "» вылезает из группы «" + grp.Text + "»");
                }
                bool valid = kv.Value.Validate() == null;
                ((Button)f.AcceptButton).PerformClick();
                Application.DoEvents();
                Ok((f.DialogResult == DialogResult.OK) == valid, kv.Key + ": ОК " + (valid ? "должно пройти" : "должно отказать") + " (" + f.DialogResult + ")");
                if (valid) Ok(ser.Serialize(f.Result.ToDict()) == ser.Serialize(kv.Value.ToDict()) || kv.Key != "form_default",
                              kv.Key + ": окно не должно менять параметры без действий пользователя");
                f.Close();
            }
        }
        Ok(boxes >= 1, "на неверном вводе ОК показывает сообщение (" + boxes + ")");
        Console.WriteLine(fails == 0 ? "UI: OK" : "UI: провалов " + fails);
        return fails == 0 ? 0 : 1;
    }
}
