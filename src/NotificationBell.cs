// Campanita de notificaciones (junto al nombre del perfil, en Empresas) y la ventana que despliega con los
// avisos de los últimos 60 días (notificaciones-campana.sql). El número rojo son los avisos sin leer; al abrir
// la ventana se dan todos por leídos. Cada aviso lleva a su sección de Empresas, igual que su ventana emergente.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public class BellButton : Control
    {
        int _count;
        bool _hover;
        public int Count { get => _count; set { if (_count != value) { _count = Math.Max(0, value); Invalidate(); } } }

        public BellButton()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            Size = new Size(Theme.Px(34), Theme.Px(34));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Parent?.BackColor ?? Theme.Surface)) g.FillRectangle(b, ClientRectangle);
            int s = Math.Min(Width, Height);
            var c = new RectangleF((Width - s) / 2f + 1, (Height - s) / 2f + 1, s - 2, s - 2);
            if (_hover) using (var b = new SolidBrush(Theme.SurfaceHi)) g.FillEllipse(b, c);

            // la campana: cúpula, faldón, badajo
            float cx = c.X + c.Width / 2f, top = c.Y + c.Height * 0.24f, bot = c.Y + c.Height * 0.68f, half = c.Width * 0.22f;
            using (var path = new GraphicsPath())
            {
                path.AddBezier(cx - half, bot, cx - half, bot - c.Height * 0.12f, cx - half * 0.95f, top, cx, top);
                path.AddBezier(cx, top, cx + half * 0.95f, top, cx + half, bot - c.Height * 0.12f, cx + half, bot);
                path.AddLine(cx + half, bot, cx + half * 1.3f, bot + c.Height * 0.05f);
                path.AddLine(cx + half * 1.3f, bot + c.Height * 0.05f, cx - half * 1.3f, bot + c.Height * 0.05f);
                path.CloseFigure();
                var col = _count > 0 || _hover ? Theme.Text : Blend(Theme.Text, Theme.Surface, 0.3f);
                using var pen = new Pen(col, Math.Max(1.4f, c.Width / 18f)) { LineJoin = LineJoin.Round };
                g.DrawPath(pen, path);
                float r = c.Width * 0.06f;
                using (var b = new SolidBrush(col)) g.FillEllipse(b, cx - r, bot + c.Height * 0.08f, r * 2, r * 2);
                g.DrawLine(pen, cx, top - c.Height * 0.06f, cx, top);
            }
            // número de avisos sin leer
            if (_count > 0)
            {
                string t = _count > 9 ? "9+" : _count.ToString();
                using var f = Theme.Font(7f, FontStyle.Bold);
                var sz = TextRenderer.MeasureText(t, f, Size.Empty, TextFormatFlags.NoPadding);
                int h = Theme.Px(15), w = Math.Max(h, sz.Width + Theme.Px(7));
                var br = new Rectangle((int)(c.Right - w + Theme.Px(2)), (int)c.Y, w, h);
                using (var p = Theme.Round(br, h / 2)) using (var b = new SolidBrush(Color.FromArgb(220, 60, 60))) g.FillPath(b, p);
                TextRenderer.DrawText(g, t, f, br, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        static Color Blend(Color a, Color b, float t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    // Un aviso de la lista.
    public sealed class NotifItem
    {
        public string Icon = "🔔", Title = "", Body = "", Foot = "";
        public Color Accent = Color.FromArgb(96, 165, 250);
        public bool Unread, Resolved;
        public Action Open;
    }

    public class NotificationsPanel : Form
    {
        readonly Panel _list;
        readonly Label _state;
        string _sub = "";
        public string Title = "Notificaciones", ResolvedText = "Resuelta";

        public NotificationsPanel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Surface;
            KeyPreview = true;
            DoubleBuffered = true;
            Size = new Size(Theme.Px(400), Theme.Px(480));
            Padding = new Padding(1, Theme.Px(50), 1, Theme.Px(8));
            _list = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface };
            Native.UseDarkScrollBars(_list);
            _state = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Subtle, Font = Theme.Font(9.5f), BackColor = Theme.Surface, Visible = false };
            Controls.Add(_list); Controls.Add(_state);
            using (var p = Theme.Round(new Rectangle(0, 0, Width, Height), 12)) Region = new Region(p);
        }

        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ClassStyle |= 0x20000; return cp; } }   // sombra

        public void ShowState(string text) { _list.Visible = false; _state.Text = text; _state.Visible = true; }

        public void SetItems(IList<NotifItem> items, string sub)
        {
            _sub = sub ?? ""; Invalidate();
            _list.SuspendLayout();
            foreach (Control c in _list.Controls) c.Dispose();
            _list.Controls.Clear();
            for (int i = items.Count - 1; i >= 0; i--)   // Dock Top: el último añadido queda arriba
            {
                var it = new NotifItemView(items[i], ResolvedText, i < items.Count - 1) { Dock = DockStyle.Top };
                it.Clicked += () => { Close(); try { items[it.Index].Open?.Invoke(); } catch { } };
                it.Index = i;
                _list.Controls.Add(it);
            }
            _list.ResumeLayout();
            _state.Visible = false; _list.Visible = true;
            foreach (Control c in _list.Controls) ((NotifItemView)c).Fit(_list.ClientSize.Width);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); if (_list != null) foreach (Control c in _list.Controls) ((NotifItemView)c).Fit(_list.ClientSize.Width); }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); BeginInvoke((Action)Close); }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) { if (keyData == Keys.Escape) { Close(); return true; } return base.ProcessCmdKey(ref msg, keyData); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);
            using var fT = Theme.Font(11f, FontStyle.Bold); using var fS = Theme.Font(8.5f);
            TextRenderer.DrawText(g, Title, fT, new Rectangle(Theme.Px(16), 0, Width - Theme.Px(32), Theme.Px(48)), Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, _sub, fS, new Rectangle(Theme.Px(16), 0, Width - Theme.Px(32), Theme.Px(48)), Theme.Subtle, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            using (var pen = new Pen(Color.FromArgb(50, 255, 255, 255))) g.DrawLine(pen, Theme.Px(12), Theme.Px(48), Width - Theme.Px(12), Theme.Px(48));
            using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 12)) using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1.2f)) g.DrawPath(pen, p);
        }

        // Una fila de la lista: pastilla del color del aviso con su icono, título, texto (hasta 3 líneas) y pie.
        sealed class NotifItemView : Control
        {
            readonly NotifItem _n; readonly string _resolved; readonly bool _line;
            bool _hover;
            public int Index;
            public event Action Clicked;
            static readonly TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;
            static int PadL => Theme.Px(62);
            static int PadR => Theme.Px(16);

            public NotifItemView(NotifItem n, string resolved, bool line)
            {
                _n = n; _resolved = resolved; _line = line;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                Height = Theme.Px(80);
            }

            public void Fit(int width)
            {
                if (width <= 0) return;
                int w = width - PadL - PadR;
                using var fT = Theme.Font(9.75f, FontStyle.Bold); using var fB = Theme.Font(9f); using var fF = Theme.Font(8f);
                int hT = TextRenderer.MeasureText(_n.Title, fT, new Size(w, 999), Wrap).Height;
                int hB = Math.Min(TextRenderer.MeasureText(_n.Body, fB, new Size(w, 999), Wrap).Height, fB.Height * 3);
                Height = Theme.Px(12) + hT + Theme.Px(3) + hB + Theme.Px(6) + fF.Height + Theme.Px(12);
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) Clicked?.Invoke(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                var bg = _hover ? Theme.SurfaceHi : _n.Unread ? Color.FromArgb(Theme.Surface.R + 6, Theme.Surface.G + 9, Theme.Surface.B + 14) : Theme.Surface;
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);
                bool dim = _n.Resolved && !_n.Unread;
                if (_n.Unread) using (var b = new SolidBrush(Color.FromArgb(96, 165, 250))) g.FillEllipse(b, Theme.Px(6), Theme.Px(24), Theme.Px(7), Theme.Px(7));
                var ico = new Rectangle(Theme.Px(18), Theme.Px(12), Theme.Px(34), Theme.Px(34));
                using (var p = Theme.Round(ico, Theme.Px(10))) using (var b = new SolidBrush(Color.FromArgb(dim ? 30 : 55, _n.Accent))) g.FillPath(b, p);
                using (var fI = new Font("Segoe UI Emoji", 13f * Theme.DpiComp))
                    TextRenderer.DrawText(g, _n.Icon, fI, ico, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                using var fT = Theme.Font(9.75f, FontStyle.Bold); using var fB = Theme.Font(9f); using var fF = Theme.Font(8f);
                int w = Width - PadL - PadR, y = Theme.Px(12);
                int hT = TextRenderer.MeasureText(_n.Title, fT, new Size(w, 999), Wrap).Height;
                TextRenderer.DrawText(g, _n.Title, fT, new Rectangle(PadL, y, w, hT), dim ? Theme.Subtle : Theme.Text, Wrap); y += hT + Theme.Px(3);
                int hB = Math.Min(TextRenderer.MeasureText(_n.Body, fB, new Size(w, 999), Wrap).Height, fB.Height * 3);
                TextRenderer.DrawText(g, _n.Body, fB, new Rectangle(PadL, y, w, hB), dim ? Theme.Subtle : Color.FromArgb(205, 210, 214), Wrap); y += hB + Theme.Px(6);
                int x = PadL;
                if (_n.Resolved)
                {
                    var sz = TextRenderer.MeasureText(_resolved, fF, Size.Empty, TextFormatFlags.NoPadding);
                    var tag = new Rectangle(x, y - 1, sz.Width + Theme.Px(10), fF.Height + 2);
                    using (var p = Theme.Round(tag, Theme.Px(6))) using (var b = new SolidBrush(Color.FromArgb(40, 76, 175, 80))) g.FillPath(b, p);
                    TextRenderer.DrawText(g, _resolved, fF, tag, Color.FromArgb(129, 199, 132), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x = tag.Right + Theme.Px(8);
                }
                TextRenderer.DrawText(g, _n.Foot, fF, new Rectangle(x, y, Width - x - PadR, fF.Height + 2), Theme.Subtle, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (_line) using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255))) g.DrawLine(pen, PadL, Height - 1, Width - PadR, Height - 1);
            }
        }
    }
}
