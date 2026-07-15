// RibbonUi — кнопки команд ABlockGen в ЛЕНТЕ AutoCAD (вкладка + панель,
// 3 кнопки: ATVITRAGEAR, ATVITRAGE, ATSPECREPORT). Перенос отработанного
// паттерна ATableSpec/RibbonUi.cs (фидбэк Алексея 15.07: «добавить кнопки
// вызова команд — неудобно набивать вручную»): Autodesk.Windows (WPF-лента),
// запуск команды как клик (SendStringToExecute), PNG-иконки из бандла,
// пересоздание вкладки на смене workspace (WSCURRENT).
//
// «Отчёт» (ATSPECREPORT) — команда СОСЕДНЕЙ программы ATableSpec: просьба
// Алексея иметь её под рукой в витражном workflow (сгенерил каркас → сразу
// спецификация). Если ATableSpec не установлен, AutoCAD ответит «Неизвестная
// команда» — кнопка ничего не ломает.
//
// ВНИМАНИЕ: компилируется на Windows (NuGet AutoCAD.NET 24.0.0 = AutoCAD 2021).
// В песочнице не собирается — проверяется автосборкой GitHub Actions (check.yml).
// Целевой рантайм: .NET Framework 4.8 (AutoCAD 2013–2024).

using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ABlockGenPlugin
{
    internal static class RibbonUi
    {
        private const string TabId = "ABG_TAB";
        private const string PanelId = "ABG_PANEL_SRC";
        private static readonly AbgCommand Handler = new AbgCommand();
        private static bool _sysVarHooked;

        // Вызывается из Plugin.Initialize. Не критична: любой сбой проглатываем —
        // команды работают и без ленты.
        public static void Init()
        {
            try
            {
                if (ComponentManager.Ribbon != null)
                    Build();
                else
                    ComponentManager.ItemInitialized += OnItemInitialized;

                if (!_sysVarHooked)
                {
                    AcApp.SystemVariableChanged += OnSysVarChanged;
                    _sysVarHooked = true;
                }
            }
            catch { /* лента не обязательна */ }
        }

        public static void Cleanup()
        {
            try
            {
                ComponentManager.ItemInitialized -= OnItemInitialized;
                if (_sysVarHooked)
                {
                    AcApp.SystemVariableChanged -= OnSysVarChanged;
                    _sysVarHooked = false;
                }
                RemoveTab();
            }
            catch { }
        }

        // Лента может быть ещё не создана на момент загрузки плагина — ждём её.
        private static void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            try { Build(); } catch { }
        }

        // Лента пересоздаётся при смене рабочего пространства — возвращаем вкладку.
        private static void OnSysVarChanged(object sender, SystemVariableChangedEventArgs e)
        {
            if (e != null && string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
            {
                try { Build(); } catch { }
            }
        }

        private static void RemoveTab()
        {
            RibbonControl rc = ComponentManager.Ribbon;
            if (rc == null) return;
            RibbonTab old = null;
            foreach (RibbonTab t in rc.Tabs)
                if (t.Id == TabId) { old = t; break; }
            if (old != null) rc.Tabs.Remove(old);
        }

        private static void Build()
        {
            RibbonControl rc = ComponentManager.Ribbon;
            if (rc == null) return;

            RemoveTab();   // идемпотентно

            var tab = new RibbonTab { Title = "ABlockGen", Id = TabId };
            var src = new RibbonPanelSource { Title = "ABlockGen", Id = PanelId };
            var panel = new RibbonPanel { Source = src };
            tab.Panels.Add(panel);

            src.Items.Add(MakeButton("Витраж по АР", "ATVITRAGEAR",
                "ATVITRAGEAR — каркас по АР-чертежу: образец стойки → образец " +
                "ригеля → рамка на АР-графику → точка вставки.", "abg_ar"));
            src.Items.Add(MakeButton("Витраж по проёму", "ATVITRAGE",
                "ATVITRAGE — сетка по прямоугольнику проёма + диалог параметров.",
                "abg_grid"));
            src.Items.Add(MakeButton("Отчёт", "ATSPECREPORT",
                "ATSPECREPORT (ATableSpec) — спецификация/отчёт по построенному " +
                "каркасу.", "abg_report"));

            rc.Tabs.Add(tab);
        }

        private static RibbonButton MakeButton(string text, string cmd, string tip, string iconBase)
        {
            var b = new RibbonButton
            {
                Text = text,
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = cmd,
                CommandHandler = Handler,
                ToolTip = tip
            };
            ImageSource img = LoadIcon(iconBase);
            if (img != null) { b.LargeImage = img; b.Image = img; b.ShowImage = true; }
            else b.ShowImage = false;
            return b;
        }

        private static ImageSource LoadIcon(string baseName)
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return null;
                string p = Path.GetFullPath(Path.Combine(dir, "..", "icons", baseName + ".png"));
                if (!File.Exists(p)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(p, UriKind.Absolute);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // Запуск команды как из командной строки («клик» = ввод команды).
        private sealed class AbgCommand : System.Windows.Input.ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) { return true; }
            public void Execute(object parameter)
            {
                string cmd = parameter as string;
                if (string.IsNullOrEmpty(cmd)) return;
                Document doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                doc.SendStringToExecute(cmd + " ", true, false, true);
            }
        }
    }
}
