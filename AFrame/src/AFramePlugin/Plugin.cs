using System;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AFramePlugin.Plugin))]

namespace AFramePlugin
{
    /// <summary>
    /// AFrame — подсистема НВФ (этап 3 фасадного направления):
    /// кронштейны + направляющие + кляммеры по согласованной раскладке
    /// AClad. Отдельный бандл (паттерн AClad): итерационный этап,
    /// переустанавливается часто. Гибрид: тонкий C# + замороженный
    /// py-движок (frame_engine.exe). Концепт и решения —
    /// Facades/docs/STAGE3_FRAME.md.
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
                        "\nAFrame загружен (сборка DLL от " + BuildStamp() +
                        "). Команда: ATFRAME — подсистема НВФ " +
                        "(кронштейны/направляющие/кляммеры) по " +
                        "раскладке ATCLAD.\n");
            }
            catch { }
            try { FacadesRibbon.Init();
            FacadesClassic.Init(); } catch { }   // панель «Фасады»
        }

        // маркер версии сборки (урок ABlockGen: «какая сборка у
        // конструктора» не гадаем)
        internal static string BuildStamp()
        {
            try
            {
                string p = Assembly.GetExecutingAssembly().Location;
                string stamp = File.GetLastWriteTime(p).ToString("dd.MM.yyyy HH:mm");
                // 24.09 (рецензия): дата файла не опознаёт сборку однозначно —
                // паспорт build-info.json (номер сборки, коммит) рядом с DLL
                string bi = Path.Combine(Path.GetDirectoryName(p) ?? ".", "build-info.json");
                if (File.Exists(bi))
                {
                    var d = new System.Web.Script.Serialization.JavaScriptSerializer()
                        .DeserializeObject(File.ReadAllText(bi, System.Text.Encoding.UTF8))
                        as System.Collections.Generic.Dictionary<string, object>;
                    object n, sha;
                    if (d != null && d.TryGetValue("build", out n) && d.TryGetValue("sha", out sha))
                    {
                        string sh = System.Convert.ToString(sha);
                        stamp += ", сборка №" + n + ", коммит " + (sh.Length > 7 ? sh.Substring(0, 7) : sh);
                    }
                }
                return stamp;
            }
            catch { return "?"; }
        }

        public void Terminate() { try { FacadesRibbon.Cleanup();
            FacadesClassic.Cleanup(); } catch { } }
    }
}
