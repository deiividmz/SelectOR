// Teleindicador del tren (Teleindicadores.cs): desplegable con los carteles LED de los destinos en la
// cabecera «TREN SELECCIONADO» de Exploración y Horarios. La vista 3D enseña el cartel elegido al
// momento; los ficheros del tren solo se cambian al pulsar CONDUCIR. El destino se recuerda por tren.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        sealed class TeleItem
        {
            public string Name;     // null = carteles originales del tren
            public string Sample;   // fichero del cartel (para la miniatura)
        }

        sealed class TeleUi
        {
            public ThemeCombo Combo;
            public Panel Host;
            public string ConPath;
            public List<string> Folders = new();
            public bool Loading;
            public int Chosen = -1;   // opción elegida (con la lista abierta, SelectedIndex sigue al ratón)
            public Action Rerender;   // vuelve a pintar la vista 3D de esa pestaña
        }

        TeleUi _teleExplore, _teleTT;
        const int TeleEditH = 16, TeleListH = 26;

        // Desplegable (oculto hasta que el tren tenga destinos), a la derecha del título de la cabecera.
        TeleUi MakeTeleUi(Panel hdr, Action rerender)
        {
            var ui = new TeleUi { Rerender = rerender };
            var cb = new ThemeCombo
            {
                DrawMode = DrawMode.OwnerDrawVariable, ItemHeight = TeleEditH + 4, BackColor = Theme.Surface2, ForeColor = Theme.Text,
                Font = Theme.Font(9f), IntegralHeight = false, MaxDropDownItems = 9, Width = 160, TabStop = false
            };
            cb.MeasureItem += (s, e) => e.ItemHeight = TeleListH + 4;
            cb.DrawItem += (s, e) => DrawTeleItem(cb, e, ui.Chosen);
            cb.SelectedIndexChanged += (s, e) => { if (!cb.DroppedDown) OnTeleChosen(ui); };
            cb.DropDownClosed += (s, e) => OnTeleChosen(ui);
            var host = new Panel { Dock = DockStyle.Left, Width = cb.Width + 12, BackColor = Theme.Bg, Visible = false, Padding = new Padding(10, 0, 0, 0) };
            host.Controls.Add(cb);
            void Place() { cb.Location = new Point(10, Math.Max(0, (host.Height - cb.Height) / 2)); }
            host.Resize += (s, e) => Place();
            hdr.Controls.Add(host);
            host.BringToFront();   // se acomoda después del título (a su derecha)
            if (hdr.Height < cb.Height + 2) hdr.Height = cb.Height + 2;
            ui.Combo = cb; ui.Host = host;
            return ui;
        }

        void DrawTeleItem(ThemeCombo cb, DrawItemEventArgs e, int chosenIndex)
        {
            if (e.Index < 0 || e.Index >= cb.Items.Count) return;
            var it = cb.Items[e.Index] as TeleItem;
            var g = e.Graphics;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool hot = !edit && (e.State & DrawItemState.Selected) != 0;
            bool chosen = !edit && e.Index == chosenIndex;
            Color bg = chosen ? Color.FromArgb(46, 94, 50) : hot ? Theme.SurfaceHi : Theme.Surface2;
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
            if (it == null) return;
            if (it.Name == null)
            {
                using var f = Theme.Font(9f, edit ? FontStyle.Regular : FontStyle.Bold);
                TextRenderer.DrawText(g, edit ? Tr("Original") : Tr("Original del tren"), f, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 30, e.Bounds.Height), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            else
            {
                var th = Teleindicadores.LedThumb(it.Sample, edit ? TeleEditH : TeleListH);
                if (th != null)
                    g.DrawImage(th, e.Bounds.X + (edit ? 3 : 6), e.Bounds.Y + (e.Bounds.Height - th.Height) / 2, th.Width, th.Height);
                else
                    TextRenderer.DrawText(g, Teleindicadores.Nice(it.Name), cb.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 30, e.Bounds.Height), Theme.Text,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            if (chosen)
            {
                using var f = new Font("Segoe UI Symbol", 11f * Theme.DpiComp);
                TextRenderer.DrawText(g, "✓", f, new Rectangle(e.Bounds.Right - 28, e.Bounds.Y, 24, e.Bounds.Height), Color.FromArgb(143, 224, 147),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // Al cambiar de tren: carga sus destinos y elige el recordado (o el que tiene puesto ahora).
        void TeleRefresh(TeleUi ui, TrainItem c)
        {
            if (ui == null) return;
            ui.Loading = true;
            try
            {
                ui.ConPath = c?.FilePath;
                ui.Folders = c != null && _curFolder != null ? Teleindicadores.FoldersOf(_curFolder.Path, c.FilePath) : new List<string>();
                var dests = ui.Folders.Count > 0 ? Teleindicadores.Destinations(ui.Folders) : new List<(string name, string sample)>();
                var cb = ui.Combo;
                cb.BeginUpdate();
                cb.Items.Clear();
                if (dests.Count == 0) { cb.EndUpdate(); ui.Host.Visible = false; return; }
                cb.Items.Add(new TeleItem { Name = null });
                foreach (var (name, sample) in dests) cb.Items.Add(new TeleItem { Name = name, Sample = sample });
                cb.EndUpdate();

                string choice = TeleChoice(ui.ConPath, ui.Folders);
                int sel = 0;
                for (int i = 1; i < cb.Items.Count; i++)
                    if (string.Equals(((TeleItem)cb.Items[i]).Name, choice, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }
                cb.SelectedIndex = sel; ui.Chosen = sel;

                // Anchos: el desplegable, lo justo para el cartel más largo; la lista, algo más.
                int wEdit = 90, wList = 180;
                foreach (var (_, sample) in dests)
                {
                    var t1 = Teleindicadores.LedThumb(sample, TeleEditH); if (t1 != null) wEdit = Math.Max(wEdit, t1.Width + 8);
                    var t2 = Teleindicadores.LedThumb(sample, TeleListH); if (t2 != null) wList = Math.Max(wList, t2.Width + 44 + SystemInformation.VerticalScrollBarWidth);
                }
                using (var f = Theme.Font(9f)) wEdit = Math.Max(wEdit, TextRenderer.MeasureText(Tr("Original"), f).Width + 16);
                cb.Width = Math.Min(360, wEdit + 24);
                cb.DropDownWidth = Math.Min(520, Math.Max(cb.Width, wList));
                ui.Host.Width = cb.Width + 12;
                ui.Host.Visible = true;
                TeleRedirect(ui);
            }
            catch { ui.Host.Visible = false; }
            finally { ui.Loading = false; }
        }

        // Destino elegido para ese tren: el recordado; si nunca se eligió, el que tiene puesto ahora.
        string TeleChoice(string conPath, List<string> folders)
        {
            if (!string.IsNullOrEmpty(conPath) && _prefs.TeleDest.TryGetValue(conPath, out var v)) return string.IsNullOrEmpty(v) ? null : v;
            return Teleindicadores.Installed(folders);
        }

        string TeleSelectedName(TeleUi ui) => ui?.Combo?.SelectedItem is TeleItem it ? it.Name : null;

        void OnTeleChosen(TeleUi ui)
        {
            if (ui.Loading || string.IsNullOrEmpty(ui.ConPath)) return;
            if (ui.Combo.SelectedIndex == ui.Chosen) return;   // sin cambios (p. ej. se cerró la lista sin elegir)
            ui.Chosen = ui.Combo.SelectedIndex;
            _prefs.TeleDest[ui.ConPath] = TeleSelectedName(ui) ?? "";
            try { _prefs.Save(); } catch { }
            TeleRedirect(ui);
            try { ui.Rerender?.Invoke(); } catch { }
            UpdateStatus();
        }

        // Vista 3D: el cartel elegido en lugar del que hay ahora en la carpeta del tren.
        void TeleRedirect(TeleUi ui)
        {
            if (ui == null || ui.Folders.Count == 0 || !ui.Host.Visible) return;
            ShapeRenderer.SetTextureRedirects(ui.Folders, Teleindicadores.Redirects(ui.Folders, TeleSelectedName(ui)));
        }

        // «   ·   Teleindicador: Atocha» para la barra inferior (solo si el tren tiene destinos).
        string TeleSuffix(TeleUi ui)
        {
            if (ui == null || !ui.Host.Visible || ui.Combo.SelectedItem is not TeleItem it) return "";
            return $"   ·   {Tr("Teleindicador")}: {(it.Name == null ? Tr("original") : Teleindicadores.Nice(it.Name))}";
        }

        // Al pulsar CONDUCIR: pone en la carpeta del tren el destino elegido para él (si se eligió alguno).
        void TeleApplyForLaunch(TrainItem c)
        {
            try
            {
                if (c == null || _curFolder == null || string.IsNullOrEmpty(c.FilePath)) return;
                if (!_prefs.TeleDest.TryGetValue(c.FilePath, out var name)) return;
                var folders = Teleindicadores.FoldersOf(_curFolder.Path, c.FilePath);
                if (folders.Count == 0) return;
                if (!string.IsNullOrEmpty(name) && string.Equals(Teleindicadores.Installed(folders), name, StringComparison.OrdinalIgnoreCase)) return;   // ya está puesto
                var err = Teleindicadores.Apply(folders, string.IsNullOrEmpty(name) ? null : name);
                if (err != null)
                    Warn(string.Format(Tr("No se pudo cambiar el teleindicador del tren:\n{0}\n\nSe conducirá con el cartel que tenga puesto."), err));
            }
            catch { }
        }
    }
}
