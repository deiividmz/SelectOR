// Conexión aparte con el servidor de una sala de «Servidores SelectOR» (no es un jugador: el servidor la reconoce por su
// primer mensaje, «SELECTOR HELLO»). Hoy sirve para el HUD (WHO → «SELECTOR YOU»: dentro / entrando / fuera y si eres
// el despachador) y para la contraseña de las salas libres (GATE OWNER). Sabe también pedir desvíos (WANT / PASSED /
// CANCEL / FIX), pero desde la 1.2.63 SelectOR ya no se los pide (los desvíos según el itinerario están aparcados).
// Con un servidor que no la conoce (el de Open Rails), no contesta «READY» y se retira.
// Mensajes: los mismos de Open Rails (texto UTF-16 con la longitud delante); ver «SelectOR Servidor MP\Interlock.cs».

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SelectOR
{
    public sealed class SwitchPilot : IDisposable
    {
        public enum State { Asked, Granted, Waiting, Refused }
        public sealed class Status { public State State; public int Sel; public string Holder; public int HolderPrio; public DateTime Since = DateTime.UtcNow; }

        readonly string _host; readonly int _port; readonly string _name;
        int _prio;
        TcpClient _tcp; NetworkStream _ns;
        readonly object _wl = new();
        CancellationTokenSource _cts;
        DateTime _nextTry = DateTime.MinValue;
        bool _starting;
        public bool Ready { get; private set; }
        public bool Unsupported { get; private set; }   // el servidor no tiene piloto de desvíos
        readonly Dictionary<string, Status> _status = new();
        readonly HashSet<string> _asked = new();
        public event Action<string, Status, Status> Changed;   // desvío, antes, ahora (en el hilo de la conexión)
        // Posición real de todos los desvíos según el despachador (SWITCHSTATES de Open Rails, reenviado por el servidor):
        // un byte por desvío en el orden de su índice del .tdb. null hasta el primero.
        public byte[] States { get; private set; }
        public DateTime StatesAt { get; private set; }
        // ¿Está mi Open Rails dentro de la sala? (respuesta a «WHO»): «in», «wait» (entrando) u «out»; null = sin saber.
        public string Presence { get; private set; }
        public bool IsDispatcher { get; private set; }
        public DateTime PresenceAt { get; private set; }
        DateTime _whoAt = DateTime.MinValue;
        // La sala pasa a protegerla yo (se fue quien la abrió): token y contraseña (null = sin contraseña).
        public event Action<string, string> GateOwner;
        public int LocalPort { get { try { return (_tcp?.Client?.LocalEndPoint as System.Net.IPEndPoint)?.Port ?? 0; } catch { return 0; } } }

        public SwitchPilot(string host, int port, string name, int prio) { _host = host; _port = port; _name = name; _prio = prio; }

        public static string Key(int tx, int tz, int id) => tx + " " + tz + " " + id;

        public int Prio
        {
            get => _prio;
            set { if (_prio == value) return; _prio = value; if (Ready) Send($"HELLO {_name} {_prio} 2"); }
        }

        // Se conecta (una vez; si se cae, lo reintenta cada 10 s). Solo con Open Rails ya dentro de la sala.
        public void EnsureStarted()
        {
            if (Unsupported || _starting || (_tcp != null && _tcp.Connected) || DateTime.UtcNow < _nextTry) return;
            _starting = true; _nextTry = DateTime.UtcNow.AddSeconds(10);
            _ = Task.Run(Connect);
        }

        async Task Connect()
        {
            try
            {
                Close();
                var tcp = new TcpClient { NoDelay = true };
                using (var to = new CancellationTokenSource(5000)) await tcp.ConnectAsync(_host, _port, to.Token);
                _tcp = tcp; _ns = tcp.GetStream(); _cts = new CancellationTokenSource();
                Send($"HELLO {_name} {_prio} 2");
                var rx = Task.Run(() => Receive(_cts.Token));
                await Task.Delay(4000);
                if (!Ready && _tcp == tcp) { Unsupported = true; Close(); }   // no es un servidor de SelectOR
            }
            catch { Close(); }
            finally { _starting = false; }
        }

        async Task Receive(CancellationToken tok)
        {
            var dec = Encoding.Unicode.GetDecoder();
            var buf = new byte[8192]; var chars = new char[8192];
            var sb = new StringBuilder();
            try
            {
                while (!tok.IsCancellationRequested)
                {
                    int n = await _ns.ReadAsync(buf.AsMemory(0, buf.Length), tok);
                    if (n <= 0) break;
                    int c = dec.GetChars(buf, 0, n, chars, 0);
                    sb.Append(chars, 0, c);
                    string body;
                    while ((body = NextFrame(sb)) != null) OnMessage(body);
                    if (sb.Length > 200_000) sb.Clear();
                }
            }
            catch { }
            lock (_wl) { Ready = false; _asked.Clear(); }
            Presence = null;
        }

        // Como lo lee Open Rails: primer «:», las cifras justo antes son la longitud de lo que va detrás de «: ».
        internal static string NextFrame(StringBuilder sb)
        {
            string s = sb.ToString();
            int i = s.IndexOf(':');
            if (i < 0) return null;
            int j = i; while (j > 0 && char.IsDigit(s[j - 1])) j--;
            if (j == i || !int.TryParse(s.AsSpan(j, i - j), out int len)) { sb.Remove(0, i + 1); return NextFrame(sb); }
            if (i + 2 + len > s.Length) return null;
            string body = s.Substring(i + 2, len);
            sb.Remove(0, i + 2 + len);
            return body;
        }

        void OnMessage(string body)
        {
            if (!body.StartsWith("SELECTOR ", StringComparison.Ordinal)) return;
            var t = body.Substring(9).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) return;
            if (t[0] == "READY") { Ready = true; return; }
            if (t[0] == "STATES" && t.Length >= 2) { var st = DecodeStates(t[1]); if (st != null) { States = st; StatesAt = DateTime.UtcNow; } return; }
            if (t[0] == "YOU" && t.Length >= 3) { Presence = t[1]; IsDispatcher = t[2] == "d"; PresenceAt = DateTime.UtcNow; return; }
            if (t[0] == "GATE" && t.Length >= 4 && t[1] == "OWNER") { try { GateOwner?.Invoke(t[2], t[3] == "-" ? null : t[3]); } catch { } return; }
            if (t.Length < 4) return;
            string key = t[1] + " " + t[2] + " " + t[3];
            Status now = t[0] switch
            {
                "GRANTED" => new Status { State = State.Granted, Sel = t.Length > 4 && int.TryParse(t[4], out int s1) ? s1 : -1 },
                "WAIT" => new Status { State = State.Waiting, Holder = t.Length > 4 ? t[4] : "?", HolderPrio = t.Length > 5 && int.TryParse(t[5], out int p) ? p : 1 },
                "REFUSED" => new Status { State = State.Refused },
                _ => null
            };
            if (now == null) return;
            Status before;
            lock (_wl)
            {
                _status.TryGetValue(key, out before);
                if (before != null && before.State == now.State && before.Sel == now.Sel && before.Holder == now.Holder) now.Since = before.Since;   // (cada 2 s llega igual)
                _status[key] = now;
            }
            try { Changed?.Invoke(key, before, now); } catch { }
        }

        // Como lo lee Open Rails (MSGSwitchStatus): base64 de [int32][gzip con un byte por desvío].
        static byte[] DecodeStates(string b64)
        {
            try
            {
                var raw = Convert.FromBase64String(b64);
                using var ms = new System.IO.MemoryStream(raw, 4, raw.Length - 4);
                using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var outp = new System.IO.MemoryStream();
                gz.CopyTo(outp);
                return outp.ToArray();
            }
            catch { return null; }
        }

        // Cada vuelta de la hoja de ruta: los desvíos que tiene por delante (o aún bajo el tren) y en qué posición, y
        // los que ya ha dejado atrás. Lo que deja de pedirse sin pasarlo (itinerario nuevo) se suelta.
        public void Tick(IEnumerable<(int tx, int tz, int id, int sel, double dist, double eta)> ahead, IEnumerable<(int tx, int tz, int id)> passed)
        {
            if (!Ready) return;
            if ((DateTime.UtcNow - _whoAt).TotalSeconds >= 3) { _whoAt = DateTime.UtcNow; Send("WHO " + _name); }   // ¿sigo dentro? (el HUD lo enseña)
            var now = new HashSet<string>();
            foreach (var a in ahead)
            {
                string k = Key(a.tx, a.tz, a.id);
                now.Add(k);
                Send(string.Create(CultureInfo.InvariantCulture, $"WANT {k} {a.sel} {Math.Round(a.dist)} {Math.Round(Math.Max(0, a.eta))}"));
            }
            var gone = new HashSet<string>(passed.Select(p => Key(p.tx, p.tz, p.id)));
            List<string> drop;
            lock (_wl) { drop = _asked.Where(k => !now.Contains(k)).ToList(); _asked.Clear(); foreach (var k in now) _asked.Add(k); }
            foreach (var k in drop)
            {
                Send((gone.Contains(k) ? "PASSED " : "CANCEL ") + k);
                lock (_wl) _status.Remove(k);
            }
        }

        // El despachador no lo tiene como se pidió: que el servidor se lo vuelva a mandar.
        public void Fix(int tx, int tz, int id) { if (Ready) Send("FIX " + Key(tx, tz, id)); }

        public Status StatusOf(int tx, int tz, int id) { lock (_wl) return _status.TryGetValue(Key(tx, tz, id), out var s) ? s : null; }

        void Send(string body)
        {
            try
            {
                string msg = "SELECTOR " + body;
                var data = Encoding.Unicode.GetBytes(" " + msg.Length + ": " + msg);
                lock (_wl) { _ns?.Write(data, 0, data.Length); }
            }
            catch { Close(); }
        }

        void Close()
        {
            try { _cts?.Cancel(); } catch { }
            try { _tcp?.Close(); } catch { }
            lock (_wl) { _tcp = null; _ns = null; Ready = false; _asked.Clear(); }
            Presence = null;
        }

        public void Dispose() { Unsupported = true; Close(); }
    }
}
