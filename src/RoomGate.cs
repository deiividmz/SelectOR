// Contraseña de las salas para cualquier ruta, en el servidor de la sala («SelectOR Servidor MP\Gate.cs»): una pregunta
// por conexión corta (como el piloto de desvíos, el servidor la reconoce por su «SELECTOR HELLO»). Con un servidor de
// antes, que contesta «READY 1» y no conoce el resto, Ask devuelve null y SelectOR usa el sistema anterior.

using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SelectOR
{
    public static class RoomGate
    {
        // La respuesta (sin «SELECTOR »), o null si el servidor no la conoce o no contesta.
        public static async Task<string[]> Ask(string host, int port, string name, string body, int timeoutMs = 6000)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var tcp = new TcpClient { NoDelay = true };
                await tcp.ConnectAsync(host, port, cts.Token);
                var ns = tcp.GetStream();
                async Task Send(string b)
                {
                    string msg = "SELECTOR " + b;
                    var data = Encoding.Unicode.GetBytes(" " + msg.Length + ": " + msg);
                    await ns.WriteAsync(data, cts.Token);
                }
                await Send($"HELLO {name} 0 2");   // prioridad 0: solo para la contraseña (no es un piloto)
                await Send(body);
                string want = body.Split(' ')[0];
                var dec = Encoding.Unicode.GetDecoder();
                var buf = new byte[4096]; var chars = new char[4096];
                var sb = new StringBuilder();
                while (true)
                {
                    int n = await ns.ReadAsync(buf.AsMemory(0, buf.Length), cts.Token);
                    if (n <= 0) return null;
                    sb.Append(chars, 0, dec.GetChars(buf, 0, n, chars, 0));
                    string f;
                    while ((f = SwitchPilot.NextFrame(sb)) != null)
                    {
                        if (!f.StartsWith("SELECTOR ", StringComparison.Ordinal)) continue;
                        var t = f.Substring(9).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (t.Length == 0) continue;
                        if (t[0] == "READY") { if (t.Length < 2 || t[1] == "1") return null; continue; }   // servidor de antes
                        if (t[0] == want) return t;
                    }
                }
            }
            catch { return null; }
        }
    }
}
