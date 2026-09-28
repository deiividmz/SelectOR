// Diálogo simple para pedir un texto (p. ej. el nombre de maquinista), con el tema oscuro.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public class TextPromptDialog : Form
    {
        public string Value { get; private set; } = "";
        readonly RoundedInput _input;

        public TextPromptDialog(string title, string label, string initial, string placeholder = "", string okText = null)
        {
            Text = title;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(460, 176);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label
            {
                Text = "  " + title, Dock = DockStyle.Top, Height = 42,
                Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface
            };

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 14, 18, 8) };
            // Un texto largo se parte en varias líneas y el diálogo crece lo necesario.
            int lblH = Math.Max(24, TextRenderer.MeasureText(label ?? "", Font, new Size(ClientSize.Width - 36, 1000), TextFormatFlags.WordBreak).Height + 6);
            if (lblH > 24) ClientSize = new Size(ClientSize.Width, ClientSize.Height + lblH - 24);
            var lbl = new Label { Text = label, Dock = DockStyle.Top, AutoSize = false, Height = lblH, ForeColor = Theme.Subtle };
            _input = new RoundedInput(placeholder) { Dock = DockStyle.Top, Height = 36 };
            _input.Box.Text = initial ?? "";
            body.Controls.Add(_input);
            body.Controls.Add(lbl);

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Bg, Padding = new Padding(18, 8, 18, 12) };
            var ok = new RoundButton { Text = okText ?? I18n.T("Guardar"), Width = 150, Height = 38, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            var cancel = new RoundButton { Text = I18n.T("Cancelar"), Width = 120, Height = 38, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f, Dock = DockStyle.Right };
            var spacer = new Panel { Width = 10, Dock = DockStyle.Right };
            ok.Click += (s, e) => { Value = (_input.Box.Text ?? "").Trim(); DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(ok); buttons.Controls.Add(spacer); buttons.Controls.Add(cancel);

            Controls.Add(body);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);
        }
    }
}
