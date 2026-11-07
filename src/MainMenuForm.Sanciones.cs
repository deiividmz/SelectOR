// (1.2.90) SANCIONES DEL ADMINISTRADOR (sql/sanciones.sql). Solo las pone el administrador, a mano:
//  · «Sancionar…»: desde Revisión → «Sospechosos» o desde Servicios. C1 Exceso de viajeros, C2 Carga imposible o C3 Otra
//    falta; los puntos que quiera (0 a 15), una multa opcional a la empresa y, si quiere, anular el servicio. Con motivo.
//  · Revisión → «Sospechosos»: servicios que conviene mirar (viajeros muy por encima de las plazas, más viajeros de los
//    que podían, carga imposible para su potencia, media por encima de la velocidad máxima, plazas de más). Nada se
//    sanciona solo: el administrador sanciona o descarta.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        const int RevTabSanctions = 4, RevTabSuspects = 6;
        static bool IsCodeC(string code) => code == "pax_excess" || code == "load_impossible" || code == "admin_other";
        static readonly string RevIntroSus = "Servicios de los últimos 30 días que conviene mirar: muchos más viajeros que plazas, más viajeros de los que podían subir, una carga imposible para la potencia del tren, una media por encima de su velocidad máxima o más plazas que el tren. Nada se sanciona solo: elige uno y sanciónalo o descártalo.";

        sealed class SuspectRow
        {
            public string Id, Company, CompanyId, Driver, DriverId, Route, Consist, When; public double Km, Minutes, AvgKmh, MaxKmh, Capacity, TrainCapacity, Mass, PowerKw, TPerKw, Net;
            public int Pax, PaxModel, PaxClaimed, Score; public bool Sanctioned; public List<string> Reasons = new();
        }
        readonly List<SuspectRow> _susRows = new(), _susShown = new();
        Panel _susPanel; CardTable _susList; Label _susDetail, _susMsg; RoundButton _susSanction, _susDismiss;
        // (1.2.91, petición 2026-11-07) la composición 2D del tren de cada servicio (la que se guardó al abrirlo: ServiceImages)
        readonly Dictionary<string, Bitmap> _susStrips = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _susNoImg = new(StringComparer.OrdinalIgnoreCase);
        PictureBox _susPic; int _susSeq;

        Panel BuildSuspectsPanel()
        {
            _susPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = false };
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            _susList = new CardTable
            {
                Dock = DockStyle.Fill, Margin = new Padding(0, 0, 8, 0), Icon = "🔍", CardHeight = 84, MinWidth = 380,
                TitleCol = 0, SubCols = new[] { 1, 2 }, QuoteCol = 3, PillCol = 4, QuoteMarks = false, AutoHeight = true, SubChips = true
            };
            _susList.Accent = row => _susShown.FirstOrDefault(x => x.Id == row.Key) is SuspectRow r ? (r.Sanctioned ? Theme.SurfaceHi : r.Score >= 3 ? Carne.Red : Carne.Gold) : null;
            _susList.StripHeight = 56; _susList.StripRows = 1;   // (1.2.91) el tren en 2D, debajo del texto de la tarjeta
            _susList.Strip = row => row.Key != null && _susStrips.TryGetValue(row.Key, out var b) ? b : null;
            _susList.StripNote = row => row.Key != null && _susNoImg.Contains(row.Key) ? Tr("Sin composición guardada") : null;
            _susList.SelectedIndexChanged += (s, e) => ShowSuspect();
            _susList.ItemActivated += _ => SanctionSuspect();
            var right = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0) };
            _susDetail = new EmojiLabel { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Font(9.5f), Text = Tr("Elige un servicio de la lista.") };
            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Surface };
            _susSanction = EmpButton(Tr("Sancionar…"), primary: true); _susSanction.Width = 160;
            _susSanction.Click += (s, e) => SanctionSuspect();
            _susDismiss = EmpButton(Tr("Descartar")); _susDismiss.Width = 150; _susDismiss.Margin = new Padding(8, 10, 2, 2);
            _susDismiss.Click += (s, e) => DismissSuspect();
            _susMsg = EmpMsg(); _susMsg.MaximumSize = new Size(420, 0);
            btns.Controls.Add(_susSanction); btns.Controls.Add(_susDismiss); btns.Controls.Add(_susMsg);
            _susPic = new PictureBox { Dock = DockStyle.Top, Height = Theme.Px(84), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Theme.Surface, Visible = false, Margin = new Padding(0) };
            var picGap = new Panel { Dock = DockStyle.Top, Height = Theme.Px(10), BackColor = Theme.Surface };
            right.Controls.Add(_susDetail); right.Controls.Add(picGap); right.Controls.Add(_susPic); right.Controls.Add(btns);
            t.Controls.Add(_susList, 0, 0); t.Controls.Add(right, 1, 0);
            _susPanel.Controls.Add(t);
            return _susPanel;
        }

        async void LoadSuspects()
        {
            if (!Supa.IsSuperadmin || _susList == null) return;
            _susList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("admin_suspects", new { p_days = 30, p_limit = 300 });
            _susRows.Clear();
            if (err != null)
            {
                _susList.SetEmpty(MissingRpc(err) ? Tr("El servidor aún no tiene las sanciones (falta sanciones.sql).") : Tr("Error: ") + err);
                return;
            }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                {
                    var r = new SuspectRow
                    {
                        Id = Str(e, "service_id"), Company = Str(e, "company"), CompanyId = Str(e, "company_id"), Driver = Str(e, "driver"), DriverId = Str(e, "driver_id"),
                        Route = Str(e, "route"), Consist = Str(e, "consist"), When = FmtDate(Str(e, "ended_at")), Km = Num(e, "km"), Minutes = Num(e, "minutes"),
                        AvgKmh = Num(e, "avg_kmh"), MaxKmh = Num(e, "max_kmh"), Pax = (int)Num(e, "pax"), PaxModel = (int)Num(e, "pax_model"),
                        PaxClaimed = (int)Num(e, "pax_claimed"), Capacity = Num(e, "capacity"), TrainCapacity = Num(e, "train_capacity"), Mass = Num(e, "mass"),
                        PowerKw = Num(e, "power_kw"), TPerKw = Num(e, "t_per_kw"), Net = Num(e, "net"), Score = (int)Num(e, "score"), Sanctioned = Bool(e, "sanctioned")
                    };
                    if (e.TryGetProperty("reasons", out var rs) && rs.ValueKind == JsonValueKind.Array) foreach (var x in rs.EnumerateArray()) r.Reasons.Add(x.GetString() ?? "");
                    _susRows.Add(r);
                }
            }
            catch { }
            FillSuspects();
            FetchSuspectStrips(++_susSeq);
        }

        // (1.2.91) la composición 2D de cada servicio de la lista: de la caché de disco o de Supabase (ServiceImages)
        async void FetchSuspectStrips(int seq)
        {
            foreach (var r in _susRows.ToList())
            {
                if (seq != _susSeq || IsDisposed) return;
                if (string.IsNullOrEmpty(r.Id) || _susStrips.ContainsKey(r.Id) || _susNoImg.Contains(r.Id)) continue;
                string file = null;
                try { file = await ServiceImages.GetAsync(r.CompanyId, r.Id); } catch { file = null; }
                var bmp = file == null ? null : await Task.Run(() => LoadStripFile(file));
                if (seq != _susSeq || IsDisposed) { bmp?.Dispose(); return; }
                if (bmp == null) _susNoImg.Add(r.Id); else _susStrips[r.Id] = bmp;
                _susList?.Invalidate();
                if (SelectedSuspect()?.Id == r.Id) ShowSuspect();
            }
        }

        string N0(double v) => v.ToString("N0", I18n.Culture);

        // por qué es sospechoso, en palabras
        string SuspectReason(SuspectRow r, string code) => code switch
        {
            "pax" => string.Format(Tr("{0} viajeros con {1} plazas ({2} veces)"), N0(r.PaxModel), N0(r.Capacity), (r.PaxModel / Math.Max(1, r.Capacity)).ToString("0.#", I18n.Culture)),
            "pax_claim" => string.Format(Tr("decía {0} viajeros y solo podían subir {1}"), N0(r.PaxClaimed), N0(r.Pax)),
            "load" => string.Format(Tr("{0} t con {1} kW ({2} t por kW)"), N0(r.Mass), N0(r.PowerKw), r.TPerKw.ToString("0.##", I18n.Culture)),
            "speed" => string.Format(Tr("media de {0} km/h con un tren de {1} km/h como máximo"), N0(r.AvgKmh), N0(r.MaxKmh)),
            "seats" => string.Format(Tr("{0} plazas declaradas en un tren de {1}"), N0(r.Capacity), N0(r.TrainCapacity)),
            _ => code
        };

        void FillSuspects()
        {
            if (_susList == null) return;
            string keep = SelectedSuspect()?.Id;
            var words = _revQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _susList.ClearRows(); _susShown.Clear();
            foreach (var r in _susRows)
            {
                string why = string.Join("   ·   ", r.Reasons.Select(x => SuspectReason(r, x)));
                string all = (r.Driver + " " + r.Company + " " + r.Route + " " + r.Consist + " " + why).ToLowerInvariant();
                if (words.Any(w => all.IndexOf(w, StringComparison.Ordinal) < 0)) continue;
                _susShown.Add(r);
                string pill = r.Sanctioned ? Tr("Ya sancionado") : r.Score >= 3 ? Tr("Muy sospechoso") : Tr("Sospechoso");
                _susList.AddRow(new[] { r.Driver + " · " + r.Company, (r.Route.Length > 0 ? r.Route : "—") + "  ·  " + r.When, r.Consist + "  ·  " + r.Km.ToString("N1", I18n.Culture) + " km", "⚠  " + why, pill },
                    new Color?[] { null, null, null, Carne.Gold, r.Sanctioned ? Theme.Subtle : r.Score >= 3 ? Carne.Red : Carne.Gold }, null, r.Id);
            }
            if (_susShown.Count == 0) _susList.SetEmpty(Tr("No hay servicios sospechosos en los últimos 30 días."));
            if (keep != null) { int k = _susShown.FindIndex(x => x.Id == keep); if (k >= 0) _susList.SelectRow(k); }
            ShowSuspect();
        }

        SuspectRow SelectedSuspect() { int i = _susList?.SelectedRow ?? -1; return i >= 0 && i < _susShown.Count ? _susShown[i] : null; }

        void ShowSuspect()
        {
            if (_susDetail == null) return;
            var r = SelectedSuspect();
            if (_susSanction != null) _susSanction.Enabled = r != null;
            if (_susDismiss != null) _susDismiss.Enabled = r != null;
            if (_susPic != null)
            {
                var img = r != null && _susStrips.TryGetValue(r.Id, out var b) ? b : null;
                _susPic.Image = img; _susPic.Visible = img != null;
            }
            if (r == null) { _susDetail.Text = Tr("Elige un servicio de la lista."); return; }
            var l = new List<string>
            {
                r.Driver + " · " + r.Company, (r.Route.Length > 0 ? r.Route : "—") + " · " + r.When, r.Consist, "",
                string.Format(Tr("{0} km en {1} min (media de {2} km/h)"), r.Km.ToString("N1", I18n.Culture), N0(r.Minutes), N0(r.AvgKmh)),
            };
            if (r.Capacity > 0 || r.PaxModel > 0) l.Add(string.Format(Tr("Viajeros: {0} (cobraron {1}) · plazas: {2}"), N0(r.PaxModel), N0(r.Pax), N0(r.Capacity)));
            if (r.Mass > 0) l.Add(string.Format(Tr("Masa: {0} t · potencia del tren: {1} kW"), N0(r.Mass), r.PowerKw > 0 ? N0(r.PowerKw) : "—"));
            l.Add(string.Format(Tr("Beneficio del servicio: {0}"), EurT(r.Net)));
            l.Add("");
            l.Add(Tr("Por qué:"));
            foreach (var x in r.Reasons) l.Add("⚠  " + SuspectReason(r, x));
            if (r.Sanctioned) { l.Add(""); l.Add(Tr("Este servicio ya tiene una sanción.")); }
            _susDetail.Text = string.Join("\n", l);
        }

        void SanctionSuspect()
        {
            var r = SelectedSuspect(); if (r == null) return;
            string kind = r.Reasons.Contains("pax") || r.Reasons.Contains("pax_claim") || r.Reasons.Contains("seats") ? "pax_excess" : r.Reasons.Contains("load") ? "load_impossible" : "admin_other";
            string why = string.Join("; ", r.Reasons.Select(x => SuspectReason(r, x)));
            SanctionDialog(r.Id, r.Driver, r.Company, r.Route, r.When, kind, why, ok =>
            {
                Msg(_susMsg, ok, false);
                LoadSuspects(); LoadReview();
            });
        }

        async void DismissSuspect()
        {
            var r = SelectedSuspect(); if (r == null) return;
            var (_, err) = await Supa.RpcAsync("admin_suspect_dismiss", new { p_service = r.Id });
            Msg(_susMsg, err != null ? Tr("Error: ") + err : Tr("Descartado: ya no sale en la lista."), err != null);
            if (err == null) { _susRows.Remove(r); FillSuspects(); }
        }

        // Servicios → «Sancionar…» (administrador)
        void SanctionServiceRow()
        {
            if (!Supa.IsSuperadmin || _svcCards == null) return;
            int i = SelectedServiceIndex();
            if (i < 0 || i >= _svcRows.Count) { Msg(_empHomeMsg, Tr("Selecciona un servicio de la lista."), true); return; }
            var s = _svcRows[i];
            if (s.Status == "open") { Msg(_empHomeMsg, Tr("Solo se pueden sancionar servicios ya registrados."), true); return; }
            SanctionDialog(s.Id, s.Driver, _empSel?.Name ?? "", s.Route, s.Date, "admin_other", "", ok =>
            {
                Msg(_empHomeMsg, ok, false);
                LoadCompanies();
                if (_empSel != null) LoadServices(_empSel);
            });
        }

        static readonly string[] SanctionCodes = { "pax_excess", "load_impossible", "admin_other" };
        static int SanctionDefaultPoints(string code) => code == "admin_other" ? 2 : 4;

        // La ventana de sancionar: tipo, puntos, multa, anular el servicio y motivo.
        void SanctionDialog(string serviceId, string driver, string company, string route, string when, string kind, string why, Action<string> done)
        {
            int k0 = Math.Max(0, Array.IndexOf(SanctionCodes, kind));
            using var dlg = new FormDialog(Tr("Sancionar"), Tr("Sancionar"), 600, "⚖️", driver + " · " + company + " · " + (route.Length > 0 ? route + " · " : "") + when);
            dlg.AddCombo("code", Tr("Falta"), SanctionCodes.Select(Carne.Label), k0);
            dlg.AddCombo("pts", Tr("Puntos que se quitan del carné"), Enumerable.Range(0, 16).Select(p => p == 0 ? Tr("Ninguno") : p.ToString(I18n.Culture)), SanctionDefaultPoints(SanctionCodes[k0]));
            dlg.AddText("fine", Tr("Multa a la empresa en euros (0 = sin multa)"), "0");
            dlg.AddCombo("annul", Tr("Anular el servicio (deja de contar y su dinero sale de la tesorería)"), new[] { Tr("No"), Tr("Sí") }, 0);
            dlg.AddMultiline("reason", Tr("Motivo (lo verá el maquinista)"), why, 90);
            dlg.AddInfo(Tr("El maquinista recibe el aviso y puede alegar. Si aceptas su alegación, o anulas la sanción en Revisión, se devuelven los puntos, la multa y, si lo anulaste, el servicio."));
            string result = null;
            dlg.Validate = async f =>
            {
                string reason = f.Get("reason").Trim();
                if (reason.Length < 3) return Tr("Escribe el motivo de la sanción.");
                if (reason.Length > 500) return Tr("El motivo es demasiado largo (500 caracteres como mucho).");
                string ft = f.Get("fine").Replace("€", "").Replace(" ", "").Replace(".", "").Replace(",", ".");
                if (!double.TryParse(ft.Length == 0 ? "0" : ft, NumberStyles.Float, CultureInfo.InvariantCulture, out double fine) || fine < 0) return Tr("La multa no es un número válido.");
                int pts = Math.Max(0, f.Index("pts")); bool annul = f.Index("annul") == 1;
                if (pts == 0 && fine <= 0 && !annul) return Tr("Elige puntos, multa o anular el servicio.");
                string code = SanctionCodes[Math.Max(0, f.Index("code"))];
                var (_, e) = await Supa.RpcAsync("admin_sanction", new { p_service = serviceId, p_code = code, p_points = pts, p_fine = Math.Round(fine, 2), p_annul = annul, p_reason = reason });
                if (e != null) return MissingRpc(e) ? Tr("El servidor aún no tiene las sanciones (falta sanciones.sql).") : Tr("Error: ") + e;
                var parts = new List<string>();
                if (pts > 0) parts.Add(Carne.PointsText(-pts));
                if (fine > 0) parts.Add(string.Format(Tr("multa de {0}"), EurT(fine)));
                if (annul) parts.Add(Tr("servicio anulado"));
                result = string.Format(Tr("Sanción puesta a {0}: {1}. Le ha llegado el aviso."), driver, string.Join(", ", parts));
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && result != null) done(result);
        }
    }
}
