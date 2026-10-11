// Aspecto común de las ventanas emergentes (el de «Programar servicio»): barra de título oscura, franja de colores,
// cabecera con el icono en un círculo, título y subtítulo, pie con una raya encima y los botones de la aplicación.
// Lo usan ThemedBox, FormDialog, TextPromptDialog, las ventanas de cuenta y el resto de diálogos.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SelectOR
{
    public static class DialogKit
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;

        // Colores del tema, el icono de la aplicación y la barra de título oscura (Windows 10/11).
        public static void Dress(Form f, bool colors = true)
        {
            if (colors) { f.BackColor = Theme.Bg; f.ForeColor = Theme.Text; }
            try { f.Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); } catch { }
            if (f.IsHandleCreated) DarkTitle(f.Handle);
            else f.HandleCreated += (s, e) => DarkTitle(f.Handle);
        }

        static int Ref(Color c) => c.R | (c.G << 8) | (c.B << 16);
        public static void DarkTitle(IntPtr h)
        {
            try
            {
                int on = 1; DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
                int cap = Ref(Theme.Surface); DwmSetWindowAttribute(h, DWMWA_CAPTION_COLOR, ref cap, 4);
                int txt = Ref(Theme.Text); DwmSetWindowAttribute(h, DWMWA_TEXT_COLOR, ref txt, 4);
                int brd = Ref(Color.FromArgb(58, 62, 66)); DwmSetWindowAttribute(h, DWMWA_BORDER_COLOR, ref brd, 4);
            }
            catch { }
        }

        // ---- botones ----
        public static RoundButton Primary(string text, int width = 170) => new RoundButton
        {
            Text = text, Width = Theme.Px(width), Height = Theme.Px(38), Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2,
            TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold
        };
        public static RoundButton Secondary(string text, int width = 130) => new RoundButton
        {
            Text = text, Width = Theme.Px(width), Height = Theme.Px(38), Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f
        };
        public static RoundButton Danger(string text, int width = 170) => new RoundButton
        {
            Text = text, Width = Theme.Px(width), Height = Theme.Px(38), Radius = 10, BaseColor = Color.FromArgb(170, 55, 55), HoverColor = Color.FromArgb(200, 70, 70), GradientTo = Color.FromArgb(140, 42, 42),
            TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold
        };
        // ancho a la medida del texto (como mínimo «min»)
        public static RoundButton Fit(RoundButton b, int min = 110)
        {
            using var f = Theme.Font(10.5f, FontStyle.Bold);
            b.Width = Math.Max(Theme.Px(min), TextRenderer.MeasureText(b.Text, f).Width + Theme.Px(44));
            return b;
        }

        // Desplegable del tema (ThemeCombo) con sus elementos dibujados a juego.
        public static ThemeCombo Combo()
        {
            var c = new ThemeCombo { Font = Theme.Font(9.75f), ItemHeight = Theme.Px(24), BackColor = Theme.Surface2, ForeColor = Theme.Text, MaxDropDownItems = 14 };
            c.DrawItem += DrawComboItem;
            return c;
        }
        public static void DrawComboItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || sender is not ComboBox c) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, c.GetItemText(c.Items[e.Index]), c.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                sel ? Color.White : Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }

        // ---- cabecera: icono en un círculo, título y subtítulo ----
        public sealed class Header : Control
        {
            string _title = "", _sub = "", _icon = "🚆";
            public string Title { get => _title; set { _title = value ?? ""; Invalidate(); } }
            public string Sub { get => _sub; set { bool had = _sub.Length > 0; _sub = value ?? ""; if (had != _sub.Length > 0) Height = Theme.Px(_sub.Length > 0 ? 74 : 62); Invalidate(); } }
            public string Icon { get => _icon; set { _icon = value ?? ""; Invalidate(); } }
            public override string Text { get => _title; set { _title = (value ?? "").Trim(); Invalidate(); } }   // (como la etiqueta de antes)
            public Color IconColor = Theme.Accent;
            public bool Glyph;   // el icono es una letra (?, !, ×, i) y no un emoji
            readonly Font _fT = Theme.Font(14.5f, FontStyle.Bold), _fS = Theme.Font(9.25f), _fI = EmojiPicker.EmojiFont(16f), _fG = Theme.Font(15f, FontStyle.Bold);

            public Header(string title, string sub = null, string icon = null, Color? color = null)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Dock = DockStyle.Top;
                _title = title ?? ""; _sub = sub ?? ""; if (icon != null) _icon = icon;
                if (color != null) IconColor = color.Value;
                Height = Theme.Px(_sub.Length > 0 ? 74 : 62);
            }
            protected override void Dispose(bool disposing) { if (disposing) { _fT.Dispose(); _fS.Dispose(); _fI.Dispose(); _fG.Dispose(); } base.Dispose(disposing); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                using (var b = new LinearGradientBrush(ClientRectangle, Theme.Surface, Theme.Bg, LinearGradientMode.Vertical)) g.FillRectangle(b, ClientRectangle);
                int d = Theme.Px(_sub.Length > 0 ? 44 : 38), x = Theme.Px(24), cy = Height / 2;
                if (_icon.Length > 0)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(40, IconColor))) g.FillEllipse(b, x, cy - d / 2, d, d);
                    using (var p = new Pen(Color.FromArgb(Glyph ? 230 : 150, IconColor), Glyph ? 2f : 1.5f)) g.DrawEllipse(p, x, cy - d / 2, d, d);
                    g.SmoothingMode = SmoothingMode.None;
                    TextRenderer.DrawText(g, _icon, Glyph ? _fG : _fI, new Rectangle(x, cy - d / 2, d, d), Glyph ? IconColor : Theme.Text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += d + Theme.Px(16);
                }
                const TextFormatFlags L = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
                if (_sub.Length == 0)
                    TextRenderer.DrawText(g, _title, _fT, new Rectangle(x, 0, Width - x - Theme.Px(20), Height), Theme.Text, L);
                else
                {
                    TextRenderer.DrawText(g, _title, _fT, new Rectangle(x, cy - Theme.Px(24), Width - x - Theme.Px(20), Theme.Px(28)), Theme.Text, L);
                    TextRenderer.DrawText(g, _sub, _fS, new Rectangle(x, cy + Theme.Px(4), Width - x - Theme.Px(20), Theme.Px(20)), Theme.Subtle, L);
                }
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
            }
        }

        // Pie con el aspecto común para un panel de botones que ya existe (fondo de tarjeta y raya encima).
        public static void StyleFooter(Panel p)
        {
            p.BackColor = Theme.Surface; p.Height = Theme.Px(66);
            p.Padding = new Padding(Theme.Px(24), Theme.Px(14), Theme.Px(24), Theme.Px(14));
            p.Paint += (s, e) => { using var pen = new Pen(Theme.Border); e.Graphics.DrawLine(pen, 0, 0, p.Width, 0); };
        }

        // Franja y cabecera encima de una ventana que no las tenía: el contenido que no está anclado baja lo que ocupan.
        public static Header Crown(Form f, string title, string sub = null, string icon = null, Color? color = null)
        {
            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var head = new Header(title, sub, icon, color);
            int dh = head.Height + stripe.Height;
            foreach (Control c in f.Controls) if (c.Dock == DockStyle.None) c.Top += dh;
            f.Controls.Add(head); f.Controls.Add(stripe);
            if (!f.AutoSize) f.ClientSize = new Size(f.ClientSize.Width, f.ClientSize.Height + dh);
            return head;
        }

        // Rótulo de paso: número en un círculo y el título en versalitas.
        public sealed class StepLabel : Label
        {
            public int N;
            readonly Font _fN = Theme.Font(8f, FontStyle.Bold), _fT = Theme.Font(8.25f, FontStyle.Bold);
            public StepLabel() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); }
            protected override void Dispose(bool disposing) { if (disposing) { _fN.Dispose(); _fT.Dispose(); } base.Dispose(disposing); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg);
                int d = Theme.Px(20);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Accent)) g.FillEllipse(b, 0, (Height - d) / 2, d, d);
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, N.ToString(), _fN, new Rectangle(0, (Height - d) / 2, d, d), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                int tx = d + Theme.Px(10);
                int tw = TextRenderer.MeasureText(Text, _fT, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, Text, _fT, new Rectangle(tx, 0, Width - tx, Height), Theme.AccentHi, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, tx + tw + Theme.Px(12), Height / 2, Width, Height / 2);
            }
        }

        // ---- pie: fondo de tarjeta, raya encima y los botones a la derecha ----
        public sealed class Footer : Panel
        {
            public Footer()
            {
                Dock = DockStyle.Bottom; Height = Theme.Px(66); BackColor = Theme.Surface;
                Padding = new Padding(Theme.Px(24), Theme.Px(14), Theme.Px(24), Theme.Px(14));
                SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using var p = new Pen(Theme.Border); e.Graphics.DrawLine(p, 0, 0, Width, 0);
            }
            // de derecha a izquierda: el primero queda pegado a la derecha (el principal)
            public void AddRight(params Control[] buttons)
            {
                for (int i = 0; i < buttons.Length; i++)
                {
                    if (i > 0) { var sp = new Panel { Width = Theme.Px(10), Dock = DockStyle.Right, BackColor = Color.Transparent }; Controls.Add(sp); sp.BringToFront(); }
                    var b = buttons[i]; b.Dock = DockStyle.Right; Controls.Add(b); b.BringToFront();
                }
            }
            // a la izquierda (p. ej. un botón secundario o un texto)
            public void AddLeft(Control c) { c.Dock = DockStyle.Left; Controls.Add(c); c.BringToFront(); }
        }
    }
}
