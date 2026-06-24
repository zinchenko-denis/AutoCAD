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
            RibbonUi.Init();          // кнопки команд в ленте (классика — отдельным заходом)
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
                doc.Editor.WriteMessage(
                    "\nATableSpec загружен. Команды: ATSPEC — спецификация из блоков; " +
                    "ATSPECREPORT — свой отчёт по формулам; ATSPECEDIT — правка готовой таблицы; " +
                    "ATSPECEXPORT — выгрузка таблицы в CSV; ATSPECUPDATE — пересчитать отчётные таблицы.\n");
        }

        public void Terminate() { RibbonUi.Cleanup(); ReportReactor.Detach(); }
    }
}
