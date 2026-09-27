// Acerca de SelectOR: autoría (David MZP) y aviso de compatibilidad/licencia de Open Rails.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public class AboutDialog : Form
    {
        public bool CheckForUpdatesRequested { get; private set; }

        public AboutDialog()
        {
            Text = I18n.T("Acerca de SelectOR");
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 452);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var logo = new SelectorLogo { Location = new Point(24, 20), Width = 220, Height = 44 };

            string orVer = "";
            try { orVer = ORTS.Common.VersionInfo.VersionOrBuild; } catch { }

            string appVer = "";
            try { var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version; if (v != null) appVer = $"{v.Major}.{v.Minor}.{v.Build}"; } catch { }

            string bodyText =
                I18n.T("SelectOR — menú de selección de trenes y rutas") + "\r\n" +
                (string.IsNullOrEmpty(appVer) ? "" : I18n.T("Versión") + " " + appVer + "\r\n") + "\r\n" +
                I18n.T("© 2026 David MZP. SelectOR. Todos los derechos reservados.") + "\r\n\r\n" +
                I18n.T("Complemento independiente que funciona sobre Open Rails") + (string.IsNullOrEmpty(orVer) ? "" : "  (" + orVer + ")") + ".\r\n" +
                I18n.T("No modifica su código fuente: reutiliza sus bibliotecas y\r\nrespeta su licencia y derechos. Este copyright ampara solo el\r\nmenú SelectOR y no afecta a Open Rails ni a su licencia.") + "\r\n\r\n" +
                I18n.T("Open Rails es software libre (GNU GPL v3) de The Open Rails\r\nproject — openrails.org. Las marcas y contenidos de rutas/\r\ntrenes pertenecen a sus respectivos autores.");

            // La ventana se AJUSTA al texto (evita cortes en cualquier idioma/contenido): medimos y dimensionamos.
            int bodyX = 26, bodyY = 82, bodyW = 508;
            var bodyFont = Theme.Font(10f);
            var measured = TextRenderer.MeasureText(bodyText, bodyFont, new Size(bodyW, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
            int bodyH = measured.Height + 6;

            var body = new Label
            {
                Location = new Point(bodyX, bodyY),
                Size = new Size(bodyW, bodyH),
                ForeColor = Theme.Text,
                Font = bodyFont,
                Text = bodyText
            };

            ClientSize = new Size(560, bodyY + bodyH + 58);   // + zona del botón Cerrar

            var btnClose = new RoundButton
            {
                Text = I18n.T("Cerrar"), Width = 120, Height = 36, Radius = 10,
                BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, GradientTo = Theme.Accent2,
                TextColor = Color.White, FontSize = 10.5f, FontStyle = FontStyle.Bold,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                Location = new Point(ClientSize.Width - 140, ClientSize.Height - 50)
            };
            btnClose.Click += (s, e) => Close();

            // Buscar actualizaciones a mano (el aviso automático sale al arrancar y cada 3 h).
            var btnUpd = new RoundButton
            {
                Text = I18n.T("Buscar actualizaciones"), Width = 230, Height = 36, Radius = 10,
                BaseColor = Theme.Surface2, HoverColor = Theme.SurfaceHi, TextColor = Theme.Text, FontSize = 10.5f,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                Location = new Point(24, ClientSize.Height - 50)
            };
            btnUpd.Click += (s, e) => { CheckForUpdatesRequested = true; Close(); };
            Controls.Add(btnUpd);

            Controls.Add(body);
            Controls.Add(logo);
            Controls.Add(btnClose);
            Controls.Add(stripe);
        }
    }
}
