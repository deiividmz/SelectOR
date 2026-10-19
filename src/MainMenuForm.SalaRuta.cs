// Quién está en la sala compartida, en la página Ruta (1.2.69): una franja bajo la ficha de la ruta de la sala
// (CGL_V22N_RE) con cuántos jugadores hay y sus nombres. Se pregunta a la sala cada 15 s con la conexión corta de
// siempre (SalaPeek: no entra en la sala), solo mientras se ve la página Ruta con esa ruta.

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Panel _rutaSala;
        Label _rutaSalaText;
        Timer _rutaSalaTimer;
        bool _rutaSalaBusy;
        string _rutaSalaLast;   // (para no repintar si no cambia)

        // (BuildRutaPage) la franja, oculta fuera de la ruta de la sala
        Panel BuildRutaSala()
        {
            _rutaSala = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0), Visible = false };
            var box = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(14, 0, 14, 0) };
            _rutaSalaText = new Label { Dock = DockStyle.Fill, ForeColor = Theme.Text, BackColor = Theme.Surface, Font = Theme.Font(9.5f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Padding = new Padding(16, 0, 0, 0) };
            _rutaSalaText.Paint += (s, e) =>
            {
                // el punto: verde si hay alguien, gris si no o si no se sabe
                bool any = _rutaSalaText.Tag is int n && n > 0;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var b = new SolidBrush(any ? Theme.Accent : Theme.Subtle);
                int cy = _rutaSalaText.Height / 2;
                e.Graphics.FillEllipse(b, 2, cy - 5, 10, 10);
            };
            box.Controls.Add(_rutaSalaText);
            _rutaSala.Controls.Add(box);
            _rutaSalaTimer = new Timer { Interval = 15000 };
            _rutaSalaTimer.Tick += (s, e) => RefreshRutaSala();
            _rutaSalaTimer.Start();
            return _rutaSala;
        }

        // (UpdateRouteOverview y cada 15 s)
        async void RefreshRutaSala()
        {
            try
            {
                if (_rutaSala == null) return;
                bool show = SalaRouteHere;
                if (_rutaSala.Visible != show) _rutaSala.Visible = show;
                if (!show || _activePage != 0 || !Visible || WindowState == FormWindowState.Minimized || _rutaSalaBusy) return;
                _rutaSalaBusy = true;
                SalaInfo info;
                try { info = await SalaPeek(Supa.IsLoggedIn && !string.IsNullOrWhiteSpace(Supa.Username) ? Supa.Username : "SelectOR"); }
                finally { _rutaSalaBusy = false; }
                if (!SalaRouteHere) return;
                string txt; int count = -1;
                if (info == null) txt = Tr("SALA COMPARTIDA") + "  ·  " + Tr("no se puede consultar ahora");
                else
                {
                    var names = info.Trains.Where(t => t.player).Select(t => t.name).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();
                    count = Math.Max(names.Count, info.Players);
                    txt = Tr("SALA COMPARTIDA") + "  ·  " + (count == 0 ? Tr("nadie conectado ahora")
                          : Plural(count, "{0} conectado", "{0} conectados") + (names.Count > 0 ? ": " + string.Join(" · ", names) : ""));
                }
                if (txt == _rutaSalaLast) return;
                _rutaSalaLast = txt;
                _rutaSalaText.Tag = count;
                _rutaSalaText.Text = txt;
                _rutaSalaText.Invalidate();
            }
            catch { }
        }
    }
}
