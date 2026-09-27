// Aviso de nueva versión: muestra la versión, las novedades y, al pulsar «Descargar e instalar»,
// descarga el paquete con barra de progreso, lo instala y reinicia SelectOR.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public class UpdateDialog : Form
    {
        readonly ReleaseInfo _r;
        readonly Func<string> _blockReason;   // p. ej. «termina el servicio antes de actualizar»
        readonly RoundButton _ok, _later;
        readonly Label _status;
        readonly ProgressBar _bar;
        bool _busy;

        /// <summary>true si la instalación terminó y la app debe cerrarse (ya se lanzó la nueva).</summary>
        public bool Installed { get; private set; }

        public UpdateDialog(ReleaseInfo r, Func<string> blockReason = null)
        {
            _r = r; _blockReason = blockReason;
            string title = I18n.T("Nueva versión de SelectOR");
            Text = title;
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(520, 380);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label
            {
                Text = "  " + title, Dock = DockStyle.Top, Height = 42,
                Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text,
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface
            };

            var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(18, 12, 18, 6) };
            var ver = new Label
            {
                Dock = DockStyle.Top, Height = 30, ForeColor = Theme.AccentHi,
                Font = Theme.Font(11.5f, FontStyle.Bold),
                Text = string.Format(I18n.T("Versión {0} disponible (tienes la {1})"), r.Version, Updater.CurrentVersionText)
            };
            var lblNotes = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Theme.Subtle, Text = I18n.T("Novedades") };
            var notes = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text,
                Font = Theme.Font(9.5f),
                Text = string.IsNullOrWhiteSpace(r.Notes) ? I18n.T("Mejoras y correcciones.") : r.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n")
            };
            var notesWrap = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(10, 8, 6, 8) };
            notesWrap.Controls.Add(notes);
            _bar = new ProgressBar { Dock = DockStyle.Bottom, Height = 8, Visible = false, Minimum = 0, Maximum = 1000 };
            _status = new Label { Dock = DockStyle.Bottom, Height = 40, ForeColor = Theme.Subtle, TextAlign = ContentAlignment.MiddleLeft };
            body.Controls.Add(notesWrap);
            body.Controls.Add(lblNotes);
            body.Controls.Add(ver);
            body.Controls.Add(_status);
            body.Controls.Add(_bar);

            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.Bg, Padding = new Padding(18, 8, 18, 12) };
            _ok = new RoundButton { Text = I18n.T("Descargar e instalar"), Width = 210, Height = 38, Radius = 10, BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2, TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold, Dock = DockStyle.Right };
            _later = new RoundButton { Text = I18n.T("Más tarde"), Width = 130, Height = 38, Radius = 10, BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f, Dock = DockStyle.Right };
            var spacer = new Panel { Width = 10, Dock = DockStyle.Right };
            _ok.Click += async (s, e) => await Install();
            _later.Click += (s, e) => { if (!_busy) { DialogResult = DialogResult.Cancel; Close(); } };
            buttons.Controls.Add(_ok); buttons.Controls.Add(spacer); buttons.Controls.Add(_later);

            Controls.Add(body);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);

            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };   // no cerrar a mitad de la instalación
        }

        async System.Threading.Tasks.Task Install()
        {
            if (_busy) return;
            string block = _blockReason?.Invoke();
            if (!string.IsNullOrEmpty(block)) { SetStatus(block, true); return; }

            _busy = true; _ok.Enabled = false; _later.Enabled = false;
            _bar.Visible = true; _bar.Value = 0;
            SetStatus(I18n.T("Descargando…"), false);
            var prog = new Progress<double>(p =>
            {
                _bar.Value = (int)Math.Round(Math.Max(0, Math.Min(1, p)) * 1000);
                SetStatus(string.Format(I18n.T("Descargando… {0} %"), (int)Math.Round(p * 100)), false);
            });
            string err = await Updater.DownloadAndInstallAsync(_r, prog);
            _busy = false;
            if (err != null)
            {
                _bar.Visible = false;
                SetStatus(err, true);
                _ok.Enabled = true; _later.Enabled = true;
                return;
            }
            SetStatus(I18n.T("Actualización instalada. Reiniciando SelectOR…"), false);
            Installed = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        void SetStatus(string t, bool error)
        {
            _status.Text = t;
            _status.ForeColor = error ? Color.FromArgb(229, 115, 115) : Theme.Subtle;
        }
    }
}
