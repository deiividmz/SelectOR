// Mi cuenta (Empresas): cambiar la contraseña y eliminar la cuenta, sin salir de SelectOR.
//  · Las dos ventanas piden la contraseña actual y hacen el trabajo con la función que se les pasa
//    (devuelve el error o null). Si hay error, la ventana sigue abierta y lo enseña.

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    // Base: cabecera con la franja, cuerpo con filas de campos, mensaje de error y botones.
    public class AccountDialogBase : Form
    {
        protected readonly Panel Body;
        protected readonly Label Note;
        protected readonly RoundButton Ok, Cancel;
        bool _busy;

        protected AccountDialogBase(string title, string okText, bool danger)
        {
            Text = title;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(Theme.Px(480), Theme.Px(200));
            try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label
            {
                Text = "  " + title, Dock = DockStyle.Top, Height = Theme.Px(42),
                Font = Theme.Font(13f, FontStyle.Bold), ForeColor = danger ? Color.FromArgb(240, 140, 140) : Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface
            };
            Body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(18), Theme.Px(12), Theme.Px(18), Theme.Px(4)) };
            Note = new Label { Dock = DockStyle.Bottom, Height = Theme.Px(40), ForeColor = Color.FromArgb(235, 130, 130), BackColor = Theme.Bg,
                               Padding = new Padding(Theme.Px(18), 0, Theme.Px(18), 0), TextAlign = ContentAlignment.MiddleLeft };

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = Theme.Px(58), BackColor = Theme.Bg, Padding = new Padding(Theme.Px(18), Theme.Px(8), Theme.Px(18), Theme.Px(12)) };
            Ok = new RoundButton
            {
                Text = okText, Width = Theme.Px(200), Height = Theme.Px(38), Radius = 10, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right,
                BaseColor = danger ? Color.FromArgb(170, 55, 55) : Theme.Accent, HoverColor = danger ? Color.FromArgb(200, 70, 70) : Theme.AccentHi,
                GradientTo = danger ? Color.FromArgb(150, 45, 45) : Theme.Accent2
            };
            Cancel = new RoundButton { Text = I18n.T("Cancelar"), Width = Theme.Px(120), Height = Theme.Px(38), Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f, Dock = DockStyle.Right };
            Ok.Click += async (s, e) => await Submit();
            Cancel.Click += (s, e) => { if (!_busy) { DialogResult = DialogResult.Cancel; Close(); } };
            buttons.Controls.Add(Ok); buttons.Controls.Add(new Panel { Width = Theme.Px(10), Dock = DockStyle.Right }); buttons.Controls.Add(Cancel);

            Controls.Add(Body);
            Controls.Add(Note);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);
            KeyPreview = true;
            KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Escape && !_busy) { DialogResult = DialogResult.Cancel; Close(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Submit(); }
            };
        }

        // Añade (de arriba abajo) un texto, un campo de contraseña o una casilla.
        int _y;
        protected Label AddText(string text, Color? color = null)
        {
            int w = ClientSize.Width - Theme.Px(36);
            int h = TextRenderer.MeasureText(text, Font, new Size(w, 2000), TextFormatFlags.WordBreak).Height + Theme.Px(6);
            var l = new Label { Text = text, AutoSize = false, Location = new Point(Theme.Px(18), Theme.Px(12) + _y), Size = new Size(w, h), ForeColor = color ?? Theme.Subtle };
            Body.Controls.Add(l); _y += h + Theme.Px(4);
            return l;
        }

        protected TextBox AddPassword(string label)
        {
            int w = ClientSize.Width - Theme.Px(36);
            var l = new Label { Text = label, AutoSize = false, Location = new Point(Theme.Px(18), Theme.Px(12) + _y), Size = new Size(w, Theme.Px(20)), ForeColor = Theme.Subtle };
            _y += Theme.Px(22);
            var inp = new RoundedInput { Location = new Point(Theme.Px(18), Theme.Px(12) + _y), Size = new Size(w, Theme.Px(36)) };
            inp.Box.UseSystemPasswordChar = true;
            inp.Box.MaxLength = 72;
            Body.Controls.Add(l); Body.Controls.Add(inp);
            _y += Theme.Px(36) + Theme.Px(8);
            return inp.Box;
        }

        protected CheckBox AddCheck(string text)
        {
            int w = ClientSize.Width - Theme.Px(36);
            var c = new ThemeCheck { Text = text, Location = new Point(Theme.Px(18), Theme.Px(12) + _y), Size = new Size(w, Theme.Px(28)), ForeColor = Theme.Text };
            Body.Controls.Add(c); _y += Theme.Px(32);
            return c;
        }

        // Ajusta el alto de la ventana a lo añadido.
        protected void FitHeight() => ClientSize = new Size(ClientSize.Width, Theme.Px(4) + Theme.Px(42) + Theme.Px(12) + _y + Theme.Px(4) + Theme.Px(40) + Theme.Px(58));

        protected virtual Task<string> Run() => Task.FromResult<string>(null);

        async Task Submit()
        {
            if (_busy) return;
            _busy = true; Ok.Enabled = false; Cancel.Enabled = false; UseWaitCursor = true;
            Note.ForeColor = Theme.Subtle; Note.Text = I18n.T("Un momento…");
            string err;
            try { err = await Run(); } catch (Exception ex) { err = ex.Message; }
            _busy = false; Ok.Enabled = true; Cancel.Enabled = true; UseWaitCursor = false;
            if (err == null) { DialogResult = DialogResult.OK; Close(); return; }
            Note.ForeColor = Color.FromArgb(235, 130, 130); Note.Text = err;
        }
    }

    // Cambiar la contraseña: la actual, la nueva y la nueva otra vez.
    public sealed class ChangePasswordDialog : AccountDialogBase
    {
        readonly TextBox _cur, _new1, _new2;
        readonly Func<string, string, Task<string>> _change;

        public ChangePasswordDialog(string account, Func<string, string, Task<string>> change)
            : base(I18n.T("Cambiar contraseña"), I18n.T("Cambiar contraseña"), false)
        {
            _change = change;
            AddText(string.Format(I18n.T("Cuenta: {0}. La contraseña nueva sirve desde ya para iniciar sesión en SelectOR y en la web."), account));
            _cur = AddPassword(I18n.T("Contraseña actual"));
            _new1 = AddPassword(I18n.T("Contraseña nueva (6 caracteres o más)"));
            _new2 = AddPassword(I18n.T("Repite la contraseña nueva"));
            FitHeight();
            Shown += (s, e) => _cur.Focus();
        }

        protected override Task<string> Run()
        {
            string cur = _cur.Text, n1 = _new1.Text, n2 = _new2.Text;
            if (cur.Length == 0) return Task.FromResult(I18n.T("Escribe tu contraseña actual."));
            if (n1.Length < 6) return Task.FromResult(I18n.T("La contraseña nueva debe tener 6 caracteres o más."));
            if (n1 != n2) return Task.FromResult(I18n.T("Las dos contraseñas nuevas no coinciden."));
            if (n1 == cur) return Task.FromResult(I18n.T("La contraseña nueva es igual que la actual."));
            return _change(cur, n1);
        }
    }

    // Eliminar la cuenta: qué se borra y qué no, la contraseña y una casilla de confirmación.
    public sealed class DeleteAccountDialog : AccountDialogBase
    {
        readonly TextBox _pw;
        readonly CheckBox _ok;
        readonly Func<string, Task<string>> _delete;

        public DeleteAccountDialog(string account, Func<string, Task<string>> delete)
            : base(I18n.T("Eliminar mi cuenta"), I18n.T("Eliminar mi cuenta"), true)
        {
            _delete = delete;
            AddText(string.Format(I18n.T("Vas a eliminar la cuenta {0}. No se puede deshacer."), account), Color.FromArgb(240, 150, 150));
            AddText(I18n.T("Se borran tu perfil, tu carné por puntos, tus avisos, tus mensajes del chat y tus solicitudes, y sales de todas tus empresas. "
                         + "Los servicios que condujiste se quedan en sus empresas, pero sin tu nombre."));
            AddText(I18n.T("Si eres el gerente de una empresa, pasa a otro gerente de esa empresa. Si eres el único gerente, antes tienes que nombrar gerente a otro socio (Socios) o eliminar la empresa."));
            _pw = AddPassword(I18n.T("Tu contraseña, para confirmar"));
            _ok = AddCheck(I18n.T("Entiendo que mi cuenta se elimina para siempre"));
            FitHeight();
            Shown += (s, e) => _pw.Focus();
        }

        protected override Task<string> Run()
        {
            if (_pw.Text.Length == 0) return Task.FromResult(I18n.T("Escribe tu contraseña para confirmar."));
            if (!_ok.Checked) return Task.FromResult(I18n.T("Marca la casilla para confirmar."));
            return _delete(_pw.Text);
        }
    }
}
