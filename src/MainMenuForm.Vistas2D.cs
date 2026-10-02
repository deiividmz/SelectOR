// Todo lo posible antes de abrir el menú (durante la pantalla de inicio): las pestañas ya montadas y dibujadas
// una vez (la primera visita era la más lenta), la clasificación de todos los trenes (tipo, masa, longitud) y
// las vistas 2D de los primeros trenes de Exploración y de las primeras máquinas de Compra (dibujadas o leídas
// de la caché de disco). Después no queda nada trabajando en segundo plano: la interfaz queda libre.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int Prewarm2DTrains = 48, Prewarm2DMachines = 30;
        const int Prewarm2DMaxMs = 8000;      // nunca retrasa la apertura más de esto

        async void Prewarm2D()
        {
            try { WarmPages(); } catch { }
            try
            {
                var trains = _lstConsists != null ? _lstConsists.PrefetchAsync(Prewarm2DTrains) : Task.CompletedTask;
                var machines = Task.Run(async () =>
                {
                    // las máquinas de Compra salen de PrewarmFleetLists (formaciones fijas ya calculadas)
                    for (int i = 0; i < 100 && _fleetPre.engs == null; i++) await Task.Delay(100);
                    var engs = _fleetPre.engs;
                    if (engs == null || _thumbs == null) return;
                    var paths = engs.Select(e => e.path).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(Prewarm2DMachines).ToList();
                    await Task.WhenAll(paths.Select(p => _thumbs.FileAsync(p)));
                });
                // tipo, masa y longitud de todos los trenes (filtros y fichas listos al abrir): de la caché, casi al
                // momento; la primera vez, unos segundos
                var classify = Task.Run(async () => { while (_classTotal > 0 && _classDone < _classTotal) await Task.Delay(100); });
                await Task.WhenAny(Task.WhenAll(trains, machines, classify), Task.Delay(Prewarm2DMaxMs));
            }
            catch { }
            finally { LoadStep("vistas2d"); }
        }

        // Cada pestaña se monta una vez con la ventana aún oculta (controles, maquetación, horario, editor…)
        // y se vuelve a la que toca. Multijugador (pide la lista a internet) y Empresas (sesión) no.
        void WarmPages()
        {
            if (_uiRevealed) return;
            int keep = _activePage, keepTab = _prefs.LastTab;
            // Además de montarla, se dibuja una vez fuera de pantalla: así quedan hechas las cachés de textos,
            // imágenes escaladas y emojis que el primer pintado de cada pestaña pagaba al abrirla.
            void Paint()
            {
                int w = Math.Max(1, _pageHost.Width), h = Math.Max(1, _pageHost.Height);
                if (w < 50 || h < 50) return;
                using var bmp = new System.Drawing.Bitmap(w, h);
                _pageHost.DrawToBitmap(bmp, new System.Drawing.Rectangle(0, 0, w, h));
            }
            // Con la ventana oculta, mostrar la pestaña no la maqueta: se le da ya el tamaño del hueco y se maqueta.
            void Fit(int p)
            {
                Control pg = p switch { 0 => _pageRuta, 1 => _pageActividad, 2 => _pageExplora, 3 => _pageHorarios, _ => _pageEditor };
                pg.Bounds = _pageHost.ClientRectangle;
                pg.PerformLayout();
            }
            foreach (int p in new[] { 0, 1, 2, 3, PageEditor })
            {
                try { if (p != keep) ShowPage(p); Fit(p); Paint(); } catch { }
            }
            try { ShowPage(keep); Fit(keep); Paint(); } catch { }
            _prefs.LastTab = keepTab;
        }
    }
}
