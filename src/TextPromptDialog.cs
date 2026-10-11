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

        public TextPromptDialog(string title, string label, string initial, string placeholder = "", string okText = null, string icon = "✏️")
        {
            Text = title;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            DialogKit.Dress(this);

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new DialogKit.Header(title, null, icon);
            int W = Theme.Px(480), pad = Theme.Px(24);
            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(pad, Theme.Px(16), pad, Theme.Px(14)) };
            // Un texto largo se parte en varias líneas y el diálogo crece lo necesario.
            int lblH = Math.Max(Theme.Px(24), TextRenderer.MeasureText(label ?? "", Font, new Size(W - 2 * pad, 1000), TextFormatFlags.WordBreak).Height + Theme.Px(8));
            var lbl = new Label { Text = label, Dock = DockStyle.Top, AutoSize = false, Height = lblH, ForeColor = Theme.Subtle };
            _input = new RoundedInput(placeholder) { Dock = DockStyle.Top, Height = Theme.Px(36) };
            _input.Box.Text = initial ?? "";
            body.Controls.Add(_input);
            body.Controls.Add(lbl);

            var buttons = new DialogKit.Footer();
            var ok = DialogKit.Fit(DialogKit.Primary(okText ?? I18n.T("Guardar")), 150);
            var cancel = DialogKit.Secondary(I18n.T("Cancelar"));
            ok.Click += (s, e) => { Value = (_input.Box.Text ?? "").Trim(); DialogResult = DialogResult.OK; Close(); };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            // Intro acepta y Esc cancela (como un diálogo normal)
            _input.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Value = (_input.Box.Text ?? "").Trim(); DialogResult = DialogResult.OK; Close(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; DialogResult = DialogResult.Cancel; Close(); }
            };
            buttons.AddRight(ok, cancel);

            Controls.Add(body);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);
            ClientSize = new Size(W, stripe.Height + header.Height + body.Padding.Vertical + lblH + _input.Height + buttons.Height);
        }
    }
}
