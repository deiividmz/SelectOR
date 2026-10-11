// Diálogo de formulario con el tema oscuro: varios campos (texto, contraseña, texto largo, lista) con su
// etiqueta, un texto de ayuda opcional y Aceptar/Cancelar. Validate devuelve un error (o null) y el
// diálogo no se cierra mientras haya error. Lo usan las normas, los préstamos y la recuperación de la
// contraseña.

using System;
using System.Collections.Generic;
using System.Linq;
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

        public readonly DialogKit.Header Head;   // cabecera (icono y subtítulo se pueden cambiar)

        public FormDialog(string title, string okText = null, int width = W, string icon = "📝", string sub = null)
        {
            Text = title;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            DialogKit.Dress(this);

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = Head = new DialogKit.Header(title, sub, icon);
            _body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(24), Theme.Px(14), Theme.Px(24), Theme.Px(6)) };
            _err = new Label { Dock = DockStyle.Bottom, Height = 0, ForeColor = Color.FromArgb(229, 115, 115), AutoSize = false, Padding = new Padding(Theme.Px(24), 0, Theme.Px(24), 0) };

            var buttons = new DialogKit.Footer();
            _ok = DialogKit.Primary(okText ?? I18n.T("Guardar")); DialogKit.Fit(_ok, 170);
            var cancel = DialogKit.Secondary(I18n.T("Cancelar"));
            _ok.Click += async (s, e) => await Accept();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.AddRight(_ok, cancel);
            ClientSize = new Size(Theme.Px(width), stripe.Height + header.Height + buttons.Height + _body.Padding.Vertical);   // cada campo suma su alto

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
            int h = string.IsNullOrEmpty(text) ? 0 : TextRenderer.MeasureText(text, Font, new Size(ClientSize.Width - Theme.Px(48), 1000), TextFormatFlags.WordBreak).Height + 8;
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
            int h = Math.Max(TextRenderer.MeasureText(text, Font, new Size(ClientSize.Width - Theme.Px(48), 1000), TextFormatFlags.WordBreak).Height,
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
            var host = new Card { Fill = Theme.Surface2, BorderColor = Theme.Border, Radius = 9, Padding = new Padding(10, 8, 6, 8) };
            var tb = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Dock = DockStyle.Fill, Font = Theme.Font(10f), Text = value ?? "", AcceptsReturn = true };
            host.Controls.Add(tb);
            Native.UseDarkScrollBars(tb);
            AddRow(host, height);
            AddRow(new Panel { BackColor = Theme.Bg }, 6);
            _fields[key] = tb;
            return tb;
        }

        public ComboBox AddCombo(string key, string label, IEnumerable<string> items, int selected = 0)
        {
            AddLabel(label);
            var cb = DialogKit.Combo();
            foreach (var it in items) cb.Items.Add(it);
            if (cb.Items.Count > 0) cb.SelectedIndex = Math.Max(0, Math.Min(selected, cb.Items.Count - 1));
            var host = new Panel { BackColor = Theme.Bg };
            cb.Dock = DockStyle.Top;
            host.Controls.Add(cb);
            AddRow(host, Math.Max(30, cb.Height + 2));
            AddRow(new Panel { BackColor = Theme.Bg }, 6);
            _fields[key] = cb;
            return cb;
        }

        // Lista con casillas (p. ej. las empresas autorizadas de una sala). Checked(key) da los índices marcados.
        public CheckedListBox AddChecklist(string key, string label, IEnumerable<string> items, ICollection<int> marked, int height = 130)
        {
            AddLabel(label);
            var host = new Card { Fill = Theme.Surface2, BorderColor = Theme.Border, Radius = 9, Padding = new Padding(8, 6, 6, 6) };
            var cl = new CheckedListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Surface2, ForeColor = Theme.Text, Font = Theme.Font(9.5f), CheckOnClick = true, IntegralHeight = false };
            int i = 0;
            foreach (var it in items) { cl.Items.Add(it, marked != null && marked.Contains(i)); i++; }
            host.Controls.Add(cl);
            Native.UseDarkScrollBars(cl);
            AddRow(host, height);
            AddRow(new Panel { BackColor = Theme.Bg }, 6);
            _fields[key] = cl;
            return cl;
        }
        public List<int> Checked(string key) => _fields.TryGetValue(key, out var c) && c is CheckedListBox cl ? cl.CheckedIndices.Cast<int>().ToList() : new List<int>();

        public string Get(string key) => _fields.TryGetValue(key, out var c) ? (c is ComboBox cb ? cb.SelectedIndex.ToString() : (c.Text ?? "").Trim()) : "";
        public int Index(string key) => _fields.TryGetValue(key, out var c) && c is ComboBox cb ? cb.SelectedIndex : -1;
        public Control Field(string key) => _fields.TryGetValue(key, out var c) ? c : null;
    }
}
