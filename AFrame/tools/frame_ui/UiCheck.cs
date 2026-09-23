// Прогон окна ATFRAME без AutoCAD (mono + xvfb): логика FrameSettings
// (умолчания = прежние ответы по Enter в командной строке), круг
// ToDict/FromDict, пересечения контролов, ОК/отказ, снимки окна.
// Сборка (из AFrame):
//   mcs -out:frameui.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Web.Extensions.dll \
//       tools/frame_ui/UiCheck.cs src/AFramePlugin/FrameForm.cs src/AFramePlugin/FrameSettings.cs
//   xvfb-run -a mono frameui.exe /tmp/frame_ui
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using AFramePlugin;

static class FrameUiCheck
{
    static int fails = 0;
    static void Ok(bool c, string m) { if (!c) { fails++; Console.WriteLine("FAIL: " + m); } }

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "/tmp/frame_ui";
        Directory.CreateDirectory(outDir);
        var ser = new JavaScriptSerializer();
        int boxes = 0;
        var closer = new Timer { Interval = 150 };
        closer.Tick += delegate {
            foreach (Form of in new List<Form>(Application.OpenForms.Cast<Form>()))
                if (!(of is FrameForm)) { boxes++; of.Close(); }
        };
        closer.Start();

        // 1. умолчания = прежние ответы по Enter
        var d = new FrameSettings();
        Ok(d.Mode == "all" && d.SubType == "vertical" && !d.Manual, "умолчания: подсистема+кляммеры, вертикальная, расчёт");
        Ok((string)d.SysOverride()["name"] == "Вектор-1", "расчёт вертикальной — пресет Вектор-1, как раньше");
        var cd = d.CalcDict();
        Ok((string)cd["wind_region"] == "II" && (string)cd["terrain"] == "B" && (double)cd["height"] == 30 &&
           (double)cd["q_clad"] == 25 && (double)cd["offset"] == 230 && (double)cd["na_max"] == 3000,
           "расчёт: II/B/30/25/230/3000 — прежние дефолты");
        Ok(d.RailProfileOrNull == null && d.NspTypeOrNull == null, "профиль «Авто» → null (подбор)");
        var m = new FrameSettings { Steps = "manual" };
        var so = m.SysOverride();
        Ok((string)so["name"] == "Standart" && (double)so["bracket_step"] == 800 && (double)so["bracket_step_corner"] == 800 &&
           !so.ContainsKey("rail_gap") && m.CalcDict() == null, "вручную: 800/800, «прочее» не шлём без правки");
        m.RailGap = 12;
        Ok(m.MiscChanged && (double)m.SysOverride()["rail_gap"] == 12, "вручную: правленый зазор уходит в систему");
        var i = new FrameSettings(); i.SetSubType("interfloor");
        Ok(i.QClad == 8 && i.Profile == "НСП-1" && i.NspTypeOrNull == "НСП-1" && i.SysName == "Межэтажная",
           "межэтажная: вес 8, НСП-1 (как раньше)");
        i.SetSubType("vertical");
        Ok(i.QClad == 25 && i.Profile == "Авто", "назад в вертикальную: вес 25, профиль «Авто»");
        var o = new FrameSettings(); o.SetSubType("ortho");
        Ok(o.SysName == "Ортогональная" && o.RailProfileOrNull == null, "ортогональная: профили автоматом");
        var v = new FrameSettings { Profile = "ШП-60-20" };
        Ok(v.RailProfileOrNull == "ШП-60-20", "явный профиль вертикальной");
        // 2. проверки ввода
        Ok(new FrameSettings { Height = 0 }.Validate(true) != null, "высота 0 — ошибка");
        Ok(new FrameSettings { Mode = "clamps", Height = 0 }.Validate(true) == null, "только кляммеры — расчёт не нужен");
        Ok(new FrameSettings { RowStep = 20 }.Validate(false) != null, "шаг швов 20 без раскладки — ошибка");
        Ok(new FrameSettings { RowStep = 20 }.Validate(true) == null, "шаг швов не нужен, если раскладка есть");
        var ib = new FrameSettings { AskFloors = false, FloorStep = 0 }; ib.SetSubType("interfloor");
        Ok(ib.Validate(true) != null, "межэтажная без отметок и без высоты этажа — ошибка");
        // 3. круг ToDict/FromDict
        var full = new FrameSettings { Mode = "clamps", Steps = "manual", WindRegion = "IV", Terrain = "A", Height = 57,
                                       StepMain = 600, RailStepCorner = 400, Axes = "points", RowStep = 0,
                                       AskCorners = false, Signs = "samples" };
        full.SetSubType("ortho");
        var r = FrameSettings.FromDict(ser.DeserializeObject(ser.Serialize(full.ToDict())) as Dictionary<string, object>);
        Ok(ser.Serialize(r.ToDict()) == ser.Serialize(full.ToDict()), "ToDict/FromDict — без потерь");
        Ok(FrameSettings.FromDict(new Dictionary<string, object> { { "mode", "мусор" }, { "profile", "ГП-99" } }).Mode == "all",
           "мусор в метке — умолчания");
        Ok(d.Describe(true).Contains("кляммеры") && full.Describe(false).Contains("Только кляммеры"), "описание");

        // 4. окно: снимки, пересечения, ОК
        var shots = new List<Tuple<string, FrameSettings, bool>>();
        shots.Add(Tuple.Create("frame_default", new FrameSettings(), true));
        var sm = new FrameSettings { Steps = "manual" };
        shots.Add(Tuple.Create("frame_manual", sm, false));
        var sc = new FrameSettings { Mode = "clamps" };
        shots.Add(Tuple.Create("frame_clamps", sc, true));
        var si = new FrameSettings(); si.SetSubType("interfloor");
        shots.Add(Tuple.Create("frame_interfloor", si, true));
        // ошибка, которую поля окна не «исправят» ограничением диапазона:
        // межэтажная без отметок и без высоты этажа
        var sbad = new FrameSettings { AskFloors = false, FloorStep = 0 };
        sbad.SetSubType("interfloor");
        shots.Add(Tuple.Create("frame_error", sbad, true));
        foreach (var kv in shots)
        {
            using (var f = new FrameForm(kv.Item2, kv.Item3))
            {
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0);
                f.Show(); Application.DoEvents();
                for (int t = 0; t < 20; t++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    using (var gr = Graphics.FromImage(bmp))
                        gr.CopyFromScreen(f.Location, Point.Empty, f.Size);
                    bmp.Save(Path.Combine(outDir, kv.Item1 + ".png"));
                }
                foreach (Control grp in f.Controls)
                {
                    var ch = new List<Control>(); foreach (Control c in grp.Controls) if (c.Visible) ch.Add(c);
                    for (int a = 0; a < ch.Count; a++)
                        for (int b = a + 1; b < ch.Count; b++)
                            if (ch[a].Bounds.IntersectsWith(ch[b].Bounds))
                                Ok(false, kv.Item1 + ": пересекаются «" + ch[a].Text + "» и «" + ch[b].Text + "» " +
                                   ch[a].Bounds + " / " + ch[b].Bounds);
                    if (grp is GroupBox)
                        foreach (Control c in grp.Controls)
                            if (c.Visible)
                                Ok(c.Right <= grp.Width - 2 && c.Bottom <= grp.Height,
                                   kv.Item1 + ": «" + c.Text + "» вылезает из группы «" + grp.Text + "»");
                }
                bool valid = kv.Item2.Validate(kv.Item3) == null;
                ((Button)f.AcceptButton).PerformClick();
                Application.DoEvents();
                Ok((f.DialogResult == DialogResult.OK) == valid,
                   kv.Item1 + ": ОК " + (valid ? "должно пройти" : "должно отказать") + " (" + f.DialogResult + ")");
                if (valid)
                    Ok(ser.Serialize(f.Result.ToDict()) == ser.Serialize(kv.Item2.ToDict()),
                       kv.Item1 + ": окно не должно менять параметры без действий пользователя");
                f.Close();
            }
        }
        Ok(boxes >= 1, "на неверном вводе ОК показывает сообщение (" + boxes + ")");
        Console.WriteLine(fails == 0 ? "UI: OK" : "UI: провалов " + fails);
        return fails == 0 ? 0 : 1;
    }
}
