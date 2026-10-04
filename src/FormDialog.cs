// Diálogo de formulario con el tema oscuro: varios campos (texto, contraseña, texto largo, lista) con su
// etiqueta, un texto de ayuda opcional y Aceptar/Cancelar. Validate devuelve un error (o null) y el
// diálogo no se cierra mientras haya error. Lo usan las normas, los préstamos y la recuperación de la
// contraseña.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class FormDialog : Form
    {
        readonly Panel _body;
        readonly Label _err;
        readonly Dictionary<string, Control> _fields = new Dictionary<string, Control>();
        readonly List<Control> _order = new List<Control>();
        const int W = 520;
        // Comprueba (y puede hacer la operación, asíncrona). Devuelve el error a enseñar o null para cerrar.
        public Func<FormDialog, Task<string>> Validate;
        readonly RoundButton _ok;

        public FormDialog(string title, string okText = null, int width = W)
        {
            Text = title;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(width, 124);   // cabecera + botones; cada campo suma su alto
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label
            {
                Text = "  " + title, Dock = DockStyle.Top, Height = 42,
                Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface
            };
            _body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 12, 18, 4) };
            _err = new Label { Dock = DockStyle.Bottom, Height = 0, ForeColor = Color.FromArgb(229, 115, 115), AutoSize = false, Padding = new Padding(18, 0, 18, 0) };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Bg, Padding = new Padding(18, 8, 18, 12) };
            _ok = new RoundButton { Text = okText ?? I18n.T("Guardar"), Width = 170, Height = 38, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            var cancel = new RoundButton { Text = I18n.T("Cancelar"), Width = 120, Height = 38, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f, Dock = DockStyle.Right };
            var spacer = new Panel { Width = 10, Dock = DockStyle.Right };
            _ok.Click += async (s, e) => await Accept();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(_ok); buttons.Controls.Add(spacer); buttons.Controls.Add(cancel);

            Controls.Add(_body);
            Controls.Add(_err);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);
            CancelButton = null;
        }

        bool _busy;
        async Task Accept()
        {
            if (_busy) return;
            _busy = true; _ok.Enabled = false;
            string err = null;
            try { err = Validate == null ? null : await Validate(this); }
            catch (Exception ex) { err = ex.Message; }
            _busy = false;
            if (IsDisposed) return;
            _ok.Enabled = true;
            if (err != null) { ShowError(err); return; }
            DialogResult = DialogResult.OK; Close();
        }

        public void ShowError(string text)
        {
            int h = string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, Font, new Size(ClientSize.Width - 36, 1000), TextFormatFlags.WordBreak).Height + 8;
            int dh = h - _err.Height;
            _err.Text = text ?? ""; _err.Height = h;
            if (dh != 0) ClientSize = new Size(ClientSize.Width, ClientSize.Height + dh);
        }

        void AddRow(Control c, int h)
        {
            c.Dock = DockStyle.Top; c.Height = h;
            _order.Add(c);
            _body.Controls.Add(c); c.BringToFront();
            ClientSize = new Size(ClientSize.Width, ClientSize.Height + h);
        }

        // Texto de ayuda (se puede cambiar después: deja sitio para dos líneas como mínimo).
        public Label AddInfo(string text, Color? color = null)
        {
            int h = Math.Max(TextRenderer.MeasureText(text, Font, new Size(ClientSize.Width - 36, 1000), TextFormatFlags.WordBreak).Height,
                             2 * TextRenderer.MeasureText("A", Font).Height) + 10;
            var l = new Label { Text = text, AutoSize = false, ForeColor = color ?? Theme.Subtle };
            AddRow(l, h);
            return l;
        }

        void AddLabel(string label) => AddRow(new Label { Text = label, AutoSize = false, ForeColor = Theme.Subtle, TextAlign = ContentAlignment.BottomLeft }, 24);

        public RoundedInput AddText(string key, string label, string value = "", string placeholder = "", bool password = false)
        {
            AddLabel(label);
            var inp = new RoundedInput(placeholder);
            inp.Box.Text = value ?? "";
            if (password) inp.Box.UseSystemPasswordChar = true;
            AddRow(inp, 36);
            AddRow(new Panel { BackColor = Theme.Bg }, 6);
            _fields[key] = inp.Box;
            return inp;
        }

        public TextBox AddMultiline(string key, string label, string value = "", int height = 140)
        {
            AddLabel(label);
            var host = new Panel { BackColor = Theme.Surface2, Padding = new Padding(8) };
            var tb = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Dock = DockStyle.Fill, Font = Theme.Font(10f), Text = value ?? "", AcceptsReturn = true };
            host.Controls.Add(tb);
            AddRow(host, height);
            AddRow(new Panel { BackColor = Theme.Bg }, 6);
            _fields[key] = tb;
            return tb;
        }

        public ComboBox AddCombo(string key, string label, IEnumerable<string> items, int selected = 0)
        {
            AddLabel(label);
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(10f) };
            foreach (var it in items) cb.Items.Add(it);
            if (cb.Items.Count > 0) cb.SelectedIndex = Math.Max(0, Math.Min(selected, cb.Items.Count - 1));
            var host = new Panel { BackColor = Theme.Bg };
            cb.Dock = DockStyle.Top;
            host.Controls.Add(cb);
            AddRow(host, 30);
            AddRow(new Panel { BackColor = Theme.Bg }, 6);
            _fields[key] = cb;
            return cb;
        }

        public string Get(string key) => _fields.TryGetValue(key, out var c) ? (c is ComboBox cb ? cb.SelectedIndex.ToString() : (c.Text ?? "").Trim()) : "";
        public int Index(string key) => _fields.TryGetValue(key, out var c) && c is ComboBox cb ? cb.SelectedIndex : -1;
        public Control Field(string key) => _fields.TryGetValue(key, out var c) ? c : null;
    }
}
