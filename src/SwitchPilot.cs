// Conexión aparte con el servidor de una sala de «Servidores SelectOR» (no es un jugador: el servidor la reconoce por su
// primer mensaje, «SELECTOR HELLO»). Hoy sirve para el HUD (WHO → «SELECTOR YOU»: dentro / entrando / fuera y si eres
// el despachador), para la contraseña de las salas libres (GATE OWNER) y para el chat de la sala (SAY → «SELECTOR
// CHAT»; servidor con «READY 3»; ver «SelectOR Servidor MP\Chat.cs»). Sabe también pedir desvíos (WANT / PASSED /
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
        // Chat de la sala (servidor con «READY 3»): hora (UTC), tipo ('m' mensaje, 'i' entra, 'o' sale), quién y texto.
        public bool ChatOk { get; private set; }
        public event Action<DateTime, char, string, string> Chat;
        public event Action ChatReset;          // conexión nueva: el servidor vuelve a mandar lo último
        public event Action<string> ChatError;  // «notin» (no estás en la sala) · «slow» (demasiados seguidos)
        // Mapa grande (servidor con «READY 4»; MainMenuForm.MapaSala.cs y RoomMap.cs): agujas, señales y trenes de la sala.
        public bool MapOk { get; private set; }
        // Servidor con «READY 7»: pedir paso y liberar la señal de mi tren (ASKCLEAR/RELEASE, como Tab y Ctrl+Tab en Open Rails);
        // con el despachador fijo, las órdenes a las señales son solo del administrador.
        public bool DriverOk { get; private set; }
        public event Action<int, int, int, int> SwitchSet;          // el despachador acaba de cambiar una aguja (tx tz id posición)
        public byte[] SigStates { get; private set; }                // dos bytes por cabeza (estado + 1, dibujo + 1)
        public DateTime SigStatesAt { get; private set; }
        public List<(string name, int tx, int tz, double x, double z, double kmh, bool player)> Trains { get; private set; } = new();
        // De cada tren (servidor nuevo): su tramo del .tdb, MUDirection, sentido de la cola y longitud, para su camino por delante.
        public Dictionary<string, (int node, int mu, int dir, double len)> TrainTrack { get; private set; } = new();
        public bool Helper { get; private set; }                     // el despachador te ha nombrado ayudante (señales)
        public event Action<string> MapError;                        // busy · nohelper · notin · nodisp · far · locked · notdisp
        public event Action<string, int, int, string> TabAsked;      // (puesto de mando) un maquinista pide paso: quién, señal, 0 no · 1 sí · 2 espera al jefe, motivo
        // jefes de circulación (1.2.75): quién manda en cada estación, y en cada señal y aguja
        public sealed class JefeZone { public string Zone, Name; public int[] Sigs, Nodes; public bool Manual; }   // Manual: mando manual (1.2.75)
        // (1.2.75) itinerarios de la sala: de qué señal a cuál, de quién, en qué estado y qué agujas tiene enclavadas
        public sealed class Itin { public int Origin, Dest; public string Owner, Role, State, Train, Why; public int Left, Queue; public (int node, int pos)[] Switches = Array.Empty<(int, int)>(); }   // State: set · in · cancel · queue (Queue: lugar)
        public IReadOnlyList<Itin> Itins { get; private set; } = Array.Empty<Itin>();
        public DateTime ItinsAt { get; private set; }
        public event Action ItinsChanged;
        // (1.2.77) por qué no se abre cada señal en parada que espera un tren (Motivos.cs del despachador): señal → (tren, «es · en»)
        public IReadOnlyDictionary<int, (string train, string text)> SigWhy { get; private set; } = new Dictionary<int, (string, string)>();
        public event Action WhyChanged;
        // la parte en el idioma de SelectOR de un texto «es · en»
        public static string Lang(string t) { if (string.IsNullOrEmpty(t)) return t; int k = t.IndexOf(" · ", StringComparison.Ordinal); return k < 0 ? t : I18n.English ? t.Substring(k + 3) : t.Substring(0, k); }
        public event Action<string, bool, int, string> ItinReply;   // quién, bien o no, señal de origen, texto (es · en)
        public Itin ItinFrom(int sig) => Itins.FirstOrDefault(i => i.Origin == sig);
        public IReadOnlyList<JefeZone> JefeZones { get; private set; } = Array.Empty<JefeZone>();
        Dictionary<int, string> _sigJefe = new(), _nodeJefe = new();
        public string JefeOfSig(int sig) => _sigJefe.TryGetValue(sig, out var n) ? n : null;
        public string JefeOfNode(int node) => _nodeJefe.TryGetValue(node, out var n) ? n : null;
        public string JefeOfZone(string zone) => JefeZones.FirstOrDefault(z => z.Zone == zone)?.Name;
        public string JefeMe { get; private set; }                   // yo, como jefe (tras JEFEOK)
        public event Action JefeChanged;
        public event Action<string> AiRetired;                       // (1.2.78) «*» o el servicio (con %xx): la sala ha pasado la orden de retirar trenes IA
        public event Action<string> JefeError;                       // ticket · busy estación nombre · offline · notake
        // Servidor con «READY 5»: el despachador también pide señales desde el mapa (con el agente del mapa en su Open Rails)
        // y nombra ayudantes desde él; quién es quién en la sala.
        public bool AgentOk { get; private set; }
        public string DispatcherName { get; private set; }
        public HashSet<string> Helpers { get; private set; } = new(StringComparer.Ordinal);
        // Servidor con «READY 6»: agujas enclavadas en el Open Rails del despachador (las manda su agente del mapa), una
        // letra por aguja en el orden de States: «.» libre · S señal abierta · T reservada por un tren · O tren encima.
        // null = no se sabe (servidor de antes, o el despachador sin el agente).
        public string Blocked { get; private set; }
        public int LocalPort { get { try { return (_tcp?.Client?.LocalEndPoint as System.Net.IPEndPoint)?.Port ?? 0; } catch { return 0; } } }

        public SwitchPilot(string host, int port, string name, int prio) { _host = host; _port = port; _name = name; _prio = prio; }

        public static string Key(int tx, int tz, int id) => tx + " " + tz + " " + id;

        public int Prio
        {
            get => _prio;
            set { if (_prio == value) return; _prio = value; if (Ready) Send($"HELLO {_name} {_prio} 3"); }
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
                _tcp = tcp; _ns = tcp.GetStream(); _cts = new CancellationTokenSource(); Blocked = null;
                Send($"HELLO {_name} {_prio} 3");
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

        void OnJefes(string json)
        {
            try
            {
                var l = new List<JefeZone>();
                using (var d = System.Text.Json.JsonDocument.Parse(json))
                    foreach (var e in d.RootElement.EnumerateArray())
                        l.Add(new JefeZone
                        {
                            Zone = e.GetProperty("z").GetString(), Name = e.GetProperty("n").GetString(),
                            Sigs = e.GetProperty("s").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                            Nodes = e.GetProperty("w").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
                            Manual = e.TryGetProperty("m", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.True
                        });
                var sj = new Dictionary<int, string>(); var nj = new Dictionary<int, string>();
                foreach (var z in l) { foreach (int s in z.Sigs) sj[s] = z.Name; foreach (int n in z.Nodes) nj[n] = z.Name; }
                _sigJefe = sj; _nodeJefe = nj; JefeZones = l;
                try { JefeChanged?.Invoke(); } catch { }
            }
            catch { }
        }

        void OnItins(string json)
        {
            try
            {
                var l = new List<Itin>();
                using (var d = System.Text.Json.JsonDocument.Parse(json))
                    foreach (var e in d.RootElement.EnumerateArray())
                        l.Add(new Itin
                        {
                            Origin = e.GetProperty("o").GetInt32(), Dest = e.GetProperty("d").GetInt32(),
                            Owner = e.GetProperty("n").GetString(), Role = e.GetProperty("r").GetString(), State = e.GetProperty("st").GetString(),
                            Left = e.GetProperty("t").GetInt32(), Train = e.TryGetProperty("tr", out var tr) ? tr.GetString() : null,
                            Queue = e.TryGetProperty("q", out var q) ? q.GetInt32() : 0,
                            Why = e.TryGetProperty("y", out var y) ? y.GetString() : null,
                            Switches = e.GetProperty("w").EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetInt32())).ToArray()
                        });
                Itins = l; ItinsAt = DateTime.UtcNow;
                try { ItinsChanged?.Invoke(); } catch { }
            }
            catch { }
        }

        void OnMessage(string body)
        {
            if (!body.StartsWith("SELECTOR ", StringComparison.Ordinal)) return;
            if (body.StartsWith("SELECTOR CHAT ", StringComparison.Ordinal)) { OnChat(body.Substring(14)); return; }
            if (body.StartsWith("SELECTOR TRAINS ", StringComparison.Ordinal)) { OnTrains(body.Substring(16)); return; }
            if (body.StartsWith("SELECTOR TAB ", StringComparison.Ordinal))
            {
                var f = body.Substring(13).Split(' ', 4);
                if (f.Length >= 3 && int.TryParse(f[1], out int sig) && int.TryParse(f[2], out int tst)) try { TabAsked?.Invoke(f[0], sig, tst, f.Length > 3 ? f[3] : ""); } catch { }
                return;
            }
            if (body.StartsWith("SELECTOR JEFES ", StringComparison.Ordinal)) { OnJefes(body.Substring(15)); return; }
            if (body.StartsWith("SELECTOR ITINS ", StringComparison.Ordinal)) { OnItins(body.Substring(15)); return; }
            if (body.StartsWith("SELECTOR WHY ", StringComparison.Ordinal))
            {
                try
                {
                    var d = new Dictionary<int, (string, string)>();
                    using (var doc = System.Text.Json.JsonDocument.Parse(body.Substring(13)))
                        foreach (var e in doc.RootElement.EnumerateArray())
                            d[e.GetProperty("s").GetInt32()] = (e.TryGetProperty("tr", out var tr) ? tr.GetString() : "", e.GetProperty("t").GetString());
                    SigWhy = d;
                    try { WhyChanged?.Invoke(); } catch { }
                }
                catch { }
                return;
            }
            if (body.StartsWith("SELECTOR ITIN ", StringComparison.Ordinal))
            {
                var f = body.Substring(14).Split(' ', 4);
                if (f.Length >= 3 && int.TryParse(f[2], out int og)) try { ItinReply?.Invoke(f[0], f[1] == "1", og, f.Length > 3 ? f[3] : ""); } catch { }
                return;
            }
            if (body.StartsWith("SELECTOR SIGS ", StringComparison.Ordinal))
            {
                var st = DecodeStates(body.Substring(14).Trim());
                if (st != null) { SigStates = st; SigStatesAt = DateTime.UtcNow; }
                return;
            }
            var t = body.Substring(9).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) return;
            if (t[0] == "READY")
            {
                int rv = t.Length > 1 && int.TryParse(t[1], out int rv0) ? rv0 : 0;
                ChatOk = rv >= 3;
                MapOk = rv >= 4;
                AgentOk = rv >= 5;
                DriverOk = rv >= 7;
                Ready = true;
                if (ChatOk) try { ChatReset?.Invoke(); } catch { }
                return;
            }
            if (t[0] == "CHATERR" && t.Length > 1) { try { ChatError?.Invoke(t[1]); } catch { } return; }
            if (t[0] == "HELPER" && t.Length > 1) { Helper = t[1] == "1"; return; }
            if (t[0] == "JEFEOK" && t.Length > 1) { JefeMe = t[1]; try { JefeChanged?.Invoke(); } catch { } return; }
            if (t[0] == "AIRETIRED" && t.Length > 1) { try { AiRetired?.Invoke(t[1]); } catch { } return; }
            if (t[0] == "JEFEERR" && t.Length > 1) { try { JefeError?.Invoke(string.Join(" ", t.Skip(1))); } catch { } return; }
            if (t[0] == "ROLES" && t.Length > 2)
            {
                DispatcherName = t[1] == "-" ? null : t[1];
                Helpers = new HashSet<string>(t[2] == "-" ? Array.Empty<string>() : t[2].Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                return;
            }
            // (1.2.75) con lo que venga detrás: «busy motivo», «jefe nombre»
            if (t[0] == "MAPERR" && t.Length > 1) { try { MapError?.Invoke(string.Join(" ", t.Skip(1))); } catch { } return; }
            if (t[0] == "BLOCKED" && t.Length > 1) { Blocked = t[1] == "-" ? null : t[1]; return; }
            if (t[0] == "SW" && t.Length >= 5 && int.TryParse(t[1], out int sx) && int.TryParse(t[2], out int sz) && int.TryParse(t[3], out int sid) && int.TryParse(t[4], out int sp))
            { try { SwitchSet?.Invoke(sx, sz, sid, sp); } catch { } return; }
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

        // «hora tipo quién[<TAB>texto]»
        void OnChat(string s)
        {
            try
            {
                int tab = s.IndexOf('\t');
                string head = tab >= 0 ? s.Substring(0, tab) : s, text = tab >= 0 ? s.Substring(tab + 1) : "";
                var h = head.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (h.Length < 3 || !long.TryParse(h[0], out long at) || h[1].Length != 1) return;
                Chat?.Invoke(DateTimeOffset.FromUnixTimeSeconds(at).UtcDateTime, h[1][0], h[2].Trim(), text);
            }
            catch { }
        }

        // «nombre tx tz x z kmh p;…»
        void OnTrains(string s)
        {
            var l = new List<(string, int, int, double, double, double, bool)>();
            var track = new Dictionary<string, (int, int, int, double)>(StringComparer.Ordinal);
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
            TrainTrack = track;
            Trains = l;
        }

        // Mapa grande: cambiar una aguja (como desde Ctrl+9) o una señal (solo ayudantes; orden: 0 normal · 1 parada ·
        // 2 anuncio · 3 vía libre · 4 rebase autorizado).
        public void Throw(int tx, int tz, int id, int pos) { if (Ready && MapOk) Send($"THROW {tx} {tz} {id} {pos}"); }
        // Un desvío concreto del .tdb, por su nodo: los de una travesía o un escape comparten identificador y por THROW solo
        // cambia el primero (solo con el despachador fijo, 1.2.71).
        public void ThrowNode(int node, int pos) { if (Ready && DriverOk) Send($"THROWNODE {node} {pos}"); }
        public void Signal(int obj, int pick) { if (Ready && MapOk) Send($"SIGNAL {obj} {pick}"); }
        public void Pass() { if (Ready && DriverOk) Send("ASKCLEAR"); }
        public void Release() { if (Ready && DriverOk) Send("RELEASE"); }
        public void SetHelper(string name, bool on) { if (Ready && AgentOk) Send($"SETHELPER {name} {(on ? 1 : 0)}"); }

        // Escribir en el chat de la sala (lo ven los demás en su HUD y en el chat de Open Rails).
        public void Say(string text) { if (Ready && ChatOk) Send("SAY " + text); }

        // jefe de circulación (1.2.75): vale de Supabase (dispatch_ticket_issue)
        public void JefeTake(string ticket) { if (Ready) Send("JEFE TAKE " + ticket); }
        public void JefeAdd(string zone, IEnumerable<int> sigs, IEnumerable<int> nodes, IEnumerable<string> keys, bool force)
        {
            static string Csv<T>(IEnumerable<T> l) { var s = string.Join(",", l); return s.Length == 0 ? "-" : s; }
            if (Ready) Send($"JEFE ADD {Uri.EscapeDataString(zone)} {Csv(sigs)} {Csv(nodes)} {Csv(keys)}" + (force ? " force" : ""));
        }
        public void JefeDel(string zone) { if (Ready) Send("JEFE DEL " + Uri.EscapeDataString(zone)); }
        public void JefeLeave() { if (Ready) Send("JEFE LEAVE"); JefeMe = null; }
        public void JefeDrop(string ticket, string name) { if (Ready) Send("JEFE DROP " + ticket + " " + name); }
        public void TabAnswer(string user, int sig, bool ok) { if (Ready) Send($"JEFE {(ok ? "TABOK" : "TABNO")} {user} {sig}"); }
        // el jefe: agujas y señales aunque no esté conduciendo (el servidor lo deja solo a él)
        public void JefeThrowNode(int node, int pos) { if (Ready) Send($"THROWNODE {node} {pos}"); }
        public void JefeSignal(int obj, int pick) { if (Ready) Send($"SIGNAL {obj} {pick}"); }
        // (1.2.78) el administrador quita ya los trenes IA de la sala: todos (service null) o los de un servicio
        public void AiRetire(string ticket, string service) { if (Ready) Send("JEFE AIRETIRE " + ticket + " " + (string.IsNullOrEmpty(service) ? "*" : Uri.EscapeDataString(service))); }
        public void JefeManual(string zone, bool on) { if (Ready) Send($"JEFE MANUAL {Uri.EscapeDataString(zone)} {(on ? 1 : 0)}"); }
        // (1.2.75) itinerarios: de la señal origen a la destino (−1: topera) con esas agujas (nodo del .tdb, posición); anular
        public void ItinSet(int origin, int dest, IEnumerable<(int node, int pos)> switches)
        {
            var w = string.Join(",", switches.Select(x => x.node + ":" + x.pos));
            if (Ready) Send($"ITIN SET {origin} {dest} {(w.Length == 0 ? "-" : w)}");
        }
        public void ItinDel(int origin) { if (Ready) Send($"ITIN DEL {origin}"); }
        public void ItinFirst(int origin) { if (Ready) Send($"ITIN FIRST {origin}"); }   // (el jefe: el primero de la cola)

        // Como lo lee Open Rails (MSGSwitchStatus / MSGSignalStatus): base64 de [int32 cuántos][gzip con los bytes de estado (+ textos)].
        static byte[] DecodeStates(string b64)
        {
            try
            {
                var raw = Convert.FromBase64String(b64);
                int n = BitConverter.ToInt32(raw, 0);   // cuántos bytes de estado (las señales llevan detrás sus textos)
                using var ms = new System.IO.MemoryStream(raw, 4, raw.Length - 4);
                using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
                using var outp = new System.IO.MemoryStream();
                gz.CopyTo(outp);
                var all = outp.ToArray();
                return n > 0 && n < all.Length ? all.AsSpan(0, n).ToArray() : all;
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
            JefeMe = null;   // (al volver, el puesto vuelve a entrar como jefe)
            Presence = null;
        }

        public void Dispose() { Unsupported = true; Close(); }
    }
}
