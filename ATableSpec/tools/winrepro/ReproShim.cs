using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace AtSpecPlugin
{
    // Перехват MessageBox.Show в репро: пишем в лог, отвечаем Yes/OK.
    public static class ReproMsg
    {
        public static readonly List<string> Log = new List<string>();

        static DialogResult Answer(MessageBoxButtons b)
        {
            return (b == MessageBoxButtons.YesNo || b == MessageBoxButtons.YesNoCancel)
                ? DialogResult.Yes : DialogResult.OK;
        }
        static DialogResult Add(string text, string caption, MessageBoxButtons b)
        {
            Log.Add("[" + caption + "] " + text);
            Console.WriteLine("MSGBOX [" + caption + "]: " +
                (text.Length > 120 ? text.Substring(0, 120) + "…" : text));
            return Answer(b);
        }

        public static DialogResult Show(string text) { return Add(text, "", MessageBoxButtons.OK); }
        public static DialogResult Show(string text, string caption) { return Add(text, caption, MessageBoxButtons.OK); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons b) { return Add(text, caption, b); }
        public static DialogResult Show(string text, string caption, MessageBoxButtons b, MessageBoxIcon i) { return Add(text, caption, b); }
        public static DialogResult Show(IWin32Window o, string text) { return Add(text, "", MessageBoxButtons.OK); }
        public static DialogResult Show(IWin32Window o, string text, string caption) { return Add(text, caption, MessageBoxButtons.OK); }
        public static DialogResult Show(IWin32Window o, string text, string caption, MessageBoxButtons b) { return Add(text, caption, b); }
        public static DialogResult Show(IWin32Window o, string text, string caption, MessageBoxButtons b, MessageBoxIcon i) { return Add(text, caption, b); }
    }
}
