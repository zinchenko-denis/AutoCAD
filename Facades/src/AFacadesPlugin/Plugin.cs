using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AFacadesPlugin.Plugin))]

namespace AFacadesPlugin
{
    /// <summary>
    /// AFacades — фасадное направление (НВФ + СФТК), этап 1 по ТЗ Германа:
    /// ручное задание зон облицовки контурами -> слой с именем облицовки,
    /// штриховка с вычетом проёмов, марка и площадь на чертеже, таблица
    /// площадей и погонажей. Гибрид-паттерн ATableSpec/ABlockGen:
    /// тонкий C# + замороженный py-движок (facades_engine.exe).
    /// </summary>
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    doc.Editor.WriteMessage(
                        "\nAFacades загружен (сборка DLL от " + BuildStamp() +
                        "). Команда: ATFZONE — зоны облицовки из замкнутых " +
                        "контуров (площади, отливы, откосы).\n");
            }
            catch { }
        }

        // маркер версии сборки в командной строке (урок ABlockGen 18.07:
        // «какая сборка у конструктора» больше не гадаем)
        internal static string BuildStamp()
        {
            try
            {
                string p = Assembly.GetExecutingAssembly().Location;
                return File.GetLastWriteTime(p).ToString("dd.MM.yyyy HH:mm");
            }
            catch { return "?"; }
        }

        public void Terminate() { }
    }
}
