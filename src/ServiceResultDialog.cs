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
            public double Income, Canon, Energy, Salary, Rental, Maintenance, Net, Balance;
            public string Driver = "";      // maquinista (para la vista de detalle desde Servicios)
            public string DateText = "";    // fecha (vista de detalle)
            public bool History;            // true = vista de detalle desde Servicios (oculta el rango)
            // Datos adicionales del viaje (máxima info que registra OR/servidor)
            public string Consist = "";     // tren conducido
            public string Path = "";        // recorrido (.pat)
            public string StatusText = "";  // estado (completado / en curso / …)
            public bool InProgress;         // true = servicio "En conducción": aún no hay km/tiempo/economía
            public string Notes = "";       // notas del servicio, si las hay
            // Rango
            public string RankName = "";
            public bool RankedUp;
            public double ValidKm;          // km válidos totales tras este viaje
            public double RankPct;          // progreso al siguiente rango [0..1]
            public string NextRankName;     // null si ya es el rango máximo
            public double NextRankKm;
        }

        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static readonly Color Green = Color.FromArgb(129, 199, 132);
        static readonly Color Red = Color.FromArgb(229, 115, 115);
        static readonly Color Gold = Color.FromArgb(255, 196, 84);

        public ServiceResultDialog(Data d)
        {
            Text = I18n.T(d.InProgress ? "Servicio en conducción" : (d.Valid ? "Servicio registrado" : "Servicio no válido"));
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
                Text = "  " + I18n.T(d.InProgress ? "▶  Servicio EN CONDUCCIÓN" : (d.Valid ? "Servicio registrado" : "Servicio NO VÁLIDO")),
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

            // Viajeros transportados (modelo de embarque en andenes)
            if (d.Pax > 0)
                body.Controls.Add(new Label
                {
                    AutoSize = true, ForeColor = Color.FromArgb(129, 199, 132),
                    Font = Theme.Font(10.5f, FontStyle.Bold),
                    Text = "🧍 " + string.Format(I18n.T("{0} viajeros transportados"), d.Pax.ToString("N0", Es)),
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
                var warn = new Card { Width = W, Fill = Color.FromArgb(64, 44, 44), Radius = 12, Padding = new Padding(16, 12, 16, 12), Margin = new Padding(0, 0, 0, 8) };
                warn.Controls.Add(new Label
                {
                    Dock = DockStyle.Fill, ForeColor = Red, Font = Theme.Font(10f, FontStyle.Bold),
                    Text = "⚠  " + I18n.T("Velocidad media imposible (> 350 km/h). El viaje no genera ingresos ni cuenta para los rankings."),
                    AutoSize = false
                });
                warn.Height = 78;
                body.Controls.Add(warn);
            }

            // Rango (solo en el resultado del viaje; en la vista de detalle desde Servicios se oculta)
            if (!d.History)
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
                Text = d.NextRankName == null
                    ? I18n.T("¡Rango máximo alcanzado!")
                    : string.Format(I18n.T("Siguiente: {0}  ·  {1} de {2} km"), d.NextRankName,
                                    d.ValidKm.ToString("N0", Es), d.NextRankKm.ToString("N0", Es))
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
