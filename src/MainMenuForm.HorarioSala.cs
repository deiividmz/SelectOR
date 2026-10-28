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
        SwitchPilot _horarioPilot;   // (1.2.78) su propia conexión con la sala, solo para mirar (como el puesto de mando)

        void BuildSalaAiButton()
        {
            // (1.2.77) el icono en color, como los demás de SelectOR (EmojiIcon)
            _salaAiBtn = new EmojiIcon { Text = "🚆", Size = new Size(30, 30),
                                     ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(12.5f), Cursor = Cursors.Hand, Visible = false };
            _salaAiBtn.Click += (s, e) => OpenHorarioSala();
            new ToolTip().SetToolTip(_salaAiBtn, Tr("Horario de la sala: trenes IA (administrador y gestores del horario)"));
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
            if (!Supa.IsSuperadmin && !Supa.IsAiEditor) { ThemedBox.Show(this, Tr("Solo el administrador y los gestores del horario."), Tr("Horario de la sala"), MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (_horario != null && !_horario.IsDisposed) { _horario.WindowState = FormWindowState.Normal; _horario.Activate(); return; }
            if (!SalaRouteHere) return;
            string dir = _curRoute.Path;
            bool admin = Supa.IsSuperadmin;   // (1.2.81) un gestor: solo los servicios
            _horario = new HorarioSalaForm(dir, () => RouteGraphFor(dir), "CGL_V22N_RE", () => HorarioMapData(dir), HorarioAiTrains, admin) { RetireAi = admin ? HorarioRetire : null };
            // (1.2.78) los trenes IA en su mapa aunque no estés conduciendo en la sala: una conexión aparte, solo para mirar
            try
            {
                _horarioPilot?.Dispose();
                string me = (_txtMPUser?.Text ?? "").Trim();
                _horarioPilot = new SwitchPilot(SalaHost, SalaPort, me.Length > 0 ? me : "SelectOR", 1);
                _horarioPilot.EnsureStarted();
            }
            catch { _horarioPilot = null; }
            _horario.FormClosed += (s, e) => { try { _horarioPilot?.Dispose(); } catch { } _horarioPilot = null; };
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

        // (1.2.78) retirar ya los trenes IA de la sala (todos: null; o los de un servicio), con el vale de administrador.
        // null = la sala lo ha pasado al despachador; si no, el motivo.
        async Task<string> HorarioRetire(string service)
        {
            var p = RoomMapOn ? _pilot : _horarioPilot;
            if (p == null || !p.Ready) return Tr("Sin conexión con la sala.");
            var (ticket, err) = await DispatchForm.SupaTicket();
            if (ticket == null) return err ?? Tr("No disponible.");
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<string> ok = _ => tcs.TrySetResult(null);
            Action<string> bad = e => tcs.TrySetResult(e.StartsWith("nodisp") ? Tr("La sala no tiene despachador ahora mismo.") : Tr("La sala no acepta tu permiso de administrador (puede haber caducado). Vuelve a intentarlo."));
            Action<string> badMap = e => { if (e.StartsWith("nodisp")) bad(e); };
            p.AiRetired += ok; p.JefeError += bad; p.MapError += badMap;
            try
            {
                p.AiRetire(ticket, service);
                var done = await Task.WhenAny(tcs.Task, Task.Delay(10000));
                return done == tcs.Task ? tcs.Task.Result : Tr("La sala no contesta. Vuelve a intentarlo.");
            }
            finally { p.AiRetired -= ok; p.JefeError -= bad; p.MapError -= badMap; }
        }

        // (1.2.77) los trenes IA que circulan ahora por la sala: por la conexión de la sala si conduces en ella y, si no, por la
        // del horario (1.2.78)
        List<(string name, double lat, double lon, double kmh)> HorarioAiTrains()
        {
            var l = new List<(string, double, double, double)>();
            var src = RoomMapOn ? _pilot : _horarioPilot != null && _horarioPilot.Ready && _horarioPilot.MapOk ? _horarioPilot : null;
            if (src == null) return l;
            foreach (var t in src.Trains)
                if (!t.player && RoomAi.Is(t.name) && OrGeo.TryLatLon(t.tx * 2048.0 + t.x, t.tz * 2048.0 + t.z, out double la, out double lo))
                    l.Add((t.name, la, lo, t.kmh));
            return l;
        }
    }
}
