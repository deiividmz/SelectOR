// Lista de servidores públicos de multijugador (tsimserver.com), tabla HTML sencilla.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SelectOR
{
    public class GameServer
    {
        public string ReportedTime;
        public string Ip;
        public string Port;
        public string Route;
        public string Players;
        public string PlayerNames;

        public override string ToString() => $"{Ip}:{Port}";
    }

    public static class Servers
    {
        public const string Url = "http://www.tsimserver.com/ORFiles031205/ServerInfo.html";

        static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public static async Task<List<GameServer>> FetchAsync()
        {
            var list = new List<GameServer>();
            string html;
            try { html = await _http.GetStringAsync(Url); }
            catch { return list; }

            // Cada fila: <tr><TD>time</TD><TD>ip</TD><TD>port</TD><TD>route</TD><TD>players</TD><TD>names</TD></tr>
            var rowRx = new Regex(@"<tr>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var cellRx = new Regex(@"<td[^>]*>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            foreach (Match row in rowRx.Matches(html))
            {
                var cells = new List<string>();
                foreach (Match c in cellRx.Matches(row.Groups[1].Value))
                    cells.Add(Clean(c.Groups[1].Value));
                if (cells.Count < 6) continue;
                // saltar cabecera
                if (cells[1].Equals("IP Address", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(cells[1]) || string.IsNullOrWhiteSpace(cells[2])) continue;
                list.Add(new GameServer
                {
                    ReportedTime = cells[0],
                    Ip = cells[1],
                    Port = cells[2],
                    Route = cells[3],
                    Players = cells[4],
                    PlayerNames = cells[5]
                });
            }
            return list;
        }

        static string Clean(string s)
        {
            s = Regex.Replace(s ?? "", "<.*?>", "");
            s = s.Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();
            return s;
        }
    }
}
