using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AtSpecPlugin.Plugin))]

namespace AtSpecPlugin
{
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            ReportReactor.Attach();   // авто-пересчёт отчётных таблиц по правке блока
            RibbonUi.Init();          // кнопки команд в ленте
            ClassicUi.Init();         // кнопки команд в классическом интерфейсе (меню + тулбар)
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
                doc.Editor.WriteMessage(
                    "\nATableSpec загружен. Команды: ATSPEC — спецификация из блоков; " +
                    "ATSPECREPORT — свой отчёт по формулам; ATSPECEDIT — правка готовой таблицы; " +
                    "ATSPECEXPORT — выгрузка таблицы в CSV; ATSPECUPDATE — пересчитать отчётные таблицы.\n");
        }

        public void Terminate() { ClassicUi.Cleanup(); RibbonUi.Cleanup(); ReportReactor.Detach(); }
    }
}
