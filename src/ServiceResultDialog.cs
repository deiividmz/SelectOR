// Ventana de un SERVICIO (solo lectura): registrado, en conducción o no registrado.
//  · Banda de color según el estado (verde registrado · azul en conducción · rojo no registrado) con la
//    empresa, el maquinista, la fecha y el trayecto.
//  · Datos del tren y del viaje en fichas; en un servicio registrado, la LIQUIDACIÓN en forma de ticket
//    con el cálculo de cada importe (ServiceCalc) y las PARADAS COMERCIALES que hizo el tren.
//  · Al volver de conducir, además: el carné por puntos, el rango y el saldo de la empresa.
// Los bloques nuevos se dibujan a mano (sin un control por línea).

using System;
using System.Linq;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace SelectOR
{
    public class ServiceResultDialog : Form
    {
        public sealed class Data
        {
            public string Company = "";
            public string Route = "";
            public bool Valid = true;
            public double Km;
            public int DurationS;
            public int Pax;                 // viajeros embarcados (modelo PseudoPAX; 0 si no aplica)
            public double Capacity = double.NaN;   // plazas del tren (viajeros); NaN = no se sabe
            public double MassT = double.NaN;      // toneladas del tren (mercancías: el ingreso se calcula con ellas)
            public double Income, Canon, Energy, Salary, Rental, Maintenance, Net, Balance;
            public string Driver = "";      // maquinista
            public string DateText = "";    // fecha
            public DateTime StartLocal = DateTime.MinValue;   // salida (en conducción: para el tiempo en marcha)
            public bool History;            // true = vista de detalle desde Servicios (oculta el rango)
            public string ServiceId = "";
            public string Consist = "";     // tren conducido
            public int Cars, Engines;       // coches/vagones de su .con y cuántos son motrices (0 = no se sabe)
            public string Path = "";        // recorrido (.pat)
            public string StatusText = "";  // estado (completado / en curso / …)
            public bool InProgress;         // true = servicio "En conducción": aún no hay km/tiempo/economía
            public string Notes = "";       // notas del servicio, si las hay
            public string AnnulText = "";   // servicio anulado por el superadmin: quién, cuándo y por qué
            public string RecoveredNote = "";   // registrado al volver a abrir SelectOR (se cerró todo durante el viaje)
            public bool Annulled => !string.IsNullOrEmpty(AnnulText);
            // Datos con los que el servidor calculó la economía (services.calc); null = servicio anterior
            public System.Text.Json.JsonElement? Calc;
            // Paradas comerciales: estación, hora del simulador, suben, bajan (null = no se sabe)
            public List<(string station, string time, int board, int alight)> Stops;
            // Viaje NO registrado (Valid = false): por qué no se ha guardado (viaje corto, velocidad imposible…).
            public List<string> Reasons = new List<string>();
            // Rango
            public string RankName = "";
            public bool RankedUp;
            public double ValidKm;          // km válidos totales tras este viaje
            public double RankPct;          // progreso al siguiente rango [0..1]
            public string NextRankName;     // null si ya es el rango máximo
            public double NextRankKm;
            public bool RankFrozen;         // rango congelado (carné por debajo de 6 puntos)
            // Carné por puntos: infracciones de este servicio y puntos que quedan (−1 = no se sabe).
            public List<Carne.Item> Infractions = new List<Carne.Item>();
            public int LicensePoints = -1;
            public DateTime? SuspendedUntil;
        }

        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static readonly Color Green = Color.FromArgb(129, 199, 132);
        static readonly Color Red = Color.FromArgb(229, 115, 115);
        static readonly Color Gold = Color.FromArgb(255, 196, 84);
        static readonly Color Blue = Color.FromArgb(120, 144, 226);
        static readonly Color Orange = Color.FromArgb(255, 167, 89);
        const int W = 560;   // ancho útil de los bloques

        public ServiceResultDialog(Data d)
        {
            Text = I18n.T(d.InProgress ? "Servicio en conducción" : d.Annulled ? "Servicio anulado" : (d.Valid ? "Servicio registrado" : "Servicio no registrado"));
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = false, BackColor = Theme.Bg, Padding = new Padding(18, 12, 18, 10)
            };

            // 1) banda del estado (fija arriba)
            var band = new ServiceBand(d) { Dock = DockStyle.Top };

            // 2) el tren y el viaje
            var meta = new List<(string, string, Color)>();
            meta.Add((I18n.T("TREN"), string.IsNullOrWhiteSpace(d.Consist) ? "—" : d.Consist, Theme.Text));
            meta.Add((I18n.T("COCHES"), d.Cars > 0 ? d.Cars.ToString("N0", Es) + (d.Engines > 0 ? "  (" + d.Engines + " " + I18n.T(d.Engines == 1 ? "motriz" : "motrices") + ")" : "") : "—", Theme.Text));
            meta.Add((I18n.T("RUTA"), string.IsNullOrWhiteSpace(d.Route) ? "—" : d.Route, Theme.Text));
            body.Controls.Add(new Tiles(meta, small: true) { Width = W, Margin = new Padding(0, 0, 0, 8) });
            if (!string.IsNullOrEmpty(d.RecoveredNote)) body.Controls.Add(NoteCard(d.RecoveredNote, Theme.Surface, Gold));

            if (d.InProgress)
            {
                var live = new List<(string, string, Color)>();
                string marcha = d.StartLocal != DateTime.MinValue ? FormatHm(DateTime.Now - d.StartLocal) : "—";
                live.Add((I18n.T("EN MARCHA"), marcha, Blue));
                live.Add((d.Capacity > 0 ? I18n.T("PLAZAS") : I18n.T("CARGA"), d.Capacity > 0 ? d.Capacity.ToString("N0", Es) : d.MassT > 0 ? d.MassT.ToString("N0", Es) + " t" : "—", Blue));
                live.Add((I18n.T("ESTADO"), "▶ " + I18n.T("conduciendo"), Blue));
                body.Controls.Add(new Tiles(live) { Width = W, Margin = new Padding(0, 0, 0, 8) });
                body.Controls.Add(NoteCard(I18n.T("Los kilómetros, el tiempo y la liquidación se registrarán cuando termine el viaje. Puedes seguirlo en el mapa en vivo."), Theme.Surface, Theme.Subtle));
            }
            else
            {
                double vel = d.DurationS > 0 ? d.Km / (d.DurationS / 3600.0) : 0;
                body.Controls.Add(new Tiles(new List<(string, string, Color)>
                {
                    (I18n.T("KILÓMETROS"), d.Km.ToString("N1", Es), Green),
                    (I18n.T("TIEMPO"), FormatHm(TimeSpan.FromSeconds(d.DurationS)), Blue),
                    (I18n.T("VEL. MEDIA"), vel.ToString("N0", Es) + " km/h", Orange),
                }) { Width = W, Margin = new Padding(0, 0, 0, 8) });

                // carga: viajeros y plazas, o toneladas (con ellas se calcula el ingreso)
                string carga = null;
                if (d.Capacity > 0)
                    carga = "🧍 " + (d.Pax > 0
                        ? string.Format(I18n.T("{0} viajeros transportados · tren de {1} plazas"), d.Pax.ToString("N0", Es), d.Capacity.ToString("N0", Es))
                        : string.Format(I18n.T("Tren de {0} plazas · no ha subido ningún viajero"), d.Capacity.ToString("N0", Es)));
                else if (d.MassT > 0)
                    carga = "⚖ " + string.Format(I18n.T("Carga: {0} t · el ingreso se calcula con esta masa"), d.MassT.ToString("N0", Es));
                else if (d.Pax > 0)
                    carga = "🧍 " + string.Format(I18n.T("{0} viajeros transportados"), d.Pax.ToString("N0", Es));
                if (carga != null && d.Valid)
                    body.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Green, Font = Theme.Font(9.5f, FontStyle.Bold), Text = carga, Margin = new Padding(2, 0, 2, 10) });

                if (d.Valid)
                {
                    body.Controls.Add(new Receipt(d) { Width = W, Margin = new Padding(0, 0, 0, 10) });
                    if (d.Stops != null) body.Controls.Add(new StopsCard(d.Stops) { Width = W, Margin = new Padding(0, 0, 0, 10) });
                    if (!d.History)
                        body.Controls.Add(new Label
                        {
                            AutoSize = false, Width = W, Height = 22, ForeColor = Theme.Subtle, Font = Theme.Font(9.5f),
                            Text = I18n.T("Saldo de la empresa") + ":  " + d.Balance.ToString("N2", Es) + " €",
                            TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2, 0, 2, 8)
                        });
                }
                else if (!d.Annulled)   // anulado: lo explica su propia tarjeta (AnnulText)
                {
                    // Por qué no se registra el viaje: una línea por motivo y, debajo, qué supone.
                    var motivos = d.Reasons.Count > 0 ? d.Reasons : new List<string> { I18n.T("Velocidad media imposible (más de 350 km/h).") };
                    string warnTxt = "";
                    foreach (var m in motivos) warnTxt += "⚠  " + m + Environment.NewLine;
                    warnTxt += Environment.NewLine + I18n.T("El viaje no se guarda: no genera ingresos ni cuenta para los rankings.");
                    body.Controls.Add(NoteCard(warnTxt, Color.FromArgb(64, 44, 44), Red, bold: true));
                }
            }

            if (!string.IsNullOrWhiteSpace(d.AnnulText)) body.Controls.Add(NoteCard(d.AnnulText, Color.FromArgb(64, 44, 44), Red, bold: true));
            if (!string.IsNullOrWhiteSpace(d.Notes)) body.Controls.Add(NoteCard("📝  " + d.Notes, Theme.Surface, Theme.Text));

            // Carné por puntos: lo que ha pasado en este servicio y cómo queda el carné.
            if (!d.History && !d.InProgress && (d.Infractions.Count > 0 || d.SuspendedUntil != null
                                                || (d.LicensePoints >= 0 && d.LicensePoints < Carne.MaxPoints)))
                body.Controls.Add(LicenseCard(d, W));

            // Rango (solo en el resultado del viaje)
            if (!d.History && !string.IsNullOrEmpty(d.RankName)) body.Controls.Add(RankCard(d));

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 12, BackColor = Theme.Bg };
            Controls.Add(body);
            Controls.Add(bottom);
            Controls.Add(band);

            // Alto de la ventana: todo sin barra de desplazamiento si cabe; si no, se desplaza.
            PerformLayout();
            int contentH = body.Padding.Vertical;
            foreach (Control c in body.Controls) contentH += c.Height + c.Margin.Vertical;
            int screenH = Screen.FromControl(this)?.WorkingArea.Height ?? 1000;
            int chrome = band.Height + bottom.Height + 2;
            bool scroll = contentH + chrome > screenH - 60;
            if (scroll) { body.AutoScroll = true; Native.UseDarkScrollBars(body); }
            ClientSize = new Size(W + 36 + (scroll ? SystemInformation.VerticalScrollBarWidth : 0), Math.Min(contentH + chrome, screenH - 60));
        }

        static string FormatHm(TimeSpan t)
        {
            if (t.TotalSeconds < 0) t = TimeSpan.Zero;
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min" : $"{t.Minutes} min {t.Seconds:00} s";
        }

        static Control NoteCard(string text, Color fill, Color fore, bool bold = false)
        {
            var f = Theme.Font(bold ? 10f : 9.5f, bold ? FontStyle.Bold : FontStyle.Regular);
            int h = TextRenderer.MeasureText(text, f, new Size(W - 32, 0), TextFormatFlags.WordBreak).Height;
            var card = new Card { Width = W, Height = h + 26, Fill = fill, Radius = 12, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 10) };
            card.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = fore, Font = f, Text = text, AutoSize = false, BackColor = Color.Transparent });
            return card;
        }

        Control RankCard(Data d)
        {
            var rank = new Card { Width = W, Fill = d.RankedUp ? Color.FromArgb(58, 52, 30) : Theme.Surface, Radius = 12, Padding = new Padding(16, 12, 16, 14), Margin = new Padding(0, 0, 0, 4) };
            var rg = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, BackColor = Color.Transparent, AutoSize = true };
            rg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            if (d.RankedUp)
                rg.Controls.Add(new Label { AutoSize = false, Height = 30, Dock = DockStyle.Top, ForeColor = Gold, Font = Theme.Font(12f, FontStyle.Bold), Text = "🎉  " + string.Format(I18n.T("¡Has subido a rango {0}!"), d.RankName) });
            else
            {
                rg.Controls.Add(new Label { AutoSize = false, Height = 26, Dock = DockStyle.Top, ForeColor = Theme.Subtle, Font = Theme.Font(9.5f), Text = I18n.T("TU RANGO") });
                rg.Controls.Add(new Label { AutoSize = false, Height = 30, Dock = DockStyle.Top, ForeColor = Theme.Accent, Font = Theme.Font(14f, FontStyle.Bold), Text = d.RankName });
            }
            var bar = new Panel { Height = 10, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 4) };
            double pct = Math.Max(0, Math.Min(1, d.RankPct));
            bar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(e.Graphics, new Rectangle(0, 1, bar.Width, 8), 4, Theme.Surface2);
                int w = (int)Math.Round(bar.Width * pct);
                if (w > 3) Theme.FillRound(e.Graphics, new Rectangle(0, 1, w, 8), 4, d.RankedUp ? Gold : Theme.Accent);
            };
            rg.Controls.Add(bar);
            rg.Controls.Add(new Label
            {
                AutoSize = false, Height = 22, Dock = DockStyle.Top, ForeColor = Theme.Subtle, Font = Theme.Font(9f),
                Text = (d.RankFrozen ? "❄ " + I18n.T("Rango congelado") + "  ·  " : "") + (d.NextRankName == null
                    ? I18n.T("¡Rango máximo alcanzado!")
                    : string.Format(I18n.T("Siguiente: {0}  ·  {1} de {2} km"), d.NextRankName, d.ValidKm.ToString("N0", Es), d.NextRankKm.ToString("N0", Es)))
            });
            rank.Controls.Add(rg);
            rank.Height = d.RankedUp ? 118 : 140;
            return rank;
        }

        // Tarjeta del carné: puntos (con la barra de 15 casillas), infracciones del servicio y qué supone.
        static Control LicenseCard(Data d, int W)
        {
            int pts = d.LicensePoints;
            bool susp = d.SuspendedUntil != null;
            var card = new Card { Width = W, Fill = d.Infractions.Count > 0 ? Color.FromArgb(56, 46, 40) : Theme.Surface, Radius = 12, Padding = new Padding(16, 10, 16, 12), Margin = new Padding(0, 0, 0, 8) };
            var col = new FlowLayoutPanel { Dock = DockStyle.Top, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0) };
            int iw = W - 32;
            var top = new TableLayoutPanel { Width = iw, Height = 26, ColumnCount = 2, BackColor = Color.Transparent, Margin = new Padding(0) };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            top.Controls.Add(new Label { Text = I18n.T("CARNÉ POR PUNTOS"), AutoSize = false, Dock = DockStyle.Fill, ForeColor = Theme.Subtle, Font = Theme.Font(8.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            if (pts >= 0)
                top.Controls.Add(new Label
                {
                    Text = susp ? I18n.T("SUSPENDIDO") : string.Format(I18n.T("{0} de {1} puntos"), pts, Carne.MaxPoints),
                    AutoSize = true, Anchor = AnchorStyles.Right, ForeColor = susp ? Carne.Red : Carne.PointsColor(pts),
                    Font = Theme.Font(12f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 0)
                }, 1, 0);
            col.Controls.Add(top);
            if (pts >= 0)
            {
                var bar = new Panel { Width = iw, Height = 18, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 6) };
                bar.Paint += (s, e) => Carne.PaintBar(e.Graphics, new Rectangle(0, 5, bar.Width - 1, 8), pts);
                col.Controls.Add(bar);
            }
            foreach (var it in d.Infractions)
            {
                string pt = it.Status == "annulled" ? I18n.T("anulada") : Carne.PointsText(it.Points)
                          + (it.Status == "pending" ? " · " + I18n.T("pendiente de revisión") : "");
                string txt = "⚠  " + Carne.Label(it.Code) + (it.Detail.Length > 0 ? "  —  " + it.Detail : "") + "   (" + pt + ")";
                col.Controls.Add(new Label
                {
                    Text = txt, AutoSize = true, MaximumSize = new Size(iw, 0), ForeColor = it.Status == "pending" ? Carne.Gold : Carne.Red,
                    Font = Theme.Font(9.5f, FontStyle.Bold), Margin = new Padding(0, 2, 0, 2)
                });
            }
            var notes = new System.Collections.Generic.List<string>();
            if (d.Infractions.Exists(x => x.Status == "pending"))
                notes.Add(I18n.T("Los excesos de velocidad los revisa el administrador: si los anula, se te devuelven los puntos."));
            if (susp)
                notes.Add(string.Format(I18n.T("Carné suspendido hasta el {0}: no puedes ponerte de servicio. Después vuelves con 8 puntos."), Carne.FmtLocal(d.SuspendedUntil.Value)));
            else if (pts >= 0 && pts < Carne.PenaltyBelow)
                notes.Add(I18n.T("Por debajo de 6 puntos: el rango queda congelado y tus servicios no suman a la liga ni al ranking de maquinistas."));
            else if (pts >= 0 && pts < Carne.WarnBelow)
                notes.Add(I18n.T("Aviso: el carné está por debajo de 10 puntos."));
            if (d.Valid && d.Infractions.Count == 0 && pts >= 0 && pts < Carne.MaxPoints && !susp)
                notes.Add(string.Format(I18n.T("Servicio sin infracciones: cuenta para recuperar puntos (+1 cada {0} servicios limpios seguidos)."), Carne.CleanForPoint));
            foreach (var n in notes)
                col.Controls.Add(new Label { Text = n, AutoSize = true, MaximumSize = new Size(iw, 0), ForeColor = Theme.Subtle, Font = Theme.Font(9f), Margin = new Padding(0, 4, 0, 0) });
            card.Controls.Add(col);
            card.Height = col.GetPreferredSize(new Size(iw, 0)).Height + card.Padding.Vertical + 4;
            return card;
        }


        public static string FormatDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            int h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            if (h > 0) return $"{h} h {m} min";
            if (m > 0) return $"{m} min {s} s";
            return $"{s} s";
        }

        // ------------------------------------------------------------------ bloques dibujados
        // Banda de color del estado: empresa, maquinista · fecha y el trayecto Origen → Destino.
        sealed class ServiceBand : Control
        {
            readonly Data _d;
            readonly Font _fSt = Theme.Font(8f, FontStyle.Bold), _fT = Theme.Font(15f, FontStyle.Bold), _fSub = Theme.Font(9f), _fPath = Theme.Font(9.5f, FontStyle.Bold), _fTrain;
            public ServiceBand(Data d)
            {
                _d = d;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                _fTrain = EmojiPicker.EmojiFont(11f);
                Height = Theme.Px(126);
            }
            protected override void Dispose(bool disposing) { if (disposing) { _fSt.Dispose(); _fT.Dispose(); _fSub.Dispose(); _fPath.Dispose(); _fTrain.Dispose(); } base.Dispose(disposing); }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; var r = ClientRectangle;
                Color a, b, st; string status;
                if (_d.InProgress) { a = Color.FromArgb(40, 52, 86); b = Color.FromArgb(32, 36, 48); st = Blue; status = "●  " + I18n.T("EN CONDUCCIÓN · EN DIRECTO"); }
                else if (_d.Annulled) { a = Color.FromArgb(84, 44, 44); b = Color.FromArgb(44, 32, 32); st = Red; status = "⛔  " + I18n.T("SERVICIO ANULADO"); }
                else if (_d.Valid) { a = Color.FromArgb(40, 70, 48); b = Color.FromArgb(32, 42, 36); st = Green; status = "✓  " + I18n.T("SERVICIO REGISTRADO"); }
                else { a = Color.FromArgb(84, 44, 44); b = Color.FromArgb(44, 32, 32); st = Red; status = "✕  " + I18n.T("SERVICIO NO REGISTRADO"); }
                using (var br = new LinearGradientBrush(r, a, b, LinearGradientMode.ForwardDiagonal)) g.FillRectangle(br, r);
                using (var p = new Pen(st, 2)) g.DrawLine(p, 0, 0, r.Width, 0);
                int x = Theme.Px(20), w = r.Width - 2 * x, y = Theme.Px(14);
                var fl = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
                TextRenderer.DrawText(g, status, _fSt, new Rectangle(x, y, w, Theme.Px(16)), st, fl); y += Theme.Px(20);
                TextRenderer.DrawText(g, _d.Company ?? "", _fT, new Rectangle(x, y, w, Theme.Px(28)), Theme.Text, fl); y += Theme.Px(29);
                var sub = new List<string>();
                if (!string.IsNullOrWhiteSpace(_d.Driver) && _d.Driver != "—") sub.Add(_d.Driver);
                if (_d.InProgress && _d.StartLocal != DateTime.MinValue) sub.Add(string.Format(I18n.T("salió a las {0}"), _d.StartLocal.ToString("HH:mm", Es)));
                else if (!string.IsNullOrWhiteSpace(_d.DateText)) sub.Add(_d.DateText);
                if (!string.IsNullOrWhiteSpace(_d.StatusText) && !_d.InProgress && _d.History) sub.Add(_d.StatusText);
                TextRenderer.DrawText(g, string.Join("  ·  ", sub), _fSub, new Rectangle(x, y, w, Theme.Px(18)), Color.FromArgb(200, 206, 210), fl); y += Theme.Px(24);
                // trayecto: «Almargen_Bobadilla» → Almargen ··· 🚆 ··· Bobadilla (si no se puede partir, el nombre tal cual)
                var (from, to) = SplitPath(_d.Path);
                if (from == null)
                {
                    if (!string.IsNullOrWhiteSpace(_d.Path)) TextRenderer.DrawText(g, "🛤  " + _d.Path, _fPath, new Rectangle(x, y, w, Theme.Px(24)), Theme.Text, fl);
                    return;
                }
                int fw = TextRenderer.MeasureText(from, _fPath, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(16);
                int tw = TextRenderer.MeasureText(to, _fPath, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(16);
                fw = Math.Min(fw, w / 2 - Theme.Px(40)); tw = Math.Min(tw, w / 2 - Theme.Px(40));
                var fr = new Rectangle(x, y, fw, Theme.Px(24)); var tr = new Rectangle(x + w - tw, y, tw, Theme.Px(24));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bb = new SolidBrush(Color.FromArgb(80, 0, 0, 0))) { using (var p1 = Theme.Round(fr, 6)) g.FillPath(bb, p1); using (var p2 = Theme.Round(tr, 6)) g.FillPath(bb, p2); }
                g.SmoothingMode = SmoothingMode.None;
                TextRenderer.DrawText(g, from, _fPath, fr, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, to, _fPath, tr, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                int cy = y + Theme.Px(12), mid = (fr.Right + tr.X) / 2;
                double prog = _d.InProgress ? 0.5 : 1;
                using (var dash = new Pen(Color.FromArgb(150, 255, 255, 255), 2) { DashPattern = new float[] { 3, 3 } })
                    g.DrawLine(dash, fr.Right + Theme.Px(8), cy, tr.X - Theme.Px(8), cy);
                int tx = (int)(fr.Right + Theme.Px(8) + (tr.X - fr.Right - Theme.Px(16)) * (_d.InProgress ? 0.5 : 0.5));
                ColorText.DrawCells(g, new[] { ("🚆", new Rectangle(tx - Theme.Px(14), cy - Theme.Px(12), Theme.Px(28), Theme.Px(24))) }, _fTrain, Theme.Text, new Rectangle(tx - Theme.Px(16), cy - Theme.Px(13), Theme.Px(32), Theme.Px(26)));
            }

            static (string, string) SplitPath(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return (null, null);
                foreach (var sep in new[] { " - ", " – ", "-", "_", " a " })
                {
                    var parts = p.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[0].Trim().Length > 1 && parts[1].Trim().Length > 1) return (parts[0].Trim(), parts[1].Trim());
                }
                return (null, null);
            }
        }

        // Fichas en fila: rótulo pequeño y valor.
        sealed class Tiles : Control
        {
            readonly List<(string cap, string val, Color col)> _t; readonly bool _small;
            readonly Font _fc = Theme.Font(7.5f, FontStyle.Bold), _fv, _fvs;
            public Tiles(List<(string, string, Color)> t, bool small = false)
            {
                _t = t; _small = small;
                _fv = Theme.Font(16f, FontStyle.Bold); _fvs = Theme.Font(10f, FontStyle.Bold);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Height = small ? Theme.Px(52) : Theme.Px(66);
            }
            protected override void Dispose(bool disposing) { if (disposing) { _fc.Dispose(); _fv.Dispose(); _fvs.Dispose(); } base.Dispose(disposing); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg);
                int gap = Theme.Px(8), n = _t.Count, cw = (Width - gap * (n - 1)) / n;
                for (int i = 0; i < n; i++)
                {
                    var r = new Rectangle(i * (cw + gap), 0, cw, Height - 1);
                    g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, Theme.Px(10), Theme.Surface); g.SmoothingMode = SmoothingMode.None;
                    var (cap, val, col) = _t[i];
                    TextRenderer.DrawText(g, cap, _fc, new Rectangle(r.X + Theme.Px(12), r.Y + Theme.Px(8), r.Width - Theme.Px(20), Theme.Px(14)), Theme.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, val, _small ? _fvs : _fv, new Rectangle(r.X + Theme.Px(12), r.Y + Theme.Px(22), r.Width - Theme.Px(20), r.Height - Theme.Px(26)), col,
                        TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
        }

        // Liquidación en forma de ticket: cada importe con su cálculo y el neto.
        sealed class Receipt : Control
        {
            readonly Data _d;
            readonly List<ServiceCalc.Line> _lines; readonly bool _hasDetail; readonly double _costs;
            static readonly Color Paper = Color.FromArgb(248, 246, 238), Ink = Color.FromArgb(42, 42, 42), Faint = Color.FromArgb(118, 112, 100),
                                  PosC = Color.FromArgb(46, 125, 50), NegC = Color.FromArgb(178, 59, 59), Dash = Color.FromArgb(201, 194, 176);
            readonly Font _fH = Theme.Font(7.5f, FontStyle.Bold), _fL = Theme.Font(9.5f, FontStyle.Bold), _fS = Theme.Font(8.25f), _fT = Theme.Font(12f, FontStyle.Bold), _fN = Theme.Font(8f, FontStyle.Italic);
            const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl;

            public Receipt(Data d)
            {
                _d = d;
                _lines = ServiceCalc.Explain(d.Calc, d.Km, d.Income, d.Canon, d.Energy, d.Salary, d.Rental, d.Maintenance, out _hasDetail);
                foreach (var l in _lines) if (l.Cost) _costs += l.Amount;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Height = Layout(null);
            }
            protected override void Dispose(bool disposing) { if (disposing) { _fH.Dispose(); _fL.Dispose(); _fS.Dispose(); _fT.Dispose(); _fN.Dispose(); } base.Dispose(disposing); }
            protected override void OnResize(EventArgs e) { base.OnResize(e); int h = Layout(null); if (h != Height) Height = h; }

            // Mide (g == null) o dibuja; devuelve el alto.
            int Layout(Graphics g)
            {
                int w = Math.Max(200, Width), x = Theme.Px(18), iw = w - 2 * x, y = Theme.Px(12);
                string no = string.IsNullOrEmpty(_d.ServiceId) ? "" : "Nº " + _d.ServiceId.Substring(0, Math.Min(8, _d.ServiceId.Length)).ToUpperInvariant();
                if (g != null)
                {
                    TextRenderer.DrawText(g, I18n.T("LIQUIDACIÓN DEL SERVICIO"), _fH, new Rectangle(x, y, iw, Theme.Px(14)), Faint, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, no, _fH, new Rectangle(x, y, iw, Theme.Px(14)), Faint, TextFormatFlags.NoPadding | TextFormatFlags.Right);
                }
                y += Theme.Px(18); DashLine(g, x, y, iw); y += Theme.Px(8);
                foreach (var l in _lines)
                {
                    string amt = (l.Cost ? "−" : "+") + Math.Abs(l.Amount).ToString("N2", Es) + " €";
                    int aw = TextRenderer.MeasureText(amt, _fL, Size.Empty, TextFormatFlags.NoPadding).Width;
                    if (g != null)
                    {
                        TextRenderer.DrawText(g, l.Label, _fL, new Rectangle(x, y, iw - aw - 8, Theme.Px(18)), Ink, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                        TextRenderer.DrawText(g, amt, _fL, new Rectangle(x + iw - aw, y, aw, Theme.Px(18)), l.Cost ? NegC : PosC, TextFormatFlags.NoPadding);
                    }
                    y += Theme.Px(19);
                    foreach (var st in l.Steps)
                    {
                        bool sub = st.StartsWith("   ");
                        int ind = sub ? Theme.Px(14) : 0;
                        string t = st.Trim();
                        int h = TextRenderer.MeasureText(t, _fS, new Size(iw - ind - aw / 2, 0), Wrap).Height;
                        if (g != null) TextRenderer.DrawText(g, t, _fS, new Rectangle(x + ind, y, iw - ind - aw / 2, h), Faint, Wrap);
                        y += h + Theme.Px(2);
                    }
                    y += Theme.Px(6);
                }
                DashLine(g, x, y, iw); y += Theme.Px(8);
                string net = (_d.Net >= 0 ? "+" : "−") + Math.Abs(_d.Net).ToString("N2", Es) + " €";
                if (g != null)
                {
                    TextRenderer.DrawText(g, I18n.T("NETO DEL VIAJE"), _fT, new Rectangle(x, y, iw, Theme.Px(24)), Ink, TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, net, _fT, new Rectangle(x, y, iw, Theme.Px(24)), _d.Net < 0 ? NegC : PosC, TextFormatFlags.NoPadding | TextFormatFlags.Right);
                }
                y += Theme.Px(24);
                string calc = string.Format(I18n.T("Neto = ingreso {0} − gastos {1}"), _d.Income.ToString("N2", Es) + " €", _costs.ToString("N2", Es) + " €");
                if (g != null) TextRenderer.DrawText(g, calc, _fS, new Rectangle(x, y, iw, Theme.Px(16)), Faint, TextFormatFlags.NoPadding);
                y += Theme.Px(18);
                if (!_hasDetail)
                {
                    string old = I18n.T("Este servicio se registró antes de que se guardara el detalle del cálculo: solo se puede mostrar lo que equivale cada importe por km.");
                    int h = TextRenderer.MeasureText(old, _fN, new Size(iw, 0), Wrap).Height;
                    if (g != null) TextRenderer.DrawText(g, old, _fN, new Rectangle(x, y, iw, h), Faint, Wrap);
                    y += h + Theme.Px(4);
                }
                // código de barras decorativo
                if (g != null)
                {
                    var rnd = new Random((_d.ServiceId ?? "").GetHashCode());
                    using var b = new SolidBrush(Color.FromArgb(150, Ink));
                    for (int bx = x; bx < x + iw; ) { int bw = rnd.Next(1, 4); if (rnd.Next(3) > 0) g.FillRectangle(b, bx, y + Theme.Px(4), bw, Theme.Px(20)); bx += bw + rnd.Next(1, 3); }
                }
                y += Theme.Px(30);
                return y + Theme.Px(6);
            }

            void DashLine(Graphics g, int x, int y, int w)
            {
                if (g == null) return;
                using var p = new Pen(Dash) { DashPattern = new float[] { 4, 3 } };
                g.DrawLine(p, x, y, x + w, y);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg);
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = Theme.Round(r, Theme.Px(10))) using (var b = new SolidBrush(Paper)) g.FillPath(b, path);
                // muescas laterales del ticket
                int d = Theme.Px(16);
                using (var b = new SolidBrush(Theme.Bg)) { g.FillEllipse(b, -d / 2, Height / 2 - d / 2, d, d); g.FillEllipse(b, Width - d / 2 - 1, Height / 2 - d / 2, d, d); }
                g.SmoothingMode = SmoothingMode.None;
                Layout(g);
            }
        }

        // Paradas comerciales: hora del simulador, estación y viajeros que suben y bajan.
        sealed class StopsCard : Control
        {
            readonly List<(string station, string time, int board, int alight)> _s;
            readonly Font _fT = Theme.Font(7.5f, FontStyle.Bold), _fR = Theme.Font(9.5f), _fB = Theme.Font(9.5f, FontStyle.Bold), _fH = Theme.Font(9f, FontStyle.Bold);
            public StopsCard(List<(string, string, int, int)> s)
            {
                _s = s;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Height = Theme.Px(40) + Math.Max(1, _s.Count) * Theme.Px(28) + Theme.Px(8);
            }
            protected override void Dispose(bool disposing) { if (disposing) { _fT.Dispose(); _fR.Dispose(); _fB.Dispose(); _fH.Dispose(); } base.Dispose(disposing); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Bg);
                var r = new Rectangle(0, 0, Width - 1, Height - 1);
                g.SmoothingMode = SmoothingMode.AntiAlias; Theme.FillRound(g, r, Theme.Px(12), Theme.Surface); g.SmoothingMode = SmoothingMode.None;
                int x = Theme.Px(16), w = Width - 2 * x, y = Theme.Px(12);
                TextRenderer.DrawText(g, I18n.T("PARADAS COMERCIALES") + "  ·  " + _s.Count, _fT, new Rectangle(x, y, w, Theme.Px(16)), Theme.Subtle, TextFormatFlags.NoPadding);
                y += Theme.Px(26);
                if (_s.Count == 0)
                {
                    TextRenderer.DrawText(g, I18n.T("El tren no hizo ninguna parada con apertura de puertas en un andén."), _fR, new Rectangle(x, y, w, Theme.Px(26)), Theme.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                    return;
                }
                int lineX = x + Theme.Px(58);
                using (var p = new Pen(Theme.Surface2, 2)) g.DrawLine(p, lineX, y + Theme.Px(12), lineX, y + (_s.Count - 1) * Theme.Px(28) + Theme.Px(12));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < _s.Count; i++)
                {
                    var (st, t, b, a) = _s[i];
                    int ry = y + i * Theme.Px(28);
                    TextRenderer.DrawText(g, string.IsNullOrEmpty(t) ? "—" : t, _fH, new Rectangle(x, ry, Theme.Px(46), Theme.Px(24)), Theme.Subtle, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                    int d = Theme.Px(10);
                    using (var br = new SolidBrush(i == 0 || i == _s.Count - 1 ? Theme.Accent : Theme.Surface)) g.FillEllipse(br, lineX - d / 2, ry + Theme.Px(12) - d / 2, d, d);
                    using (var pen = new Pen(Theme.Accent, 2)) g.DrawEllipse(pen, lineX - d / 2, ry + Theme.Px(12) - d / 2, d, d);
                    string pax = string.Join("    ", new[] { b > 0 ? "↑ " + b.ToString("N0", Es) : null, a > 0 ? "↓ " + a.ToString("N0", Es) : null }.Where(v => v != null));
                    int pw = TextRenderer.MeasureText(pax, _fR, Size.Empty, TextFormatFlags.NoPadding).Width;
                    TextRenderer.DrawText(g, st, _fB, new Rectangle(lineX + Theme.Px(16), ry, w - (lineX - x) - Theme.Px(20) - pw, Theme.Px(24)), Theme.Text, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    TextRenderer.DrawText(g, pax, _fR, new Rectangle(x + w - pw, ry, pw, Theme.Px(24)), Green, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
                }
                g.SmoothingMode = SmoothingMode.None;
            }
        }
    }
}
