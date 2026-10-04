using System;
using System.Collections.Generic;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace FacadeSafety
{
    /// <summary>One F2 diagnostic per startup stage; no file or drawing scan.</summary>
    internal static class FacadeStartupDiagnostics
    {
        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static readonly Queue<string> Pending = new Queue<string>();
        private static bool waiting;

        internal static void Report(string module, string build, string stage, Exception error)
        {
            // Diagnostics must never abort registration of the remaining UI/commands.
            try
            {
                if (!Reported.Add(module + ":" + stage)) return;
                string detail = error.GetType().Name + ": " + error.Message;
                Exception cause = error.GetBaseException();
                if (!ReferenceEquals(cause, error))
                    detail += " Причина: " + cause.GetType().Name + ": " + cause.Message;
                Pending.Enqueue("\n" + module + " (" + build + "): не удалось загрузить «" + stage +
                    "». " + detail +
                    "\nСохраните это сообщение из F2 для разработчика. Команды можно запускать с клавиатуры.\n");
                try { Flush(); } catch { }
                if (Pending.Count != 0 && !waiting)
                {
                    AcApp.Idle += OnIdle;
                    waiting = true;
                }
            }
            catch { }
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            try { Flush(); } catch { }
        }

        private static void Flush()
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            while (Pending.Count != 0)
            {
                doc.Editor.WriteMessage(Pending.Peek());
                Pending.Dequeue();
            }
            Cancel();
        }

        internal static void Cancel()
        {
            if (!waiting) return;
            AcApp.Idle -= OnIdle;
            waiting = false;
        }
    }
}
