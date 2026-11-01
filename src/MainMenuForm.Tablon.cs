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
        static readonly string[] FleetTabNames = { "Todos", "Disponibles", "En servicio", "En venta" };
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
            string t = Tr(listed ? "Retirar del mercado" : "Vender o desguazar…");
            if (_fleetRemoveBtn.Text == t) return;
            _fleetRemoveBtn.Text = t;
            using var fb = Theme.Font(9.5f, FontStyle.Bold);
            _fleetRemoveBtn.Width = TextRenderer.MeasureText(t, fb).Width + 36;
            _fleetRemoveBtn.TextColor = listed ? Theme.Text : RedC;
            _fleetRemoveBtn.Invalidate();
        }

        sealed class SaleQuote { public string Id, Name, Cannot; public double PriceNew, Price, Fee, Gets, Scrap, Km; public int Cond, Days; public double FeePct, ScrapPct; }

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
                        Km = Num(r, "km"), Cond = (int)Num(r, "cond"), Days = (int)Num(r, "days"), FeePct = Num(r, "fee_pct"), ScrapPct = Num(r, "scrap_pct")
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
        }

        static string CannotText(string why) => why switch
        {
            "rented" => Tr("Alquilado: no se vende, se devuelve."),
            "granted" => Tr("Cedido por el administrador: no se puede vender."),
            "in_use" => Tr("Está en un servicio: espera a que termine."),
            "listed" => Tr("Ya está a la venta."),
            _ => "",
        };

        // 0 nada · 1 poner a la venta · 2 desguazar (o devolver los alquilados)
        int SaleDialog(List<SaleQuote> qs)
        {
            int result = 0;
            bool many = qs.Count > 1;
            var sellable = qs.Where(q => q.Cannot == null).ToList();
            var scrappable = qs.Where(q => q.Cannot is null or "granted" or "rented").ToList();
            double gets = sellable.Sum(q => q.Gets), price = sellable.Sum(q => q.Price), fee = sellable.Sum(q => q.Fee), scrap = scrappable.Sum(q => q.Scrap);
            var q0 = qs[0];
            using var f = new Form { Text = Tr("Vender o desguazar"), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, KeyPreview = true };
            DialogKit.Dress(f);
            var wa = Screen.FromControl(this).WorkingArea;
            int W = Math.Min(Theme.Px(780), wa.Width - 40);
            bool stack = W < Theme.Px(640);   // pantalla estrecha: una tarjeta debajo de otra
            int cardW = stack ? W - Theme.Px(48) : (W - Theme.Px(48) - Theme.Px(16)) / 2;

            var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = stack ? 1 : 2, BackColor = Theme.Bg, Padding = new Padding(Theme.Px(24), Theme.Px(16), Theme.Px(24), Theme.Px(12)) };
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
                var card = new Card { Fill = Theme.Surface, BorderColor = Color.FromArgb(90, accent), Radius = 12, Padding = new Padding(Theme.Px(16)), Width = cardW, Margin = new Padding(0, 0, stack ? 0 : Theme.Px(16), Theme.Px(12)) };
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
            body.Controls.Add(sell); body.Controls.Add(scr);
            // las dos tarjetas con el alto de la más alta (con todo su contenido ya maquetado)
            int ch = 0;
            foreach (var (cd, cl) in cols) { cl.PerformLayout(); ch = Math.Max(ch, cl.GetPreferredSize(new Size(cardW - cd.Padding.Horizontal, 0)).Height); }
            foreach (var (cd, cl) in cols) cd.Height = ch + cd.Padding.Vertical + Theme.Px(4);

            var foot = new DialogKit.Footer();
            var cancel = DialogKit.Secondary(Tr("Cancelar")); cancel.Click += (s, e) => f.Close();
            foot.AddRight(cancel);
            f.Controls.Add(body); f.Controls.Add(foot);
            var head = DialogKit.Crown(f, Tr("Vender o desguazar"), Tr("El precio lo fija SelectOR: no lo decide la empresa"), "💶");
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
            var (json, err) = await Supa.RpcAsync("market_browse", new { });
            if (err != null) { Msg(_fleetMsg, Tr("Error: ") + err, true); return; }
            string listing = null; string name = "";
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    if (Str(e, "vehicle_id") == vehicleId) { listing = Str(e, "id"); name = Str(e, "name"); break; }
            }
            catch { }
            if (listing == null) { Msg(_fleetMsg, Tr("Ese tren ya no está a la venta."), true); LoadFleet(); return; }
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
        Label _tabMsg, _tabIntro;
        RoundButton _showJoinBtn, _showEditBtn, _adNewBtn, _adImgBtn, _adDelBtn, _jobApplyBtn, _jobNewBtn, _jobCloseBtn, _mktBuyBtn, _mktImgBtn, _mktWithdrawBtn;
        FlowLayoutPanel _tabBtns;
        int _tabSeq;
        sealed class ShowRow { public string Id, Name, Code, Slogan, About, Kinds; public bool Seeking, Mine; public int Members, Trains, Services, LeaguePos; public double Km; public Image Logo; }
        sealed class AdRow { public string Id, CompanyId, Company, Title, Body; public bool HasImage, Featured, Mine; public DateTime Expires; public Image Logo; }
        sealed class JobRow { public string Id, CompanyId, Company, Role, Title, Body, MyStatus; public int MinPoints, MinServices, Seats, Hired, Applicants, MyPoints, MyServices; public bool Member, Mine; public Image Logo; }
        sealed class MktRow { public string Id, VehicleId, SellerId, Seller, Name, Kind, Engine; public int Seats, Cond; public double Kw, Kmh, Km, Price, PriceNew; public DateTime Expires; public bool HasImage, Mine; public Image Logo; }
        readonly List<ShowRow> _showRows = new();
        readonly List<AdRow> _adRows = new();
        readonly List<JobRow> _jobRows = new();
        readonly List<MktRow> _mktRows = new();

        static readonly string[] TabIntros =
        {
            "Las empresas que quieren darse a conocer, con sus datos del mes. Si una busca socios, puedes pedir unirte desde aquí.",
            "Anuncios de las empresas: novedades, servicios nuevos, quedadas en la sala… Duran 3 o 7 días; los destacados salen primero.",
            "Ofertas de empleo de las empresas. Si cumples los requisitos, apúntate con un mensaje: la empresa acepta o rechaza tu solicitud.",
            "Trenes que venden otras empresas, al precio que fija SelectOR según su uso y estado. El comprador paga ese precio y el tren pasa a su flota con sus km.",
        };

        Panel BuildTablonSubpanel()
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var top = new TableLayoutPanel { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 42, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 4) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var tabs = MakeSubTabs(new[] { "Escaparate", "Anuncios", "Empleo", "Mercado de trenes" }, ShowTablonPage);
            tabs.Dock = DockStyle.Fill; tabs.Margin = new Padding(0);
            var search = new RoundedInput(I18n.T("🔎  Buscar…")) { Width = 240, Height = 34, Anchor = AnchorStyles.Right, Margin = new Padding(10, 3, 0, 3) };
            search.Box.TextChanged += (s, e) => { foreach (var l in new[] { _showList, _adsList, _jobsList, _mktList }) l?.Filter(search.Box.Text); };
            top.Controls.Add(tabs, 0, 0); top.Controls.Add(search, 1, 0);
            t.Controls.Add(top);

            _tabIntro = EmpIntro(TabIntros[0]); _tabIntro.MaximumSize = new Size(1000, 0);
            t.Controls.Add(_tabIntro);

            CardTable Cards(int cols, int h, int minW)
            {
                var c = EmpCards(); c.Columns = cols; c.CardHeight = h; c.MinWidth = minW;
                c.TitleCol = 0; c.PillCol = 1; c.SubCols = new[] { 2, 3 }; c.QuoteCol = 4; c.RightCol = 5; c.RightSubCols = new[] { 6 };
                c.SelectedIndexChanged += (s, e) => UpdateTablonButtons();
                return c;
            }
            _showList = Cards(2, 96, 460); _showList.ItemActivated += _ => ShowCompanyCard();
            _adsList = Cards(1, 92, 520); _adsList.Icon = null; _adsList.ItemActivated += _ => ShowAdImage();
            _jobsList = Cards(1, 92, 520); _jobsList.ItemActivated += _ => ApplyJob();
            _mktList = Cards(2, 96, 460); _mktList.ItemActivated += _ => ShowMarketImage();
            _mktList.SubCols = new[] { 2 }; _mktList.QuoteCol = 3; _mktList.QuoteMarks = false;   // el uso (km, estado, de quién, plazo) en su propia línea
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Margin = new Padding(0) };
            _tabPages = new Panel[4];
            var lists = new[] { _showList, _adsList, _jobsList, _mktList };
            for (int i = 0; i < 4; i++)
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
            _adImgBtn = B("Ver imagen", false, ShowAdImage);
            _adDelBtn = B("Quitar anuncio", false, DeleteAd);
            _jobApplyBtn = B("Apuntarme…", true, ApplyJob);
            _jobNewBtn = B("Publicar oferta…", false, NewJob);
            _jobCloseBtn = B("Cerrar oferta", false, CloseJob);
            _mktBuyBtn = B("Comprar…", true, BuyFromMarket);
            _mktImgBtn = B("Ver composición", false, ShowMarketImage);
            _mktWithdrawBtn = B("Retirar del mercado", false, WithdrawFromMarket);
            _tabBtns.Controls.AddRange(new Control[] { _showJoinBtn, _showEditBtn, _adNewBtn, _adImgBtn, _adDelBtn, _jobApplyBtn, _jobNewBtn, _jobCloseBtn, _mktBuyBtn, _mktImgBtn, _mktWithdrawBtn });
            t.Controls.Add(_tabBtns);
            _tabMsg = EmpMsg(); _tabMsg.MaximumSize = new Size(900, 0); _tabMsg.Margin = new Padding(2, 4, 2, 2);
            t.Controls.Add(_tabMsg);
            t.SizeChanged += (s, e) => _tabBtns.MaximumSize = new Size(Math.Max(200, t.ClientSize.Width - 4), 0);
            _tabPanel = t;
            UpdateTablonButtons();
            return t;
        }

        void ShowTablonPage(int i)
        {
            _tabPage = i;
            for (int k = 0; k < _tabPages.Length; k++) _tabPages[k].Visible = k == i;
            if (_tabIntro != null) _tabIntro.Text = Tr(TabIntros[i]);
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
            V(_adImgBtn, _tabPage == 1); if (_adImgBtn != null) _adImgBtn.Enabled = ad?.HasImage == true;
            V(_adDelBtn, _tabPage == 1 && (manage || Supa.IsSuperadmin)); if (_adDelBtn != null) _adDelBtn.Enabled = ad != null && (ad.Mine || Supa.IsSuperadmin);
            V(_jobApplyBtn, _tabPage == 2); if (_jobApplyBtn != null) _jobApplyBtn.Enabled = jb != null && !jb.Member && string.IsNullOrEmpty(jb.MyStatus) && jb.MyPoints >= jb.MinPoints && jb.MyServices >= jb.MinServices;
            V(_jobNewBtn, _tabPage == 2 && manage);
            V(_jobCloseBtn, _tabPage == 2 && (manage || Supa.IsSuperadmin)); if (_jobCloseBtn != null) _jobCloseBtn.Enabled = jb != null && (jb.Mine || Supa.IsSuperadmin);
            V(_mktBuyBtn, _tabPage == 3 && manage); if (_mktBuyBtn != null) _mktBuyBtn.Enabled = mk != null && mk.SellerId != _empSel?.Id;
            V(_mktImgBtn, _tabPage == 3); if (_mktImgBtn != null) _mktImgBtn.Enabled = mk?.HasImage == true;
            V(_mktWithdrawBtn, _tabPage == 3 && manage); if (_mktWithdrawBtn != null) _mktWithdrawBtn.Enabled = mk != null && (mk.Mine || Supa.IsSuperadmin);
            foreach (Control c in _tabBtns.Controls) c.Invalidate();
        }

        static T Sel<T>(CardTable l, List<T> rows) where T : class { int i = l?.SelectedRow ?? -1; return i >= 0 && i < rows.Count ? rows[i] : null; }

        void LoadTablon()
        {
            if (_tabPanel == null || !Supa.IsLoggedIn) return;
            switch (_tabPage)
            {
                case 0: LoadShowcase(); break;
                case 1: LoadAds(); break;
                case 2: LoadJobs(); break;
                default: LoadMarket(); break;
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
                _adsList.AddRow(new[] { r.Title, r.HasImage ? "🖼 " + Tr("con imagen") : "", r.Company, DaysLeft(r.Expires), r.Body, "", "" },
                    new Color?[] { null, Theme.Subtle, Theme.AccentHi, Theme.Subtle, null, null, null }, r.Logo, r.Id);
                k++;
            }
            if (_adRows.Count == 0) _adsList.SetEmpty(Tr("No hay anuncios ahora mismo."));
            UpdateTablonButtons();
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
        async void LoadMarket()
        {
            int seq = ++_tabSeq;
            if (_mktRows.Count == 0) _mktList.ShowLoading(Tr("Cargando…"));
            var (json, err) = await Supa.RpcAsync("market_browse", new { });
            if (seq != _tabSeq) return;
            _mktRows.Clear();
            if (err != null) { _mktList.SetEmpty(TabErr(err)); UpdateTablonButtons(); return; }
            try
            {
                using var d = JsonDocument.Parse(json);
                foreach (var e in d.RootElement.EnumerateArray())
                    _mktRows.Add(new MktRow
                    {
                        Id = Str(e, "id"), VehicleId = Str(e, "vehicle_id"), SellerId = Str(e, "seller_company"), Seller = Str(e, "seller"), Name = Str(e, "name"),
                        Kind = Str(e, "kind"), Engine = Str(e, "engine_type"), Seats = (int)Num(e, "seats"), Cond = (int)Num(e, "cond"), Kw = Num(e, "power_kw"),
                        Kmh = Num(e, "max_speed_kmh"), Km = Num(e, "km_total"), Price = Num(e, "price"), PriceNew = Num(e, "price_new"), Expires = Utc(e, "expires_at"),
                        HasImage = Bool(e, "has_image"), Mine = Bool(e, "mine"), Logo = LogoFor(Str(e, "seller_company"), Str(e, "logo"))
                    });
            }
            catch { }
            _mktList.ClearRows();
            foreach (var r in _mktRows)
            {
                string kind = r.Kind == "freight" ? Tr("Mercancías") : r.Kind == "pax" || r.Seats > 0 ? Tr("Viajeros") : "";
                var spec = new List<string>();
                if (kind.Length > 0) spec.Add(kind);
                if (r.Seats > 0 && r.Kind != "freight") spec.Add(string.Format(Tr("{0} plazas"), r.Seats.ToString("N0", I18n.Culture)));
                string tr = TractionName(r.Engine); if (tr != "—") spec.Add(tr);
                if (r.Kw > 0) spec.Add(r.Kw.ToString("N0", I18n.Culture) + " kW");
                if (r.Kmh > 0) spec.Add(r.Kmh.ToString("N0", I18n.Culture) + " km/h");
                string use = string.Format(Tr("{0} km · estado {1} % · de {2}"), r.Km.ToString("N0", I18n.Culture), r.Cond, r.Seller) + "  ·  " + DaysLeft(r.Expires);
                string pct = r.PriceNew > 0 ? string.Format(Tr("{0} % del precio nuevo"), Math.Round(100 * r.Price / r.PriceNew)) : "";
                _mktList.AddRow(new[] { r.Name, r.Mine ? Tr("Tu empresa") : "", string.Join("  ·  ", spec), use, "", EurT(r.Price), pct },
                    new Color?[] { null, Theme.AccentHi, Theme.Subtle, Theme.Subtle, null, Theme.AccentHi, Theme.Subtle }, r.Logo, r.Id);
            }
            if (_mktRows.Count == 0) _mktList.SetEmpty(Tr("No hay trenes a la venta ahora mismo. Una empresa pone uno a la venta desde Flota → «Vender o desguazar…»."));
            UpdateTablonButtons();
        }

        async void ShowMarketImage()
        {
            var r = Sel(_mktList, _mktRows); if (r == null || !r.HasImage) return;
            var (json, err) = await Supa.RpcAsync("market_image", new { p_listing = r.Id });
            if (err != null) { Msg(_tabMsg, Tr("Error: ") + err, true); return; }
            ShowImageWindow(r.Name, JsonText(json), string.Format(Tr("{0} · {1} km · estado {2} %"), EurT(r.Price), r.Km.ToString("N0", I18n.Culture), r.Cond));
        }

        async void BuyFromMarket()
        {
            var r = Sel(_mktList, _mktRows); var c = _empSel;
            if (r == null || c == null || !(CanManage() || Supa.IsSuperadmin)) return;
            if (r.SellerId == c.Id) { Msg(_tabMsg, Tr("No puedes comprar un tren de tu propia empresa."), true); return; }
            string q = string.Format(Tr("¿Comprar «{0}» a {1} por {2}?"), r.Name, r.Seller, EurT(r.Price)) + "\n\n"
                     + string.Format(Tr("Pasa a la flota de {0} con sus {1} km. Saldo de la empresa: {2}."), c.Name, r.Km.ToString("N0", I18n.Culture), EurT(c.Balance));
            if (c.Balance < r.Price) { ThemedBox.Show(this, q + "\n\n" + Tr("No hay saldo suficiente."), "SelectOR", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            if (ThemedBox.Show(this, q, "SelectOR", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Msg(_tabMsg, Tr("Comprando…"), false);
            var (_, err) = await Supa.RpcAsync("market_buy", new { p_listing = r.Id, p_company = c.Id });
            if (err != null) { Msg(_tabMsg, Tr("Error: ") + err, true); LoadMarket(); return; }
            Msg(_tabMsg, string.Format(Tr("Comprado: «{0}» ya está en la flota de {1}."), r.Name, c.Name), false);
            LoadCompanies();
            LoadMarket();
        }

        async void WithdrawFromMarket()
        {
            var r = Sel(_mktList, _mktRows); if (r == null || !(r.Mine || Supa.IsSuperadmin)) return;
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
