// Actualización automática al arrancar: mientras se ve la pantalla de carga, SelectOR mira si hay una
// versión publicada más nueva y, si la hay, la descarga, comprueba su firma, la instala y se reinicia en
// ella, sin preguntar. La barra de la pantalla de carga enseña el progreso.
//  · Si no hay conexión o el servidor tarda, se sigue arrancando la versión actual (no se espera más de
//    unos segundos a saber si hay versión nueva).
//  · Si la descarga o la instalación fallan, no se toca nada y se arranca la versión actual; el aviso de
//    actualización de siempre la volverá a ofrecer más tarde.
//  · Con Open Rails abierto no se actualiza (sus archivos podrían estar en uso).
//  · SELECTOR_NO_AUTOUPDATE=1 lo desactiva (pruebas).

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace SelectOR
{
    static class StartupUpdate
    {
        const int CheckTimeoutMs = 6000;

        /// <summary>true = la versión nueva está instalada y hay que reiniciar (Program la arranca y termina).</summary>
        public static bool Run()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("SELECTOR_NO_AUTOUPDATE") == "1") return false;
                if (OpenRailsRunning()) return false;
                DetectLanguage();
                Updater.CleanupOld();

                SplashScreen.Report(1, 4, I18n.T("Buscando actualizaciones…"));
                var check = Task.Run(Updater.CheckAsync);
                if (!check.Wait(CheckTimeoutMs)) return false;
                var r = check.Result;
                if (r == null) return false;

                string ver = r.Version ?? "";
                SplashScreen.Reset(string.Format(I18n.T("Actualizando SelectOR a la versión {0}…"), ver));
                var progress = new SyncProgress(st =>
                {
                    switch (st.Phase)
                    {
                        case UpdatePhase.Downloading:
                            int pct = st.Total > 0 ? (int)(st.Got * 100 / st.Total) : 0;
                            SplashScreen.Report(3 + pct * 80 / 100, 84, string.Format(I18n.T("Descargando la versión {0}… {1} %"), ver, pct));
                            break;
                        case UpdatePhase.Verifying: SplashScreen.Report(86, 90, I18n.T("Comprobando la firma de la actualización…")); break;
                        case UpdatePhase.Installing: SplashScreen.Report(91, 98, I18n.T("Instalando la actualización…")); break;
                        case UpdatePhase.Done: SplashScreen.Report(100, 100, I18n.T("Reiniciando SelectOR…")); break;
                    }
                });
                string err = Updater.DownloadAndInstallAsync(r, progress).GetAwaiter().GetResult();
                if (err == null && Updater.RestartPending)
                {
                    Thread.Sleep(700);   // que se vea «Reiniciando…»
                    return true;
                }
                // No se ha podido: se avisa un momento y se arranca la versión actual desde cero.
                SplashScreen.Reset(I18n.T("No se ha podido actualizar; se abre la versión actual."));
                Thread.Sleep(1500);
                SplashScreen.Reset("Preparando SelectOR…");
                return false;
            }
            catch
            {
                try { SplashScreen.Reset("Preparando SelectOR…"); } catch { }
                return false;
            }
        }

        static bool OpenRailsRunning()
        {
            try
            {
                foreach (var n in new[] { "RunActivity", "RunActivityLAA" })
                    if (Process.GetProcessesByName(n).Length > 0) return true;
            }
            catch { }
            return false;
        }

        // El idioma de Open Rails (registro), como lo decide luego el menú: español solo si OR está en español.
        static void DetectLanguage()
        {
            try
            {
                string lang = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\OpenRails\ORTS", "Language", null) as string;
                if (string.IsNullOrEmpty(lang)) lang = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                I18n.English = !lang.StartsWith("es", StringComparison.OrdinalIgnoreCase);
                var force = Environment.GetEnvironmentVariable("SELECTOR_LANG");
                if (!string.IsNullOrEmpty(force)) I18n.English = !force.StartsWith("es", StringComparison.OrdinalIgnoreCase);
            }
            catch { }
        }

        // IProgress que llama al momento (Progress<T> necesita un contexto de sincronización y aquí no lo hay).
        sealed class SyncProgress : IProgress<UpdateStep>
        {
            readonly Action<UpdateStep> _a;
            public SyncProgress(Action<UpdateStep> a) { _a = a; }
            public void Report(UpdateStep value) { try { _a(value); } catch { } }
        }
    }
}
