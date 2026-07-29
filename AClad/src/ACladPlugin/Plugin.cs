using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(ACladPlugin.Plugin))]

namespace ACladPlugin
{
    /// <summary>
    /// AClad — раскладка облицовки НВФ (этап 2 фасадного направления).
    /// Отдельный бандл (просьба Германа 22.07): боевой AFacades (зоны/
    /// ведомости) ставится один раз, AClad переустанавливается при
    /// каждой итерации раскладки. Гибрид-паттерн ATableSpec: тонкий C#
    /// + замороженный py-движок (clad_engine.exe).
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
                        "\nAClad загружен (сборка DLL от " + BuildStamp() +
                        "). Команда: ATCLAD — раскладка облицовки " +
                        "кассетами по зонам ATFZONE/контурам.\n");
            }
            catch { }
            try { FacadesRibbon.Init();
            FacadesClassic.Init(); } catch { }   // панель «Фасады»
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

        public void Terminate() { try { FacadesRibbon.Cleanup();
            FacadesClassic.Cleanup(); } catch { } }
    }
}
