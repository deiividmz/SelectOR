// Ventana de RESULTADO de un servicio (solo lectura). Al volver de conducir el
// servicio se registra automáticamente (la economía la calcula el servidor) y
// esta ventana muestra TODO lo registrado: km, tiempo, velocidad media, desglose
// de ingreso/gastos, neto, saldo de la empresa y el rango del maquinista, con un
// aviso destacado si ha subido de rango con este viaje. No se puede editar nada.

using System;
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
            public string Driver = "";      // maquinista (para la vista de detalle desde Servicios)
            public string DateText = "";    // fecha (vista de detalle)
            public bool History;            // true = vista de detalle desde Servicios (oculta el rango)
            // Datos adicionales del viaje (máxima info que registra OR/servidor)
            public string Consist = "";     // tren conducido
            public int Cars, Engines;       // coches/vagones de su .con y cuántos son motrices (0 = no se sabe)
            public string Path = "";        // recorrido (.pat)
            public string StatusText = "";  // estado (completado / en curso / …)
            public bool InProgress;         // true = servicio "En conducción": aún no hay km/tiempo/economía
            public string Notes = "";       // notas del servicio, si las hay
            // Viaje NO registrado (Valid = false): por qué no se ha guardado (viaje corto, velocidad imposible…).
            public System.Collections.Generic.List<string> Reasons = new System.Collections.Generic.List<string>();
            // Rango
            public string RankName = "";
            public bool RankedUp;
            public double ValidKm;          // km válidos totales tras este viaje
            public double RankPct;          // progreso al siguiente rango [0..1]
            public string NextRankName;     // null si ya es el rango máximo
            public double NextRankKm;
            public bool RankFrozen;         // rango congelado (carné por debajo de 6 puntos)
            // Carné por puntos: infracciones de este servicio y puntos que quedan (−1 = no se sabe).
            public System.Collections.Generic.List<Carne.Item> Infractions = new System.Collections.Generic.List<Carne.Item>();
            public int LicensePoints = -1;
            public DateTime? SuspendedUntil;
        }

        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static readonly Color Green = Color.FromArgb(129, 199, 132);
        static readonly Color Red = Color.FromArgb(229, 115, 115);
        static readonly Color Gold = Color.FromArgb(255, 196, 84);

        public ServiceResultDialog(Data d)
        {
            Text = I18n.T(d.InProgress ? "Servicio en conducción" : (d.Valid ? "Servicio registrado" : "Servicio no registrado"));
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(560, 640);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label
            {
                Text = "  " + I18n.T(d.InProgress ? "▶  Servicio EN CONDUCCIÓN" : (d.Valid ? "Servicio registrado" : "Servicio NO REGISTRADO")),
                Dock = DockStyle.Top, Height = 46,
                Font = Theme.Font(14f, FontStyle.Bold),
                ForeColor = d.InProgress ? Color.FromArgb(120, 144, 226) : (d.Valid ? Green : Red),
                TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface
            };

            var body = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                AutoScroll = false, BackColor = Theme.Bg, Padding = new Padding(18, 12, 18, 8)
            };
            int W = 512;   // ancho útil de las tarjetas (sin barra de scroll)

            // Cabecera: "Viaje para <empresa>"
            body.Controls.Add(new Label
            {
                AutoSize = false, Width = W, Height = 24, ForeColor = Theme.Subtle,
                Font = Theme.Font(10f), Text = string.Format(I18n.T("Viaje para {0}"), d.Company),
                Margin = new Padding(2, 0, 2, 6)
            });

            // Tarjeta de DETALLES del viaje (todos los datos que registra OR/servidor).
            var det = new System.Collections.Generic.List<(string, string)>();
            void Add(string k, string v) { if (!string.IsNullOrWhiteSpace(v)) det.Add((k, v)); }
            Add(I18n.T("Ruta"), d.Route);
            Add(I18n.T("Tren"), d.Consist);
            if (d.Cars > 0) Add(I18n.T("Coches"), d.Cars.ToString("N0", Es)
                + (d.Engines > 0 ? "  (" + d.Engines + " " + I18n.T(d.Engines == 1 ? "motriz" : "motrices") + ")" : ""));
            Add(I18n.T("Recorrido"), d.Path);
            if (d.History) { Add(I18n.T("Maquinista"), d.Driver); Add(I18n.T("Fecha"), d.DateText); }
            Add(I18n.T("Estado"), d.StatusText);
            Add(I18n.T("Notas"), d.Notes);
            if (det.Count > 0)
            {
                var card = new Card { Width = W, Fill = Theme.Surface, Radius = 12, Padding = new Padding(16, 10, 16, 10), Margin = new Padding(0, 0, 0, 8) };
                var g = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, BackColor = Color.Transparent, AutoSize = true };
                g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
                g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                foreach (var (k, v) in det)
                {
                    int r = g.RowCount; g.RowCount = r + 1; g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    g.Controls.Add(new Label { Text = k, AutoSize = false, Dock = DockStyle.Fill, Height = 24, ForeColor = Theme.Subtle, Font = Theme.Font(9.5f), TextAlign = ContentAlignment.MiddleLeft }, 0, r);
                    g.Controls.Add(new Label { Text = v, AutoSize = false, Dock = DockStyle.Fill, Height = 24, ForeColor = Theme.Text, Font = Theme.Font(9.5f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 1, r);
                }
                card.Controls.Add(g);
                card.Height = 20 + det.Count * 24 + 4;
                body.Controls.Add(card);
            }

            // Servicio EN CONDUCCIÓN: aún no hay km/tiempo/economía. Mostramos solo un aviso y
            // NO pintamos los KPIs ni la economía (los datos del viaje se registran al terminar).
            if (d.InProgress)
            {
                string liveTxt = "▶  " + I18n.T("El maquinista está realizando este servicio ahora mismo. Los kilómetros, el tiempo y la economía se registrarán cuando termine.");
                var liveFont = Theme.Font(10.5f, FontStyle.Bold);
                int liveTextW = W - 32;   // ancho útil dentro del padding (16+16)
                int liveTextH = TextRenderer.MeasureText(liveTxt, liveFont, new Size(liveTextW, 0), TextFormatFlags.WordBreak).Height;
                var live = new Card { Width = W, Fill = Color.FromArgb(38, 46, 66), Radius = 12, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
                live.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill, ForeColor = Color.FromArgb(120, 144, 226),
                    Font = liveFont, AutoSize = false, Text = liveTxt
                });
                live.Height = liveTextH + 24 + 4;   // texto + padding vertical + holgura
                body.Controls.Add(live);
            }

            // KPIs: km, tiempo, velocidad media  (solo cuando el servicio ya ha terminado)
            double vel = d.DurationS > 0 ? d.Km / (d.DurationS / 3600.0) : 0;
            if (!d.InProgress)
            {
            var kpis = new TableLayoutPanel { Width = W, Height = 84, ColumnCount = 3, RowCount = 1, BackColor = Theme.Bg, Margin = new Padding(0, 0, 0, 8) };
            for (int i = 0; i < 3; i++) kpis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
            kpis.Controls.Add(Kpi(I18n.T("Kilómetros"), d.Km.ToString("N1", Es) + " km", Theme.Accent), 0, 0);
            kpis.Controls.Add(Kpi(I18n.T("Tiempo real"), FormatDuration(d.DurationS), Color.FromArgb(120, 144, 226)), 1, 0);
            kpis.Controls.Add(Kpi(I18n.T("Velocidad media"), vel.ToString("N0", Es) + " km/h", Color.FromArgb(255, 167, 89)), 2, 0);
            body.Controls.Add(kpis);

            // Tren de viajeros: los transportados y las plazas del tren. Mercancías: las toneladas, que son
            // con las que se calcula el ingreso.
            string carga = null;
            if (d.Capacity > 0)
                carga = "🧍 " + (d.Pax > 0
                    ? string.Format(I18n.T("{0} viajeros transportados · tren de {1} plazas"), d.Pax.ToString("N0", Es), d.Capacity.ToString("N0", Es))
                    : string.Format(I18n.T("Tren de {0} plazas · no ha subido ningún viajero"), d.Capacity.ToString("N0", Es)));
            else if (d.MassT > 0)
                carga = "⚖ " + string.Format(I18n.T("Carga: {0} t · el ingreso se calcula con esta masa"), d.MassT.ToString("N0", Es));
            else if (d.Pax > 0)
                carga = "🧍 " + string.Format(I18n.T("{0} viajeros transportados"), d.Pax.ToString("N0", Es));
            if (carga != null)
                body.Controls.Add(new Label
                {
                    AutoSize = true, MaximumSize = new Size(W, 0), ForeColor = Color.FromArgb(129, 199, 132),
                    Font = Theme.Font(10.5f, FontStyle.Bold),
                    Text = carga,
                    Margin = new Padding(2, 0, 2, 8)
                });
            }

            // Economía
            if (!d.InProgress && d.Valid)
            {
                bool hasRental = d.Rental > 0;
                int ecoRows = 4 + (hasRental ? 1 : 0);
                var eco = new Card { Width = W, Height = 24 + ecoRows * 26 + 13 + 34, Fill = Theme.Surface, Radius = 12, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
                var g = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, BackColor = Color.Transparent, AutoSize = true };
                g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                g.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                EcoRow(g, I18n.T("Ingreso"), d.Income, Green, false);
                EcoRow(g, I18n.T("Cánon AI"), d.Canon, Red, true);
                EcoRow(g, I18n.T("Energía"), d.Energy, Red, true);
                EcoRow(g, I18n.T("Salario"), d.Salary, Red, true);
                if (hasRental) EcoRow(g, I18n.T("Alquiler de la unidad"), d.Rental, Red, true);
                // separador
                var sep = new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Theme.Border, Margin = new Padding(0, 6, 0, 6) };
                g.Controls.Add(sep); g.SetColumnSpan(sep, 2);
                EcoRow(g, I18n.T("Neto del viaje"), d.Net, d.Net >= 0 ? Green : Red, d.Net < 0, big: true);
                eco.Controls.Add(g);
                body.Controls.Add(eco);

                if (!d.History)   // el saldo de la empresa solo tiene sentido en el resultado del viaje
                    body.Controls.Add(new Label
                    {
                        AutoSize = false, Width = W, Height = 24, ForeColor = Theme.Subtle,
                        Font = Theme.Font(9.5f),
                        Text = I18n.T("Saldo de la empresa") + ":  " + d.Balance.ToString("N2", Es) + " €",
                        TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(2, 0, 2, 8)
                    });
            }
            else if (!d.InProgress)
            {
                // Por qué no se registra el viaje: una línea por motivo y, debajo, qué supone.
                var motivos = d.Reasons.Count > 0 ? d.Reasons
                    : new System.Collections.Generic.List<string> { I18n.T("Velocidad media imposible (más de 350 km/h).") };
                string warnTxt = "";
                foreach (var m in motivos) warnTxt += "⚠  " + m + Environment.NewLine;
                warnTxt += Environment.NewLine + I18n.T("El viaje no se guarda: no genera ingresos ni cuenta para los rankings.");
                var warnFont = Theme.Font(10f, FontStyle.Bold);
                int warnH = TextRenderer.MeasureText(warnTxt, warnFont, new Size(W - 32, 0), TextFormatFlags.WordBreak).Height;
                var warn = new Card { Width = W, Fill = Color.FromArgb(64, 44, 44), Radius = 12, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
                warn.Controls.Add(new Label { Dock = DockStyle.Fill, ForeColor = Red, Font = warnFont, Text = warnTxt, AutoSize = false });
                warn.Height = warnH + 24 + 6;
                body.Controls.Add(warn);
            }

            // Carné por puntos: lo que ha pasado en este servicio y cómo queda el carné.
            if (!d.History && !d.InProgress && (d.Infractions.Count > 0 || d.SuspendedUntil != null
                                                || (d.LicensePoints >= 0 && d.LicensePoints < Carne.MaxPoints)))
                body.Controls.Add(LicenseCard(d, W));

            // Rango (solo en el resultado del viaje; en la vista de detalle desde Servicios se oculta)
            if (!d.History && !string.IsNullOrEmpty(d.RankName))
            {
            var rank = new Card { Width = W, Fill = d.RankedUp ? Color.FromArgb(58, 52, 30) : Theme.Surface, Radius = 12, Padding = new Padding(16, 12, 16, 14), Margin = new Padding(0, 0, 0, 4) };
            var rg = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, BackColor = Color.Transparent, AutoSize = true };
            rg.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            if (d.RankedUp)
            {
                rg.Controls.Add(new Label
                {
                    AutoSize = false, Height = 30, Dock = DockStyle.Top, ForeColor = Gold,
                    Font = Theme.Font(12f, FontStyle.Bold),
                    Text = "🎉  " + string.Format(I18n.T("¡Has subido a rango {0}!"), d.RankName)
                });
            }
            else
            {
                rg.Controls.Add(new Label
                {
                    AutoSize = false, Height = 26, Dock = DockStyle.Top, ForeColor = Theme.Subtle,
                    Font = Theme.Font(9.5f), Text = I18n.T("TU RANGO")
                });
                rg.Controls.Add(new Label
                {
                    AutoSize = false, Height = 30, Dock = DockStyle.Top, ForeColor = Theme.Accent,
                    Font = Theme.Font(14f, FontStyle.Bold), Text = d.RankName
                });
            }
            // barra de progreso al siguiente rango
            var bar = new Panel { Height = 10, Dock = DockStyle.Top, BackColor = Color.Transparent, Margin = new Padding(0, 6, 0, 4) };
            double pct = Math.Max(0, Math.Min(1, d.RankPct));
            bar.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var full = new Rectangle(0, 1, bar.Width, 8);
                Theme.FillRound(e.Graphics, full, 4, Theme.Surface2);
                int w = (int)Math.Round(bar.Width * pct);
                if (w > 3) Theme.FillRound(e.Graphics, new Rectangle(0, 1, w, 8), 4, d.RankedUp ? Gold : Theme.Accent);
            };
            rg.Controls.Add(bar);
            rg.Controls.Add(new Label
            {
                AutoSize = false, Height = 22, Dock = DockStyle.Top, ForeColor = Theme.Subtle,
                Font = Theme.Font(9f),
                Text = (d.RankFrozen ? "❄ " + I18n.T("Rango congelado") + "  ·  " : "") + (d.NextRankName == null
                    ? I18n.T("¡Rango máximo alcanzado!")
                    : string.Format(I18n.T("Siguiente: {0}  ·  {1} de {2} km"), d.NextRankName,
                                    d.ValidKm.ToString("N0", Es), d.NextRankKm.ToString("N0", Es)))
            });
            rank.Controls.Add(rg);
            rank.Height = d.RankedUp ? 118 : 140;
            body.Controls.Add(rank);
            }

            // Sin botón "Hecho": la ventana se cierra con la X del título. Solo un margen inferior.
            var buttons = new Panel { Dock = DockStyle.Bottom, Height = 16, BackColor = Theme.Bg };

            Controls.Add(body);
            Controls.Add(buttons);
            Controls.Add(header);
            Controls.Add(stripe);

            // Ajusta el alto de la ventana para que TODO quepa sin barra de desplazamiento.
            PerformLayout();
            int contentH = body.Padding.Vertical;
            foreach (Control c in body.Controls) contentH += c.Height + c.Margin.Vertical;
            int chrome = stripe.Height + header.Height + buttons.Height;
            int screenH = Screen.FromControl(this)?.WorkingArea.Height ?? 1000;
            ClientSize = new Size(W + 36, Math.Min(contentH + chrome + 2, screenH - 60));
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

        // Tarjeta KPI (icono de color a un lado, valor grande).
        static Control Kpi(string caption, string value, Color accent)
        {
            var card = new Card { Dock = DockStyle.Fill, Fill = Theme.Surface, Radius = 10, Padding = new Padding(12, 8, 10, 8), Margin = new Padding(3) };
            var col = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Color.Transparent };
            col.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            col.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            col.Controls.Add(new Label { Text = caption.ToUpperInvariant(), AutoSize = false, Dock = DockStyle.Top, Height = 18, ForeColor = Theme.Subtle, Font = Theme.Font(7.5f, FontStyle.Bold), AutoEllipsis = true }, 0, 0);
            col.Controls.Add(new Label { Text = value, AutoSize = false, Dock = DockStyle.Fill, ForeColor = accent, Font = Theme.Font(15f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
            card.Controls.Add(col);
            return card;
        }

        // Fila de economía (etiqueta a la izquierda, importe a la derecha).
        void EcoRow(TableLayoutPanel g, string label, double amount, Color color, bool negative, bool big = false)
        {
            var l = new Label
            {
                Text = label, AutoSize = false, Dock = DockStyle.Fill, Height = big ? 34 : 26,
                ForeColor = big ? Theme.Text : Theme.Subtle,
                Font = Theme.Font(big ? 11.5f : 10f, big ? FontStyle.Bold : FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };
            string sign = negative ? "−" : "+";
            var a = new Label
            {
                Text = sign + Math.Abs(amount).ToString("N2", Es) + " €", AutoSize = false, Dock = DockStyle.Fill, Height = big ? 34 : 26,
                ForeColor = color, Font = Theme.Font(big ? 13f : 10f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleRight, MinimumSize = new Size(150, 0)
            };
            int r = g.RowCount; g.RowCount = r + 1;
            g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            g.Controls.Add(l, 0, r);
            g.Controls.Add(a, 1, r);
        }

        public static string FormatDuration(int seconds)
        {
            if (seconds < 0) seconds = 0;
            int h = seconds / 3600, m = (seconds % 3600) / 60, s = seconds % 60;
            if (h > 0) return $"{h} h {m} min";
            if (m > 0) return $"{m} min {s} s";
            return $"{s} s";
        }
    }
}
