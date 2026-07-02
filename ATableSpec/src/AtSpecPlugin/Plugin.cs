using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AtSpecPlugin.Plugin))]

namespace AtSpecPlugin
{
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            // Каждый компонент — под своим try/catch: исключение любого из них на старте
            // (COM классики, лента, реактор) НЕ должно валить Initialize, иначе AutoCAD
            // помечает DLL сбойной и команды не регистрируются («отвалилась автозагрузка»).
            try { ReportReactor.Attach(); } catch { }   // авто-пересчёт таблиц по правке блока
            try { RibbonUi.Init(); } catch { }          // кнопки команд в ленте
            try { ClassicUi.Init(); } catch { }         // кнопки в классике (меню + тулбар)
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    doc.Editor.WriteMessage(
                        "\nATableSpec загружен. Команды: ATSPEC — спецификация из блоков; " +
                        "ATSPECREPORT — свой отчёт по формулам; ATSPECEDIT — правка готовой таблицы; " +
                        "ATSPECEXPORT — выгрузка таблицы в CSV; ATSPECUPDATE — пересчитать отчётные таблицы.\n");
            }
            catch { }
        }

        public void Terminate() { ClassicUi.Cleanup(); RibbonUi.Cleanup(); ReportReactor.Detach(); }
    }
}
