// FacadesClassic — пункты модуля в ОБЩЕМ выпадающем меню «Фасады»
// для КЛАССИЧЕСКОГО вида AutoCAD (фидбэк Германа 27.07: «в классике
// панель не найти»). Паттерн — боевой ClassicUi ATableSpec: позднее
// связывание COM (dynamic), макросы «_CMD » БЕЗ ^C^C (грабля 25.06).
// Меню одно на все фасадные модули; каждый плагин идемпотентно
// пересоздаёт только СВОИ пункты (по подписи).

using System;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AFacadesPlugin
{
    internal static class FacadesClassic
    {
        private const string MenuName = "Фасады";
        // подпись → команда (СВОИ пункты этого модуля)
        private static readonly string[][] Items =
        {
            new[] { "Зоны фасада (ATFZONE)", "ATFZONE" },
            new[] { "Ведомость зон (ATFTABLE)", "ATFTABLE" },
        };

        public static void Init()
        {
            try { Build(); }
            catch { /* классика не обязательна */ }
        }

        public static void Cleanup()
        {
            try { RemoveMyItems(FindMenu(false)); }
            catch { }
        }

        private static dynamic FindMenu(bool create)
        {
            dynamic app = AcApp.AcadApplication;
            if (app == null) return null;
            dynamic mg = app.MenuGroups.Item(0);
            dynamic menus = mg.Menus;
            for (int i = 0; i < (int)menus.Count; i++)
            {
                dynamic m = menus.Item(i);
                string nm = null;
                try { nm = (string)m.NameNoMnemonic; } catch { }
                if (nm == null)
                    try { nm = (string)m.Name; } catch { }
                if (nm != null &&
                    nm.Replace("&", "") == MenuName)
                    return m;
            }
            if (!create) return null;
            dynamic menu = menus.Add(MenuName);
            try { menu.InsertInMenuBar((int)app.MenuBar.Count); }
            catch { }
            return menu;
        }

        private static void RemoveMyItems(dynamic menu)
        {
            if (menu == null) return;
            for (int i = (int)menu.Count - 1; i >= 0; i--)
            {
                dynamic it = menu.Item(i);
                string cap = null;
                try { cap = (string)it.Caption; } catch { }
                if (cap == null) continue;
                cap = cap.Replace("&", "");
                foreach (var my in Items)
                    if (cap == my[0])
                    { try { it.Delete(); } catch { } break; }
            }
        }

        private static void Build()
        {
            dynamic menu = FindMenu(true);
            if (menu == null) return;
            RemoveMyItems(menu);                    // идемпотентно
            foreach (var it in Items)
                menu.AddMenuItem((int)menu.Count, it[0],
                                 "_" + it[1] + " ");
        }
    }
}
