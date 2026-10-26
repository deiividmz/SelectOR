// HORARIO DE LA SALA (1.2.77): el botón 🚆 a la izquierda de ⛅, solo para el administrador; abre HorarioSalaForm (los
// trenes IA de la sala compartida de CGL: sql/horario-sala.sql; el despachador lo lee cada minuto).

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Label _salaAiBtn;
        HorarioSalaForm _horario;

        void BuildSalaAiButton()
        {
            // (1.2.77) el icono en color, como los demás de SelectOR (EmojiIcon)
            _salaAiBtn = new EmojiIcon { Text = "🚆", Size = new Size(30, 30),
                                     ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(12.5f), Cursor = Cursors.Hand, Visible = false };
            _salaAiBtn.Click += (s, e) => OpenHorarioSala();
            new ToolTip().SetToolTip(_salaAiBtn, Tr("Horario de la sala: trenes IA (administrador)"));
            Action place = () =>
            {
                int right = _salaWxBtn != null && _salaWxBtn.Visible ? _salaWxBtn.Left - 4 : _salaHost.ClientSize.Width - 4;
                _salaAiBtn.Location = new Point(right - _salaAiBtn.Width, Math.Max(0, (_salaHost.ClientSize.Height - _salaAiBtn.Height) / 2));
            };
            _salaHost.Resize += (s, e) => place();
            _salaHost.HandleCreated += (s, e) => place();
            _salaAiBtn.VisibleChanged += (s, e) => place();
            if (_salaWxBtn != null) { _salaWxBtn.VisibleChanged += (s, e) => place(); _salaWxBtn.LocationChanged += (s, e) => place(); }
            _salaHost.Controls.Add(_salaAiBtn);
            _salaAiBtn.BringToFront();
        }

        void OpenHorarioSala()
        {
            if (!Supa.IsSuperadmin) { ThemedBox.Show(this, Tr("Solo el administrador."), Tr("Horario de la sala"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (_horario != null && !_horario.IsDisposed) { _horario.WindowState = FormWindowState.Normal; _horario.Activate(); return; }
            if (!SalaRouteHere) return;
            string dir = _curRoute.Path;
            _horario = new HorarioSalaForm(dir, () => RouteGraphFor(dir), "CGL_V22N_RE", () => HorarioMapData(dir), HorarioAiTrains);
            _horario.Show(this);
        }

        // (1.2.77) el mapa del horario: el mismo trazado que la pestaña Ruta (de su caché, o se prepara una vez)
        async Task<RouteMapData> HorarioMapData(string dir)
        {
            if (_rutaMapCache.TryGetValue(dir, out var d)) return d;
            if (!RutaMapPossible(dir)) return null;
            var track = await RouteTrackAsync(dir);
            d = await Task.Run(() => BuildRouteMapData(dir, track));
            if (d != null) _rutaMapCache[dir] = d;
            return d;
        }

        // (1.2.77) los trenes IA que circulan ahora por la sala (solo si SelectOR está conectado a ella)
        List<(string name, double lat, double lon, double kmh)> HorarioAiTrains()
        {
            var l = new List<(string, double, double, double)>();
            if (!RoomMapOn) return l;
            foreach (var t in _pilot.Trains)
                if (!t.player && RoomAi.Is(t.name) && OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo))
                    l.Add((t.name, la, lo, t.kmh));
            return l;
        }
    }
}
