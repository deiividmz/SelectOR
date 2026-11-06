// Empresas → Tablón y el mercado de trenes (1.2.82; servidor: sql/tablon-mercado.sql).
//  · Tablón (todos, también sin empresa): Escaparate (las empresas que quieren salir, con sus datos del mes; ficha propia
//    para el gerente y los gestores), Anuncios (con imagen opcional, 3 o 7 días, destacar cuesta dinero del juego), Empleo
//    (ofertas con requisitos; apuntarse crea la solicitud de ingreso de siempre) y Mercado de trenes.
//  · Flota → «Vender o desguazar…» (antes «Dar de baja»): vender en el mercado al precio que fija SelectOR (la empresa no lo
//    decide; cobra ese precio menos la comisión) o desguazar (cobra poco). Un tren a la venta sale «En venta» en Flota y no
//    puede salir en servicios; se retira con «Retirar del mercado».
//  · Sin el SQL (servidor sin actualizar): el desguace como la antigua baja y el tablón con su aviso.

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
        const int TablonSubtab = 17;
        static readonly string[] FleetTabNames = { "Todos", "Disponibles", "En servicio", "En el mercado" };
        static readonly int[] FleetTabStates = { -1, 0, 1, 4 };

        static bool MissingRpc(string err) => err != null && (err.Contains("PGRST202") || err.Contains("Could not find") || err.Contains("does not exist"));
        static string EurT(double v) => v.ToString("N0", I18n.Culture) + " €";
        static bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

        // ============================ Flota: vender o desguazar ============================
        void UpdateSellButton()
        {
            if (_fleetRemoveBtn == null) return;
            var sel = FleetSelection();
            bool listed = sel.Count == 1 && sel[0].row < _fleetStatus.Count && _fleetStatus[sel[0].row] == "for_sale";
            string t = Tr(listed ? "Retirar del mercado" : "Vender, alquilar o desguazar…");
            if (_fleetRemoveBtn.Text == t) return;
            _fleetRemoveBtn.Text = t;
            using var fb = Theme.Font(9.5f, FontStyle.Bold);
            _fleetRemoveBtn.Width = TextRenderer.MeasureText(t, fb).Width + 36;
            _fleetRemoveBtn.TextColor = listed ? Theme.Text : RedC;
            _fleetRemoveBtn.Invalidate();
        }

        sealed class SaleQuote { public string Id, Name, Cannot; public double PriceNew, Price, Fee, Gets, Scrap, Km, Rent, RentGeneral; public int Cond, Days, RentPct; public double FeePct, ScrapPct; }

        async void SellOrScrapUi()
        {
            if (!CanManage() && !Supa.IsSuperadmin) { Msg(_fleetMsg, Tr("Solo el dueño o un gestor pueden gestionar la flota."), true); return; }
            var sel = FleetSelection();
            if (sel.Count == 0) { Msg(_fleetMsg, Tr("Selecciona un vehículo de la lista."), true); return; }
            // a la venta: retirarlo
            if (sel.Count == 1 && sel[0].row < _fleetStatus.Count && _fleetStatus[sel[0].row] == "for_sale") { await WithdrawVehicle(sel[0].id); return; }
            Msg(_fleetMsg, Tr("Calculando el precio…"), false);
            var quotes = new List<SaleQuote>();
            bool old = false;
            foreach (var (row, id) in sel)
            {
                var (json, err) = await Supa.RpcAsync("market_quote", new { p_vehicle = id });
                if (MissingRpc(err)) { old = true; break; }
                if (err != null) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
                try
                {
                    using var d = JsonDocument.Parse(json);
                    var r = d.RootElement;
                    string name = row < _fleetRowDetail.Count && _fleetRowDetail[row].Length > 0 ? _fleetRowDetail[row][0] : "";
                    quotes.Add(new SaleQuote
                    {
                        Id = id, Name = name, Cannot = r.TryGetProperty("cannot", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
                        PriceNew = Num(r, "price_new"), Price = Num(r, "price"), Fee = Num(r, "fee"), Gets = Num(r, "gets"), Scrap = Num(r, "scrap"),
                        Km = Num(r, "km"), Cond = (int)Num(r, "cond"), Days = (int)Num(r, "days"), FeePct = Num(r, "fee_pct"), ScrapPct = Num(r, "scrap_pct"),
                        Rent = Num(r, "rent"), RentGeneral = Num(r, "rent_general"), RentPct = (int)Num(r, "rent_pct")   // (1.2.90; 0 sin el SQL nuevo)
                    });
                }
                catch { }
            }
            Msg(_fleetMsg, "", false);
            if (old) { RemoveVehicle(); return; }   // servidor sin tablon-mercado.sql: la baja de siempre
            if (quotes.Count == 0) return;
            int choice = SaleDialog(quotes);
            if (choice == 1) await ListVehicles(quotes.Where(q => q.Cannot == null).ToList());
            else if (choice == 2) await ScrapVehicles(quotes.Where(q => q.Cannot is null or "granted").ToList(), quotes.Any(q => q.Cannot == "rented"));
            else if (choice == 3) await RentOutVehicles(quotes.Where(q => q.Cannot == null && q.Rent > 0).ToList());
        }

        // (1.2.90) ofrecer en alquiler a otras empresas: el plazo lo elige la empresa; el precio, SelectOR
        async Task RentOutVehicles(List<SaleQuote> qs)
        {
            if (qs.Count == 0) return;
            int[] opts = { 7, 14, 30 };
            int days;
            using (var dlg = new FormDialog(Tr("Ofrecer en alquiler"), Tr("Ofrecer"), 560, "🔑", Tr("El precio por servicio lo fija SelectOR")))
            {
                dlg.AddCombo("days", Tr("Plazo del alquiler"), opts.Select(d => string.Format(Tr("{0} días"), d)), 1);
                dlg.AddInfo(Tr("Sale en Tablón → Mercado de trenes. Cuando otra empresa lo alquila, pasa a su flota durante ese plazo y te paga el alquiler en cada servicio que haga. Luego vuelve a tu flota."));
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                days = opts[Math.Max(0, Math.Min(opts.Length - 1, dlg.Index("days")))];
            }
            Msg(_fleetMsg, Tr("Poniendo en alquiler…"), false);
            int done = 0; string err = null;
            foreach (var q in qs)
            {
                var (_, e) = await Supa.RpcAsync("market_list_rent", new { p_vehicle = q.Id, p_days = days });
                if (e != null) err ??= e; else done++;
            }
            if (done == 0) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            Msg(_fleetMsg, (done == 1 ? string.Format(Tr("En alquiler por {0} por servicio durante {1} días: ya lo ven las demás empresas en Tablón → Mercado de trenes."), EurT(qs[0].Rent), days)
                                      : string.Format(Tr("{0} trenes en alquiler durante {1} días."), done, days)) + (err != null ? "  " + Tr("Error: ") + err : ""), err != null);
            _fleetCards?.ClearMarks();
            LoadFleet();
        }

        static string CannotText(string why) => why switch
        {
            "rented" => Tr("Alquilado: no se vende, se devuelve."),
            "granted" => Tr("Cedido por el administrador: no se puede vender."),
            "in_use" => Tr("Está en un servicio: espera a que termine."),
            "listed" => Tr("Ya está a la venta."),
            _ => "",
        };

        // 0 nada · 1 poner a la venta · 2 desguazar (o devolver los alquilados) · 3 ofrecer en alquiler (1.2.90)
        int SaleDialog(List<SaleQuote> qs)
        {
            int result = 0;
            bool many = qs.Count > 1;
            var sellable = qs.Where(q => q.Cannot == null).ToList();
            var scrappable = qs.Where(q => q.Cannot is null or "granted" or "rented").ToList();
            double gets = sellable.Sum(q => q.Gets), price = sellable.Sum(q => q.Price), fee = sellable.Sum(q => q.Fee), scrap = scrappable.Sum(q => q.Scrap);
            var q0 = qs[0];
            bool rentable = qs.Any(q => q.Rent > 0) || qs.All(q => q.Cannot != null);   // (sin el SQL nuevo: sin alquiler)
            int nCards = rentable ? 3 : 2;
            using var f = new Form { Text = Tr("Vender, alquilar o desguazar"), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, KeyPreview = true };
            DialogKit.Dress(f);
            var wa = Screen.FromControl(this).WorkingArea;
            int W = Math.Min(Theme.Px(nCards == 3 ? 1100 : 780), wa.Width - 40);
            bool stack = W < Theme.Px(nCards == 3 ? 900 : 640);   // pantalla estrecha: una tarjeta debajo de otra
            int cardW = stack ? W - Theme.Px(48) : (W - Theme.Px(48) - Theme.Px(16) * (nCards - 1)) / nCards;

            var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = stack ? 1 : nCards, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(24), Theme.Px(16), Theme.Px(24), Theme.Px(12)) };
            for (int i = 0; i < body.ColumnCount; i++) body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / body.ColumnCount));
            var intro = new Label
            {
                AutoSize = true, MaximumSize = new Size(W - Theme.Px(48), 0), ForeColor = Theme.Subtle, Font = Theme.Font(9.25f), Margin = new Padding(0, 0, 0, Theme.Px(10)),
                Text = (many ? string.Format(Tr("{0} ejemplares elegidos."), qs.Count) : q0.Name) + "  ·  " +
                       (many ? "" : string.Format(Tr("Precio nuevo {0} · {1} km · estado {2} %"), EurT(q0.PriceNew), q0.Km.ToString("N0", I18n.Culture), q0.Cond))
            };
            body.Controls.Add(intro); body.SetColumnSpan(intro, body.ColumnCount);

            var cols = new List<(Card card, FlowLayoutPanel col)>();
            Panel Card(string title, Color accent, string big, string bigSub, string[] lines, string button, bool primary, bool enabled, string why, int code)
            {
                bool last = cols.Count == nCards - 1;
                var card = new Card { Fill = Theme.Surface, BorderColor = Color.FromArgb(90, accent), Radius = 12, Padding = new Padding(Theme.Px(16)), Width = cardW, Margin = new Padding(0, 0, stack || last ? 0 : Theme.Px(16), Theme.Px(12)) };
                var col = new FlowLayoutPanel { Location = new Point(card.Padding.Left, card.Padding.Top), FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0) };
                Label L(string t, Color c, float size, FontStyle st = FontStyle.Regular, int top = 0) => new Label { Text = t, AutoSize = true, MaximumSize = new Size(cardW - Theme.Px(34), 0), ForeColor = c, BackColor = Theme.Surface, Font = Theme.Font(size, st), Margin = new Padding(0, top, 0, 0) };
                col.Controls.Add(L(title, accent, 10f, FontStyle.Bold));
                col.Controls.Add(L(big, Theme.Text, 17f, FontStyle.Bold, Theme.Px(6)));
                col.Controls.Add(L(bigSub, Theme.Subtle, 8.75f));
                foreach (var l in lines) col.Controls.Add(L("•  " + l, Theme.Text, 9f, FontStyle.Regular, Theme.Px(6)));
                if (!string.IsNullOrEmpty(why)) col.Controls.Add(L(why, Color.FromArgb(240, 196, 90), 9f, FontStyle.Regular, Theme.Px(8)));
                var b = primary ? DialogKit.Primary(button) : DialogKit.Danger(button);
                DialogKit.Fit(b, 170); b.Height = Theme.Px(36); b.Margin = new Padding(0, Theme.Px(12), 0, 0); b.Enabled = enabled;
                b.Click += (s, e) => { result = code; f.DialogResult = DialogResult.OK; f.Close(); };
                col.Controls.Add(b);
                card.Controls.Add(col);
                cols.Add((card, col));
                return card;
            }

            int days = q0.Days > 0 ? q0.Days : 14;
            string whySell = sellable.Count == 0 ? CannotText(qs.Select(q => q.Cannot).FirstOrDefault(c => c != null)) : (many && sellable.Count < qs.Count ? string.Format(Tr("Se pueden vender {0} de {1}."), sellable.Count, qs.Count) : null);
            var sell = Card(Tr("VENDER EN EL MERCADO"), Theme.AccentHi, EurT(gets), Tr("lo que recibe la empresa al venderse"),
                new[]
                {
                    string.Format(Tr("Precio de venta: {0} (lo fija SelectOR por el uso y el estado)"), EurT(price)),
                    string.Format(Tr("Comisión del mercado: {0} ({1} %)"), EurT(fee), (q0.FeePct * 100).ToString("0.#", I18n.Culture)),
                    string.Format(Tr("A la venta {0} días; puedes retirarlo antes."), days),
                    Tr("Mientras esté a la venta no puede salir en servicios."),
                }, many ? Tr("Poner todos a la venta") : Tr("Poner a la venta"), true, sellable.Count > 0, whySell, 1);
            bool onlyRented = scrappable.Count > 0 && scrappable.All(q => q.Cannot == "rented");
            bool onlyGranted = scrappable.Count > 0 && scrappable.All(q => q.Cannot == "granted");
            string whyScrap = scrappable.Count == 0 ? CannotText(qs.Select(q => q.Cannot).FirstOrDefault(c => c != null)) : null;
            var scr = Card(onlyRented ? Tr("DEVOLVER") : Tr("DESGUAZAR"), Color.FromArgb(229, 115, 115), EurT(scrap), onlyRented ? Tr("un tren alquilado se devuelve sin cobrar nada") : Tr("lo que paga el desguace, al momento"),
                onlyRented ? new[] { Tr("Deja de pagarse el alquiler.") }
                : onlyGranted ? new[] { Tr("Cedido por el administrador: desguazarlo no paga nada."), Tr("El tren desaparece de la flota para siempre.") }
                : new[]
                {
                    string.Format(Tr("El {0} % del precio nuevo: mucho menos que venderlo."), (q0.ScrapPct * 100).ToString("0.#", I18n.Culture)),
                    Tr("El tren desaparece de la flota para siempre."),
                }, onlyRented ? Tr("Devolver") : (many ? Tr("Desguazar todos") : Tr("Desguazar")), false, scrappable.Count > 0, whyScrap, 2);
            body.Controls.Add(sell);
            if (rentable)
            {
                var rentable2 = sellable.Where(q => q.Rent > 0).ToList();
                double rent = rentable2.Sum(q => q.Rent), general = rentable2.Sum(q => q.RentGeneral);
                int off = general > 0 ? (int)Math.Round(100 - 100 * rent / general) : 0;
                string whyRent = sellable.Count == 0 ? CannotText(qs.Select(q => q.Cannot).FirstOrDefault(c => c != null))
                               : rentable2.Count == 0 ? Tr("Ese tren no tiene precio: no se puede alquilar.") : null;
                var rnt = Card(Tr("ALQUILAR A OTRA EMPRESA"), TabTeal, EurT(rent), Tr("lo que te paga cada servicio que haga"),
                    new[]
                    {
                        string.Format(Tr("SelectOR lo alquila por {0} por servicio: en el mercado sale un {1} % más barato (el precio lo fija SelectOR por el uso y el estado)."), EurT(general), off),
                        Tr("Tú eliges el plazo: 7, 14 o 30 días. Al acabar vuelve a tu flota."),
                        Tr("Mientras está alquilado lo usa la otra empresa y paga ella su mantenimiento."),
                        Tr("Mientras esté en el mercado no puede salir en servicios."),
                    }, many ? Tr("Alquilar todos") : Tr("Ofrecer en alquiler"), true, rentable2.Count > 0, whyRent, 3);
                body.Controls.Add(rnt);
            }
            body.Controls.Add(scr);
            // las dos tarjetas con el alto de la más alta (con todo su contenido ya maquetado)
            int ch = 0;
            foreach (var (cd, cl) in cols) { cl.PerformLayout(); ch = Math.Max(ch, cl.GetPreferredSize(new Size(cardW - cd.Padding.Horizontal, 0)).Height); }
            foreach (var (cd, cl) in cols) cd.Height = ch + cd.Padding.Vertical + Theme.Px(4);

            var foot = new DialogKit.Footer();
            var cancel = DialogKit.Secondary(Tr("Cancelar")); cancel.Click += (s, e) => f.Close();
            foot.AddRight(cancel);
            f.Controls.Add(body); f.Controls.Add(foot);
            var head = DialogKit.Crown(f, Tr(rentable ? "Vender, alquilar o desguazar" : "Vender o desguazar"), Tr("El precio lo fija SelectOR: no lo decide la empresa"), "💶");
            body.PerformLayout();
            int bh = body.PreferredSize.Height;
            f.ClientSize = new Size(W, Math.Min(wa.Height - 60, head.Height + Theme.Px(6) + bh + foot.Height));
            body.Top = head.Bottom + Theme.Px(6);
            f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
            f.ShowDialog(this);
            return result;
        }

        async Task ListVehicles(List<SaleQuote> qs)
        {
            if (qs.Count == 0) return;
            Msg(_fleetMsg, Tr("Poniendo a la venta…"), false);
            int done = 0; string err = null; double sum = 0;
            foreach (var q in qs)
            {
                var (json, e) = await Supa.RpcAsync("market_list", new { p_vehicle = q.Id });
                if (e != null) err ??= e; else { done++; sum += q.Price; }
            }
            if (done == 0) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            Msg(_fleetMsg, (done == 1 ? string.Format(Tr("A la venta por {0}: ya lo ven las demás empresas en Tablón → Mercado de trenes."), EurT(sum))
                                      : string.Format(Tr("{0} trenes a la venta por {1} en total."), done, EurT(sum))) + (err != null ? "  " + Tr("Error: ") + err : ""), err != null);
            _fleetCards?.ClearMarks();
            LoadFleet();
        }

        async Task ScrapVehicles(List<SaleQuote> qs, bool withRented)
        {
            if (qs.Count == 0) return;
            Msg(_fleetMsg, Tr("Desguazando…"), false);
            int done = 0; string err = null; double sum = 0;
            foreach (var q in qs)
            {
                var (_, e) = await Supa.RpcAsync("retire_vehicle", new { p_vehicle = q.Id });
                if (e != null) err ??= e; else { done++; sum += q.Scrap; }
            }
            if (done == 0) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            Msg(_fleetMsg, (done == 1 ? (sum > 0 ? string.Format(Tr("Desguazado: la empresa recibe {0}."), EurT(sum)) : Tr("Tren devuelto."))
                                      : string.Format(Tr("{0} trenes desguazados o devueltos: la empresa recibe {1}."), done, EurT(sum))) + (err != null ? "  " + Tr("Error: ") + err : ""), err != null);
            _fleetCards?.ClearMarks();
            LoadCompanies();
            LoadFleet();
        }

        async Task WithdrawVehicle(string vehicleId)
        {
            var (json, err) = await Supa.RpcAsync("market_browse2", new { });   // (1.2.90: también los de alquiler)
            if (MissingRpc(err)) (json, err) = await Supa.RpcAsync("market_browse", new { });
            if (err != null) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            string listing = null; string name = "";
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    if (Str(e, "vehicle_id") == vehicleId) { listing = Str(e, "id"); name = Str(e, "name"); break; }
            }
            catch { }
            if (listing == null) { Msg(_fleetMsg, Tr("Ese tren ya no está en el mercado."), true); LoadFleet(); return; }
            if (ThemedBox.Show(this, string.Format(Tr("¿Retirar «{0}» del mercado? Vuelve a la flota y puede salir en servicios."), name), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, e2) = await Supa.RpcAsync("market_withdraw", new { p_listing = listing });
            if (e2 != null) { Msg(_fleetMsg, Tr("Error: ") + e2, true); return; }
            Msg(_fleetMsg, string.Format(Tr("«{0}» retirado del mercado: vuelve a estar disponible."), name), false);
            LoadFleet();
        }

        // Flota con trenes a la venta: el servidor cierra los anuncios que han pasado su plazo (como mucho una vez por minuto)
        DateTime _marketTickAt = DateTime.MinValue;
        async void MarketTickIfListed()
        {
            if (!_fleetStatus.Contains("for_sale") || (DateTime.UtcNow - _marketTickAt).TotalSeconds < 60) return;
            _marketTickAt = DateTime.UtcNow;
            var (_, err) = await Supa.RpcAsync("market_tick", new { });
            if (err == null) LoadFleet();   // (si alguno caducó, ya sale disponible; si no, la misma lista)
        }

        // ============================ Empresas → Tablón ============================
        Panel _tabPanel;
        Panel[] _tabPages;
        int _tabPage;
        CardTable _showList, _adsList, _jobsList, _mktList;
        Label _tabMsg;
        TablonBanner _tabBanner;   // (1.2.90) la cabecera: un teleindicador de estación (TablonBanner.cs)
        RoundButton _showJoinBtn, _showEditBtn, _adNewBtn, _adDelBtn, _jobApplyBtn, _jobNewBtn, _jobCloseBtn, _mktBuyBtn, _mktRentBtn, _mktWithdrawBtn, _mktReturnBtn;
        FlowLayoutPanel _tabBtns;
        int _tabSeq;
        sealed class ShowRow { public string Id, Name, Code, Slogan, About, Kinds; public bool Seeking, Mine; public int Members, Trains, Services, LeaguePos; public double Km; public Image Logo; }
        sealed class AdRow { public string Id, CompanyId, Company, Title, Body; public bool HasImage, Featured, Mine; public DateTime Expires; public Image Logo; }
        sealed class JobRow { public string Id, CompanyId, Company, Role, Title, Body, MyStatus; public int MinPoints, MinServices, Seats, Hired, Applicants, MyPoints, MyServices; public bool Member, Mine; public Image Logo; }
        // (1.2.90) Listing: «sale» (a la venta), «rent» (en alquiler) o «rental» (un alquiler de tu empresa, en curso o reciente)
        sealed class MktRow
        {
            public string Id, VehicleId, SellerId, Seller, Name, Kind, Engine, Listing = "sale", Role, ConFile, EndReason; public int Seats, Cond, RentDays, Services;
            public double Kw, Kmh, Km, Price, PriceNew, Earned; public DateTime Expires, Ends, Ended; public bool HasImage, Mine, Active, InUse; public Image Logo;
            public List<TrainVeh> Vehicles = new();
            public int Have = -1; public string HaveText = "";   // el material: 2 tienes su .con · 1 tienes sus vehículos · 0 te falta · −1 no se sabe
            public string Key => Listing == "rental" ? "R:" + Id : Id;
        }
        readonly List<ShowRow> _showRows = new();
        readonly List<AdRow> _adRows = new();
        readonly List<JobRow> _jobRows = new();
        readonly List<MktRow> _mktRows = new();

        static readonly string[] TablonTabNames = { "Escaparate", "Anuncios", "Empleo", "Mercado de trenes", "Transparencia" };
        static readonly string[] TabIcons = { "🏢", "📌", "🧑‍✈️", "🚆", "📊" };
        static readonly string[] TabKickers = { "Las empresas de SelectOR", "Avisos de las empresas", "Bolsa de trabajo", "Compra, venta y alquiler", "Portal de transparencia" };
        static readonly Color TabTeal = Color.FromArgb(77, 182, 172), TabOrange = Color.FromArgb(255, 152, 67), TabBlue = Color.FromArgb(120, 144, 226);
        static readonly Color[] TabColors = { Theme.Gold, TabOrange, TabBlue, Theme.AccentHi, TabTeal };
        static readonly string[] TabIntros =
        {
            "Las empresas que quieren darse a conocer, con sus datos del mes. Si una busca socios, puedes pedir unirte desde aquí.",
            "Anuncios de las empresas: novedades, servicios nuevos, quedadas en la sala… Duran 3 o 7 días; los destacados salen primero.",
            "Ofertas de empleo de las empresas. Si cumples los requisitos, apúntate con un mensaje: la empresa acepta o rechaza tu solicitud.",
            "Trenes que venden o alquilan otras empresas, al precio que fija SelectOR según su uso y estado. En cada uno ves si tienes su material en tu carpeta.",
            "Cómo va cada empresa en el mes: lo que cuenta en la Liga y lo que no, y unos indicadores iguales para todas. Sin nombres de maquinistas.",
        };
        readonly Dictionary<int, List<string>> _tabNews = new();   // lo último de cada página, para el letrero de la cabecera

        void TablonNews(int page, IEnumerable<string> items)
        {
            _tabNews[page] = items.Take(6).ToList();
            var all = new List<string>();
            for (int i = 0; i < 6 && all.Count < 18; i++)
                foreach (var p in _tabNews.Keys.OrderBy(k => k))
                    if (i < _tabNews[p].Count) all.Add(_tabNews[p][i]);
            _tabBanner?.SetTicker(all);
        }

        void TablonBannerPage(int i)
            => _tabBanner?.SetPage(TabIcons[i], Tr(TabKickers[i]), Tr(TablonTabNames[i]), Tr(TabIntros[i]), TabColors[i]);

        Panel BuildTablonSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // (1.2.90) arriba, el teleindicador con la página, su explicación, la hora y lo último del tablón
            _tabBanner = new TablonBanner { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 0, 0, Theme.Px(6)) };
            TablonBannerPage(0);
            t.Controls.Add(_tabBanner);

            var top = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 4) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tabs = MakeSubTabs(TablonTabNames, ShowTablonPage);
            // cada pestaña con su icono; si no caben (ventana estrecha), más juntas y, si aún no, sin los iconos
            int fitLevel = -1;
            void FitTabs()
            {
                int avail = top.ClientSize.Width - top.GetColumnWidths().Skip(1).Sum() - 4;
                if (avail <= 0) return;
                using var tf = Theme.Font(9.75f, FontStyle.Bold);
                for (int level = 0; level < 3; level++)
                {
                    int total = 0;
                    var texts = new List<(RoundButton b, string t, int w)>();
                    for (int i = 0; i < tabs.Controls.Count && i < TabIcons.Length; i++)
                        if (tabs.Controls[i] is RoundButton tb)
                        {
                            string name = Tr(TablonTabNames[i]);
                            string tx = level == 0 ? TabIcons[i] + "  " + name : level == 1 ? TabIcons[i] + " " + name : name;
                            int w = TextRenderer.MeasureText(tx, tf).Width + (level == 0 ? 34 : 20);
                            texts.Add((tb, tx, w)); total += w + tb.Margin.Horizontal;
                        }
                    if (total <= avail || level == 2)
                    {
                        if (level != fitLevel) { foreach (var (b, tx, w) in texts) { b.Text = tx; b.Width = w; } fitLevel = level; }
                        return;
                    }
                }
            }
            top.SizeChanged += (s, e) => FitTabs();
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            var search = new RoundedInput(I18n.T("🔎  Buscar…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 3, 0, 3) };
            search.Box.TextChanged += (s, e) => { foreach (var l in new[] { _showList, _adsList, _jobsList, _mktList, _trnList }) l?.Filter(search.Box.Text); };
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(search, 1, 0);
            t.Controls.Add(top);

            CardTable Cards(int cols, int h, int minW)
            {
                var c = EmpCards(); c.Columns = cols; c.CardHeight = h; c.MinWidth = minW;
                c.TitleCol = 0; c.PillCol = 1; c.SubCols = new[] { 2, 3 }; c.QuoteCol = 4; c.RightCol = 5; c.RightSubCols = new[] { 6 };
                c.SelectedIndexChanged += (s, e) => UpdateTablonButtons();
                // (1.2.84) cada tarjeta del alto de su texto (nada cortado) y los datos en pastillas
                c.AutoHeight = true; c.SubChips = true;
                return c;
            }
            _showList = Cards(2, 80, 460); _showList.AvatarSize = 46; _showList.ItemActivated += _ => ShowCompanyCard();
            _showList.Accent = row => _showRows.FirstOrDefault(x => x.Id == row.Key) is ShowRow sr && sr.Seeking ? Theme.Gold : Color.FromArgb(150, Theme.Gold);
            _adsList = Cards(1, 80, 520); _adsList.Icon = null; _adsList.ThumbWidth = 220; _adsList.QuoteMarks = false; _adsList.ItemActivated += _ => ShowAdImage();
            _adsList.Accent = row => _adRows.FirstOrDefault(x => x.Id == row.Key) is AdRow ar && ar.Featured ? Theme.Gold : TabOrange;
            // la imagen del anuncio a la vista, a la izquierda de su tarjeta (doble clic: en grande)
            _adsList.ThumbSlot = row => _adRows.FirstOrDefault(a => a.Id == row.Key)?.HasImage == true;
            _adsList.Thumb = row => row.Key != null && _adImgs.TryGetValue(row.Key, out var im) ? im : null;
            _jobsList = Cards(1, 72, 520); _jobsList.ItemActivated += _ => ApplyJob(); _jobsList.Accent = _ => TabBlue;
            // (1.2.90) el mercado, en «billetes»: sin «Ver composición» (la composición ya se ve en la tarjeta); debajo del
            // uso, si tienes el material del tren en tu carpeta
            _mktList = Cards(2, 96, 460); _mktList.Ticket = true;
            _mktList.SubCols = new[] { 2, 3 }; _mktList.QuoteCol = 4; _mktList.QuoteMarks = false; _mktList.RightSubCols = new[] { 6, 7 };
            _mktList.Accent = row => _mktRows.FirstOrDefault(m => m.Key == row.Key) is MktRow m ? (m.Listing == "sale" ? Theme.AccentHi : m.Listing == "rent" ? TabTeal : m.Active ? TabBlue : Theme.SurfaceHi) : null;
            // la composición 2D de cada tren abajo de su tarjeta, como en Flota (la imagen guardada con el tren)
            _mktList.StripHeight = 56; _mktList.StripRows = 1;   // el tren entero en un renglón (AutoHeight: debajo del texto)
            _mktList.Strip = row => row.Key != null && _mktStrips.TryGetValue(row.Key, out var b) ? b : null;
            _mktList.StripNote = row => _mktRows.FirstOrDefault(m => m.Key == row.Key) is MktRow m && !m.HasImage ? Tr("Sin composición guardada") : null;
            _trnList = BuildTransparencyList(Cards);   // (1.2.90) MainMenuForm.Transparencia.cs
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
            _tabPages = new Panel[5];
            var lists = new[] { _showList, _adsList, _jobsList, _mktList, _trnList };
            for (int i = 0; i < lists.Length; i++)
            {
                var pg = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Visible = i == 0 };
                lists[i].Dock = DockStyle.Fill; pg.Controls.Add(lists[i]);
                _tabPages[i] = pg; host.Controls.Add(pg);
            }
            t.Controls.Add(host);

            _tabBtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Bg, Margin = new Padding(0), MaximumSize = new Size(4000, 0) };
            RoundButton B(string text, bool primary, Action a, int w = 0)
            {
                var b = EmpButton(Tr(text), primary);
                using var fb = Theme.Font(10.5f, FontStyle.Bold);
                b.Width = Math.Max(w, TextRenderer.MeasureText(b.Text, fb).Width + 40);
                b.Margin = new Padding(2, 8, 8, 2);
                b.Click += (s, e) => a();
                return b;
            }
            _showJoinBtn = B("Pedir unirme…", true, JoinFromShowcase);
            _showEditBtn = B("Ficha de mi empresa…", false, EditCompanyProfile);
            _adNewBtn = B("Publicar anuncio…", true, NewAd);
            // (1.2.84, petición) sin «Ver imagen»: la imagen se ve en la tarjeta y, en grande, con doble clic
            _adDelBtn = B("Quitar anuncio", false, DeleteAd);
            _jobApplyBtn = B("Apuntarme…", true, ApplyJob);
            _jobNewBtn = B("Publicar oferta…", false, NewJob);
            _jobCloseBtn = B("Cerrar oferta", false, CloseJob);
            _mktBuyBtn = B("Comprar…", true, BuyFromMarket);
            _mktRentBtn = B("Alquilar…", true, RentFromMarket);
            _mktWithdrawBtn = B("Retirar del mercado", false, WithdrawFromMarket);
            _mktReturnBtn = B("Devolver el tren", false, ReturnRental);
            _trnMonthBtn = B("Ver el mes anterior", false, ToggleTransparencyMonth);
            _trnHowBtn = B("¿Cómo se calcula?", false, ShowLeagueRules);
            _tabBtns.Controls.AddRange(new Control[] { _showJoinBtn, _showEditBtn, _adNewBtn, _adDelBtn, _jobApplyBtn, _jobNewBtn, _jobCloseBtn, _mktBuyBtn, _mktRentBtn, _mktWithdrawBtn, _mktReturnBtn, _trnMonthBtn, _trnHowBtn });
            t.Controls.Add(_tabBtns);
            _tabMsg = EmpMsg(); _tabMsg.MaximumSize = new Size(900, 0); _tabMsg.Margin = new Padding(2, 4, 2, 2);
            t.Controls.Add(_tabMsg);
            t.SizeChanged += (s, e) => _tabBtns.MaximumSize = new Size(Math.Max(200, t.ClientSize.Width - 4), 0);
            t.SizeChanged += (s, e) => { if (_tabBanner != null) _tabBanner.Compact = t.ClientSize.Height < Theme.Px(560); };   // (ventana baja: cabecera baja)
            _tabPanel = t;
            UpdateTablonButtons();
            return t;
        }

        void ShowTablonPage(int i)
        {
            _tabPage = i;
            for (int k = 0; k < _tabPages.Length; k++) _tabPages[k].Visible = k == i;
            TablonBannerPage(i);
            Msg(_tabMsg, "", false);
            UpdateTablonButtons();
            LoadTablon();
        }

        void UpdateTablonButtons()
        {
            if (_tabBtns == null) return;
            bool manage = (CanManage() || Supa.IsSuperadmin) && _empSel != null;
            void V(Control c, bool v) { if (c != null && c.Visible != v) c.Visible = v; }
            var sh = Sel(_showList, _showRows); var ad = Sel(_adsList, _adRows); var jb = Sel(_jobsList, _jobRows); var mk = Sel(_mktList, _mktRows);
            V(_showJoinBtn, _tabPage == 0); if (_showJoinBtn != null) _showJoinBtn.Enabled = sh != null && !sh.Mine && sh.Seeking;
            V(_showEditBtn, _tabPage == 0 && manage);
            V(_adNewBtn, _tabPage == 1 && manage);
            V(_adDelBtn, _tabPage == 1 && (manage || Supa.IsSuperadmin)); if (_adDelBtn != null) _adDelBtn.Enabled = ad != null && (ad.Mine || Supa.IsSuperadmin);
            V(_jobApplyBtn, _tabPage == 2); if (_jobApplyBtn != null) _jobApplyBtn.Enabled = jb != null && !jb.Member && string.IsNullOrEmpty(jb.MyStatus) && jb.MyPoints >= jb.MinPoints && jb.MyServices >= jb.MinServices;
            V(_jobNewBtn, _tabPage == 2 && manage);
            V(_jobCloseBtn, _tabPage == 2 && (manage || Supa.IsSuperadmin)); if (_jobCloseBtn != null) _jobCloseBtn.Enabled = jb != null && (jb.Mine || Supa.IsSuperadmin);
            // (1.2.90) comprar o alquilar según el anuncio elegido; «Devolver» para los que tu empresa tiene alquilados
            bool listing = mk != null && mk.Listing != "rental";
            V(_mktBuyBtn, _tabPage == 3 && manage && (mk == null || mk.Listing == "sale")); if (_mktBuyBtn != null) _mktBuyBtn.Enabled = mk != null && mk.Listing == "sale" && mk.SellerId != _empSel?.Id;
            V(_mktRentBtn, _tabPage == 3 && manage && mk != null && mk.Listing == "rent"); if (_mktRentBtn != null) _mktRentBtn.Enabled = mk != null && mk.Listing == "rent" && mk.SellerId != _empSel?.Id;
            V(_mktWithdrawBtn, _tabPage == 3 && manage && (mk == null || listing)); if (_mktWithdrawBtn != null) _mktWithdrawBtn.Enabled = listing && (mk.Mine || Supa.IsSuperadmin);
            V(_mktReturnBtn, _tabPage == 3 && manage && mk != null && mk.Listing == "rental" && mk.Role == "renter" && mk.Active);
            if (_mktReturnBtn != null) _mktReturnBtn.Enabled = mk != null && mk.Listing == "rental" && mk.Active && !mk.InUse;
            V(_trnMonthBtn, _tabPage == 4); V(_trnHowBtn, _tabPage == 4);
            if (_trnMonthBtn != null) { string mt = Tr(_trnPrev ? "Ver este mes" : "Ver el mes anterior"); if (_trnMonthBtn.Text != mt) { _trnMonthBtn.Text = mt; using var fb = Theme.Font(10.5f, FontStyle.Bold); _trnMonthBtn.Width = TextRenderer.MeasureText(mt, fb).Width + 40; } }
            foreach (Control c in _tabBtns.Controls) c.Invalidate();
        }

        static T Sel<T>(CardTable l, List<T> rows) where T : class { int i = l?.SelectedRow ?? -1; return i >= 0 && i < rows.Count ? rows[i] : null; }

        bool _tabNewsAsked;
        async void TablonPrefetchNews()
        {
            if (_tabNewsAsked) return;
            _tabNewsAsked = true;
            try
            {
                var (aj, ae) = await Supa.RpcAsync("ads_list", new { });
                if (ae == null && !_tabNews.ContainsKey(1)) { using var d = JsonDocument.Parse(aj); TablonNews(1, d.RootElement.EnumerateArray().Select(e => "📌 " + Str(e, "company") + ": " + Str(e, "title")).ToList()); }
                var (jj, je) = await Supa.RpcAsync("jobs_list", new { });
                if (je == null && !_tabNews.ContainsKey(2)) { using var d = JsonDocument.Parse(jj); TablonNews(2, d.RootElement.EnumerateArray().Select(e => "🧑‍✈️ " + string.Format(Tr("{0} busca: {1}"), Str(e, "company"), Str(e, "title"))).ToList()); }
                var (mj, me) = await Supa.RpcAsync("market_browse2", new { });
                if (me == null && !_tabNews.ContainsKey(3)) { using var d = JsonDocument.Parse(mj); TablonNews(3, d.RootElement.EnumerateArray().Select(e => MarketNews(Str(e, "listing_kind"), Str(e, "name"), Num(e, "price"))).ToList()); }
            }
            catch { }
        }

        static string MarketNews(string kind, string name, double price)
            => "🚆 " + (kind == "rent" ? string.Format(Tr("En alquiler: {0} · {1} por servicio"), name, EurT(price)) : string.Format(Tr("En venta: {0} · {1}"), name, EurT(price)));

        void LoadTablon()
        {
            if (_tabPanel == null || !Supa.IsLoggedIn) return;
            TablonPrefetchNews();
            switch (_tabPage)
            {
                case 0: LoadShowcase(); break;
                case 1: LoadAds(); break;
                case 2: LoadJobs(); break;
                case 3: LoadMarket(); break;
                default: LoadTransparency(); break;
            }
        }

        // un error de la consulta → el texto de la lista vacía (sin el SQL: el aviso de servidor sin actualizar)
        static string TabErr(string err) => MissingRpc(err) ? Tr("El servidor aún no tiene el tablón (falta tablon-mercado.sql).") : Tr("Error: ") + err;

        static string DaysLeft(DateTime exp)
        {
            var left = exp - DateTime.UtcNow;
            if (left.TotalHours < 24) return left.TotalHours < 1 ? Tr("acaba en menos de una hora") : string.Format(Tr("quedan {0} h"), (int)Math.Ceiling(left.TotalHours));
            int d = (int)Math.Ceiling(left.TotalDays);
            return d == 1 ? Tr("queda 1 día") : string.Format(Tr("quedan {0} días"), d);
        }
        static DateTime Utc(JsonElement e, string p) => DateTime.TryParse(Str(e, p), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : DateTime.UtcNow;

        // ---------- Escaparate ----------
        async void LoadShowcase()
        {
            int seq = ++_tabSeq;
            if (_showRows.Count == 0) _showList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("showcase_list", new { });
            if (seq != _tabSeq) return;
            _showRows.Clear();
            if (err != null) { _showList.SetEmpty(TabErr(err)); UpdateTablonButtons(); return; }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _showRows.Add(new ShowRow
                    {
                        Id = Str(e, "id"), Name = Str(e, "name"), Code = Str(e, "code"), Slogan = Str(e, "slogan"), About = Str(e, "about"), Kinds = Str(e, "kinds"),
                        Seeking = Bool(e, "seeking"), Mine = Bool(e, "mine"), Members = (int)Num(e, "members"), Trains = (int)Num(e, "trains"),
                        Services = (int)Num(e, "services_month"), LeaguePos = (int)Num(e, "league_pos"), Km = Num(e, "km_month"), Logo = LogoFor(Str(e, "id"), Str(e, "logo"))
                    });
            }
            catch { }
            _showList.ClearRows();
            foreach (var r in _showRows)
            {
                string kinds = r.Kinds switch { "pax" => Tr("Viajeros"), "freight" => Tr("Mercancías"), "mixed" => Tr("Viajeros y mercancías"), _ => "" };
                string pill = r.Mine ? Tr("Tu empresa") : r.Seeking ? Tr("Busca socios") : "";
                string sub1 = string.Join("  ·  ", new[] { kinds, string.Format(r.Members == 1 ? Tr("{0} socio") : Tr("{0} socios"), r.Members), string.Format(r.Trains == 1 ? Tr("{0} tren") : Tr("{0} trenes"), r.Trains) }.Where(x => x.Length > 0));
                string sub2 = string.Format(Tr("{0} km y {1} servicios este mes"), r.Km.ToString("N0", I18n.Culture), r.Services.ToString("N0", I18n.Culture));
                string quote = r.Slogan.Length > 0 ? r.Slogan : r.About;
                string right = r.LeaguePos > 0 ? r.LeaguePos.ToString(I18n.Culture) + ".º" : "—";
                _showList.AddRow(new[] { r.Name, pill, sub1, sub2, quote, right, Tr("en la Liga") },
                    new Color?[] { null, r.Mine ? Theme.AccentHi : Color.FromArgb(240, 196, 90), Theme.Subtle, Theme.Subtle, null, Theme.AccentHi, Theme.Subtle }, r.Logo, r.Id);
            }
            if (_showRows.Count == 0) _showList.SetEmpty(Tr("Todavía no hay empresas en el escaparate. El gerente o un gestor la añade con «Ficha de mi empresa…»."));
            TablonNews(0, _showRows.Where(r => r.Seeking).Select(r => "🏢 " + string.Format(Tr("{0} busca socios"), r.Name)));
            UpdateTablonButtons();
        }

        void ShowCompanyCard()
        {
            var r = Sel(_showList, _showRows); if (r == null) return;
            string txt = r.Name + (r.Code.Length > 0 ? "  (" + r.Code + ")" : "") + "\n\n" + (r.Slogan.Length > 0 ? "«" + r.Slogan + "»\n\n" : "") + (r.About.Length > 0 ? r.About + "\n\n" : "")
                       + string.Format(Tr("{0} socios · {1} trenes · {2} km este mes"), r.Members, r.Trains, r.Km.ToString("N0", I18n.Culture))
                       + (r.Seeking && !r.Mine ? "\n\n" + Tr("Busca socios: puedes pedir unirte.") : "");
            ThemedBox.Show(this, txt, Tr("Escaparate"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        async void JoinFromShowcase()
        {
            var r = Sel(_showList, _showRows); if (r == null || r.Mine) return;
            string note = null;
            using (var dlg = new FormDialog(string.Format(Tr("Pedir unirme a {0}"), r.Name), Tr("Enviar solicitud"), 560, "👤"))
            {
                dlg.AddInfo(Tr("El gerente o un gestor de la empresa verán tu solicitud y la aceptarán o rechazarán."));
                dlg.AddMultiline("note", Tr("Mensaje para la empresa (opcional)"), "", 100);
                dlg.Validate = async f => { note = f.Get("note"); await Task.CompletedTask; return note.Length > 400 ? Tr("El mensaje es demasiado largo (400 caracteres como mucho).") : null; };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            }
            var (_, err) = await Supa.RpcAsync("request_join_by_name", new { p_name = r.Name, p_note = note ?? "" });
            Msg(_tabMsg, err != null ? Tr("Error: ") + err : string.Format(Tr("Solicitud enviada a {0}."), r.Name), err != null);
        }

        async void EditCompanyProfile()
        {
            var c = _empSel; if (c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            var (json, err) = await Supa.RpcAsync("company_profile_get", new { p_company = c.Id });
            if (err != null) { Msg(_tabMsg, TabErr(err), true); return; }
            bool show = false, seek = false; string slogan = "", about = "";
            try { using var d = JsonDocument.Parse(json); var r = d.RootElement; show = Bool(r, "showcase"); seek = Bool(r, "seeking"); slogan = Str(r, "slogan"); about = Str(r, "about"); } catch { }
            using var dlg = new FormDialog(string.Format(Tr("Ficha de {0}"), c.Name), Tr("Guardar"), 600, "🏢", Tr("Lo que ven las demás empresas y los maquinistas en el escaparate"));
            var no = new[] { Tr("No"), Tr("Sí") };
            dlg.AddCombo("show", Tr("Mostrar la empresa en el escaparate"), no, show ? 1 : 0);
            dlg.AddCombo("seek", Tr("Buscamos socios (se puede pedir unirse desde el escaparate)"), no, seek ? 1 : 0);
            dlg.AddText("slogan", Tr("Eslogan (60 caracteres como mucho)"), slogan, Tr("Por ejemplo: puntuales por vocación"));
            dlg.AddMultiline("about", Tr("Presentación (600 caracteres como mucho)"), about, 110);
            dlg.AddInfo(Tr("Junto a la ficha salen los datos del mes: socios, trenes, km, servicios y el puesto en la Liga. Nada de dinero."));
            dlg.Validate = async f =>
            {
                string sl = f.Get("slogan"), ab = f.Get("about");
                if (sl.Length > 60) return Tr("El eslogan es demasiado largo (60 caracteres como mucho).");
                if (ab.Length > 600) return Tr("La presentación es demasiado larga (600 caracteres como mucho).");
                var (_, e) = await Supa.RpcAsync("company_profile_set", new { p_company = c.Id, p_showcase = f.Index("show") == 1, p_seeking = f.Index("seek") == 1, p_slogan = sl, p_about = ab });
                return e != null ? Tr("Error: ") + e : null;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_tabMsg, Tr("Ficha guardada."), false);
            LoadShowcase();
        }

        // ---------- Anuncios ----------
        async void LoadAds()
        {
            int seq = ++_tabSeq;
            if (_adRows.Count == 0) _adsList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("ads_list", new { });
            if (seq != _tabSeq) return;
            _adRows.Clear();
            if (err != null) { _adsList.SetEmpty(TabErr(err)); UpdateTablonButtons(); return; }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _adRows.Add(new AdRow
                    {
                        Id = Str(e, "id"), CompanyId = Str(e, "company_id"), Company = Str(e, "company"), Title = Str(e, "title"), Body = Str(e, "body"),
                        HasImage = Bool(e, "has_image"), Featured = Bool(e, "featured"), Mine = Bool(e, "mine"), Expires = Utc(e, "expires_at"),
                        Logo = LogoFor(Str(e, "company_id"), Str(e, "logo"))
                    });
            }
            catch { }
            _adsList.ClearRows();
            int k = 0;
            foreach (var r in _adRows)
            {
                if (r.Featured) _adsList.Badges[k] = Tr("Destacado");
                _adsList.AddRow(new[] { r.Title, "", r.Company, DaysLeft(r.Expires), r.Body, "", "" },
                    new Color?[] { null, Theme.Subtle, Theme.AccentHi, Theme.Subtle, null, null, null }, r.Logo, r.Id);
                k++;
            }
            if (_adRows.Count == 0) _adsList.SetEmpty(Tr("No hay anuncios ahora mismo."));
            TablonNews(1, _adRows.Select(r => "📌 " + r.Company + ": " + r.Title));
            UpdateTablonButtons();
            FetchAdImages(seq);
        }

        // Las imágenes de los anuncios (ad_image, una vez por anuncio), en memoria y reducidas para la tarjeta.
        readonly Dictionary<string, Image> _adImgs = new(StringComparer.OrdinalIgnoreCase);

        async void FetchAdImages(int seq)
        {
            foreach (var r in _adRows.ToList())
            {
                if (seq != _tabSeq || IsDisposed) return;
                if (!r.HasImage || _adImgs.ContainsKey(r.Id)) continue;
                var (json, err) = await Supa.RpcAsync("ad_image", new { p_ad = r.Id });
                if (err != null) return;
                string uri = JsonText(json);
                var img = await Task.Run(() => AdThumb(uri));
                if (img == null || IsDisposed) continue;
                _adImgs[r.Id] = img;
                _adsList?.Invalidate();
            }
        }

        // data URI (o base64) → imagen de como mucho 480 px de ancho
        static Image AdThumb(string data)
        {
            try
            {
                if (string.IsNullOrEmpty(data)) return null;
                int comma = data.IndexOf(',');
                string b64 = data.StartsWith("data:") && comma > 0 ? data.Substring(comma + 1) : data;
                if (b64.Length > 300_000) return null;
                using var ms = new System.IO.MemoryStream(Convert.FromBase64String(b64.Trim()));
                using var raw = Image.FromStream(ms);
                if (raw.Width < 1 || raw.Height < 1 || raw.Width > 2048 || raw.Height > 2048) return null;
                float k = Math.Min(1f, 480f / raw.Width);
                var bmp = new Bitmap(Math.Max(1, (int)(raw.Width * k)), Math.Max(1, (int)(raw.Height * k)));
                using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(raw, 0, 0, bmp.Width, bmp.Height); }
                return bmp;
            }
            catch { return null; }
        }

        async void NewAd()
        {
            var c = _empSel; if (c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            double fee = 25000;
            try { var (fj, fe) = await Supa.SelectAsync("app_settings?select=ad_featured_fee&id=eq.1"); if (fe == null) { using var d = JsonDocument.Parse(fj); if (d.RootElement.GetArrayLength() > 0) fee = Num(d.RootElement[0], "ad_featured_fee"); } } catch { }
            string image = null;
            using var dlg = new FormDialog(Tr("Publicar anuncio"), Tr("Publicar"), 600, "📌", string.Format(Tr("Lo verán todos en Tablón → Anuncios, con el nombre de {0}"), c.Name));
            dlg.AddText("title", Tr("Título (80 caracteres como mucho)"), "", Tr("Por ejemplo: nuevo servicio Sur – Norte cada hora"));
            dlg.AddMultiline("body", Tr("Texto (400 caracteres como mucho)"), "", 100);
            dlg.AddCombo("days", Tr("Duración"), new[] { Tr("3 días"), Tr("7 días") }, 1);
            dlg.AddCombo("feat", Tr("Destacado (sale el primero)"), new[] { Tr("No"), string.Format(Tr("Sí: {0} de la empresa"), EurT(fee)) }, 0);
            var imgRow = new FlowLayoutPanel { BackColor = Theme.Bg, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            var pick = DialogKit.Secondary(Tr("Elegir imagen…"), 150); DialogKit.Fit(pick, 150); pick.Height = Theme.Px(32);
            var imgLbl = new Label { AutoSize = true, ForeColor = Theme.Subtle, Font = Theme.Font(9f), Text = Tr("Sin imagen (opcional)"), Margin = new Padding(Theme.Px(10), Theme.Px(8), 0, 0) };
            pick.Click += (s, e) =>
            {
                using var od = new OpenFileDialog { Title = Tr("Elegir imagen"), Filter = "Imágenes|*.png;*.jpg;*.jpeg;*.bmp;*.gif" };
                if (od.ShowDialog(dlg) != DialogResult.OK) return;
                var (uri, why) = AdImageUri(od.FileName);
                if (uri == null) { imgLbl.Text = why; imgLbl.ForeColor = Color.FromArgb(229, 115, 115); return; }
                image = uri; imgLbl.Text = System.IO.Path.GetFileName(od.FileName); imgLbl.ForeColor = Theme.AccentHi;
            };
            imgRow.Controls.Add(pick); imgRow.Controls.Add(imgLbl);
            dlg.AddControl(imgRow, Theme.Px(40));
            dlg.AddInfo(Tr("Como mucho 3 anuncios activos por empresa. El administrador puede quitar los que no sean apropiados."));
            dlg.Validate = async f =>
            {
                string ti = f.Get("title"), bo = f.Get("body");
                if (ti.Length < 3) return Tr("El título del anuncio es demasiado corto.");
                if (ti.Length > 80 || bo.Length > 400) return Tr("El texto es demasiado largo.");
                var (_, e) = await Supa.RpcAsync("ad_create", new { p_company = c.Id, p_title = ti, p_body = bo, p_image = image, p_days = f.Index("days") == 0 ? 3 : 7, p_featured = f.Index("feat") == 1 });
                return e != null ? Tr("Error: ") + e : null;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_tabMsg, Tr("Anuncio publicado."), false);
            LoadCompanies();
            LoadAds();
        }

        // la imagen del anuncio: como mucho 960×540, en JPEG
        static (string uri, string why) AdImageUri(string file)
        {
            try
            {
                using var src = Image.FromFile(file);
                float sc = Math.Min(1f, Math.Min(960f / src.Width, 540f / src.Height));
                int w = Math.Max(1, (int)(src.Width * sc)), h = Math.Max(1, (int)(src.Height * sc));
                using var bmp = new Bitmap(w, h);
                using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.Clear(Color.Black); g.DrawImage(src, 0, 0, w, h); }
                var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders().First(x => x.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                foreach (long q in new long[] { 82, 70, 55, 40 })
                {
                    using var ms = new System.IO.MemoryStream();
                    using var ps = new System.Drawing.Imaging.EncoderParameters(1);
                    ps.Param[0] = new System.Drawing.Imaging.EncoderParameter(System.Drawing.Imaging.Encoder.Quality, q);
                    bmp.Save(ms, enc, ps);
                    string uri = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                    if (uri.Length <= 300000) return (uri, null);
                }
                return (null, Tr("La imagen es demasiado grande."));
            }
            catch (Exception ex) { return (null, Tr("No se pudo leer la imagen: ") + ex.Message); }
        }

        async void ShowAdImage()
        {
            var r = Sel(_adsList, _adRows); if (r == null || !r.HasImage) return;
            var (json, err) = await Supa.RpcAsync("ad_image", new { p_ad = r.Id });
            if (err != null) { Msg(_tabMsg, Tr("Error: ") + err, true); return; }
            ShowImageWindow(r.Title + " · " + r.Company, JsonText(json), r.Body);
        }

        async void DeleteAd()
        {
            var r = Sel(_adsList, _adRows); if (r == null || !(r.Mine || Supa.IsSuperadmin)) return;
            if (ThemedBox.Show(this, string.Format(Tr("¿Quitar el anuncio «{0}»?"), r.Title), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("ad_delete", new { p_ad = r.Id });
            Msg(_tabMsg, err != null ? Tr("Error: ") + err : Tr("Anuncio quitado."), err != null);
            LoadAds();
        }

        // ---------- Empleo ----------
        static readonly string[] JobRoles = { "pax", "freight", "driver", "manager", "dispatcher" };
        static string JobRoleText(string r) => r switch
        {
            "pax" => Tr("Maquinista de viajeros"), "freight" => Tr("Maquinista de mercancías"), "manager" => Tr("Gestor"),
            "dispatcher" => Tr("Jefe de circulación"), _ => Tr("Maquinista"),
        };

        async void LoadJobs()
        {
            int seq = ++_tabSeq;
            if (_jobRows.Count == 0) _jobsList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("jobs_list", new { });
            if (seq != _tabSeq) return;
            _jobRows.Clear();
            if (err != null) { _jobsList.SetEmpty(TabErr(err)); UpdateTablonButtons(); return; }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _jobRows.Add(new JobRow
                    {
                        Id = Str(e, "id"), CompanyId = Str(e, "company_id"), Company = Str(e, "company"), Role = Str(e, "role"), Title = Str(e, "title"), Body = Str(e, "body"),
                        MinPoints = (int)Num(e, "min_points"), MinServices = (int)Num(e, "min_services"), Seats = (int)Num(e, "seats"), Hired = (int)Num(e, "hired"),
                        Applicants = (int)Num(e, "applicants"), MyPoints = (int)Num(e, "my_points"), MyServices = (int)Num(e, "my_services"), MyStatus = Str(e, "my_status"),
                        Member = Bool(e, "member"), Mine = Bool(e, "mine"), Logo = LogoFor(Str(e, "company_id"), Str(e, "logo"))
                    });
            }
            catch { }
            _jobsList.ClearRows();
            foreach (var r in _jobRows)
            {
                var req = new List<string>();
                if (r.MinPoints > 0) req.Add(string.Format(Tr("carné con {0} puntos o más"), r.MinPoints));
                if (r.MinServices > 0) req.Add(string.Format(Tr("{0} servicios hechos"), r.MinServices));
                string reqTxt = req.Count == 0 ? Tr("sin requisitos") : Tr("Requisitos: ") + string.Join(", ", req);
                bool ok = r.MyPoints >= r.MinPoints && r.MyServices >= r.MinServices;
                string state = r.Member ? Tr("Ya eres socio") : r.MyStatus switch { "pending" => Tr("Solicitud enviada"), "approved" => Tr("Aceptada"), "rejected" => Tr("Rechazada"), _ => ok ? Tr("Cumples los requisitos") : Tr("No cumples los requisitos") };
                Color stc = r.Member || r.MyStatus == "approved" ? Theme.AccentHi : r.MyStatus == "rejected" || (!ok && string.IsNullOrEmpty(r.MyStatus)) ? Color.FromArgb(229, 115, 115) : r.MyStatus == "pending" ? Color.FromArgb(120, 144, 226) : Theme.AccentHi;
                string plazas = string.Format(r.Seats == 1 ? Tr("{0} plaza") : Tr("{0} plazas"), r.Seats) + (r.Applicants > 0 ? "  ·  " + string.Format(r.Applicants == 1 ? Tr("{0} apuntado") : Tr("{0} apuntados"), r.Applicants) : "");
                _jobsList.AddRow(new[] { r.Title, JobRoleText(r.Role), r.Company + "  ·  " + plazas, reqTxt, r.Body, state, "" },
                    new Color?[] { null, Color.FromArgb(120, 144, 226), Theme.AccentHi, Theme.Subtle, null, stc, null }, r.Logo, r.Id);
            }
            if (_jobRows.Count == 0) _jobsList.SetEmpty(Tr("No hay ofertas de empleo abiertas ahora mismo."));
            TablonNews(2, _jobRows.Select(r => "🧑‍✈️ " + string.Format(Tr("{0} busca: {1}"), r.Company, r.Title)));
            UpdateTablonButtons();
        }

        async void ApplyJob()
        {
            var r = Sel(_jobsList, _jobRows); if (r == null) return;
            if (r.Member) { Msg(_tabMsg, Tr("Ya eres socio de esa empresa."), true); return; }
            if (!string.IsNullOrEmpty(r.MyStatus)) { Msg(_tabMsg, Tr("Ya te has apuntado a esta oferta."), true); return; }
            if (r.MyPoints < r.MinPoints || r.MyServices < r.MinServices) { Msg(_tabMsg, Tr("No cumples los requisitos de esta oferta."), true); return; }
            string note = null;
            using (var dlg = new FormDialog(r.Title, Tr("Apuntarme"), 580, "🧑‍✈️", r.Company + " · " + JobRoleText(r.Role)))
            {
                if (r.Body.Length > 0) dlg.AddInfo(r.Body);
                dlg.AddMultiline("note", Tr("Mensaje para la empresa (opcional): tu experiencia, horarios…"), "", 110);
                dlg.AddInfo(Tr("Se envía como solicitud de ingreso: la empresa la acepta o la rechaza y te llega el aviso."));
                dlg.Validate = async f =>
                {
                    note = f.Get("note");
                    if (note.Length > 400) return Tr("El mensaje es demasiado largo (400 caracteres como mucho).");
                    var (_, e) = await Supa.RpcAsync("job_apply", new { p_job = r.Id, p_note = note });
                    return e != null ? Tr("Error: ") + e : null;
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
            }
            Msg(_tabMsg, string.Format(Tr("Te has apuntado a «{0}»: {1} revisará tu solicitud."), r.Title, r.Company), false);
            LoadJobs();
        }

        async void NewJob()
        {
            var c = _empSel; if (c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            using var dlg = new FormDialog(Tr("Publicar oferta de empleo"), Tr("Publicar"), 600, "🧑‍✈️", string.Format(Tr("Para {0}: dura 30 días o hasta que la cierres"), c.Name));
            dlg.AddCombo("role", Tr("Puesto"), JobRoles.Select(JobRoleText), 0);
            dlg.AddText("title", Tr("Título (80 caracteres como mucho)"), "", Tr("Por ejemplo: maquinistas para el turno de tarde"));
            dlg.AddMultiline("body", Tr("Descripción (400 caracteres como mucho)"), "", 90);
            dlg.AddCombo("pts", Tr("Puntos mínimos del carné"), Enumerable.Range(0, 16).Select(i => i == 0 ? Tr("Sin mínimo") : i.ToString(I18n.Culture)), 0);
            dlg.AddCombo("srv", Tr("Servicios hechos como mínimo"), new[] { 0, 5, 10, 25, 50, 100, 250 }.Select(i => i == 0 ? Tr("Sin mínimo") : i.ToString(I18n.Culture)), 0);
            dlg.AddCombo("seats", Tr("Plazas"), Enumerable.Range(1, 10).Select(i => i.ToString(I18n.Culture)), 0);
            dlg.Validate = async f =>
            {
                string ti = f.Get("title"), bo = f.Get("body");
                if (ti.Length < 3) return Tr("El título de la oferta es demasiado corto.");
                if (ti.Length > 80 || bo.Length > 400) return Tr("El texto es demasiado largo.");
                int srv = new[] { 0, 5, 10, 25, 50, 100, 250 }[Math.Max(0, f.Index("srv"))];
                var (_, e) = await Supa.RpcAsync("job_create", new { p_company = c.Id, p_role = JobRoles[Math.Max(0, f.Index("role"))], p_title = ti, p_body = bo, p_min_points = Math.Max(0, f.Index("pts")), p_min_services = srv, p_seats = Math.Max(0, f.Index("seats")) + 1 });
                return e != null ? Tr("Error: ") + e : null;
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            Msg(_tabMsg, Tr("Oferta publicada. Las solicitudes llegan a Socios → Solicitudes de ingreso."), false);
            LoadJobs();
        }

        async void CloseJob()
        {
            var r = Sel(_jobsList, _jobRows); if (r == null || !(r.Mine || Supa.IsSuperadmin)) return;
            if (ThemedBox.Show(this, string.Format(Tr("¿Cerrar la oferta «{0}»? Las solicitudes que ya hay siguen en Socios."), r.Title), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("job_close", new { p_job = r.Id });
            Msg(_tabMsg, err != null ? Tr("Error: ") + err : Tr("Oferta cerrada."), err != null);
            LoadJobs();
        }

        // ---------- Mercado de trenes ----------
        // (1.2.90) venta y alquiler (market_browse2: con el .con y los vehículos de cada tren) y, arriba, los alquileres de
        // tu empresa (my_rentals). Sin el SQL nuevo: market_browse, como antes (solo venta).
        bool _mktOld;
        async void LoadMarket()
        {
            int seq = ++_tabSeq;
            if (_mktRows.Count == 0) _mktList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("market_browse2", new { });
            _mktOld = MissingRpc(err);
            if (_mktOld) (json, err) = await Supa.RpcAsync("market_browse", new { });
            string rj = null;
            var co = _empSel;
            if (!_mktOld && err == null && co != null)
            {
                var (j2, e2) = await Supa.RpcAsync("my_rentals", new { p_company = co.Id });
                if (e2 == null) rj = j2;
            }
            if (seq != _tabSeq) return;
            _mktRows.Clear();
            if (err != null) { _mktList.SetEmpty(TabErr(err)); UpdateTablonButtons(); return; }
            try
            {
                if (rj != null)
                {
                    using var d = JsonDocument.Parse(rj);
                    foreach (var e in d.RootElement.EnumerateArray())
                        _mktRows.Add(new MktRow
                        {
                            Listing = "rental", Id = Str(e, "id"), VehicleId = Str(e, "vehicle_id"), Name = Str(e, "name"), Role = Str(e, "role"),
                            SellerId = Str(e, "other_company"), Seller = Str(e, "other"), Price = Num(e, "price"), RentDays = (int)Num(e, "days"),
                            Ends = Utc(e, "ends_at"), Ended = Utc(e, "ended_at"), Active = Str(e, "status") == "active", Services = (int)Num(e, "services"),
                            Earned = Num(e, "earned"), InUse = Bool(e, "in_use"), HasImage = Bool(e, "has_image"), ConFile = Str(e, "con_file"),
                            EndReason = Str(e, "end_reason"), Vehicles = VehList(e), Logo = LogoFor(Str(e, "other_company"), Str(e, "logo"))
                        });
                }
                using (var d = JsonDocument.Parse(json))
                    foreach (var e in d.RootElement.EnumerateArray())
                        _mktRows.Add(new MktRow
                        {
                            Listing = Str(e, "listing_kind") == "rent" ? "rent" : "sale", RentDays = (int)Num(e, "rent_days"),
                            Id = Str(e, "id"), VehicleId = Str(e, "vehicle_id"), SellerId = Str(e, "seller_company"), Seller = Str(e, "seller"), Name = Str(e, "name"),
                            Kind = Str(e, "kind"), Engine = Str(e, "engine_type"), Seats = (int)Num(e, "seats"), Cond = (int)Num(e, "cond"), Kw = Num(e, "power_kw"),
                            Kmh = Num(e, "max_speed_kmh"), Km = Num(e, "km_total"), Price = Num(e, "price"), PriceNew = Num(e, "price_new"), Expires = Utc(e, "expires_at"),
                            HasImage = Bool(e, "has_image"), Mine = Bool(e, "mine"), ConFile = Str(e, "con_file"), Vehicles = VehList(e),
                            Logo = LogoFor(Str(e, "seller_company"), Str(e, "logo"))
                        });
            }
            catch { }
            FillMarketCards();
            TablonNews(3, _mktRows.Where(r => r.Listing != "rental").Select(r => MarketNews(r.Listing, r.Name, r.Price)));
            FetchMarketStrips(seq);
            CheckMarketMaterial(seq);
        }

        static List<TrainVeh> VehList(JsonElement e)
        {
            var l = new List<TrainVeh>();
            try { if (e.TryGetProperty("vehicles", out var v) && v.ValueKind == JsonValueKind.Array) foreach (var x in v.EnumerateArray()) l.Add(TrainVeh.FromJson(x)); } catch { }
            return l;
        }

        void FillMarketCards()
        {
            int sel = _mktList.SelectedRow; string selKey = sel >= 0 && sel < _mktRows.Count ? _mktRows[sel].Key : null;
            _mktList.ClearRows();
            Color red = Color.FromArgb(229, 115, 115), gray = Theme.Subtle;
            int k = 0;
            foreach (var r in _mktRows)
            {
                // la línea del material: si lo tienes en tu carpeta (o al menos sus vehículos) o qué te falta
                string have = r.Have switch
                {
                    2 => "✓  " + string.Format(Tr("Lo tienes en tu carpeta: «{0}»"), r.HaveText),
                    1 => "✓  " + Tr("Tienes todo su material en tu carpeta: su .con se añade desde Flota"),
                    0 => "✗  " + string.Format(Tr("No lo tienes en tu carpeta: te falta {0}"), r.HaveText),
                    _ => r.Vehicles.Count > 0 && r.HaveText == "…" ? Tr("Mirando si lo tienes en tu carpeta…") : "",
                };
                Color hc = r.Have >= 1 ? Theme.AccentHi : r.Have == 0 ? red : gray;
                if (r.Listing == "rental")
                {
                    if (r.Role == "owner") have = "";   // (es tuyo: no hace falta)
                    string pill = !r.Active ? Tr("Alquiler terminado") : r.Role == "owner" ? Tr("Alquilado a otra empresa") : Tr("Alquilado por tu empresa");
                    string who = r.Role == "owner" ? string.Format(Tr("Lo tiene {0}"), r.Seller) : string.Format(Tr("De {0}"), r.Seller);
                    string srv = string.Format(r.Services == 1 ? Tr("{0} servicio") : Tr("{0} servicios"), r.Services);
                    string paid = string.Format(Tr(r.Role == "owner" ? "has cobrado {0}" : "has pagado {0}"), EurT(r.Earned));
                    string when = r.Active ? (r.InUse ? Tr("en un servicio ahora") : r.Ends <= DateTime.UtcNow ? Tr("vuelve al terminar el servicio") : string.Format(Tr("vuelve a su empresa: {0}"), DaysLeft(r.Ends)))
                                           : string.Format(Tr("terminó el {0}"), r.Ended.ToLocalTime().ToString("d", I18n.Culture));
                    _mktList.AddRow(new[] { r.Name, pill, string.Join("  ·  ", who, srv, paid), when, have, EurT(r.Price), Tr("por servicio"), string.Format(Tr("{0} días"), r.RentDays) },
                        new Color?[] { null, r.Active ? TabBlue : gray, Theme.Subtle, r.Active ? TabBlue : gray, hc, r.Active ? TabBlue : gray, Theme.Subtle, Theme.Subtle }, r.Logo, r.Key);
                }
                else
                {
                    string kind = r.Kind == "freight" ? Tr("Mercancías") : r.Kind == "pax" || r.Seats > 0 ? Tr("Viajeros") : "";
                    var spec = new List<string>();
                    if (kind.Length > 0) spec.Add(kind);
                    if (r.Seats > 0 && r.Kind != "freight") spec.Add(string.Format(Tr("{0} plazas"), r.Seats.ToString("N0", I18n.Culture)));
                    string tr = TractionName(r.Engine); if (tr != "—") spec.Add(tr);
                    if (r.Kw > 0) spec.Add(r.Kw.ToString("N0", I18n.Culture) + " kW");
                    if (r.Kmh > 0) spec.Add(r.Kmh.ToString("N0", I18n.Culture) + " km/h");
                    string use = string.Join("  ·  ", r.Km.ToString("N0", I18n.Culture) + " km", string.Format(Tr("estado {0} %"), r.Cond), string.Format(Tr("de {0}"), r.Seller), DaysLeft(r.Expires));
                    if (r.Mine) _mktList.Badges[k] = Tr("Tu anuncio");
                    if (r.Listing == "rent")
                        _mktList.AddRow(new[] { r.Name, Tr("En alquiler"), string.Join("  ·  ", spec), use, have, EurT(r.Price), Tr("por servicio"), string.Format(Tr("durante {0} días"), r.RentDays) },
                            new Color?[] { null, TabTeal, Theme.Subtle, Theme.Subtle, hc, TabTeal, Theme.Subtle, Theme.Subtle }, r.Logo, r.Key);
                    else
                    {
                        string pct = r.PriceNew > 0 ? string.Format(Tr("{0} % del precio nuevo"), Math.Round(100 * r.Price / r.PriceNew)) : "";
                        _mktList.AddRow(new[] { r.Name, Tr("En venta"), string.Join("  ·  ", spec), use, have, EurT(r.Price), pct, "" },
                            new Color?[] { null, Theme.AccentHi, Theme.Subtle, Theme.Subtle, hc, Theme.AccentHi, Theme.Subtle, Theme.Subtle }, r.Logo, r.Key);
                    }
                }
                k++;
            }
            if (_mktRows.Count == 0) _mktList.SetEmpty(Tr(_mktOld ? "No hay trenes a la venta ahora mismo. Una empresa pone uno a la venta desde Flota → «Vender o desguazar…»."
                                                               : "No hay trenes a la venta ni en alquiler ahora mismo. Una empresa ofrece uno desde Flota → «Vender, alquilar o desguazar…»."));
            if (selKey != null) { int i = _mktRows.FindIndex(m => m.Key == selKey); if (i >= 0) _mktList.SelectRow(i); }
            UpdateTablonButtons();
        }

        // ¿Tienes el tren en tu carpeta? Un .con de tu contenido con los mismos vehículos (como reconoce Flota los trenes de
        // la empresa) o, si no, si están todos sus .eng/.wag. Se mira aparte, sin parar la lista.
        int _mktHaveSeq;
        async void CheckMarketMaterial(int seq)
        {
            int my = ++_mktHaveSeq;
            var rows = _mktRows.Where(r => r.Vehicles.Count > 0).ToList();
            if (rows.Count == 0) return;
            if (_curFolder?.Path == null) { foreach (var r in rows) { r.Have = -1; r.HaveText = ""; } FillMarketCards(); return; }
            foreach (var r in rows) { r.Have = -1; r.HaveText = "…"; }
            FillMarketCards();
            var all = _consistsAll.Where(c => c?.FilePath != null).ToList();
            var res = await Task.Run(() =>
            {
                var leads = new HashSet<string>(rows.Select(r => r.Vehicles.FirstOrDefault(v => !v.Wagon)?.Name ?? "").Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
                var byKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in all)
                {
                    try
                    {
                        string lead = LeadEngineName(c);
                        if (lead == null || !leads.Contains(lead)) continue;
                        string key = TrainKey(TrainVehiclesOfCached(c));
                        if (!byKey.ContainsKey(key)) byKey[key] = c.FilePath;
                    }
                    catch { }
                }
                var outp = new Dictionary<string, (int have, string text)>();
                foreach (var r in rows)
                {
                    if (byKey.TryGetValue(TrainKey(r.Vehicles), out var f)) { outp[r.Key] = (2, System.IO.Path.GetFileName(f)); continue; }
                    var missing = r.Vehicles.Where(v => ResolveCarFile(v.Name, v.Folder) == null)
                                            .Select(v => string.IsNullOrEmpty(v.Folder) ? v.Name : v.Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    outp[r.Key] = missing.Count == 0 ? (1, "") : (0, string.Join(", ", missing.Take(3)) + (missing.Count > 3 ? "…" : ""));
                }
                return outp;
            });
            if (my != _mktHaveSeq || seq != _tabSeq || IsDisposed) return;
            foreach (var r in _mktRows) if (res.TryGetValue(r.Key, out var h)) { r.Have = h.have; r.HaveText = h.text; }
            FillMarketCards();
        }

        // La composición 2D de cada tren: se pide una vez (market_image o, de un alquiler, rental_image), se guarda en la
        // caché de disco y se queda en memoria para pintarla en su tarjeta.
        readonly Dictionary<string, Bitmap> _mktStrips = new(StringComparer.OrdinalIgnoreCase);

        async void FetchMarketStrips(int seq)
        {
            foreach (var r in _mktRows.ToList())
            {
                if (seq != _tabSeq || IsDisposed) return;
                if (!r.HasImage || _mktStrips.ContainsKey(r.Key)) continue;
                bool rental = r.Listing == "rental";
                string file = SavedImageFile(rental ? "alquiler" : "mercado", r.Id);
                if (!System.IO.File.Exists(file))
                {
                    var (json, err) = rental ? await Supa.RpcAsync("rental_image", new { p_rental = r.Id }) : await Supa.RpcAsync("market_image", new { p_listing = r.Id });
                    if (err != null) return;
                    string b64 = JsonText(json);
                    int comma = b64?.IndexOf(',') ?? -1;
                    if (b64 != null && b64.StartsWith("data:") && comma > 0) b64 = b64.Substring(comma + 1);
                    if (await Task.Run(() => SaveB64Image(b64, file)) == null) continue;
                }
                var bmp = await Task.Run(() => LoadStripFile(file));
                if (bmp == null || IsDisposed) continue;
                if (_mktStrips.TryGetValue(r.Key, out var old)) old.Dispose();
                _mktStrips[r.Key] = bmp;
                _mktList?.Invalidate();
            }
        }

        // el aviso del material antes de comprar o alquilar
        string MaterialWarning(MktRow r)
            => r.Have == 0 ? "\n\n⚠ " + string.Format(Tr("No lo tienes en tu carpeta: te falta {0}. Podrás tenerlo en la flota, pero no conducirlo hasta que instales ese material."), r.HaveText) : "";

        async void BuyFromMarket()
        {
            var r = Sel(_mktList, _mktRows); var c = _empSel;
            if (r == null || r.Listing != "sale" || c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            if (r.SellerId == c.Id) { Msg(_tabMsg, Tr("No puedes comprar un tren de tu propia empresa."), true); return; }
            string q = string.Format(Tr("¿Comprar «{0}» a {1} por {2}?"), r.Name, r.Seller, EurT(r.Price)) + "\n\n"
                     + string.Format(Tr("Pasa a la flota de {0} con sus {1} km. Saldo de la empresa: {2}."), c.Name, r.Km.ToString("N0", I18n.Culture), EurT(c.Balance))
                     + MaterialWarning(r);
            if (c.Balance < r.Price) { ThemedBox.Show(this, q + "\n\n" + Tr("No hay saldo suficiente."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Msg(_tabMsg, Tr("Comprando…"), false);
            var (_, err) = await Supa.RpcAsync("market_buy", new { p_listing = r.Id, p_company = c.Id });
            if (err != null) { Msg(_tabMsg, Tr("Error: ") + err, true); LoadMarket(); return; }
            Msg(_tabMsg, string.Format(Tr("Comprado: «{0}» ya está en la flota de {1}."), r.Name, c.Name), false);
            LoadCompanies();
            LoadMarket();
        }

        async void RentFromMarket()
        {
            var r = Sel(_mktList, _mktRows); var c = _empSel;
            if (r == null || r.Listing != "rent" || c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            if (r.SellerId == c.Id) { Msg(_tabMsg, Tr("No puedes alquilar un tren de tu propia empresa."), true); return; }
            string q = string.Format(Tr("¿Alquilar «{0}» a {1} durante {2} días por {3} por servicio?"), r.Name, r.Seller, r.RentDays, EurT(r.Price)) + "\n\n"
                     + string.Format(Tr("Pasa a la flota de {0} como alquilado: cada servicio que haga paga {1} a {2} (no hay que pagar nada al alquilarlo). Al acabar el plazo vuelve a {2}; si está en un servicio, al terminarlo. Puedes devolverlo antes."), c.Name, EurT(r.Price), r.Seller)
                     + MaterialWarning(r);
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Msg(_tabMsg, Tr("Alquilando…"), false);
            var (_, err) = await Supa.RpcAsync("market_rent", new { p_listing = r.Id, p_company = c.Id });
            if (err != null) { Msg(_tabMsg, Tr("Error: ") + err, true); LoadMarket(); return; }
            Msg(_tabMsg, string.Format(Tr("Alquilado: «{0}» ya está en la flota de {1} durante {2} días."), r.Name, c.Name, r.RentDays), false);
            LoadCompanies();
            LoadMarket();
        }

        async void ReturnRental()
        {
            var r = Sel(_mktList, _mktRows);
            if (r == null || r.Listing != "rental" || r.Role != "renter" || !r.Active) return;
            if (r.InUse) { Msg(_tabMsg, Tr("Ese tren está en un servicio: espera a que termine."), true); return; }
            if (ThemedBox.Show(this, string.Format(Tr("¿Devolver «{0}» a {1} antes de tiempo? Sale de tu flota y deja de pagarse su alquiler."), r.Name, r.Seller), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("rental_end", new { p_rental = r.Id });
            Msg(_tabMsg, err != null ? Tr("Error: ") + err : string.Format(Tr("«{0}» devuelto a {1}."), r.Name, r.Seller), err != null);
            LoadCompanies();
            LoadMarket();
        }

        async void WithdrawFromMarket()
        {
            var r = Sel(_mktList, _mktRows); if (r == null || r.Listing == "rental" || !(r.Mine || Supa.IsSuperadmin)) return;
            if (ThemedBox.Show(this, string.Format(Tr("¿Retirar «{0}» del mercado? Vuelve a la flota y puede salir en servicios."), r.Name), "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var (_, err) = await Supa.RpcAsync("market_withdraw", new { p_listing = r.Id });
            Msg(_tabMsg, err != null ? Tr("Error: ") + err : string.Format(Tr("«{0}» retirado del mercado: vuelve a estar disponible."), r.Name), err != null);
            LoadMarket();
        }

        // una imagen (data URI) en una ventana, con un texto debajo
        void ShowImageWindow(string title, string dataUri, string caption)
        {
            var img = DecodeLogo(dataUri);
            if (img == null) { Msg(_tabMsg, Tr("No se pudo leer la imagen."), true); return; }
            using (img)
            using (var f = new Form { Text = title, BackColor = Theme.Bg, ForeColor = Theme.Text, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, KeyPreview = true, Padding = new Padding(Theme.Px(12)) })
            {
                var wa = Screen.FromControl(this).WorkingArea;
                int w = Math.Min(wa.Width - 80, Math.Max(Theme.Px(420), img.Width + Theme.Px(24)));
                int capH = string.IsNullOrEmpty(caption) ? 0 : Theme.Px(44);
                int h = Math.Min(wa.Height - 80, img.Height * (w - Theme.Px(24)) / Math.Max(1, img.Width) + Theme.Px(24) + capH);
                f.ClientSize = new Size(w, Math.Max(Theme.Px(200), h));
                DialogKit.Dress(f, false);
                var pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Image = img, BackColor = Theme.Bg };
                if (capH > 0) f.Controls.Add(new Label { Dock = DockStyle.Bottom, Height = capH, Text = caption, ForeColor = Theme.Subtle, Font = Theme.Font(9.25f), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true });
                f.Controls.Add(pb); pb.BringToFront();
                f.KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
                f.ShowDialog(this);
            }
        }
    }
}
