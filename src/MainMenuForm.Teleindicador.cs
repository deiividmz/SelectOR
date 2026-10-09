// Teleindicador del tren (Teleindicadores.cs): selector ◀ [cartel] ▾ ▶ con galería de los carteles LED de los destinos en la
// cabecera «TREN SELECCIONADO» de Conducción libre y Horarios. La vista 3D enseña el cartel elegido al
// momento; los ficheros del tren solo se cambian al pulsar CONDUCIR. El destino se recuerda por tren.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        sealed class TeleUi
        {
            public TeleSelector Sel;
            public Panel Host;
            public string ConPath;
            public List<string> Folders = new();
            public bool Loading;
            public bool Active;       // el tren tiene destinos (el selector está en uso)
            public Control Row;       // fila propia (Horarios): se enseña solo si hay destinos
            public Action Rerender;   // vuelve a pintar la vista 3D de esa pestaña
        }

        TeleUi _teleExplore, _teleTT;

        // Selector «TELEINDICADOR ◀ [cartel] ▾ ▶» (oculto hasta que el tren tenga destinos). El cartel abre la galería
        // con todos los destinos (TeleSelector.cs).
        //  · Conducción libre: en la cabecera, a la derecha del título (con aire entre los dos), todo centrado en vertical.
        //  · Horarios (ownRow): «hdr» es una fila propia bajo el título, donde junto a él no cabe.
        TeleUi MakeTeleUi(Panel hdr, Action rerender, bool ownRow = false)
        {
            var ui = new TeleUi { Rerender = rerender, Row = ownRow ? hdr : null };
            var cap = new Label { Text = Tr("TELEINDICADOR"), AutoSize = true, ForeColor = Theme.Subtle, BackColor = hdr.BackColor,
                                  Font = Theme.Font(7.75f, FontStyle.Bold), Margin = Padding.Empty, Padding = Padding.Empty };
            var sel = new TeleSelector { BackColor = hdr.BackColor };
            sel.Chosen += () => OnTeleChosen(ui);
            var host = new Panel { Dock = DockStyle.Left, Width = sel.Width + 14, BackColor = hdr.BackColor, Visible = false };   // mismo fondo que la tarjeta
            host.Controls.Add(cap); host.Controls.Add(sel);
            int x0 = ownRow ? 0 : Theme.Px(26);   // aire entre el título y el teleindicador
            void Place()
            {
                int capW = ownRow ? Math.Max(cap.Width, Theme.Px(104)) : cap.Width;   // en Horarios, columna fija como las demás filas
                cap.Location = new Point(x0, Math.Max(0, (host.Height - cap.Height) / 2));
                sel.Location = new Point(x0 + capW + Theme.Px(8), Math.Max(0, (host.Height - sel.Height) / 2));
                host.Width = sel.Right + 2;
            }
            host.Resize += (s, e) => Place();
            sel.Resize += (s, e) => Place();
            hdr.Controls.Add(host);
            host.BringToFront();   // se acomoda después del título (a su derecha)
            if (hdr.Height < sel.Height + 2) hdr.Height = sel.Height + 2;
            ui.Sel = sel; ui.Host = host;
            return ui;
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
                if (dests.Count == 0) { ui.Sel.SetItems(Array.Empty<TeleOption>(), -1); ShowTele(ui, false); return; }
                var items = new List<TeleOption> { new TeleOption { Name = null } };
                foreach (var (name, sample) in dests) items.Add(new TeleOption { Name = name, Sample = sample });
                string choice = TeleChoice(ui.ConPath, ui.Folders);
                int sel = 0;
                for (int i = 1; i < items.Count; i++)
                    if (string.Equals(items[i].Name, choice, StringComparison.OrdinalIgnoreCase)) { sel = i; break; }
                ui.Sel.SetItems(items, sel);
                ShowTele(ui, true);
                TeleRedirect(ui);
            }
            catch { ShowTele(ui, false); }
            finally { ui.Loading = false; }
            // la barra inferior se escribió con los destinos del tren anterior (Horarios la actualiza antes): otra vez
            try { UpdateStatus(); } catch { }
        }

        static void ShowTele(TeleUi ui, bool on)
        {
            ui.Active = on;
            ui.Host.Visible = on;
            if (ui.Row != null) ui.Row.Visible = on;
        }

        // Destino elegido para ese tren: el recordado; si nunca se eligió, el que tiene puesto ahora.
        string TeleChoice(string conPath, List<string> folders)
        {
            if (!string.IsNullOrEmpty(conPath) && _prefs.TeleDest.TryGetValue(conPath, out var v)) return string.IsNullOrEmpty(v) ? null : v;
            return Teleindicadores.Installed(folders);
        }

        string TeleSelectedName(TeleUi ui) => ui?.Sel?.Selected?.Name;

        void OnTeleChosen(TeleUi ui)
        {
            if (ui.Loading || string.IsNullOrEmpty(ui.ConPath)) return;
            _prefs.TeleDest[ui.ConPath] = TeleSelectedName(ui) ?? "";
            try { _prefs.Save(); } catch { }
            TeleRedirect(ui);
            try { ui.Rerender?.Invoke(); } catch { }
            UpdateStatus();
        }

        // Vista 3D: el cartel elegido en lugar del que hay ahora en la carpeta del tren.
        void TeleRedirect(TeleUi ui)
        {
            if (ui == null || ui.Folders.Count == 0 || !ui.Active) return;
            ShapeRenderer.SetTextureRedirects(ui.Folders, Teleindicadores.Redirects(ui.Folders, TeleSelectedName(ui)));
        }

        // «   ·   Teleindicador: Atocha» para la barra inferior (solo si el tren tiene destinos).
        string TeleSuffix(TeleUi ui)
        {
            var it = ui?.Sel?.Selected;
            if (ui == null || !ui.Active || it == null) return "";
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
