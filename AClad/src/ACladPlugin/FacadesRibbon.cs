// FacadesRibbon — панель модуля в ОБЩЕЙ вкладке «Фасады» ленты AutoCAD
// (просьба Германа 26.07: панель управления с иконками, чтобы не вводить
// команды вручную). Паттерн — боевой RibbonUi ATableSpec; отличие: вкладка
// ОДНА на все фасадные модули (AFacades/AClad/AFrame), каждый плагин
// идемпотентно пересоздаёт только СВОЮ панель — соседние не трогает.
// Иконки — PNG из бандла (Contents/icons/<имя>.png); нет файла — текст.

using System;
using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ACladPlugin
{
    internal static class FacadesRibbon
    {
        private const string TabId = "FACADES_TAB";
        private const string TabTitle = "Фасады";
        private const string PanelId = "ACLAD_PANEL_SRC";
        private static readonly CmdHandler Handler = new CmdHandler();
        private static bool _sysVarHooked;

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
                RemovePanel();
            }
            catch { }
        }

        private static void OnItemInitialized(object sender,
                                              RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            try { Build(); } catch { }
        }

        private static void OnSysVarChanged(object sender,
            SystemVariableChangedEventArgs e)
        {
            if (e != null && string.Equals(e.Name, "WSCURRENT",
                    StringComparison.OrdinalIgnoreCase))
            {
                try { Build(); } catch { }
            }
        }

        private static RibbonTab FindOrCreateTab(RibbonControl rc)
        {
            foreach (RibbonTab t in rc.Tabs)
                if (t.Id == TabId) return t;
            var tab = new RibbonTab { Title = TabTitle, Id = TabId };
            rc.Tabs.Add(tab);
            return tab;
        }

        private static void RemovePanel()
        {
            RibbonControl rc = ComponentManager.Ribbon;
            if (rc == null) return;
            foreach (RibbonTab t in rc.Tabs)
            {
                if (t.Id != TabId) continue;
                RibbonPanel old = null;
                foreach (RibbonPanel p in t.Panels)
                    if (p.Source != null && p.Source.Id == PanelId)
                    { old = p; break; }
                if (old != null) t.Panels.Remove(old);
                if (t.Panels.Count == 0) rc.Tabs.Remove(t);
                return;
            }
        }

        private static void Build()
        {
            RibbonControl rc = ComponentManager.Ribbon;
            if (rc == null) return;
            RemovePanel();                       // идемпотентно
            RibbonTab tab = FindOrCreateTab(rc);
            var src = new RibbonPanelSource
            { Title = "Раскладка", Id = PanelId };
src.Items.Add(MakeButton("Раскладка", "ATCLAD",
                "ATCLAD — раскладка облицовки кассетами по зонам/" +
                "контурам.", "acl_clad"));
            src.Items.Add(MakeButton("Размеры", "ATCLADDIM",
                "ATCLADDIM — размеры облицовки: ширины ряда / высоты " +
                "столбца.", "acl_dim"));
            var panel = new RibbonPanel { Source = src };
            tab.Panels.Add(panel);}

        private static RibbonButton MakeButton(string text, string cmd,
                                               string tip, string icon)
        {
            var b = new RibbonButton
            {
                Text = text,
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation =
                    System.Windows.Controls.Orientation.Vertical,
                CommandParameter = cmd,
                CommandHandler = Handler,
                ToolTip = tip
            };
            ImageSource img = LoadIcon(icon);
            if (img != null)
            { b.LargeImage = img; b.Image = img; b.ShowImage = true; }
            else b.ShowImage = false;
            return b;
        }

        private static ImageSource LoadIcon(string baseName)
        {
            try
            {
                string dir = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(dir)) return null;
                string p = Path.GetFullPath(Path.Combine(
                    dir, "..", "icons", baseName + ".png"));
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

        private sealed class CmdHandler : System.Windows.Input.ICommand
        {
            public event EventHandler CanExecuteChanged
            { add { } remove { } }
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
