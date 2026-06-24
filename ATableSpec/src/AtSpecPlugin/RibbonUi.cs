// RibbonUi — кнопки команд ATableSpec в ЛЕНТЕ AutoCAD (вкладка + панель, 5 кнопок).
// Строится в рантайме через Autodesk.Windows (WPF-лента). Команда запускается
// как клик: SendStringToExecute("ATSPEC ", ...). Иконки — PNG из бандла
// (Contents/icons/<имя>.png); если файла нет — кнопка показывается текстом.
//
// Классическое меню/тулбар (для классического интерфейса) — отдельным заходом
// (там COM/Customization). Лента покрывает ленточный интерфейс.
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

namespace AtSpecPlugin
{
    internal static class RibbonUi
    {
        private const string TabId = "ATSPEC_TAB";
        private const string PanelId = "ATSPEC_PANEL_SRC";
        private static readonly AtsCommand Handler = new AtsCommand();
        private static bool _sysVarHooked;

        // Вызывается из Plugin.Initialize. Не критична: любой сбой проглатываем —
        // команды и реактор работают и без ленты.
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

            var tab = new RibbonTab { Title = "ATableSpec", Id = TabId };
            var src = new RibbonPanelSource { Title = "ATableSpec", Id = PanelId };
            var panel = new RibbonPanel { Source = src };
            tab.Panels.Add(panel);

            src.Items.Add(MakeButton("Спецификация", "ATSPEC",
                "ATSPEC — ведомость/спецификация из выбранных блоков.", "ats_spec"));
            src.Items.Add(MakeButton("Отчёт", "ATSPECREPORT",
                "ATSPECREPORT — свой отчёт по формулам (окно-построитель).", "ats_report"));
            src.Items.Add(MakeButton("Правка", "ATSPECEDIT",
                "ATSPECEDIT — правка готовой таблицы ATableSpec на месте.", "ats_edit"));
            src.Items.Add(MakeButton("Экспорт CSV", "ATSPECEXPORT",
                "ATSPECEXPORT — выгрузка таблицы в CSV.", "ats_export"));
            src.Items.Add(MakeButton("Пересчёт", "ATSPECUPDATE",
                "ATSPECUPDATE — пересчитать отчётные таблицы.", "ats_update"));

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
        private sealed class AtsCommand : System.Windows.Input.ICommand
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
