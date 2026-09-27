// Composición 2D del tren completo: vista lateral de cada coche del .con, a escala y en orden.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Orts.Formats.Msts;

namespace SelectOR
{
    public class CompositionDialog : Form
    {
        readonly string _conPath;
        readonly string _contentPath;
        readonly string _title;
        readonly Func<string, bool, Color?> _classify;   // (nombre .eng/.wag, es tracción) → color de resaltado, o null
        Label _status;
        Panel _scroll;
        Panel _legend;   // leyenda de colores (solo cuando hay clasificación: Flota/Compra)
        PictureBox _pic;

        const float Ppm = 24f;          // píxeles por metro
        const float WorldHeight = 5.6f; // altura de encuadre (m), igual para todos

        /// <param name="classify">Opcional: clasifica cada coche para resaltarlo (p. ej. en Flota →
        /// verde si la empresa ya tiene esa máquina, rojo si haría falta comprarla, null/gris para
        /// vagones). Si se omite, la composición se ve igual que siempre (sin resaltar nada).</param>
        public CompositionDialog(string conPath, string contentPath, string title, Func<string, bool, Color?> classify = null)
        {
            _conPath = conPath; _contentPath = contentPath; _title = title; _classify = classify;
            Text = I18n.T("Composición del tren");
            BackColor = Theme.Bg; ForeColor = Theme.Text;
            Font = Theme.Font(9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1180, 420);
            MinimumSize = new Size(700, 320);
            try { Icon = Icon.ExtractAssociatedIcon((Environment.ProcessPath ?? AppContext.BaseDirectory)); } catch { }

            var stripe = new LiveryStripe { Dock = DockStyle.Top };
            var header = new Label { Text = "  " + (_title ?? "Composición"), Dock = DockStyle.Top, Height = 40, Font = Theme.Font(13f, FontStyle.Bold), ForeColor = Theme.Text, TextAlign = ContentAlignment.MiddleLeft, BackColor = Theme.Surface };
            _status = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Theme.Subtle, Padding = new Padding(16, 4, 0, 0), Text = I18n.T("Generando composición…") };

            _legend = new Panel { Dock = DockStyle.Top, Height = _classify != null ? 28 : 0, BackColor = Theme.Bg, Visible = _classify != null };
            _legend.Paint += DrawLegend;

            _scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Theme.Surface, Padding = new Padding(16) };
            _pic = new PictureBox { SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Color.Transparent, Location = new Point(16, 16) };
            _scroll.Controls.Add(_pic);

            Controls.Add(_scroll);
            Controls.Add(_legend);
            Controls.Add(_status);
            Controls.Add(header);
            Controls.Add(stripe);

            Load += (s, e) => Generate();
        }

        // Leyenda de colores con las MISMAS tintas que las franjas de la composición:
        // verde = ya la tienes, rojo = falta comprarla, gris = vagón.
        void DrawLegend(object sender, PaintEventArgs e)
        {
            if (_classify == null) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var items = new (Color c, string t)[]
            {
                (Theme.Accent,                      I18n.T("ya la tienes")),
                (Color.FromArgb(229, 115, 115),     I18n.T("falta comprarla")),
                (Theme.Subtle,                      I18n.T("vagón")),
            };
            using var f = Theme.Font(9f);
            int x = 16, cy = _legend.Height / 2;
            foreach (var (c, t) in items)
            {
                var box = new Rectangle(x, cy - 6, 13, 13);
                using (var br = new SolidBrush(Color.FromArgb(70, c))) g.FillRectangle(br, box);
                using (var pen = new Pen(c, 1.6f)) g.DrawRectangle(pen, box);
                x += box.Width + 6;
                var sz = TextRenderer.MeasureText(g, t, f, Size.Empty, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, t, f, new Point(x, cy - sz.Height / 2), Theme.Text, TextFormatFlags.NoPadding);
                x += sz.Width + 22;
            }
        }

        class Car { public string Name; public ShapeGeom Geom; public bool Flip; public Color? Highlight; }

        void Generate()
        {
            var conPath = _conPath; var content = _contentPath;
            Task.Run(() =>
            {
                var cars = new List<Car>();
                int missing = 0;
                try
                {
                    if (conPath != null && conPath.EndsWith(".eng", StringComparison.OrdinalIgnoreCase))
                    {
                        // Un solo vehículo (p. ej. locomotora de la flota): renderiza ese .eng directamente.
                        string name = System.IO.Path.GetFileNameWithoutExtension(conPath);
                        var geom = SafeBuild(conPath);
                        if (geom == null) missing++;
                        else
                        {
                            Color? hi = null; try { hi = _classify?.Invoke(name, true); } catch { }
                            cars.Add(new Car { Name = name, Geom = geom, Flip = false, Highlight = hi });
                        }
                    }
                    else
                    {
                        var cf = OrCompat.OpenConsist(conPath);
                        var trainset = System.IO.Path.Combine(content, "TRAINS", "TRAINSET");
                        foreach (var w in cf.Train.TrainCfg.WagonList)
                        {
                            string model = FindModel(trainset, w.Folder, w.Name, w.IsEngine);
                            ShapeGeom geom = model != null ? SafeBuild(model) : null;
                            if (geom == null) { missing++; continue; }
                            Color? hi = null;
                            try { hi = _classify?.Invoke(w.Name, w.IsEngine); } catch { }
                            cars.Add(new Car { Name = w.Name, Geom = geom, Flip = w.Flip, Highlight = hi });
                        }
                    }
                }
                catch { }
                if (!IsHandleCreated) return;
                BeginInvoke((Action)(() => Compose(cars, missing)));
            });
        }

        void OnScrollResize(object s, EventArgs e) => CenterPic();

        void CenterPic()
        {
            if (_pic.Image == null) return;
            int cw = _scroll.ClientSize.Width, ch = _scroll.ClientSize.Height;
            int x = _pic.Width < cw ? (cw - _pic.Width) / 2 : 16;
            int y = _pic.Height < ch ? (ch - _pic.Height) / 2 : 16;
            _pic.Location = new Point(x, y);
        }

        static string FindModel(string trainset, string folder, string name, bool isEngine)
        {
            try
            {
                var dir = System.IO.Path.Combine(trainset, folder);
                var exts = isEngine ? new[] { ".eng", ".wag" } : new[] { ".wag", ".eng" };
                foreach (var ext in exts)
                {
                    var p = System.IO.Path.Combine(dir, name + ext);
                    if (File.Exists(p)) return p;
                }
            }
            catch { }
            return null;
        }

        static ShapeGeom SafeBuild(string model)
        {
            try { return ShapeRenderer.BuildGeometry(model); } catch { return null; }
        }

        void Compose(List<Car> cars, int missing)
        {
            if (cars.Count == 0) { _status.Text = I18n.T("No se pudo generar la composición (modelos no encontrados)."); return; }

            int gap = 4;
            int h = (int)(WorldHeight * Ppm);
            var slots = new List<(Bitmap bmp, Color? hi)>();
            float totalLen = 0;
            int n = cars.Count;
            // ---- Orientación 2D = misma regla que Open Rails (fuente fiable) ----
            // OR coloca cada coche así (Train.cs, CalculatePositionOfCars):
            //     matrix = Identity; if (car.Flipped) rotar 180° en Y;  car.Flipped = wagon.Flip.
            // Es decir, OR NO usa ninguna heurística de geometría/textura: solo gira 180° el vehículo
            // si su flag Flip está activo (asume que el modelo está hecho con +Z hacia delante; los
            // modelos "al revés" salen igual de girados que en el simulador). Reproducimos exactamente eso:
            //   rotate = Flip XOR K,  con K constante de nuestro sistema de coordenadas (Z negada + vista
            //   desde +X en RenderSide) que hace que la cabeza (coche 0 del consist) quede a la IZQUIERDA.
            const bool K = true;
            for (int i = 0; i < n; i++)
            {
                var car = cars[i];
                bool rotate = car.Flip ^ K;
                var bmp = ShapeRenderer.RenderSide(car.Geom, Ppm, WorldHeight, rotate);
                if (bmp != null) { slots.Add((bmp, car.Highlight)); totalLen += (car.Geom.Max.Z - car.Geom.Min.Z); }
            }
            if (slots.Count == 0) { _status.Text = I18n.T("No se pudo renderizar la composición."); return; }

            int totalW = slots.Sum(s => s.bmp.Width) + gap * (slots.Count - 1);
            var composite = new Bitmap(Math.Max(totalW, 1), h);
            using (var g = Graphics.FromImage(composite))
            {
                g.Clear(Color.Transparent);
                // línea de carril
                using (var pen = new Pen(Color.FromArgb(90, Theme.Subtle), 1.5f))
                    g.DrawLine(pen, 0, h - 2, totalW, h - 2);
                int x = 0;
                foreach (var s in slots)
                {
                    if (s.hi.HasValue)
                    {
                        // Franja de fondo tintada (verde = ya la tienes, rojo = falta comprarla,
                        // gris = vagón/coche sin tracción) detrás de la silueta del coche.
                        var band = new Rectangle(x, 2, s.bmp.Width, h - 4);
                        using (var br = new SolidBrush(Color.FromArgb(70, s.hi.Value))) g.FillRectangle(br, band);
                        using (var pen = new Pen(s.hi.Value, 2.5f)) g.DrawLine(pen, x, h - 3, x + s.bmp.Width, h - 3);
                    }
                    g.DrawImage(s.bmp, x, h - s.bmp.Height); // alinear al carril (abajo)
                    x += s.bmp.Width + gap;
                    s.bmp.Dispose();
                }
            }
            _pic.Image = composite;
            _status.Text = string.Format(I18n.T("{0} vehículos · longitud ≈ {1} m"), cars.Count, totalLen.ToString("F0"))
                + (missing > 0 ? string.Format(I18n.T("  ({0} sin modelo)"), missing) : "");
            _legend?.Invalidate();
            // La ventana no cambia de tamaño ni de posición: si la composición es más ancha, se desplaza con la barra.
            CenterPic();
            _scroll.Resize -= OnScrollResize; _scroll.Resize += OnScrollResize;
        }
    }
}
