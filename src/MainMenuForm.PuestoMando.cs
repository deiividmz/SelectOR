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
            // (1.2.91) un botón con su icono y su texto (MainMenuForm.SalaBotones.cs); este, el último: ya están los tres
            _salaDispBtn = new SalaButton("🖥", Tr("Puesto de mando"));
            _salaDispBtn.Click += (s, e) => OpenDispatch();
            new ToolTip().SetToolTip(_salaDispBtn, Tr("Puesto de mando de la sala"));
            _salaHost.Controls.Add(_salaDispBtn);
            _salaHost.Resize += (s, e) => SalaButtonsLayout();
            _salaHost.HandleCreated += (s, e) => SalaButtonsLayout();
            SalaButtonsLayout();
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
            if ((Supa.IsSuperadmin || Supa.IsObjectEditor) && Supa.IsLoggedIn) _dispatch.OpenObjects = OpenObjetosSala;
            _dispatch.MineOnlyGet = () => _prefs?.PuestoSoloMias ?? false;   // (2026-11-08) «Solo las mías»
            _dispatch.MineOnlySet = v => { if (_prefs == null) return; _prefs.PuestoSoloMias = v; try { _prefs.Save(); } catch { } };   // (2026-11-04) 🚧 objetos de la sala (1.2.91: y los autorizados)
            _dispatch.Show(this);
        }
    }
}
