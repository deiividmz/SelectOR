// Conducción libre con MUCHOS trenes (un usuario con más de 10.000 .con veía «solo unos cuantos»): lo que no se ve tiene
// que decir por qué.
//  · Franja encima de la lista (TrainNotice): los trenes a los que les falta algún .eng o .wag no salen en «Todos» (van a
//    «⚠ Incompletos»): «8.900 trenes no salen aquí porque les falta algún .eng o .wag · Ver cuáles» (al pulsarla, esa
//    pestaña); con un filtro de tipo (viajeros, mercancías…) mientras se clasifican: «Clasificando trenes: 3.200 de
//    10.000… irán apareciendo».
//  · La primera lectura de los .con (sin índice en disco tarda: ~13 s con 12.000) cuenta: «Leyendo trenes: 4.500 de
//    12.000…» (FastConsists.Progress).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        TrainNotice _trainNotice;
        int _incompleteCount;
        Timer _consistsProgressTimer;

        // (al montar la lista de trenes) la franja, encima de la lista.
        void BuildTrainNotice(Control host)
        {
            _trainNotice = new TrainNotice { Dock = DockStyle.Top, Visible = false };
            _trainNotice.LinkClicked += () =>
            {
                // «Ver cuáles»: la pestaña «⚠ Incompletos»
                if (_trainWarnChip == null) return;
                typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(_trainWarnChip, new object[] { EventArgs.Empty });
            };
            host.Controls.Add(_trainNotice);
        }

        // Lo que dice la franja (o nada): después de rehacer la lista y en cada paso de la clasificación.
        void UpdateTrainNotice()
        {
            if (_trainNotice == null) return;
            string text = null, link = null;
            bool filtering = _trainKind >= 1 && _trainKind <= 4;
            if (filtering && _classTotal > 0 && _classDone < _classTotal)
                text = string.Format(Tr("Clasificando trenes: {0:N0} de {1:N0}… Con este filtro irán apareciendo; en «Todos» ya están todos."), _classDone, _classTotal);
            else if (_trainKind != 6 && _incompleteCount > 0)
            {
                text = _incompleteCount == 1 ? Tr("1 tren no sale aquí porque le falta algún .eng o .wag.")
                                             : string.Format(Tr("{0:N0} trenes no salen aquí porque les falta algún .eng o .wag."), _incompleteCount);
                link = Tr("Ver cuáles");
            }
            bool show = text != null && _consistsAll.Count > 0;
            if (_trainNotice.Text != (text ?? "") || _trainNotice.Link != link) { _trainNotice.Text = text ?? ""; _trainNotice.Link = link; _trainNotice.Invalidate(); }
            if (_trainNotice.Visible != show) _trainNotice.Visible = show;
        }

        // Primera lectura de los trenes: el «Cargando trenes…» cuenta los que lleva.
        void StartConsistsProgress()
        {
            FastConsists.ResetProgress();
            _consistsProgressTimer ??= new Timer { Interval = 250 };
            _consistsProgressTimer.Tick -= ConsistsProgressTick; _consistsProgressTimer.Tick += ConsistsProgressTick;
            _consistsProgressTimer.Start();
        }

        void ConsistsProgressTick(object s, EventArgs e)
        {
            if (!_consistsLoading) { _consistsProgressTimer?.Stop(); return; }
            int done = FastConsists.ProgressDone, total = FastConsists.ProgressTotal;
            if (total > 0) _consistsOverlay?.SetState(ListStatePanel.Mode.Loading, string.Format(Tr("Leyendo trenes: {0:N0} de {1:N0}…"), Math.Min(done, total), total));
        }

        // ---------------- la franja ----------------
        sealed class TrainNotice : Control
        {
            public string Link;
            public event Action LinkClicked;
            Rectangle _linkRect;
            public TrainNotice()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Bg; Height = Theme.Px(38); Padding = new Padding(0, 0, 0, Theme.Px(6));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
                var box = new Rectangle(0, 0, Width - 1, Height - Theme.Px(6) - 1);
                using (var p = Theme.Round(box, 8)) using (var b = new SolidBrush(Color.FromArgb(52, 47, 36))) g.FillPath(b, p);
                using (var bar = new SolidBrush(Color.FromArgb(255, 183, 77))) g.FillRectangle(bar, box.X, box.Y + 6, 3, box.Height - 12);
                using var f = Theme.Font(9f); using var fb = Theme.Font(9f, FontStyle.Bold | FontStyle.Underline);
                int x = box.X + Theme.Px(14);
                var tf = TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
                int lw = string.IsNullOrEmpty(Link) ? 0 : TextRenderer.MeasureText(g, Link, fb, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + Theme.Px(14);
                var tr = new Rectangle(x, box.Y, Math.Max(10, box.Right - x - lw - Theme.Px(10)), box.Height);
                TextRenderer.DrawText(g, Text, f, tr, Color.FromArgb(255, 213, 140), tf);
                _linkRect = Rectangle.Empty;
                if (lw > 0)
                {
                    int tw = Math.Min(tr.Width, TextRenderer.MeasureText(g, Text, f, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width);   // (sin EndEllipsis: con él mide 0)
                    _linkRect = new Rectangle(x + tw + Theme.Px(12), box.Y, lw, box.Height);
                    TextRenderer.DrawText(g, Link, fb, _linkRect, Color.FromArgb(255, 183, 77), tf);
                }
            }
            protected override void OnMouseMove(MouseEventArgs e) { Cursor = _linkRect.Contains(e.Location) || (!string.IsNullOrEmpty(Link)) ? Cursors.Hand : Cursors.Default; base.OnMouseMove(e); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && !string.IsNullOrEmpty(Link)) LinkClicked?.Invoke(); }
            public void ClickLink() => LinkClicked?.Invoke();   // (pruebas)
        }
    }
}
