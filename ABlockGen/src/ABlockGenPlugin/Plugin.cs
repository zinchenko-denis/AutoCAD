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
                        "\nABlockGen загружен. Команды: ATVITRAGEAR — каркас по " +
                        "АР-чертежу (образцы стойки/ригеля → рамка); ATVITRAGE — " +
                        "сетка по проёму. Кнопки: лента и тулбар «ABlockGen».\n");
            }
            catch { }
        }

        public void Terminate()
        {
            ClassicUi.Cleanup();
            RibbonUi.Cleanup();
        }
    }
}
