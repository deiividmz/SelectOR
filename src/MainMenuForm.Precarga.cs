// Precarga de Empresas durante la pantalla de inicio: con la sesión recordada, se entra y se traen los datos de
// cada sección (servicios, socios, banca, ranking, perfil, flota, normas, rutas y, para el administrador, su
// administración) antes de enseñar el menú. Así, la primera vez que se abre cada sección ya tiene sus datos y solo
// se actualiza por detrás. La pantalla de inicio espera a que no quede ninguna petición en curso, con un tope para
// que una conexión lenta no la deje colgada. Sin sesión recordada no se espera nada.
// Después, con los datos ya en casa, cada sección se maqueta y se dibuja una vez a escondidas a su tamaño real
// (WarmEmpresas): la primera visita a cada una ya va tan rápida como las siguientes. (Un intento anterior la
// empeoraba porque se maquetaba con otro tamaño y sin datos: al abrirla se volvía a maquetar entera.)

using System;
using System.Threading.Tasks;
using System.Windows.Forms;

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
                WarmEmpresas();
                await Calma();   // (por si alguna sección pidió algo al enseñarse)
                LoadLog("empresas: secciones maquetadas y dibujadas");
            }
            catch { }
            finally { LoadStep("empresas"); }
        }

        bool _warmingEmp;      // precarga visual de Empresas en curso: las secciones no piden datos al enseñarse
        bool[] _empSubShow;    // qué secciones ve este usuario (RefreshSubtabs)

        void WarmEmpresas()
        {
            if (_uiRevealed) return;
            WarmEmpresasCore();
        }

        // Empresas y cada una de sus secciones: a la vista una vez, maquetadas al tamaño del hueco y dibujadas fuera de
        // pantalla (como WarmPages con las demás pestañas). Luego se vuelve a la pestaña y la sección que tocaban.
        internal void WarmEmpresasCore()
        {
            if (_pageEmpresas == null || _pageHost == null || !Supa.IsLoggedIn || _empSubtabs == null) return;
            int keepPage = _activePage, keepTab = _prefs.LastTab, keepSub = _empSubtab;
            void FitPaint(Control pg)
            {
                pg.Bounds = _pageHost.ClientRectangle;
                pg.PerformLayout();
                int w = _pageHost.Width, h = _pageHost.Height;
                if (w < 50 || h < 50) return;
                using var bmp = new System.Drawing.Bitmap(w, h);
                _pageHost.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, w, h));
            }
            _warmingEmp = true;
            try
            {
                using (Perf.T("WarmEmpresas · pestaña")) { ShowPage(PageEmpresas); FitPaint(_pageEmpresas); }
                var show = _empSubShow;
                if (show != null)
                {
                    for (int k = 0; k < _empSubtabs.Length && k < show.Length; k++)
                    {
                        if (_empSubtabs[k] == null || !show[k] || k == keepSub) continue;
                        try { using (Perf.T("WarmEmpresas · sección " + k)) { ShowSubtab(k); FitPaint(_pageEmpresas); } } catch { }
                    }
                    int back = keepSub >= 0 && keepSub < show.Length && show[keepSub] ? keepSub : Array.IndexOf(show, true);
                    if (back >= 0) { ShowSubtab(back); FitPaint(_pageEmpresas); }
                }
            }
            catch { }
            finally
            {
                _warmingEmp = false;
                try
                {
                    ShowPage(keepPage);
                    Control pg = keepPage switch { 0 => _pageRuta, 1 => _pageActividad, 2 => _pageExplora, 3 => _pageHorarios, 4 => _pageMulti, PageEditor => _pageEditor, _ => _pageEmpresas };
                    pg.Bounds = _pageHost.ClientRectangle; pg.PerformLayout();
                }
                catch { }
                _prefs.LastTab = keepTab;
            }
        }
    }
}
