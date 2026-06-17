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
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc != null)
                doc.Editor.WriteMessage(
                    "\nATableSpec загружен. Команды: ATSPEC — спецификация из блоков; " +
                    "ATSPECREPORT — свой отчёт по формулам; ATSPECUPDATE — пересчитать отчётные таблицы.\n");
        }

        public void Terminate() { ReportReactor.Detach(); }
    }
}
