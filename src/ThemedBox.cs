// Mensajes y preguntas con el aspecto de SelectOR (en lugar del cuadro de mensaje de Windows): la franja de colores, la
// cabecera, un icono redondo según el tipo, el texto y los botones de la aplicación. Se usa igual que MessageBox.Show.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SelectOR
{
    public static class ThemedBox
    {
        // Para las pruebas: si devuelve una respuesta, no se enseña nada (texto, título, botones).
        public static Func<string, string, MessageBoxButtons, DialogResult?> AutoAnswer;

        public static DialogResult Show(string text) => Show(null, text, "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.None);
        public static DialogResult Show(string text, string caption) => Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons) => Show(null, text, caption, buttons, MessageBoxIcon.None);
        public static DialogResult Show(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(null, text, caption, buttons, icon);
        public static DialogResult Show(IWin32Window owner, string text) => Show(owner, text, "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.None);
        public static DialogResult Show(IWin32Window owner, string text, string caption) => Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons) => Show(owner, text, caption, buttons, MessageBoxIcon.None);
        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def)
            => Show(owner, text, caption, buttons, icon, def == MessageBoxDefaultButton.Button2 ? 1 : def == MessageBoxDefaultButton.Button3 ? 2 : 0);

        public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, int defaultButton = 0)
        {
            var auto = AutoAnswer?.Invoke(text ?? "", caption ?? "", buttons);
            if (auto != null) return auto.Value;
            using var f = new BoxForm(text ?? "", caption, buttons, icon, defaultButton);
            if (owner == null) { f.StartPosition = FormStartPosition.CenterScreen; f.ShowInTaskbar = true; }
            return f.ShowDialog(owner);
        }

        sealed class BoxForm : Form
        {
            readonly Color _iconColor; readonly string _glyph;
            public BoxForm(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, int defaultButton)
            {
                // el título: el que se pide o, si es el genérico, según el tipo de mensaje
                string title = string.IsNullOrWhiteSpace(caption) || caption == "SelectOR" || caption == "Selector de Trenes y Rutas"
                    ? icon switch
                    {
                        MessageBoxIcon.Question => I18n.T("Confirmar"),
                        MessageBoxIcon.Warning => I18n.T("Atención"),
                        MessageBoxIcon.Error => I18n.T("Error"),
                        _ => "SelectOR",
                    }
                    : caption;
                (_iconColor, _glyph) = icon switch
                {
                    MessageBoxIcon.Question => (Color.FromArgb(120, 144, 226), "?"),
                    MessageBoxIcon.Warning => (Color.FromArgb(232, 178, 80), "!"),
                    MessageBoxIcon.Error => (Color.FromArgb(229, 115, 115), "×"),
                    _ => (Theme.Accent, "i"),
                };
                Text = title;
                BackColor = Theme.Bg; ForeColor = Theme.Text; Font = Theme.Font(10f);
                StartPosition = FormStartPosition.CenterParent;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false; KeyPreview = true;
                try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); } catch { }

                var stripe = new LiveryStripe { Dock = DockStyle.Top };
                var header = new Label { Text = "  " + title, Dock = DockStyle.Top, Height = 42, Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };
                // cuerpo: el icono y el texto
                var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Padding = new Padding(20, 18, 22, 8) };
                body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                var ic = new Panel { Width = 40, Height = 40, BackColor = Theme.Bg, Margin = new Padding(0, 2, 0, 0) };
                ic.Paint += (s, e) =>
                {
                    var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(Color.FromArgb(55, _iconColor))) g.FillEllipse(b, 0, 0, 39, 39);
                    using (var p = new Pen(_iconColor, 2f)) g.DrawEllipse(p, 1, 1, 37, 37);
                    using var fi = Theme.Font(15f, FontStyle.Bold);
                    TextRenderer.DrawText(g, _glyph, fi, new Rectangle(0, 0, 40, 40), _iconColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                };
                body.Controls.Add(ic, 0, 0);
                var msg = new Label { Text = text, AutoSize = true, MaximumSize = new Size(Theme.Px(470), 0), ForeColor = Theme.Text, Font = Theme.Font(10.25f), Margin = new Padding(0, 4, 0, 0), UseMnemonic = false };
                body.Controls.Add(msg, 1, 0);

                // botones, a la derecha (el primero, el principal)
                var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 62, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Theme.Bg, Padding = new Padding(18, 8, 18, 14) };
                var list = buttons switch
                {
                    MessageBoxButtons.OKCancel => new[] { (I18n.T("Aceptar"), DialogResult.OK), (I18n.T("Cancelar"), DialogResult.Cancel) },
                    MessageBoxButtons.YesNo => new[] { (I18n.T("Sí"), DialogResult.Yes), (I18n.T("No"), DialogResult.No) },
                    MessageBoxButtons.YesNoCancel => new[] { (I18n.T("Sí"), DialogResult.Yes), (I18n.T("No"), DialogResult.No), (I18n.T("Cancelar"), DialogResult.Cancel) },
                    MessageBoxButtons.RetryCancel => new[] { (I18n.T("Reintentar"), DialogResult.Retry), (I18n.T("Cancelar"), DialogResult.Cancel) },
                    MessageBoxButtons.AbortRetryIgnore => new[] { (I18n.T("Anular"), DialogResult.Abort), (I18n.T("Reintentar"), DialogResult.Retry), (I18n.T("Omitir"), DialogResult.Ignore) },
                    _ => new[] { (I18n.T("Aceptar"), DialogResult.OK) },
                };
                var made = new List<RoundButton>();
                for (int k = 0; k < list.Length; k++)
                {
                    var (t, r) = list[k];
                    bool primary = k == 0;
                    var b = new RoundButton
                    {
                        Text = t, Height = 38, Radius = 10, FontSize = 10.5f, FontStyle = primary ? FontStyle.Bold : FontStyle.Regular,
                        BaseColor = primary ? (icon == MessageBoxIcon.Warning && buttons != MessageBoxButtons.OK ? Color.FromArgb(150, 60, 60) : Theme.Accent) : Theme.Surface2,
                        HoverColor = primary ? (icon == MessageBoxIcon.Warning && buttons != MessageBoxButtons.OK ? Color.FromArgb(180, 72, 72) : Theme.AccentHi) : Theme.SurfaceHi,
                        TextColor = primary ? Color.White : Theme.Text, Margin = new Padding(10, 0, 0, 0)
                    };
                    using (var fb = Theme.Font(10.5f, FontStyle.Bold)) b.Width = Math.Max(110, TextRenderer.MeasureText(t, fb).Width + 44);
                    b.Click += (s, e) => { DialogResult = r; Close(); };
                    made.Add(b);
                }
                for (int k = made.Count - 1; k >= 0; k--) bar.Controls.Add(made[k]);   // de derecha a izquierda: el principal a la derecha
                Controls.Add(body); Controls.Add(bar); Controls.Add(header); Controls.Add(stripe);

                // teclado: Intro, el botón por defecto; Esc, cancelar (o «No», o «Aceptar» si es el único)
                int di = Math.Max(0, Math.Min(made.Count - 1, defaultButton));
                var def = made[di]; var defResult = list[di].Item2;
                DialogResult esc = Array.Exists(list, x => x.Item2 == DialogResult.Cancel) ? DialogResult.Cancel
                                 : Array.Exists(list, x => x.Item2 == DialogResult.No) ? DialogResult.No : list[0].Item2;
                KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Escape) { DialogResult = esc; Close(); }
                    else if (e.KeyCode == Keys.Enter) { DialogResult = defResult; Close(); }
                };
                Shown += (s, e) => def.Focus();

                // a la medida del texto
                int w = Math.Max(Theme.Px(420), Math.Min(Theme.Px(600), msg.PreferredSize.Width + 56 + 42 + 20));
                int bodyH = Math.Max(40, msg.GetPreferredSize(new Size(Theme.Px(470), 0)).Height) + 26 + 10;
                ClientSize = new Size(w, 6 + 42 + bodyH + 62);
            }
        }
    }
}
