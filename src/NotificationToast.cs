// Aviso emergente (abajo a la derecha de la pantalla): solicitudes aceptadas o rechazadas, compras,
// cambios de rol… No roba el foco a lo que estés haciendo; se cierra solo a los 20 s (no mientras el
// ratón está encima), con la X o al hacer clic, que además lleva a la sección de Empresas que toca.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class NotificationToast : Form
    {
        readonly string _icon, _title, _body, _foot;
        readonly Color _accent;
        readonly Timer _life, _fade;
        Rectangle _hitClose;
        bool _hover, _closing;
        int _lifeLeftMs = 20000;
        public Action Opened;          // clic en el aviso
        public const int ToastW = 380;

        public NotificationToast(string icon, string title, string body, string foot, Color accent)
        {
            _icon = icon; _title = title ?? ""; _body = body ?? ""; _foot = foot ?? ""; _accent = accent;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
            Opacity = 0;
            Size = new Size(ToastW, MeasureHeight());
            using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), 12)) Region = new Region(p);

            _fade = new Timer { Interval = 15 };
            _fade.Tick += (s, e) =>
            {
                if (_closing) { Opacity = Math.Max(0, Opacity - 0.12); if (Opacity <= 0.01) { _fade.Stop(); Close(); } }
                else { Opacity = Math.Min(0.97, Opacity + 0.12); if (Opacity >= 0.97) _fade.Stop(); }
            };
            _life = new Timer { Interval = 250 };
            _life.Tick += (s, e) => { if (!_hover) { _lifeLeftMs -= 250; if (_lifeLeftMs <= 0) FadeOut(); } };
        }

        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*NOACTIVATE*/ | 0x00000080 /*TOOLWINDOW*/; return cp; }
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); _fade.Start(); _life.Start(); }

        public void FadeOut()
        {
            if (_closing) return;
            _closing = true; _life.Stop(); _fade.Start();
        }

        const int PadL = 62, PadR = 34, PadT = 12;

        int MeasureHeight()
        {
            using var fT = Theme.Font(10.5f, FontStyle.Bold);
            using var fB = Theme.Font(9.5f);
            using var fF = Theme.Font(8f);
            int w = ToastW - PadL - PadR;
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int h = PadT + TextRenderer.MeasureText(_title, fT, new Size(w, 999), flags).Height + 4
                  + TextRenderer.MeasureText(_body, fB, new Size(w, 999), flags).Height + 8
                  + TextRenderer.MeasureText("Ág", fF, new Size(w, 999), flags).Height + 12;
            return Math.Max(78, Math.Min(260, h));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rc = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);
            using (var p = Theme.Round(rc, 12))
            using (var pen = new Pen(Color.FromArgb(160, _accent), 1.4f)) g.DrawPath(pen, p);
            using (var b = new SolidBrush(_accent)) g.FillRectangle(b, 0, 10, 4, Height - 20);

            // Icono en una pastilla del color del aviso
            var ico = new Rectangle(16, 14, 34, 34);
            using (var p = Theme.Round(ico, 10)) using (var b = new SolidBrush(Color.FromArgb(55, _accent))) g.FillPath(b, p);
            using (var fI = new Font("Segoe UI Emoji", 14f * Theme.DpiComp))
                TextRenderer.DrawText(g, _icon, fI, ico, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            using var fT = Theme.Font(10.5f, FontStyle.Bold);
            using var fB = Theme.Font(9.5f);
            using var fF = Theme.Font(8f);
            int w = Width - PadL - PadR, y = PadT;
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
            int hT = TextRenderer.MeasureText(_title, fT, new Size(w, 999), flags).Height;
            TextRenderer.DrawText(g, _title, fT, new Rectangle(PadL, y, w, hT), Theme.Text, flags); y += hT + 4;
            int hB = TextRenderer.MeasureText(_body, fB, new Size(w, 999), flags).Height;
            TextRenderer.DrawText(g, _body, fB, new Rectangle(PadL, y, w, hB), Color.FromArgb(205, 210, 214), flags | TextFormatFlags.EndEllipsis); y += hB + 8;
            TextRenderer.DrawText(g, _foot, fF, new Rectangle(PadL, y, w, Height - y), Theme.Subtle, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            _hitClose = new Rectangle(Width - 28, 10, 16, 16);
            using (var pen = new Pen(_hover ? Theme.Text : Theme.Subtle, 1.6f))
            {
                g.DrawLine(pen, _hitClose.Left + 4, _hitClose.Top + 4, _hitClose.Right - 4, _hitClose.Bottom - 4);
                g.DrawLine(pen, _hitClose.Right - 4, _hitClose.Top + 4, _hitClose.Left + 4, _hitClose.Bottom - 4);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (!_hitClose.Contains(e.Location)) { try { Opened?.Invoke(); } catch { } }
            FadeOut();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _life.Stop(); _life.Dispose(); _fade.Stop(); _fade.Dispose(); } catch { }
            base.OnFormClosed(e);
        }
    }
}
