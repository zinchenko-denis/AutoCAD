// Прогон ядра ведомостей работ без AutoCAD (mono): формы Германа → заполненные xlsx для проверки
// Facades/tools/vedomost/check.py (openpyxl). Сборка (из корня репо):
//   mcs -out:/tmp/vedomost.exe -r:System.Web.Extensions.dll -r:System.IO.Compression.dll -r:System.Xml.dll \
//       Facades/tools/vedomost/Check.cs Facades/src/AFacadesPlugin/WorkStatement.cs
//   mono /tmp/vedomost.exe <папка форм> <папка вывода>
using System;
using System.Collections.Generic;
using System.IO;
using AFacadesPlugin;

static class VedomostCheck
{
    static Dictionary<string, object> Rep(double net, double ws, double wsl, double ds, double vs, double vt, double vb)
    {
        return new Dictionary<string, object> {
            { "area_net_m2", net }, { "window_count", 1 }, { "window_slopes_m", ws }, { "window_sills_m", wsl },
            { "door_slopes_m", ds }, { "vitrage_side_m", vs }, { "vitrage_top_m", vt }, { "vitrage_bottom_m", vb },
            { "jambs_total_m", ws + ds }, { "sills_total_m", wsl } };
    }

    static WorkStatement.Group G(string layer, params Dictionary<string, object>[] reps)
    {
        var g = new WorkStatement.Group { Layer = layer };
        g.Reports.AddRange(reps);
        return g;
    }

    static void Run(string tdir, string name, List<WorkStatement.Group> groups, double par, string outp)
    {
        var cfg = WorkStatement.LoadConfig(Path.Combine(tdir, name + ".json"));
        string tpl = Path.IsPathRooted(cfg.Template) ? cfg.Template : Path.Combine(tdir, cfg.Template);
        var r = WorkStatement.Build(tpl, cfg, groups, par, outp);
        Console.WriteLine("== " + Path.GetFileName(outp) + " (строк добавлено " + r.RowsAdded + ")");
        foreach (var l in r.Lines) Console.WriteLine("   " + l);
        foreach (var l in r.Manual) Console.WriteLine("   вручную: " + l);
    }

    static int Main(string[] a)
    {
        string tdir = a[0], odir = a[1];
        Directory.CreateDirectory(odir);
        var z1 = Rep(600.5, 120, 30, 20, 14, 6, 6);
        var z2 = Rep(399.5, 80, 20, 10, 0, 0, 0);
        var z3 = Rep(250, 50, 12, 0, 8, 4, 4);
        // 1. штукатурка, один слой с толщиной: текст строки утеплителя — с толщиной, формула =D3 остаётся
        Run(tdir, "shtukaturka", new List<WorkStatement.Group> { G("ШТ утеплитель 150 мм", z1, z2) }, 45.2,
            Path.Combine(odir, "sht_one.xlsx"));
        // 2. вент, два слоя (150 и «– 100 мм»): строка утеплителя делится, строки ниже сдвигаются
        Run(tdir, "vent", new List<WorkStatement.Group> { G("НВФ утеплитель 150 мм", z1, z2), G("Нвф утеплитель– 100 мм", z3) },
            30.0, Path.Combine(odir, "vent_two.xlsx"));
        // 3. та же книга как форма: строки 150 и 100 уже есть — площади идут в них, копий нет
        File.Copy(Path.Combine(tdir, "vent.json"), Path.Combine(odir, "vent2.json"), true);
        File.WriteAllText(Path.Combine(odir, "vent2.json"),
            File.ReadAllText(Path.Combine(odir, "vent2.json")).Replace("\"vent.xlsx\"", "\"vent_two.xlsx\"")
                .Replace("\"D11\":", "\"D12\":").Replace("\"D13\":", "\"D14\":").Replace("\"D16\":", "\"D17\":")
                .Replace("\"D19\":", "\"D20\":").Replace("\"D20\": {\"parapet\"", "\"D21\": {\"parapet\""));
        Run(odir, "vent2", new List<WorkStatement.Group> { G("НВФ утеплитель 100 мм", z3), G("НВФ утеплитель 150 мм", z1, z2) },
            30.0, Path.Combine(odir, "vent_again.xlsx"));
        // 4. зона старой сборки (без типов проёмов): все проёмы — окна
        var old = new Dictionary<string, object> { { "area_net_m2", 100.0 }, { "jambs_total_m", 40.0 }, { "sills_total_m", 12.0 } };
        Run(tdir, "vent", new List<WorkStatement.Group> { G("Облицовка", old) }, 0, Path.Combine(odir, "vent_old.xlsx"));
        return 0;
    }
}
