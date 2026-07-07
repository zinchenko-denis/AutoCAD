// ClassicUi — кнопки команд ATableSpec в КЛАССИЧЕСКОМ интерфейсе AutoCAD:
// выпадающее меню «ATableSpec» в строке меню + плавающий тулбар с 5 кнопками.
// Делается в рантайме через COM ActiveX (AcadApplication) ПОЗДНИМ СВЯЗЫВАНИЕМ (dynamic),
// чтобы НЕ зависеть от interop-PIA (их нет в NuGet AutoCAD.NET) — для компиляции хватает
// Microsoft.CSharp. Иконки тулбара — BMP из Contents/icons по ПОЛНОМУ пути (классика не
// грузит PNG → были «?»). Макрос команды — «_CMD » без ^C^C: в COM-макросе ^C^C у Алексея
// бралось буквально («неизвестная команда»), а «_CMD» отрабатывает.
//
// Позиция тулбара (история): v1 — запись в Cleanup=Terminate (COM уже мёртв, не работало);
// v2 — запись в BeginQuit (Алексей 07.07: всё равно сбрасывается); v3 (текущая) —
// (а) позиция ПЕРЕ-применяется на первом Application.Idle: профиль/workspace доприменяются
// ПОСЛЕ Plugin.Initialize и двигают тулбары (лента по той же причине пересоздаётся на
// WSCURRENT), т.е. ранний Float() мог перетираться; (б) периодическое сохранение таймером
// (5 с, пишем только при изменении) — не зависит от пути выхода, переживает аварийный;
// (в) тулбар ищется по ВСЕМ группам меню, а не только Item(0) (у Алексея грузится СПДС
// CUIX — порядок групп на выходе не обязан совпадать со стартовым). BeginQuit/Cleanup
// остались запасными точками записи. Диагностика — команда ATSPECTBPOS (Commands.cs).
//
// Всё в try/catch: не ляжет COM/иконки — команды/лента/реактор работают. Рантайм
// кросс-версийно проверяет Алексей (вероятны 1–2 итерации).
//
// ВНИМАНИЕ: компилируется на Windows (NuGet AutoCAD.NET 24.0.0 = AutoCAD 2021).
// В песочнице не собирается — проверяется автосборкой GitHub Actions (check.yml).
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013–2024).

using System;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
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

        // ── состояние механики позиции (v3) ──
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
            // v3: пере-применение позиции + старт таймера — на первом Idle,
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

        // ── позиция тулбара: тулбар пересоздаётся при каждом запуске (RemoveToolbar+Add),
        //    поэтому AutoCAD его позицию не хранит — храним сами в HKCU\Software\ATableSpec.
        //    DockStatus: 0..3 = стороны докинга, 4 = плавающий (Top/Left в пикселях экрана).
        private const string RegKey = @"Software\ATableSpec";

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

        // Диагностика для ATSPECTBPOS: реестр + живое состояние тулбара + статус опроса.
        public static string DebugPos()
        {
            string reg = "реестр: пусто";
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                    if (k != null)
                        reg = string.Format("реестр: Dock={0} Top={1} Left={2}",
                            k.GetValue("TbDock", "?"), k.GetValue("TbTop", "?"), k.GetValue("TbLeft", "?"));
            }
            catch (Exception ex) { reg = "реестр: ошибка " + ex.Message; }

            string live = "тулбар: не найден";
            try
            {
                dynamic tb = FindToolbar();
                if (tb != null)
                {
                    int dock = (int)tb.DockStatus;
                    string tl = "";
                    if (dock == 4) { try { tl = string.Format(" Top={0} Left={1}", (int)tb.Top, (int)tb.Left); } catch { } }
                    bool vis = false; try { vis = (bool)tb.Visible; } catch { }
                    live = string.Format("тулбар: Dock={0}{1} Visible={2}", dock, tl, vis);
                }
            }
            catch (Exception ex) { live = "тулбар: ошибка " + ex.Message; }

            return reg + " | " + live + " | опрос=" + (_poll != null ? "вкл" : "выкл");
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
