using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(ABlockGenPlugin.Plugin))]

namespace ABlockGenPlugin
{
    /// <summary>
    /// ABlockGen — отдельная программа (НЕ развитие ATableSpec): полуавтоматический
    /// синтез витража вхождениями существующих определений блоков чертежа.
    /// Блочный контракт совместим с ATableSpec: сгенерил витраж — сразу спецификация.
    /// </summary>
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            // Компоненты — под своими try/catch (урок ATableSpec: исключение на старте
            // помечает DLL сбойной и команды не регистрируются).
            RibbonUi.Init();    // вкладка ленты (фидбэк Алексея 15.07: кнопки)
            ClassicUi.Init();   // меню + тулбар классического интерфейса
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    doc.Editor.WriteMessage(
                        "\nABlockGen загружен (сборка DLL от " + BuildStamp() +
                        "). Команды: ATVITRAGEAR — каркас по АР-чертежу " +
                        "(образцы одной рамкой); ATVITRAGE — сетка по проёму. " +
                        "Кнопки: лента и тулбар «ABlockGen».\n");
            }
            catch { }
        }

        // дата файла DLL (mtime из zip бандла = момент сборки CI) —
        // маркер версии в командной строке: «какая сборка у Алексея»
        // больше не гадаем (урок 18.07)
        internal static string BuildStamp()
        {
            try
            {
                string p = Assembly.GetExecutingAssembly().Location;
                return File.GetLastWriteTime(p).ToString("dd.MM.yyyy HH:mm");
            }
            catch { return "?"; }
        }

        public void Terminate()
        {
            ClassicUi.Cleanup();
            RibbonUi.Cleanup();
        }
    }
}
