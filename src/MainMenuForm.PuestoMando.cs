// PUESTO DE MANDO (1.2.75): el botón 🖥 junto a «Conducir en la sala compartida» (con la ruta de la sala, en Conducción
// libre) abre la ventana del puesto de mando (DispatchForm.cs): mirar, para todos; mandar, el jefe de circulación
// (DispatchForm.Jefe.cs), uno de los que apunta el administrador.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Label _salaDispBtn;
        DispatchForm _dispatch;

        void BuildSalaDispatchButton()
        {
            // (1.2.77) el icono en color, como los demás de SelectOR (EmojiIcon)
            _salaDispBtn = new EmojiIcon { Text = "🖥", Size = new Size(30, 30),
                                       ForeColor = Theme.Subtle, BackColor = Theme.Surface, Font = Theme.Font(12.5f), Cursor = Cursors.Hand };
            _salaDispBtn.Click += (s, e) => OpenDispatch();
            new ToolTip().SetToolTip(_salaDispBtn, Tr("Puesto de mando de la sala"));
            Action place = () =>
            {
                int right = _salaAiBtn != null && _salaAiBtn.Visible ? _salaAiBtn.Left - 4 : _salaWxBtn != null && _salaWxBtn.Visible ? _salaWxBtn.Left - 4 : _salaHost.ClientSize.Width - 4;
                _salaDispBtn.Location = new Point(right - _salaDispBtn.Width, Math.Max(0, (_salaHost.ClientSize.Height - _salaDispBtn.Height) / 2));
            };
            _salaHost.Resize += (s, e) => place();
            _salaHost.HandleCreated += (s, e) => place();
            _salaDispBtn.VisibleChanged += (s, e) => place();
            if (_salaWxBtn != null) { _salaWxBtn.VisibleChanged += (s, e) => place(); _salaWxBtn.LocationChanged += (s, e) => place(); }
            if (_salaAiBtn != null) { _salaAiBtn.VisibleChanged += (s, e) => place(); _salaAiBtn.LocationChanged += (s, e) => place(); }
            _salaHost.Controls.Add(_salaDispBtn);
            _salaDispBtn.BringToFront();
        }

        void OpenDispatch()
        {
            if (_dispatch != null && !_dispatch.IsDisposed) { _dispatch.WindowState = FormWindowState.Normal; _dispatch.Activate(); return; }
            if (!SalaRouteHere) return;
            string dir = _curRoute.Path;
            _dispatch = new DispatchForm(dir, SalaHost, SalaPort, (_txtMPUser?.Text ?? "").Trim(),
                d => { var good = TdbNames(d); return RouteGraph.Build(d, n => CleanMapStation(FixTdbName(n, good))); })
            {
                ClockText = () => Tr("Hora de la sala") + " " + SpainNow(_salaOffset).time.Substring(0, 5)
                                  + (SalaWeatherText().Length > 0 ? " · " + SalaWeatherText() : "")
            };
            // jefe de circulación (DispatchForm.Jefe.cs): con sesión, Supabase dice quién puede tomar el mando
            if (Supa.IsLoggedIn)
            {
                _dispatch.ChiefMe = DispatchForm.SupaChiefMe; _dispatch.IssueTicket = DispatchForm.SupaTicket;
                _dispatch.ChiefsList = DispatchForm.SupaChiefs; _dispatch.ChiefSet = DispatchForm.SupaChiefSet;
            }
            _dispatch.Show(this);
        }
    }
}
