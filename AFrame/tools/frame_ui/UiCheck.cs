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

        // 3б. облицовка (26.09 Денис; 29.09c Герман): плитка — направляющие заданным
        //     шагом, шины трёх видов, без кляммеров; тип и шаги кронштейнов — как у всех
        Ok(d.Cladding == "porcelain" && !d.IsTile, "по умолчанию — керамогранит, прежний алгоритм");
        var tk = new FrameSettings { Cladding = "clinker", TileStepH = 550, TileStepHCorner = 400, TileWhip = 3000 };
        tk.RailBrand = "ШК-40";
        Ok(tk.IsTile && !tk.Manual && tk.CalcDict() != null && (string)tk.SysOverride()["name"] == "Вектор-1",
           "плитка: кронштейны по расчёту, как у керамогранита (пресет Вектор-1)");
        Ok(tk.RailBrandClinker == "ШК-40" && tk.RailBrandConcrete == "", "марка шины — у клинкерной своя");
        tk.Cladding = "concrete";
        Ok(tk.RailBrand == "" && (tk.RailBrand = "Б-2") == "Б-2" && tk.RailBrandConcrete == "Б-2" &&
           tk.RailBrandClinker == "ШК-40", "марка шины у бетонной — отдельно, клинкерная не тронута");
        tk.Cladding = "clinker";
        var tko = new FrameSettings { Cladding = "concrete", Mode = "clamps" };
        tko.SetSubType("ortho");
        Ok(!tko.ClampsOnly && tko.EffSubType == "ortho" && tko.Ortho && tko.SysName == "Ортогональная",
           "плитка: не «только кляммеры»; тип — выбранный (ортогональная)");
        var tkm = new FrameSettings { Cladding = "clinker", Steps = "manual", StepMain = 700, StepCorner = 500 };
        var tso = tkm.SysOverride();
        Ok(tkm.Manual && tkm.CalcDict() == null && (double)tso["bracket_step"] == 700 && (double)tso["bracket_step_corner"] == 500,
           "плитка вручную: шаги кронштейнов 700/500, как у всех");
        Ok(new FrameSettings { Cladding = "clinker", RowStep = 0 }.Validate(false) != null,
           "плитка без раскладки и без шага рядов — ошибка (шинам нужны ряды)");
        Ok(new FrameSettings { Cladding = "clinker", RowStep = 0 }.Validate(true) == null,
           "плитка с раскладкой — шаг рядов не нужен");
        Ok(new FrameSettings { Cladding = "clinker", TileStepH = 50 }.Validate(true) != null, "шаг направляющих 50 — ошибка");
        Ok(new FrameSettings { Cladding = "clinker", TileStepHCorner = 50 }.Validate(true) != null, "угловой шаг 50 — ошибка");
        Ok(new FrameSettings { Cladding = "clinker", TileWhip = 100 }.Validate(true) != null, "хлыст 100 — ошибка");
        Ok(new FrameSettings { Cladding = "clinker", Height = 0 }.Validate(true) != null,
           "плитка по расчёту — высота здания нужна (как у всех)");
        Ok(new FrameSettings { Cladding = "clinker", AxisStep = 10, RowStep = 80 }.Validate(false) == null,
           "плитка без раскладки: шаг осей по швам не проверяется (осей по швам нет)");
        Ok(tk.Describe(true).Contains("Шины") && tk.Describe(true).Contains("550") && tk.Describe(true).Contains("ШК-40") &&
           tk.Describe(true).Contains("3000") && tk.TypeTitle == "Вертикальная под клинкерную плитку",
           "плитка: описание (шины, шаг, марка, хлыст) и заголовок «" + tk.TypeTitle + "»");
        var tr2 = FrameSettings.FromDict(ser.DeserializeObject(ser.Serialize(tk.ToDict())) as Dictionary<string, object>);
        Ok(tr2.Cladding == "clinker" && tr2.TileStepH == 550 && tr2.TileStepHCorner == 400 && tr2.TileWhip == 3000 &&
           tr2.RailBrandClinker == "ШК-40" && tr2.RailBrandConcrete == "Б-2", "плитка: ToDict/FromDict");
        Ok(FrameSettings.FromDict(new Dictionary<string, object> { { "cladding", "дерево" } }).Cladding == "porcelain",
           "неизвестная облицовка в метке — керамогранит");

        // 3в. 29.09: запрос движку из окна — FrameSettings.EngineParams (команда и синтетика
        //     «в ролях» пользуются одним методом): прежняя раскладка полей сохранена
        var ep0 = new FrameSettings().EngineParams(0);
        Ok((string)ep0["sub_type"] == "vertical" && (string)((Dictionary<string, object>)ep0["system"])["name"] == "Вектор-1" &&
           ep0.ContainsKey("calc") && !ep0.ContainsKey("exact_step") && (double)ep0["floor_step"] == 0 &&
           (string)ep0["cladding"] == "porcelain" && !ep0.ContainsKey("parts") && !ep0.ContainsKey("tile_step_x"),
           "запрос по умолчанию: вертикальная, Вектор-1, расчёт, без ручного шага, керамогранит");
        var epm = new FrameSettings { Steps = "manual", RailStepCorner = 400, Mode = "frame" }.EngineParams(0);
        Ok(epm.ContainsKey("exact_step") && (double)epm["rail_step_corner"] == 400 && !epm.ContainsKey("calc") &&
           (string)epm["parts"] == "frame", "вручную: буквальный шаг, шаг стоек в угловой, без расчёта, только подсистема");
        var fi = new FrameSettings { FloorStep = 3300 }; fi.SetSubType("interfloor");
        Ok((double)fi.EngineParams(0)["floor_step"] == 3300 && (double)fi.EngineParams(2)["floor_step"] == 0 &&
           (string)fi.EngineParams(0)["nsp_type"] == "НСП-1", "межэтажная: шаг этажа только без отметок");
        var ft = new FrameSettings { Cladding = "clinker", TileStepH = 550, TileStepHCorner = 400, TileWhip = 3000, RowStep = 88 };
        ft.RailBrand = " ШК-40 "; ft.SetSubType("ortho");
        var ept = ft.EngineParams(0);
        Ok((string)ept["sub_type"] == "ortho" && (double)ept["tile_step_x"] == 550 && (double)ept["tile_step_x_corner"] == 400 &&
           (double)ept["tile_whip"] == 3000 && (string)ept["tile_rail_brand"] == "ШК-40" && (double)ept["tile_row_step"] == 88 &&
           (string)ept["parts"] == "frame" && ept.ContainsKey("calc"), "плитка: все поля плитки, тип, расчёт, без кляммеров");
        var epc = new FrameSettings { Mode = "clamps" }.EngineParams(0);
        Ok((string)epc["parts"] == "clamps" && !epc.ContainsKey("calc"), "только кляммеры: parts=clamps, расчёта нет");
        Ok(!new FrameSettings { Cladding = "concrete", Mode = "clamps" }.EngineParams(0)["parts"].Equals("clamps"),
           "плитка «только кляммеры» не шлёт (кляммеров у плитки нет)");

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
        var st1 = new FrameSettings { Cladding = "clinker", TileStepHCorner = 400 };
        st1.RailBrand = "ШК-40 (клинкер)";
        shots.Add(Tuple.Create("frame_tile", st1, true));
        var st2 = new FrameSettings { Cladding = "concrete", RowStep = 72, Steps = "manual" };
        st2.SetSubType("interfloor");
        shots.Add(Tuple.Create("frame_tile_nolayout", st2, false));
        var st3 = new FrameSettings { Cladding = "clinker" };
        st3.SetSubType("ortho");
        shots.Add(Tuple.Create("frame_tile_ortho", st3, true));
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
                            {
                                Ok(c.Right <= grp.Width - 2 && c.Bottom <= grp.Height,
                                   kv.Item1 + ": «" + c.Text + "» вылезает из группы «" + grp.Text + "»");
                                // 26.09: подпись не обрезана (переключатель/флажок — плюс кружок ~20)
                                int extra = (c is RadioButton || c is CheckBox) ? 20 : 0;
                                if ((c is RadioButton || c is CheckBox || c is Label) && c.Text.Length > 0 &&
                                    !(c is Label && ((Label)c).AutoSize == false && c.Height > 30))
                                    Ok(TextRenderer.MeasureText(c.Text, c.Font).Width + extra <= c.Width + 4,
                                       kv.Item1 + ": подпись «" + c.Text + "» не влезает (" +
                                       (TextRenderer.MeasureText(c.Text, c.Font).Width + extra) + " > " + c.Width + ")");
                            }
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
