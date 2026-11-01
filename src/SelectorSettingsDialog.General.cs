// Ajustes de SelectOR → General (1.2.82): el idioma de SelectOR y el tamaño del HUD, la hoja de ruta y el chat.
//  · Idioma: «Automático» (el de Open Rails o, si no tiene, el de Windows) o uno de I18n.Languages. Vale desde que se vuelve
//    a abrir SelectOR (los textos se ponen al crear cada ventana): botón para volver a abrirlo ya.
//  · Tamaño del HUD (HudScale): al momento, también con Open Rails abierto.

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class SelectorSettingsDialog
    {
        RoundButton _tabGen;
        Panel _pageGen;

        Panel BuildGeneralPage()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(20), Theme.Px(16), Theme.Px(20), Theme.Px(8)), Visible = false };
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Theme.Bg };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Label Caption(string t) => new Label { Text = t, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Font = Theme.Font(10f, FontStyle.Bold), Margin = new Padding(0, Theme.Px(14), Theme.Px(16), 0) };
            Label Note(string t) => new Label { Text = t, AutoSize = true, MaximumSize = new Size(Theme.Px(560), 0), ForeColor = Theme.Subtle, BackColor = Theme.Bg, Font = Theme.Font(8.75f), Margin = new Padding(0, Theme.Px(4), 0, Theme.Px(6)) };

            // ── idioma
            var langCb = DialogKit.Combo(); langCb.Width = Theme.Px(300); langCb.Margin = new Padding(0, Theme.Px(8), 0, 0);
            var codes = new[] { "auto" }.Concat(I18n.Languages.Select(l => l.code)).ToArray();
            langCb.Items.Add(I18n.T("Automático (el de Open Rails o Windows)"));
            foreach (var (code, name) in I18n.Languages) langCb.Items.Add(name);
            int cur = Array.IndexOf(codes, string.IsNullOrEmpty(_prefs.UiLanguage) ? "auto" : _prefs.UiLanguage);
            langCb.SelectedIndex = cur >= 0 ? cur : 0;
            var restart = DialogKit.Fit(DialogKit.Secondary(I18n.T("Volver a abrir SelectOR")), 180); restart.Height = Theme.Px(32);
            restart.Margin = new Padding(0, Theme.Px(4), 0, 0); restart.Visible = false;
            var langNote = Note(I18n.T("Se aplica al volver a abrir SelectOR."));
            langCb.SelectedIndexChanged += (s, e) =>
            {
                if (langCb.SelectedIndex < 0) return;
                string want = codes[langCb.SelectedIndex];
                if (want == (_prefs.UiLanguage ?? "auto")) return;
                _prefs.UiLanguage = want; Save();
                restart.Visible = I18n.Resolve(want, null) != I18n.Lang || want != "auto";
            };
            restart.Click += (s, e) =>
            {
                Save();
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory }); } catch { return; }
                Application.Exit();
            };
            var langCol = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            langCol.Controls.Add(langCb); langCol.Controls.Add(langNote); langCol.Controls.Add(restart);
            grid.Controls.Add(Caption(I18n.T("Idioma de SelectOR")), 0, 0); grid.Controls.Add(langCol, 1, 0);

            // ── tamaño del HUD
            var hudCb = DialogKit.Combo(); hudCb.Width = Theme.Px(120); hudCb.Margin = new Padding(0, Theme.Px(8), 0, 0);
            foreach (int pc in HudScale.Choices) hudCb.Items.Add(pc + " %");
            int hc = Array.IndexOf(HudScale.Choices, _prefs.HudScalePct); hudCb.SelectedIndex = hc >= 0 ? hc : 0;
            hudCb.SelectedIndexChanged += (s, e) => { if (hudCb.SelectedIndex < 0) return; _prefs.HudScalePct = HudScale.Choices[hudCb.SelectedIndex]; Save(); HudScale.Set(_prefs.HudScalePct); };
            var hudCol = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Bg, Margin = new Padding(0) };
            hudCol.Controls.Add(hudCb); hudCol.Controls.Add(Note(I18n.T("Para pantallas grandes: el HUD, la hoja de ruta y el chat, más grandes y nítidos. Se aplica al momento.")));
            grid.Controls.Add(Caption(I18n.T("Tamaño del HUD, la hoja de ruta y el chat:").TrimEnd(':')), 0, 1); grid.Controls.Add(hudCol, 1, 1);

            page.Controls.Add(grid);
            return page;
        }
    }
}
