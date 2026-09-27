// Aviso de actualizaciones: comprueba al arrancar y cada 3 horas si hay una versión publicada
// más nueva; si la hay, muestra el aviso (no mientras Open Rails está en marcha).

using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        System.Windows.Forms.Timer _updTimer;
        string _updDismissedVersion;   // «Más tarde» → no volver a insistir con esa versión en esta sesión
        bool _updChecking, _updShowing;

        void StartUpdateChecks()
        {
            Updater.CleanupOld();
            if (_updTimer != null) return;
            _updTimer = new System.Windows.Forms.Timer { Interval = 5000 };   // primera comprobación a los 5 s
            _updTimer.Tick += (s, e) =>
            {
                _updTimer.Interval = 3 * 60 * 60 * 1000;
                CheckForUpdates(manual: false);
            };
            _updTimer.Start();
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

        async void CheckForUpdates(bool manual)
        {
            if (_updChecking || _updShowing) return;
            if (!manual && OpenRailsRunning()) return;   // no molestar en plena conducción
            _updChecking = true;
            ReleaseInfo r;
            try { r = await Updater.CheckAsync(); }
            finally { _updChecking = false; }

            if (r == null)
            {
                if (manual)
                    MessageBox.Show(this, string.Format(Tr("Tienes la última versión de SelectOR ({0})."), Updater.CurrentVersionText),
                        "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!manual && r.Version == _updDismissedVersion) return;

            _updShowing = true;
            bool installed;
            try
            {
                using var dlg = new UpdateDialog(r, UpdateBlockReason);
                dlg.ShowDialog(this);
                installed = dlg.Installed;
                if (!installed) _updDismissedVersion = r.Version;
            }
            finally { _updShowing = false; }

            if (installed) Close();   // la nueva versión ya se ha lanzado; se guardan las prefs al cerrar
        }

        // Motivo por el que ahora no se puede actualizar (null = se puede).
        string UpdateBlockReason()
        {
            if (OpenRailsRunning()) return Tr("Cierra Open Rails antes de actualizar.");
            if (_pendingServiceId != null) return Tr("Registra el servicio en curso antes de actualizar.");
            return null;
        }
    }
}
