using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(VitrageGenPlugin.Plugin))]

namespace VitrageGenPlugin
{
    /// <summary>
    /// VitrageGen — отдельная программа (НЕ развитие ATableSpec): полуавтоматический
    /// синтез витража вхождениями существующих определений блоков чертежа.
    /// Блочный контракт совместим с ATableSpec: сгенерил витраж — сразу спецификация.
    /// </summary>
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
            // Компоненты — под своими try/catch (урок ATableSpec: исключение на старте
            // помечает DLL сбойной и команды не регистрируются).
            try
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    doc.Editor.WriteMessage(
                        "\nVitrageGen загружен. Команда: ATVITRAGE — построить витраж " +
                        "по прямоугольнику проёма (Этап 1, полуавтомат).\n");
            }
            catch { }
        }

        public void Terminate() { }
    }
}
