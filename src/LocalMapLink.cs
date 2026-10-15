// Conducción libre: la conexión con el agente del mapa dentro de Open Rails (MapAgent\LocalLink.cs), en este mismo
// ordenador. SelectOR escucha en 127.0.0.1 (un puerto libre cualquiera) y le pasa a Open Rails el puerto y una clave
// al lanzarlo (SELECTOR_MAP_PORT / SELECTOR_MAP_TOKEN); el agente se conecta y dice la clave («HELLO clave»): una
// conexión sin ella se cierra. Por aquí llegan la posición de las agujas, el aspecto de las señales, las agujas
// enclavadas y los trenes de la IA, y salen las órdenes del mapa grande (THROW / SIGNAL), con su respuesta (RES), y
// las de los mandos (CAB / BTN; en cualquier modo, sin lo del mapa fuera de Conducción libre: SELECTOR_MAP_NOMAP=1).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SelectOR
{
    public sealed class LocalMapLink : IDisposable
    {
        readonly TcpListener _listen;
        TcpClient _c; StreamWriter _w;
        readonly object _lk = new();
        volatile bool _disposed;
        public int Port { get; }
        public string Token { get; }
        public bool Connected { get; private set; }
        public DateTime ConnectedAt { get; private set; }
        public byte[] States { get; private set; }        // posición de cada aguja (orden de SWITCHSTATES)
        public DateTime StatesAt { get; private set; }
        public byte[] SigStates { get; private set; }     // dos bytes por cabeza (estado + 1, dibujo + 1), como en la red
        public DateTime SigStatesAt { get; private set; }
        public string Blocked { get; private set; }       // una letra por aguja: «.» · S · T · O
        public List<(string name, int tx, int tz, double x, double z, double kmh, bool player)> Trains { get; private set; } = new();
        public Dictionary<string, (int node, int mu, int dir, double len)> TrainTrack { get; private set; } = new();   // («@yo» = tu tren)
        // Respuesta de Open Rails a una orden: «THROW» o «SIGNAL», número, posición u orden, resultado (ok · S · T · O · no).
        public event Action<string, int, int, string> Result;
        public event Action Changed;

        LocalMapLink(TcpListener l)
        {
            _listen = l; Port = ((IPEndPoint)l.LocalEndpoint).Port;
            Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _ = Task.Run(AcceptLoop);
        }

        // null = no se ha podido escuchar.
        public static LocalMapLink Start()
        {
            try { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); return new LocalMapLink(l); }
            catch { return null; }
        }

        async Task AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient c;
                try { c = await _listen.AcceptTcpClientAsync(); }
                catch { if (_disposed) return; await Task.Delay(500); continue; }
                _ = Task.Run(() => Serve(c));
            }
        }

        async Task Serve(TcpClient c)
        {
            try
            {
                c.NoDelay = true;
                var ns = c.GetStream();
                var r = new StreamReader(ns, new UTF8Encoding(false));
                var first = r.ReadLineAsync();
                if (await Task.WhenAny(first, Task.Delay(5000)) != first || first.Result != "HELLO " + Token) { c.Close(); return; }
                lock (_lk)
                {
                    try { _c?.Close(); } catch { }
                    _c = c; _w = new StreamWriter(ns, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                    Connected = true; ConnectedAt = DateTime.UtcNow;
                }
                string line;
                while (!_disposed && (line = await r.ReadLineAsync()) != null) OnLine(line);
            }
            catch { }
            lock (_lk) if (_c == c) { Connected = false; _c = null; _w = null; }
            try { c.Close(); } catch { }
        }

        void OnLine(string line)
        {
            int sp = line.IndexOf(' ');
            string k = sp < 0 ? line : line.Substring(0, sp), rest = sp < 0 ? "" : line.Substring(sp + 1);
            try
            {
                switch (k)
                {
                    case "SW": States = Convert.FromBase64String(rest.Trim()); StatesAt = DateTime.UtcNow; break;
                    case "SIG": SigStates = Convert.FromBase64String(rest.Trim()); SigStatesAt = DateTime.UtcNow; break;
                    case "BLK": Blocked = rest.Trim(); break;
                    case "TRAINS": { var tt = new Dictionary<string, (int, int, int, double)>(StringComparer.Ordinal); var l = ParseTrains(rest, tt); TrainTrack = tt; Trains = l; break; }
                    case "RES":
                        var t = rest.Split(' ');
                        if (t.Length >= 4 && int.TryParse(t[1], out int id) && int.TryParse(t[2], out int v)) Result?.Invoke(t[0], id, v, t[3]);
                        break;
                    default: return;
                }
                Changed?.Invoke();
            }
            catch { }
        }

        // «nombre tx tz x z kmh p;…» (como en la sala)
        static List<(string, int, int, double, double, double, bool)> ParseTrains(string s, Dictionary<string, (int, int, int, double)> track)
        {
            var l = new List<(string, int, int, double, double, double, bool)>();
            foreach (var part in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = part.Trim().Split(' ');
                if (f.Length < 7 || !int.TryParse(f[1], out int tx) || !int.TryParse(f[2], out int tz)
                    || !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) || !double.TryParse(f[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double z)) continue;
                double.TryParse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture, out double kmh);
                l.Add((f[0], tx, tz, x, z, kmh, f[6] == "1"));
                if (f.Length >= 11 && int.TryParse(f[7], out int node) && node >= 0 && int.TryParse(f[8], out int mu) && int.TryParse(f[9], out int dir)
                    && double.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out double len)) track[f[0]] = (node, mu, dir, len);
            }
            return l;
        }

        void SendLine(string s) { lock (_lk) { try { _w?.WriteLine(s); } catch { } } }

        // Cambiar una aguja (su número en el orden de SWITCHSTATES) o una señal (0 automática · 1 parada · 2 anuncio ·
        // 3 vía libre · 4 rebase autorizado), como desde Ctrl+9.
        public void Throw(int order, int pos) => SendLine($"THROW {order} {pos}");
        public void Send(string line) => SendLine(line);   // (mandos: «CAB tipo valor», «BTN orden 1|0»)
        public void Signal(int obj, int pick) => SendLine($"SIGNAL {obj} {pick}");

        public void Dispose()
        {
            _disposed = true;
            try { _listen.Stop(); } catch { }
            lock (_lk) { try { _c?.Close(); } catch { } _c = null; _w = null; Connected = false; }
        }
    }
}
