// El maquinista cancela su servicio en curso (sql/cancelar-servicio.sql): en lugar de registrarlo, se elimina
// en el servidor como un viaje corto (sin ingresos, sin km, sin ranking) y la unidad queda libre. Desde la barra
// superior mientras conduce (sigue en conducción libre, como al registrar a mitad de viaje) o desde Empresas,
// junto a «Registrar servicio», si el servicio quedó pendiente. Las infracciones detectadas sí se mandan antes.

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        RoundButton _empCancelOpenBtn;

        // Mientras conduce (barra superior; la barra ya ha pedido la segunda pulsación).
        async Task<(bool ok, string msg)> CancelServiceMidRun()
        {
            if (_pendingServiceId == null) return (false, null);
            _tripDurationS = ServiceSeconds();
            _estimatedKm = Math.Round(_trackedMeters / 1000.0, 1);
            var (ok, msg) = await CancelOwnServiceCore();
            if (!ok) return (false, msg);

            // Vuelta a conducción libre (como tras «Registrar servicio» a mitad de viaje).
            _paxBoarded = 0; _paxKm = 0;
            _fleetCheckCompany = null;
            bool bigWasOpen = HudAlive && _serviceHud.BigMapOpen;
            bool hudWasHidden = HudAlive && !_serviceHud.Visible;
            ShowServiceHud(false, force: true);
            if (HudAlive)
            {
                if (hudWasHidden) _serviceHud.Hide();
                if (bigWasOpen) _serviceHud.ToggleBigMap();
            }
            return (true, msg);
        }

        // Desde Empresas: un servicio que quedó pendiente (no se pudo registrar al cerrar Open Rails).
        async void CancelPendingService()
        {
            if (_pendingServiceId == null) return;
            if (MessageBox.Show(this, Tr("¿Cancelar el servicio en curso? No se registrará: ni ingresos, ni km, ni ranking, y la unidad quedará libre. Las infracciones detectadas sí cuentan."),
                    "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            Msg(_empHomeMsg, Tr("Cancelando el servicio…"), false);
            var (ok, msg) = await CancelOwnServiceCore();
            Msg(_empHomeMsg, msg, !ok);
            if (ok) { _empOnDutyCompany = null; UpdateDutyUi(); UpdateStatus(); }
        }

        async Task<(bool ok, string msg)> CancelOwnServiceCore()
        {
            var svc = _pendingServiceId;
            if (svc == null) return (false, null);
            // Las infracciones detectadas cuentan igual que al registrar (cancelar no sirve para librarse de
            // ellas); en un viaje corto, como siempre, no cuenta ninguna.
            int durRule = ServiceSecondsForRule();
            bool corto = _estimatedKm < MinServiceKm || durRule < MinServiceSeconds;
            var infr = await ReportInfractionsAsync(svc, _estimatedKm, durRule == int.MaxValue ? _tripDurationS : durRule, corto);
            if (infr.Err != null) return (false, Tr("No se pudo cancelar el servicio: ") + infr.Err);
            if (!infr.Voided)   // anulado por una infracción: el servidor ya lo ha borrado
            {
                var (_, err) = await Supa.RpcAsync("cancel_my_service", new { p_service = svc });
                if (err != null)
                {
                    bool sinFuncion = err.IndexOf("PGRST202", StringComparison.OrdinalIgnoreCase) >= 0
                                      || err.IndexOf("Could not find the function", StringComparison.OrdinalIgnoreCase) >= 0;
                    return (false, sinFuncion ? Tr("No se puede cancelar: el servidor aún no está actualizado.")
                                              : Tr("No se pudo cancelar el servicio: ") + err);
                }
            }
            _pendingServiceId = null; _svcOpenedUtc = null; DeleteServiceJournal();
            _estimatedKm = 0;
            UpdateDutyUi();
            LoadCompanies(soft: true);
            RefreshActiveSubtab(skipCompanyLists: true);
            string m = Tr("Servicio cancelado: no se ha registrado y la unidad queda libre.");
            if (infr.Items != null && infr.Items.Count > 0) m += " " + Tr("Las infracciones detectadas sí cuentan.");
            return (true, m);
        }

        // Botón rojo junto a «Registrar servicio» (Empresas), con la misma visibilidad.
        void BuildCancelPendingButton(Control parent)
        {
            _empCancelOpenBtn = new RoundButton
            {
                Text = Tr("Cancelar servicio"), Radius = 12, Size = new Size(170, 50),
                BaseColor = Theme.Surface2, HoverColor = Color.FromArgb(176, 64, 64), ActiveColor = Color.FromArgb(150, 52, 52),
                TextColor = Color.FromArgb(235, 120, 120), FontSize = 11f, FontStyle = FontStyle.Bold, Visible = false, Margin = new Padding(0, 0, 14, 0)
            };
            _empCancelOpenBtn.Click += (s, e) => CancelPendingService();
            parent.Controls.Add(_empCancelOpenBtn);
        }
    }
}
