using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ACladPlugin
{
    /// <summary>
    /// 26.09 (Герман, сборка №25: «видеть прогресс раскладки и понимать время,
    /// которое нужно программе»). Окно хода ATTILE: сколько плиток уложено, сколько
    /// прошло и сколько примерно осталось; Esc или «Прервать» — остановить (всё
    /// созданное удаляется, прежняя раскладка цела). Чистый WinForms без AutoCAD —
    /// проверяется под mono (tools/attile_ui/ProgressCheck.cs).
    /// Отрисовка — синхронно (Refresh) внутри пачки; сообщения окна (кнопка, Esc)
    /// разбираются только МЕЖДУ пачками — Pump() вызывается вне транзакций.
    /// </summary>
    internal sealed class TileProgressForm : Form
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private readonly Label _count = new Label();
        private readonly Label _time = new Label();
        private readonly Label _hint = new Label();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly Button _stop = new Button();
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        private readonly int _total;
        private long _lastPaint = -1000000;

        public bool CancelRequested { get; private set; }

        public TileProgressForm(string title, int total)
        {
            _total = Math.Max(1, total);
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            ControlBox = false;             // закрыть можно только «Прервать»/Esc
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            ClientSize = new Size(460, 122);
            _count.SetBounds(12, 10, 436, 20);
            _bar.SetBounds(12, 34, 436, 18);
            _bar.Minimum = 0;
            _bar.Maximum = _total;
            _time.SetBounds(12, 60, 436, 20);
            _hint.SetBounds(12, 94, 344, 20);
            _hint.ForeColor = SystemColors.GrayText;
            _hint.Text = "Esc или «Прервать» — остановить, чертёж не изменится";
            _stop.Text = "Прервать";
            _stop.SetBounds(362, 88, 86, 26);
            _stop.Click += delegate { RequestCancel(); };
            KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) RequestCancel(); };
            Controls.AddRange(new Control[] { _count, _bar, _time, _hint, _stop });
            Report(0, true);
        }

        /// <summary>Обновить текст и полосу; не чаще 4 раз в секунду (force — сразу).</summary>
        public void Report(int done, bool force)
        {
            long ms = _sw.ElapsedMilliseconds;
            if (!force && ms - _lastPaint < 250) return;
            _lastPaint = ms;
            done = Math.Max(0, Math.Min(done, _total));
            _count.Text = CountText(done, _total);
            _bar.Value = done;
            _time.Text = "Прошло " + Clock(ms) + " · " + Eta(ms, done, _total);
            if (Visible) Refresh();
        }

        /// <summary>Разобрать сообщения окна (кнопка, Esc). Только вне транзакций.</summary>
        public void Pump()
        {
            try { Application.DoEvents(); } catch { }
        }

        public void RequestCancel()
        {
            CancelRequested = true;
            _stop.Enabled = false;
            _hint.Text = "Останавливаю — удаляю уложенное…";
            if (Visible) Refresh();
        }

        internal static string N(long v) { return v.ToString("#,0", Ru); }

        internal static string CountText(int done, int total)
        {
            return "Уложено " + N(done) + " из " + N(total) + " (" + (done * 100L / Math.Max(1, total)) + " %)";
        }

        internal static string Clock(long ms)
        {
            long t = ms / 1000;
            return t >= 3600
                ? (t / 3600) + ":" + ((t % 3600) / 60).ToString("00") + ":" + (t % 60).ToString("00")
                : (t / 60) + ":" + (t % 60).ToString("00");
        }

        /// <summary>Оценка остатка по средней скорости с начала отрисовки.</summary>
        internal static string Eta(long ms, int done, int total)
        {
            if (done >= total) return "почти готово";
            if (done <= 0 || (ms < 1500 && done * 50L < total)) return "оцениваю время…";
            double rest = ms / 1000.0 * (total - done) / done;
            if (rest < 60) return "осталось меньше минуты";
            if (rest < 3600) return "осталось около " + (long)Math.Ceiling(rest / 60) + " мин";
            return "осталось около " + (rest / 3600).ToString("0.#", Ru) + " ч";
        }
    }
}
