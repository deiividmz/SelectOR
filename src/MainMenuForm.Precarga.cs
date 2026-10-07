// Precarga de Empresas durante la pantalla de inicio: con la sesión recordada, se entra y se traen los datos de
// cada sección (servicios, socios, banca, ranking, perfil, flota, normas, rutas y, para el administrador, su
// administración) antes de enseñar el menú. Así, la primera vez que se abre cada sección ya tiene sus datos y solo
// se actualiza por detrás. La pantalla de inicio espera a que no quede ninguna petición en curso, con un tope para
// que una conexión lenta no la deje colgada. Sin sesión recordada no se espera nada.
// (Montar y dibujar las secciones a escondidas se ha medido: no mejora la primera visita, la empeora.)

using System;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int PreloadMaxMs = 12000;

        async void PreloadEmpresas()
        {
            var until = DateTime.UtcNow.AddMilliseconds(PreloadMaxMs);
            bool Tiempo() => DateTime.UtcNow < until;
            // Hasta que no quede ninguna petición al servidor en curso (y siga así un momento).
            async Task Calma()
            {
                int quietos = 0;
                while (Tiempo() && quietos < 4)
                {
                    await Task.Delay(100);
                    quietos = Supa.InFlight == 0 ? quietos + 1 : 0;
                }
            }
            try
            {
                // Solo con la sesión recordada (es lo que inicia sesión al arrancar): sin ella no hay nada que traer.
                if (!_prefs.RememberPassword || string.IsNullOrEmpty(_prefs.EmpresasEmail) || !Supa.IsConfigured) return;
                LoadLog("empresas: precarga");
                // el inicio de sesión automático (lo lanza InitData)
                while (_empConnecting && Tiempo()) await Task.Delay(100);
                if (!Supa.IsLoggedIn) return;
                // empresas, la elegida, sus servicios y socios (si nadie las ha pedido todavía, se piden aquí)
                for (int i = 0; i < 5 && !_empLoaded; i++) await Task.Delay(100);
                if (!_empLoaded) LoadCompanies();
                while (!_empLoaded && Tiempo()) await Task.Delay(100);
                await Calma();
                if (!Supa.IsLoggedIn) return;
                // los datos de las demás secciones (solo lectura: nada que cambie el servidor)
                if (_empSel != null)
                {
                    LoadLedger();
                    LoadFleet();
                    LoadRules();
                    if (CanManage() || Supa.IsSuperadmin) LoadPurchaseRequests();
                    if (Supa.IsSuperadmin || _routeMode != "collect") LoadCoRoutes();
                }
                LoadRankTab();
                LoadProfile();
                if (Supa.IsSuperadmin)
                {
                    LoadReview(onlyCount: true); LoadLoansAdmin(onlyCount: true);
                    LoadCatalog();
                    LoadUsers(); LoadAllCompanies();
                }
                await Task.Delay(150);
                await Calma();
                LoadLog("empresas: precarga hecha");
            }
            catch { }
            finally { LoadStep("empresas"); }
        }

    }
}
