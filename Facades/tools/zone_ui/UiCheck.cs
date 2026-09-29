// Прогон окна ATFZONE без AutoCAD (mono + xvfb): запоминание настроек окна между
// запусками и сеансами AutoCAD (Герман 29.09: «размер шрифта, штриховку, цвет
// штриховки — хотелось бы, чтобы они сохранялись»), отказ не сохраняет, снимок окна.
// Сборка (из Facades):
//   mcs -out:zoneui.exe -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Web.Extensions.dll \
//       tools/zone_ui/UiCheck.cs src/AFacadesPlugin/ZoneForm.cs
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
        Console.WriteLine(fails == 0 ? "ZONE UI: OK" : "ZONE UI: провалов " + fails);
        return fails == 0 ? 0 : 1;
    }
}
