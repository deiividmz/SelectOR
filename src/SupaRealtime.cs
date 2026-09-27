// Cliente mínimo de Supabase Realtime (Phoenix Channels sobre WebSocket), sin dependencias.
// Se suscribe a los cambios (INSERT/UPDATE/DELETE) de las tablas indicadas y llama a
// OnChange(tabla) cuando llega uno. La RLS de cada tabla filtra qué cambios te llegan
// (se envía el JWT del usuario en el phx_join). Reconexión automática con reintentos.

using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SelectOR
{
    public sealed class SupaRealtime
    {
        readonly string _url;             // https://xxxx.supabase.co
        readonly string _anon;            // anon key (pública)
        readonly Func<string> _token;     // proveedor del JWT del usuario (para la RLS)
        readonly string[] _tables;
        readonly Action<string> _onChange;

        ClientWebSocket _ws;
        CancellationTokenSource _cts;
        volatile bool _running;
        int _ref;

        public SupaRealtime(string url, string anon, Func<string> tokenProvider, string[] tables, Action<string> onChange)
        {
            _url = url; _anon = anon; _token = tokenProvider; _tables = tables; _onChange = onChange;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => RunLoop(_cts.Token));
        }

        public void Stop()
        {
            _running = false;
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); } catch { }
            _ws = null;
        }

        async Task RunLoop(CancellationToken ct)
        {
            while (_running && !ct.IsCancellationRequested)
            {
                try { await SessionAsync(ct); }
                catch { /* caída de red: se reintenta */ }
                if (_running && !ct.IsCancellationRequested)
                    try { await Task.Delay(4000, ct); } catch { }
            }
        }

        async Task SessionAsync(CancellationToken ct)
        {
            var token = _token?.Invoke();
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(_url) || string.IsNullOrEmpty(_anon))
            { try { await Task.Delay(2000, ct); } catch { } return; }

            var wss = _url.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/')
                      + "/realtime/v1/websocket?apikey=" + Uri.EscapeDataString(_anon) + "&vsn=1.0.0";

            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(wss), ct);

            // Un canal (topic) por tabla: si una tabla no está publicada en supabase_realtime,
            // solo falla SU topic, sin tumbar la escucha de las demás.
            // Formato Phoenix v1 (objeto): {topic, event, payload, ref}.
            foreach (var t in _tables)
            {
                var changes = new[] { new { @event = "*", schema = "public", table = t } };
                var joinPayload = new { config = new { postgres_changes = changes, @private = false }, access_token = token };
                var joinFrame = new { topic = "realtime:sel_" + t, @event = "phx_join", payload = joinPayload, @ref = NextRef() };
                await SendAsync(joinFrame, ct);
            }

            // Latido periódico para mantener viva la conexión.
            using var hb = new Timer(_ =>
            {
                try { _ = SendAsync(new { topic = "phoenix", @event = "heartbeat", payload = new { }, @ref = NextRef() }, CancellationToken.None); }
                catch { }
            }, null, 25000, 25000);

            var buf = new byte[16 * 1024];
            var sb = new StringBuilder();
            while (_running && _ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                sb.Clear();
                WebSocketReceiveResult res;
                do
                {
                    res = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                    if (res.MessageType == WebSocketMessageType.Close)
                    {
                        try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
                        return;
                    }
                    sb.Append(Encoding.UTF8.GetString(buf, 0, res.Count));
                } while (!res.EndOfMessage);
                HandleFrame(sb.ToString());
            }
        }

        string NextRef() => Interlocked.Increment(ref _ref).ToString();

        async Task SendAsync(object frame, CancellationToken ct)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame));
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }

        // Formato Phoenix v1 (objeto): {topic, event, payload, ref}.
        void HandleFrame(string text)
        {
            try
            {
                using var d = JsonDocument.Parse(text);
                var root = d.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (!root.TryGetProperty("event", out var evEl) || evEl.GetString() != "postgres_changes") return;
                if (!root.TryGetProperty("payload", out var payload)) return;
                if (payload.TryGetProperty("data", out var data) && data.TryGetProperty("table", out var tbl))
                {
                    var table = tbl.GetString();
                    if (!string.IsNullOrEmpty(table)) _onChange?.Invoke(table);
                }
            }
            catch { }
        }
    }
}
