// Окно хода ATTILE (TileProgress.cs) без AutoCAD — mono + xvfb:
// тексты счёта/времени/остатка, вёрстка (ничего не пересекается и не вылезает),
// отмена кнопкой и Esc, снимки окна в трёх состояниях.
//   cd AClad && mcs -r:System.Windows.Forms.dll -r:System.Drawing.dll \
//     -out:/tmp/progress_check.exe tools/attile_ui/ProgressCheck.cs src/ACladPlugin/TileProgress.cs
//   xvfb-run -a mono /tmp/progress_check.exe /tmp/attile_ui
using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ACladPlugin;

static class ProgressCheck
{
    static int fails = 0;
    static void Ok(bool c, string m) { if (!c) { fails++; Console.WriteLine("FAIL: " + m); } }

    static void Shot(Form f, string dir, string name)
    {
        for (int t = 0; t < 10; t++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
        using (var bmp = new Bitmap(f.Width, f.Height))
        {
            using (var gr = Graphics.FromImage(bmp))
                gr.CopyFromScreen(f.Location, Point.Empty, f.Size);
            bmp.Save(Path.Combine(dir, name + ".png"));
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : "/tmp/attile_ui";
        Directory.CreateDirectory(outDir);

        // 1. тексты
        string nb = "\u00a0";   // разделитель разрядов ru-RU — неразрывный пробел
        Ok(TileProgressForm.CountText(0, 159695) == "Уложено 0 из 159" + nb + "695 (0 %)",
           "счёт в начале: " + TileProgressForm.CountText(0, 159695));
        Ok(TileProgressForm.CountText(12345, 159695) == "Уложено 12" + nb + "345 из 159" + nb + "695 (7 %)",
           "счёт в середине: " + TileProgressForm.CountText(12345, 159695));
        Ok(TileProgressForm.Clock(0) == "0:00", "часы 0");
        Ok(TileProgressForm.Clock(65000) == "1:05", "часы 1:05");
        Ok(TileProgressForm.Clock(3723000) == "1:02:03", "часы 1:02:03");
        Ok(TileProgressForm.Eta(500, 10, 100000) == "оцениваю время…", "рано оценивать");
        Ok(TileProgressForm.Eta(10000, 5000, 10000) == "осталось меньше минуты", "меньше минуты");
        // 60 с на 20 000 из 160 000 → осталось 420 с = 7 мин
        Ok(TileProgressForm.Eta(60000, 20000, 160000) == "осталось около 7 мин",
           "7 мин: " + TileProgressForm.Eta(60000, 20000, 160000));
        Ok(TileProgressForm.Eta(600000, 1000, 10000).StartsWith("осталось около 1,5 ч"),
           "1,5 ч: " + TileProgressForm.Eta(600000, 1000, 10000));
        Ok(TileProgressForm.Eta(1000, 100, 100) == "почти готово", "конец");

        // 2. окно: вёрстка и снимки
        using (var f = new TileProgressForm("ATTILE — раскладка", 159695))
        {
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(10, 10);
            f.Show();
            Application.DoEvents();
            var cr = f.ClientRectangle;
            var ctl = new System.Collections.Generic.List<Control>();
            foreach (Control c in f.Controls) ctl.Add(c);
            for (int a = 0; a < ctl.Count; a++)
            {
                Ok(cr.Contains(ctl[a].Bounds), "«" + ctl[a].Text + "» вылезает из окна " + ctl[a].Bounds);
                for (int b = a + 1; b < ctl.Count; b++)
                    Ok(!ctl[a].Bounds.IntersectsWith(ctl[b].Bounds),
                       "пересекаются «" + ctl[a].Text + "» и «" + ctl[b].Text + "»");
            }
            // подсказка помещается в свою ширину (не наезжает на кнопку)
            foreach (Control c in f.Controls)
                if (c is Label)
                {
                    var sz = TextRenderer.MeasureText(c.Text.Length > 0 ? c.Text : "Уложено 159 695 из 159 695 (100 %)", c.Font);
                    Ok(sz.Width <= c.Width + 2, "текст не влезает: «" + c.Text + "» " + sz.Width + " > " + c.Width);
                }
            Shot(f, outDir, "progress_start");
            f.Report(52310, true);
            Shot(f, outDir, "progress_mid");
            Ok(!f.CancelRequested, "без действий — не прервано");

            // 3. кнопка «Прервать»
            Button stop = null;
            foreach (Control c in f.Controls) if (c is Button) stop = (Button)c;
            Ok(stop != null && stop.Text == "Прервать", "есть кнопка «Прервать»");
            if (stop != null) stop.PerformClick();
            Ok(f.CancelRequested, "кнопка прерывает");
            Ok(stop != null && !stop.Enabled, "после нажатия кнопка неактивна");
            Shot(f, outDir, "progress_cancel");
            f.Close();
        }
        // 4. Esc
        using (var f = new TileProgressForm("ATTILE — раскладка", 1000))
        {
            f.Show();
            Application.DoEvents();
            var m = typeof(Control).GetMethod("OnKeyDown", BindingFlags.NonPublic | BindingFlags.Instance);
            m.Invoke(f, new object[] { new KeyEventArgs(Keys.Escape) });
            Ok(f.CancelRequested, "Esc прерывает");
            f.Close();
        }
        Console.WriteLine(fails == 0 ? "PROGRESS: OK" : "PROGRESS: провалов " + fails);
        return fails == 0 ? 0 : 1;
    }
}
