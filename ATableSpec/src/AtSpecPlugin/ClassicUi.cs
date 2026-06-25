// ClassicUi — кнопки команд ATableSpec в КЛАССИЧЕСКОМ интерфейсе AutoCAD:
// выпадающее меню «ATableSpec» в строке меню + плавающий тулбар с 5 кнопками.
// Делается в рантайме через COM ActiveX (AcadApplication) ПОЗДНИМ СВЯЗЫВАНИЕМ (dynamic),
// чтобы НЕ зависеть от interop-PIA (их нет в NuGet AutoCAD.NET) — для компиляции хватает
// Microsoft.CSharp. Иконки тулбара — BMP из Contents/icons по ПОЛНОМУ пути (классика не
// грузит PNG → были «?»). Макрос команды — «_CMD » без ^C^C: в COM-макросе ^C^C у Алексея
// бралось буквально («неизвестная команда»), а «_CMD» отрабатывает.
// Всё в try/catch: не ляжет COM/иконки — команды/лента/реактор работают. Рантайм
// кросс-версийно проверяет Алексей (вероятны 1–2 итерации).
//
// ВНИМАНИЕ: компилируется на Windows (NuGet AutoCAD.NET 24.0.0 = AutoCAD 2021).
// В песочнице не собирается — проверяется автосборкой GitHub Actions (check.yml).
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013–2024).

using System;
using System.IO;
using System.Reflection;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AtSpecPlugin
{
    internal static class ClassicUi
    {
        private const string GroupName = "ATableSpec";   // имя меню и тулбара

        // (подпись, команда, база имени иконки)
        private static readonly string[][] Items = new[]
        {
            new[] { "Спецификация", "ATSPEC",       "ats_spec" },
            new[] { "Отчёт",        "ATSPECREPORT", "ats_report" },
            new[] { "Правка",       "ATSPECEDIT",   "ats_edit" },
            new[] { "Экспорт CSV",  "ATSPECEXPORT", "ats_export" },
            new[] { "Пересчёт",     "ATSPECUPDATE", "ats_update" },
        };

        public static void Init()
        {
            try { Build(); } catch { /* классика не обязательна */ }
        }

        public static void Cleanup()
        {
            try
            {
                dynamic app = AcApp.AcadApplication;
                if (app == null) return;
                dynamic mg = app.MenuGroups.Item(0);
                RemoveMenu(mg);
                RemoveToolbar(mg);
            }
            catch { }
        }

        private static void Build()
        {
            dynamic app = AcApp.AcadApplication;
            if (app == null) return;

            string icons = IconsDir();              // полный путь к Contents/icons (или null)

            dynamic mg = app.MenuGroups.Item(0);    // основная группа меню (ACAD)

            // ── выпадающее меню в строке меню ──
            RemoveMenu(mg);                          // идемпотентно
            dynamic menus = mg.Menus;
            dynamic menu = menus.Add(GroupName);     // AcadPopupMenu
            foreach (var it in Items)
                menu.AddMenuItem(menu.Count, it[0], "_" + it[1] + " ");
            try { menu.InsertInMenuBar((int)app.MenuBar.Count); } catch { }

            // ── плавающий тулбар ──
            RemoveToolbar(mg);                       // идемпотентно
            dynamic toolbars = mg.Toolbars;
            dynamic tb = toolbars.Add(GroupName);    // AcadToolbar
            foreach (var it in Items)
            {
                dynamic b = tb.AddToolbarButton(tb.Count, it[0], it[0], "_" + it[1] + " ", false);
                if (icons != null)
                    try { b.SetBitmaps(Path.Combine(icons, it[2] + "_16.bmp"),
                                       Path.Combine(icons, it[2] + "_32.bmp")); } catch { }
            }
            try { tb.Visible = true; } catch { }
        }

        private static void RemoveMenu(dynamic mg)
        {
            try
            {
                dynamic menus = mg.Menus;
                for (int i = (int)menus.Count - 1; i >= 0; i--)
                {
                    dynamic m = menus.Item(i);
                    if (NameEquals(m, GroupName))
                    {
                        try { m.RemoveFromMenuBar(); } catch { }
                        try { m.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        private static void RemoveToolbar(dynamic mg)
        {
            try
            {
                dynamic tbs = mg.Toolbars;
                for (int i = (int)tbs.Count - 1; i >= 0; i--)
                {
                    dynamic t = tbs.Item(i);
                    if (NameEquals(t, GroupName))
                        try { t.Delete(); } catch { }
                }
            }
            catch { }
        }

        private static bool NameEquals(dynamic obj, string name)
        {
            try { return string.Equals((string)obj.Name, name, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // полный путь к папке иконок бандла (Contents/icons) или null
        private static string IconsDir()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return null;
                string icons = Path.GetFullPath(Path.Combine(dir, "..", "icons"));
                return Directory.Exists(icons) ? icons : null;
            }
            catch { return null; }
        }
    }
}
