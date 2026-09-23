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
                        "). Команды: ATTILE — раскладка облицовки (разбежка, " +
                        "принудительные русты), ATCLADDIM — размеры; ATCLAD — " +
                        "прежняя раскладка кассетами (с клавиатуры, для старых чертежей).\n");
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
