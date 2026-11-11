// Revisión → «Expulsiones» (2026-11-08, solo el administrador): quién ha desconectado a quién de la sala compartida desde el
// Puesto de Mando, cuándo y por qué, y los vagones sueltos retirados (admin_room_kicks, sql/expulsiones-sala.sql).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int RevTabKicks = 7;
        const string RevIntroKicks = "Las expulsiones de la sala compartida hechas desde el Puesto de Mando: quién desconectó a quién, cuándo y por qué, y los vagones sueltos retirados. Desde aquí puedes sancionar y prohibir la sala (sus requisitos están en Empresas → Ajustes → Sala compartida).";
        Panel _kickPanel;
        CardTable _kickList;
        readonly List<(string when, string kind, string jefe, string target, string reason, string zone)> _kickRows = new();
        readonly List<string> _kickShown = new();   // (2026-11-09) el usuario de cada fila a la vista (para sancionar o prohibir)

        Panel BuildKicksPanel()
        {
            _kickPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            _kickList = new CardTable
            {
                Dock = DockStyle.Fill, Margin = new Padding(0), Icon = "⛔", CardHeight = 76, MinWidth = 380,
                TitleCol = 0, SubCols = new[] { 1 }, QuoteCol = 2, PillCol = 3, QuoteMarks = true, AutoHeight = true
            };
            _kickPanel.Controls.Add(_kickList);
            _kickPanel.Controls.Add(BuildKickActions());   // (2026-11-09) sancionar, prohibir y requisitos (MainMenuForm.SalaAcceso.cs)
            return _kickPanel;
        }

        async void LoadKicks()
        {
            if (!Supa.IsSuperadmin || _kickList == null) return;
            var (json, err) = await Supa.RpcAsync("admin_room_kicks", new { p_limit = 500 });
            _kickRows.Clear();
            if (err != null)
            {
                _kickList.SetEmpty(MissingRpc(err) ? Tr("El servidor aún no tiene las expulsiones (falta expulsiones-sala.sql).") : Tr("Error: ") + err);
                return;
            }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _kickRows.Add((FmtDate(Str(e, "at")), Str(e, "kind"), Str(e, "jefe"), Str(e, "target"), Str(e, "reason"), Str(e, "zone")));
            }
            catch { }
            FillKicks();
        }

        void FillKicks()
        {
            if (_kickList == null) return;
            var words = _revQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _kickList.ClearRows(); _kickShown.Clear();
            int n = 0;
            foreach (var r in _kickRows)
            {
                string all = (r.jefe + " " + r.target + " " + r.reason + " " + r.zone).ToLowerInvariant();
                if (words.Any(w => all.IndexOf(w, StringComparison.Ordinal) < 0)) continue;
                bool loose = r.kind == "loose";
                string title = loose ? string.Format(Tr("{0} retira vagones sueltos de {1}"), r.jefe, r.target) : string.Format(Tr("{0} desconecta a {1}"), r.jefe, r.target);
                string quote = loose ? r.zone : r.reason;
                _kickList.AddRow(new[] { title, r.when, quote, loose ? Tr("Vagones sueltos") : Tr("Desconectado") },
                                 new Color?[] { null, null, null, loose ? Theme.Subtle : Carne.Red }, null, null);
                _kickShown.Add(r.target);
                n++;
            }
            if (n == 0) _kickList.SetEmpty(Tr("No hay expulsiones."));
        }
    }
}
