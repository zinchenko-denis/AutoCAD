// Прогон окна ATFZONE без AutoCAD (mono + xvfb): запоминание настроек окна между
// запусками и сеансами AutoCAD (Герман 29.09: «размер шрифта, штриховку, цвет
// штриховки — хотелось бы, чтобы они сохранялись»), отказ не сохраняет, снимок окна.
// Сборка (из Facades):
//   mcs -out:zoneui.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Web.Extensions.dll \
//       tools/zone_ui/UiCheck.cs src/AFacadesPlugin/ZoneForm.cs src/AFacadesPlugin/ZoneTable.cs \
//       src/AFacadesPlugin/LayerPickForm.cs
//   HOME=/tmp/zone_home xvfb-run -a mono zoneui.exe /tmp/zone_ui
// (HOME — чтобы не трогать настоящие настройки: под mono %APPDATA% = $HOME/.config)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AFacadesPlugin;

static class ZoneUiCheck
{
    static int fails = 0;
    static void Ok(bool c, string m) { if (!c) { fails++; Console.WriteLine("FAIL: " + m); } }
    const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    static T F<T>(ZoneForm f, string name) where T : class
    {
        return typeof(ZoneForm).GetField(name, NP).GetValue(f) as T;
    }

    static void NewSession()
    {
        // новый сеанс AutoCAD: история наименований в памяти пуста
        var h = typeof(ZoneForm).GetField("History", BindingFlags.NonPublic | BindingFlags.Static)
                                .GetValue(null) as List<string>;
        h.Clear();
    }

    static void CloseWith(ZoneForm f, DialogResult r)
    {
        f.DialogResult = r;
        f.Close();
        Application.DoEvents();
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "/tmp/zone_ui";
        Directory.CreateDirectory(outDir);
        string sp = ZoneForm.SettingsPath();
        if (File.Exists(sp)) File.Delete(sp);

        // 1. первый запуск — прежние умолчания
        NewSession();
        using (var f = new ZoneForm(3, new[] { "Фасад_А" }))
        {
            f.Show(); Application.DoEvents();
            Ok(f.TextHeight == 250 && f.Pattern == "ANSI31" && f.PatternScale == 25 && f.ColorAci == 8 &&
               f.MakeTable && f.WriteJson && !f.MergeZones && !f.MakeDims && f.Prefix == "Ф-",
               "первый запуск: умолчания 250 / ANSI31 / 25 / 8, таблица и JSON включены");
            // пользователь меняет настройки и жмёт OK
            F<ComboBox>(f, "_cladding").Text = "клинкер 290х82";
            F<TextBox>(f, "_textH").Text = "400";
            F<ComboBox>(f, "_pattern").Text = "AR-B816";
            F<TextBox>(f, "_scale").Text = "10";
            F<TextBox>(f, "_color").Text = "3";
            F<TextBox>(f, "_prefix").Text = "К-";
            F<CheckBox>(f, "_dims").Checked = true;
            F<CheckBox>(f, "_table").Checked = false;
            CloseWith(f, DialogResult.OK);
        }
        Ok(File.Exists(sp), "после OK настройки записаны: " + sp);

        // 2. новый сеанс AutoCAD — окно открывается с прошлыми настройками
        NewSession();
        using (var f = new ZoneForm(1, new[] { "Фасад_А" }))
        {
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0);
            f.Show();
            for (int t = 0; t < 20; t++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            Ok(f.TextHeight == 400 && f.Pattern == "AR-B816" && f.PatternScale == 10 && f.ColorAci == 3,
               "новый сеанс: высота 400, AR-B816, масштаб 10, цвет 3 (" + f.TextHeight + "/" + f.Pattern + "/" +
               f.PatternScale + "/" + f.ColorAci + ")");
            Ok(f.MakeDims && !f.MakeTable && f.WriteJson && !f.MergeZones && f.Prefix == "К-",
               "новый сеанс: флажки и префикс — прошлые");
            Ok(f.Cladding == "клинкер 290х82" && F<ComboBox>(f, "_cladding").Items.Contains("клинкер 290х82") &&
               F<ComboBox>(f, "_cladding").Items.Contains("Фасад_А"),
               "новый сеанс: последнее наименование сверху, слои чертежа — в списке");
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                using (var gr = Graphics.FromImage(bmp))
                    gr.CopyFromScreen(f.Location, Point.Empty, f.Size);
                bmp.Save(Path.Combine(outDir, "zone_saved.png"));
            }
            // 3. «Отмена» не сохраняет
            F<TextBox>(f, "_textH").Text = "999";
            CloseWith(f, DialogResult.Cancel);
        }
        NewSession();
        using (var f = new ZoneForm(1))
        {
            f.Show(); Application.DoEvents();
            Ok(f.TextHeight == 400, "«Отмена» не пишет настройки (" + f.TextHeight + ")");
            CloseWith(f, DialogResult.Cancel);
        }
        // 4. битый файл настроек — прежние умолчания, без падения
        File.WriteAllText(sp, "{ мусор");
        NewSession();
        using (var f = new ZoneForm(1))
        {
            f.Show(); Application.DoEvents();
            Ok(f.TextHeight == 250 && f.Pattern == "ANSI31", "битый файл — умолчания");
            CloseWith(f, DialogResult.Cancel);
        }
        // 5. флажок «после OK указать витражи, двери и парапет» — по умолчанию включён, запоминается
        if (File.Exists(sp)) File.Delete(sp);
        NewSession();
        using (var f = new ZoneForm(2))
        {
            f.Show(); Application.DoEvents();
            Ok(f.AskKinds, "по умолчанию вопросы про витражи/двери/парапет включены");
            F<CheckBox>(f, "_kinds").Checked = false;
            CloseWith(f, DialogResult.OK);
        }
        NewSession();
        using (var f = new ZoneForm(2))
        {
            f.Show(); Application.DoEvents();
            Ok(!f.AskKinds, "флажок вопросов запомнился выключенным");
            CloseWith(f, DialogResult.Cancel);
        }

        // 6. ведомость (ZoneTable — общая для ATFZONE, ATFTABLE и Excel)
        var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
        Func<string, Dictionary<string, object>> J = t => ser.DeserializeObject(t) as Dictionary<string, object>;
        var zNew = J("{\"zone_id\":\"Ф-1\",\"cladding\":\"клинкер\",\"report\":{\"area_outer_m2\":60,\"area_net_m2\":41.75," +
                     "\"openings_count\":5,\"openings_total_m2\":18.25,\"window_count\":2,\"window_area_m2\":3.05," +
                     "\"vitrage_count\":1,\"vitrage_area_m2\":10.5,\"door_count\":2,\"door_area_m2\":4.7," +
                     "\"window_sills_m\":2.3,\"window_slopes_m\":6.3,\"door_slopes_m\":11.4,\"vitrage_side_m\":7," +
                     "\"vitrage_top_m\":3,\"vitrage_bottom_m\":3,\"sills_total_m\":2.3,\"jambs_total_m\":17.7}}");
        var zOld = J("{\"zone_id\":\"Ф-2\",\"cladding\":\"керамогранит\",\"report\":{\"area_outer_m2\":20," +
                     "\"area_net_m2\":18,\"openings_count\":2,\"openings_total_m2\":2,\"sills_total_m\":2," +
                     "\"jambs_total_m\":6}}");
        var rowsT = ZoneTable.ZoneRows(new List<Dictionary<string, object>> { zNew, zOld },
                                       z => (string)z["zone_id"], z => (string)z["cladding"]);
        Ok(rowsT.Count == 3 && rowsT[0].Length == ZoneTable.Cols && ZoneTable.Group.Length == ZoneTable.Cols &&
           ZoneTable.Sub.Length == ZoneTable.Cols, "ведомость: 2 зоны + итого, 16 колонок");
        Ok(rowsT[0][3] == "2" && rowsT[0][5] == "1" && rowsT[0][7] == "2" && rowsT[0][12] == "11.400" &&
           rowsT[0][13] == "7.000", "новая зона: окна 2, витражи 1, двери 2, откосы дверей 11.4, витражи бок 7");
        Ok(rowsT[1][3] == "2" && rowsT[1][4] == "2.000" && rowsT[1][10] == "2.000" && rowsT[1][11] == "6.000" &&
           rowsT[1][5] == "0", "зона старой сборки: проёмы — окна (штуки, площадь, отливы, откосы)");
        Ok(rowsT[2][0] == "ИТОГО" && rowsT[2][2] == "80.000" && rowsT[2][9] == "59.750" && rowsT[2][3] == "4",
           "итого: S участков 80, нетто 59.75, окон 4");
        // 29.09k (Герман, 9з–9и): парапет — одной строкой «Парапет», только м.п. по верху; метки
        // старой сборки (с марками и площадью) читаются, но марок и площади в таблице нет
        var prowsT = ZoneTable.ParapetRows(new List<Dictionary<string, object>>
            { J("{\"mark\":\"П-1\",\"area_m2\":6,\"top_m\":10,\"width_m\":10}"),
              J("{\"top_m\":3}") });
        Ok(prowsT.Count == 1 && prowsT[0].Length == ZoneTable.ParapetHead.Length && prowsT[0][0] == "Парапет" &&
           prowsT[0][1] == "13.000", "парапет: одна строка «Парапет», по верху 13 м.п.");
        Ok(ZoneTable.ParapetHead.Length == 2 && Array.IndexOf(ZoneTable.ParapetHead, "S, м²") < 0,
           "шапка парапета без площади и ширины");
        Ok(ZoneTable.ParapetRows(null).Count == 0, "нет парапетов — нет блока");
        foreach (var m in ZoneTable.HeaderMerges)
            Ok(m[3] < ZoneTable.Cols && m[2] <= 1, "слияние шапки в пределах двух строк и 16 колонок");

        // 7. 29.09n (Герман): ATFTABLE — выбор слоёв со штриховками зон
        var lay = new Dictionary<string, int> { { "НВФ утеплитель 150 мм", 3 }, { "Нвф утеплитель– 100 мм", 2 } };
        using (var lf = new LayerPickForm(lay, null, "вентилируемый фасад"))
        {
            lf.Show(); Application.DoEvents();
            Ok(!lf.OkEnabled && lf.Selected.Count == 0, "слои: ничего не отмечено — OK недоступна");
            lf.CheckLayer("НВФ утеплитель 150 мм", true); lf.CheckLayer("Нвф утеплитель– 100 мм", true);
            Application.DoEvents();
            Ok(lf.OkEnabled && lf.Selected.Count == 2, "слои: два отмечены — OK доступна, выбрано 2");
            lf.Close();
        }
        using (var lf1 = new LayerPickForm(new Dictionary<string, int> { { "Штукатурка", 5 } }, null, ""))
        {
            lf1.Show(); Application.DoEvents();
            Ok(lf1.OkEnabled && lf1.Selected.Count == 1, "слои: единственный слой отмечен сразу");
            lf1.Close();
        }

        Console.WriteLine(fails == 0 ? "ZONE UI: OK" : "ZONE UI: провалов " + fails);
        return fails == 0 ? 0 : 1;
    }
}
