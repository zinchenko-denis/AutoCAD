using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace FacadeSafety
{
    /// <summary>One startup warning for the three facade DLLs actually loaded by AutoCAD.
    /// This source is linked into each DLL; the AppDomain marker is shared by all three.</summary>
    internal static class FacadeBundleVersions
    {
        private const string CheckedKey = "AutoCAD.Facades.BundleVersions.Checked";
        private static readonly string[] Modules = { "AFacades", "AClad", "AFrame" };
        private static bool pending;

        internal static void Schedule()
        {
            if (pending || AppDomain.CurrentDomain.GetData(CheckedKey) != null) return;
            pending = true;
            AcApp.Idle += OnIdle;
        }

        internal static void Cancel()
        {
            if (!pending) return;
            AcApp.Idle -= OnIdle;
            pending = false;
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            try
            {
                // The document/editor may still be unavailable during startup.
                // Do not let a diagnostic callback abort the remaining Idle handlers.
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;
                Cancel();
                lock (AppDomain.CurrentDomain)
                {
                    if (AppDomain.CurrentDomain.GetData(CheckedKey) != null) return;
                    AppDomain.CurrentDomain.SetData(CheckedKey, true);
                }
                var loaded = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                    foreach (string module in Modules)
                        if (assembly.GetName().Name == module + "Plugin")
                        {
                            List<string> paths;
                            if (!loaded.TryGetValue(module, out paths))
                                loaded[module] = paths = new List<string>();
                            string path;
                            try { path = assembly.Location; } catch { path = ""; }
                            paths.Add(path);
                        }
                string warning = FindProblem(loaded);
                if (warning == null) return;
                // Each channel must work even if the other is temporarily unavailable.
                try { doc.Editor.WriteMessage("\n" + warning + "\n"); } catch (Exception) { }
                try { AcApp.ShowAlertDialog(warning); } catch (Exception) { }
            }
            catch (Exception)
            {
                try
                {
                    var doc = AcApp.DocumentManager.MdiActiveDocument;
                    if (doc != null) doc.Editor.WriteMessage(
                        "\nНе удалось проверить версии фасадных модулей. Установите AFacades, AClad и AFrame вместе из одного выпуска и перезапустите AutoCAD.\n");
                }
                catch (Exception) { }
            }
        }

        // At most three tiny local metadata files; no directory or DWG scan.
        internal static string FindProblem(IDictionary<string, List<string>> loaded)
        {
            var details = new List<string>();
            string firstBuild = null, firstSha = null;
            bool problem = false;
            foreach (string module in Modules)
            {
                List<string> paths;
                string build = null, sha = null, reason = null;
                if (!loaded.TryGetValue(module, out paths) || paths.Count == 0)
                    reason = "не загружен (отсутствует или отключён)";
                else if (paths.Count != 1)
                    reason = "загружено несколько копий DLL";
                else
                    reason = ReadVersion(module, paths[0], out build, out sha);
                if (reason != null)
                {
                    problem = true;
                    details.Add(module + " — " + reason);
                    continue;
                }
                details.Add(module + " — сборка №" + build + ", коммит " + sha.Substring(0, 7));
                if (firstBuild == null) { firstBuild = build; firstSha = sha; }
                else if (build != firstBuild || sha != firstSha) problem = true;
            }
            if (!problem) return null;
            return "Фасадные модули установлены неполным комплектом или их версии не согласованы.\n\n" +
                string.Join("\n", details.ToArray()) +
                "\n\nЗакройте AutoCAD и замените вместе AFacades.bundle, AClad.bundle и AFrame.bundle из одного выпуска." +
                " Старые копии перенесите за пределы ApplicationPlugins. Затем запустите AutoCAD заново." +
                "\nДо обновления не исправляйте зоны повторным запуском ATFZONE: старая AFacades может не создавать данные, необходимые новым модулям.";
        }

        private static string ReadVersion(string module, string dll, out string build, out string sha)
        {
            build = sha = null;
            try
            {
                if (string.IsNullOrEmpty(dll)) return "путь DLL недоступен";
                string file = Path.Combine(Path.GetDirectoryName(dll) ?? "", "build-info.json");
                if (!File.Exists(file)) return "номер выпуска неизвестен (нет build-info.json; возможно, старый модуль)";
                if (new FileInfo(file).Length > 16384) return "build-info.json повреждён";
                var data = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as Dictionary<string, object>;
                object value;
                if (data == null || !data.TryGetValue("bundle", out value) || Convert.ToString(value) != module)
                    return "build-info.json не соответствует модулю";
                int number;
                if (!data.TryGetValue("build", out value) || !int.TryParse(Convert.ToString(value), NumberStyles.None,
                    CultureInfo.InvariantCulture, out number) || number <= 0) return "номер сборки не указан или повреждён";
                build = number.ToString(CultureInfo.InvariantCulture);
                if (!data.TryGetValue("sha", out value)) return "коммит сборки не указан";
                sha = Convert.ToString(value).ToLowerInvariant();
                if (sha.Length != 40) return "коммит сборки повреждён";
                foreach (char c in sha)
                    if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return "коммит сборки повреждён";
                return null;
            }
            catch (Exception) { return "build-info.json не читается"; }
        }
    }
}
