// ClassicUi — кнопки команд ABlockGen в КЛАССИЧЕСКОМ интерфейсе AutoCAD:
// выпадающее меню «ABlockGen» в строке меню + плавающий тулбар с 3 кнопками
// (ATVITRAGEAR, ATVITRAGE, ATSPECREPORT — фидбэк Алексея 15.07). Перенос
// ОТРАБОТАННОГО ClassicUi ATableSpec со всеми его рантайм-уроками:
//   · COM ActiveX (AcadApplication) ПОЗДНИМ СВЯЗЫВАНИЕМ (dynamic) — без
//     interop-PIA, для компиляции хватает Microsoft.CSharp;
//   · иконки тулбара — BMP ПО ПОЛНОМУ ПУТИ (классика не грузит PNG → «?»),
//     фон RGB(64,64,64) — сэмпл Алексея 0016.bmp;
//   · макрос команды — «_CMD » БЕЗ ^C^C (у Алексея ^C^C в COM-макросе брался
//     буквально: «неизвестная команда»);
//   · позиция тулбара v3: пере-применение на первом Idle (workspace двигает
//     тулбары ПОСЛЕ Initialize) + периодическое сохранение таймером 5 с
//     (переживает аварийный выход) + поиск тулбара по ВСЕМ группам меню
//     (у Алексея грузится СПДС CUIX). Реестр — HKCU\Software\ABlockGen.
//
// Всё в try/catch: не ляжет COM/иконки — команды/лента работают. Рантайм
// кросс-версийно проверяет Алексей (вероятны 1–2 итерации, как было у
// ATableSpec).
//
// ВНИМАНИЕ: компилируется на Windows (NuGet AutoCAD.NET 24.0.0 = AutoCAD 2021).
// В песочнице не собирается — проверяется автосборкой GitHub Actions (check.yml).
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013–2024).

using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ABlockGenPlugin
{
    internal static class ClassicUi
    {
        private const string GroupName = "ABlockGen";   // имя меню и тулбара

        // (подпись, команда, база имени иконки)
        private static readonly string[][] Items = new[]
        {
            new[] { "Витраж по АР",     "ATVITRAGEAR",  "abg_ar" },
            new[] { "Витраж по проёму", "ATVITRAGE",    "abg_grid" },
            new[] { "Отчёт",            "ATSPECREPORT", "abg_report" },
        };

        // ── состояние механики позиции (v3, перенос ATableSpec) ──
        private static System.Windows.Forms.Timer _poll;   // периодическое сохранение
        private static bool _idleHooked;                    // подписка на Idle жива
        private static bool _inTick;                        // защита от реентрантности тика
        private static int _lastDock = int.MinValue;        // последнее записанное в реестр
        private static int _lastTop, _lastLeft;

        public static void Init()
        {
            try { Build(); } catch { /* классика не обязательна */ }
            // BeginQuit стреляет ДО разрушения COM — запасная точка записи позиции.
            try { AcApp.BeginQuit += OnBeginQuit; } catch { }
            // пере-применение позиции + старт таймера — на первом Idle,
            // когда старт AutoCAD (профиль/workspace) полностью устаканился.
            try { AcApp.Idle += OnFirstIdle; _idleHooked = true; } catch { }
        }

        private static void OnFirstIdle(object sender, EventArgs e)
        {
            try { AcApp.Idle -= OnFirstIdle; } catch { }
            _idleHooked = false;
            try
            {
                dynamic tb = FindToolbar();
                if (tb != null) RestoreToolbarPos(tb);   // повтор ПОСЛЕ workspace-восстановления
            }
            catch { }
            SeedLastFromRegistry();   // первый тик не должен переписать реестр тем же/дефолтом
            StartPoll();
        }

        private static void OnBeginQuit(object sender, EventArgs e) { SaveNow(); }

        public static void Cleanup()
        {
            try { if (_poll != null) { _poll.Stop(); _poll.Dispose(); _poll = null; } } catch { }
            try { if (_idleHooked) { AcApp.Idle -= OnFirstIdle; _idleHooked = false; } } catch { }
            try { AcApp.BeginQuit -= OnBeginQuit; } catch { }
            SaveNow();                                   // запасная попытка (если COM ещё жив)
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

        // ── позиция тулбара: тулбар пересоздаётся при каждом запуске
        //    (RemoveToolbar+Add), поэтому AutoCAD его позицию не хранит —
        //    храним сами. DockStatus: 0..3 = стороны, 4 = плавающий ──
        private const string RegKey = @"Software\ABlockGen";

        // Поиск тулбара по ВСЕМ группам меню (не только Item(0)).
        private static dynamic FindToolbar()
        {
            try
            {
                dynamic app = AcApp.AcadApplication;
                if (app == null) return null;
                dynamic groups = app.MenuGroups;
                int gc = (int)groups.Count;
                for (int g = 0; g < gc; g++)
                {
                    dynamic tbs;
                    try { tbs = groups.Item(g).Toolbars; } catch { continue; }
                    for (int i = (int)tbs.Count - 1; i >= 0; i--)
                    {
                        dynamic t = tbs.Item(i);
                        if (NameEquals(t, GroupName)) return t;
                    }
                }
            }
            catch { }
            return null;
        }

        // Немедленная запись текущей позиции (BeginQuit/Cleanup).
        private static void SaveNow()
        {
            try
            {
                dynamic tb = FindToolbar();
                if (tb == null) return;                  // не нашли — реестр НЕ трогаем
                int dock = (int)tb.DockStatus;
                int top = 0, left = 0;
                if (dock == 4) { try { top = (int)tb.Top; left = (int)tb.Left; } catch { } }
                WritePos(dock, top, left);
            }
            catch { }
        }

        // Тик таймера: пишем в реестр ТОЛЬКО при изменении позиции.
        private static void PollTick(object sender, EventArgs e)
        {
            if (_inTick) return;
            _inTick = true;
            try
            {
                dynamic tb = FindToolbar();
                if (tb == null) return;
                int dock = (int)tb.DockStatus;
                int top = 0, left = 0;
                if (dock == 4) { try { top = (int)tb.Top; left = (int)tb.Left; } catch { } }
                if (dock == _lastDock && top == _lastTop && left == _lastLeft) return;
                WritePos(dock, top, left);
            }
            catch { }
            finally { _inTick = false; }
        }

        private static void WritePos(int dock, int top, int left)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
                {
                    k.SetValue("TbDock", dock);
                    k.SetValue("TbTop", top);
                    k.SetValue("TbLeft", left);
                }
                _lastDock = dock; _lastTop = top; _lastLeft = left;
            }
            catch { }
        }

        private static void SeedLastFromRegistry()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (k == null) return;
                    _lastDock = Convert.ToInt32(k.GetValue("TbDock", int.MinValue));
                    _lastTop  = Convert.ToInt32(k.GetValue("TbTop", 0));
                    _lastLeft = Convert.ToInt32(k.GetValue("TbLeft", 0));
                }
            }
            catch { }
        }

        private static void StartPoll()
        {
            try
            {
                if (_poll != null) return;
                _poll = new System.Windows.Forms.Timer();
                _poll.Interval = 5000;                   // 5 с: цена COM-чтения копеечная
                _poll.Tick += PollTick;
                _poll.Start();
            }
            catch { }
        }

        private static void RestoreToolbarPos(dynamic tb)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    if (k == null) return;               // первый запуск — дефолтная позиция
                    int dock = Convert.ToInt32(k.GetValue("TbDock", 4));
                    int top = Convert.ToInt32(k.GetValue("TbTop", 0));
                    int left = Convert.ToInt32(k.GetValue("TbLeft", 0));
                    if (dock >= 0 && dock <= 3) tb.Dock(dock);
                    else tb.Float(top, left, 1);
                }
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
            RestoreToolbarPos(tb);                   // ранняя попытка; повтор — на первом Idle
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
