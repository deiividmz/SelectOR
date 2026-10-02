// Una sesión por conexión (sql/una-cuenta-por-ip.sql): si otra cuenta inicia sesión desde la misma IP,
// el servidor cierra esta. Aquí se comprueba cada minuto (session_ok) y, si se ha cerrado, se sale de
// Empresas con un aviso. No se vuelve a entrar solo: echaría a la otra cuenta y empezaría un tira y afloja.
// Si el servidor aún no tiene la comprobación (falta el SQL), no pasa nada.

using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        Timer _sessionCheck;
        bool _sessionChecking;

        void StartSessionCheck()
        {
            if (_sessionCheck != null || !Supa.IsLoggedIn) return;
            _sessionCheck = new Timer { Interval = 60000 };
            _sessionCheck.Tick += async (s, e) => await SessionCheck();
            _sessionCheck.Start();
        }

        void StopSessionCheck()
        {
            try { _sessionCheck?.Stop(); _sessionCheck?.Dispose(); } catch { }
            _sessionCheck = null;
        }

        async Task SessionCheck()
        {
            if (_sessionChecking || !Supa.IsLoggedIn) return;
            _sessionChecking = true;
            try
            {
                var (json, err) = await Supa.RpcAsync("session_ok", new { });
                if (err == null && json != null && json.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)) SessionClosedByServer();
            }
            catch { }
            finally { _sessionChecking = false; }
        }

        void SessionClosedByServer()
        {
            StopSessionCheck();
            StopRealtime();
            StopNotifications();
            Supa.SignOut();
            _empAutoTried = true;   // no volver a entrar solo en esta ejecución
            _empLoaded = false;
            string body = Tr("Otra cuenta ha iniciado sesión desde esta misma conexión a Internet. Solo se permite una sesión por conexión.");
            try { RefreshEmpresasView(); } catch { }
            try { if (_empAuthMsg != null) Msg(_empAuthMsg, Tr("Se ha cerrado tu sesión de Empresas.") + " " + body, true); } catch { }
            try
            {
                EnqueueToast(new NotificationToast("🔒", Tr("Se ha cerrado tu sesión de Empresas"), body,
                                                   "SelectOR · " + Tr("Empresas"), Color.FromArgb(235, 150, 80)));
            }
            catch { }
        }

        // Registro rechazado por el servidor: ya hay una cuenta creada desde esta conexión.
        static bool IsOneAccountPerIpError(string err) =>
            err != null && err.IndexOf("una cuenta desde esta conexi", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
