// Usuarios conectados (conectados.sql): cada minuto SelectOR avisa al servidor con un identificador al azar de esta
// instalación (haya iniciado sesión o no) y recibe cuántos SelectOR hay abiertos y cuántos servicios en marcha; la
// portada («Elige una ruta para empezar») lo enseña en directo. Sin el SQL en el servidor, no se enseña nada.

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Timer _presenceTimer;
        bool _presenceMissing;

        void StartPresence()
        {
            if (_presenceTimer != null || !Supa.IsConfigured) return;
            if (string.IsNullOrEmpty(_prefs.InstallId)) { _prefs.InstallId = Guid.NewGuid().ToString(); try { _prefs.Save(); } catch { } }
            _presenceTimer = new Timer { Interval = 60000 };
            _presenceTimer.Tick += async (s, e) => await PresencePing();
            _presenceTimer.Start();
            BeginInvoke((Action)(async () => { await Task.Delay(2500); await PresencePing(); }));   // la primera, al poco de abrir
        }

        async Task PresencePing()
        {
            if (_presenceMissing || IsDisposed) return;
            try
            {
                var (json, err) = await Supa.RpcAsync("presence_ping", new { p_install = _prefs.InstallId, p_version = Updater.CurrentVersionText });
                if (err != null)
                {
                    if (err.Contains("PGRST202") || err.Contains("Could not find the function")) { _presenceMissing = true; _presenceTimer?.Stop(); }
                    return;
                }
                using var d = JsonDocument.Parse(json);
                var r = d.RootElement;
                if (r.ValueKind != JsonValueKind.Object) return;
                int online = (int)Num(r, "online"), driving = (int)Num(r, "driving");
                _rutaWelcome?.SetLive(Math.Max(1, online), driving);   // (como mínimo, este)
            }
            catch { }
        }

        // Accesos directos de la portada: en el navegador.
        void OpenLink(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { ThemedBox.Show(this, Tr("No se pudo abrir el enlace: ") + ex.Message, "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
