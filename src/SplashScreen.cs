// Pantalla de inicio: se abre antes que nada y no se va hasta que el contenido está leído
// (rutas, trenes .con, máquinas .eng, formaciones fijas y el editor de composiciones).
//
// Vive en su PROPIO hilo con su propio bucle de mensajes: mientras el hilo principal construye la
// interfaz y lee el contenido —tareas que lo dejan ocupado— esta ventana sigue repintándose y la
// barra de progreso avanza con suavidad en vez de quedarse congelada.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed class SplashScreen : Form
    {
        static SplashScreen _instance;
        static Thread _thread;
        static readonly object _lock = new();

        int _target;            // porcentaje ya completado
        int _creepTo;           // final del paso en curso: la barra se acerca despacio mientras dura
        float _shown;           // porcentaje dibujado (persigue a los anteriores)
        string _step = "";
        readonly System.Windows.Forms.Timer _timer;

        /// <summary>Abre la pantalla de inicio en su propio hilo.</summary>
        public static void Begin()
        {
            lock (_lock)
            {
                if (_thread != null) return;
                var ready = new ManualResetEventSlim(false);
                _thread = new Thread(() =>
                {
                    try
                    {
                        var s = new SplashScreen();
                        lock (_lock) _instance = s;
                        ready.Set();
                        Application.Run(s);
                    }
                    catch { ready.Set(); }
                    finally { lock (_lock) _instance = null; }
                });
                _thread.IsBackground = true;
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait(3000);
            }
        }

        /// <summary>Avance de la carga: porcentaje (0-100) y qué se está leyendo (texto en español;
        /// se traduce al pintar, porque el idioma se conoce un instante después de abrir esta ventana).</summary>
        public static void Report(int percent, int creepTo, string step)
        {
            SplashScreen s;
            lock (_lock) s = _instance;
            if (s == null) return;
            try
            {
                if (!s.IsHandleCreated) return;
                s.BeginInvoke((Action)(() =>
                {
                    s._target = Math.Max(s._target, Math.Max(0, Math.Min(100, percent)));
                    s._creepTo = Math.Max(s._target, Math.Min(100, creepTo));
                    if (!string.IsNullOrEmpty(step)) s._step = step;
                    s.Invalidate();
                }));
            }
            catch { }
        }

        /// <summary>Cierra la pantalla: la barra termina de llegar al 100 % y se espera a que se vaya,
        /// para que el menú aparezca justo después y no encima de ella.</summary>
        public static void Finish()
        {
            SplashScreen s; Thread t;
            lock (_lock) { s = _instance; t = _thread; }
            if (s == null) return;
            try
            {
                if (!s.IsHandleCreated) return;
                s.BeginInvoke((Action)(() => { s._target = 100; s._creepTo = 100; s._closing = true; s.Invalidate(); }));
            }
            catch { }
            try { t?.Join(1200); } catch { }
        }

        bool _closing;

        // ---- Elegir el contenido antes de cargar ----
        // Con varias carpetas de contenido configuradas en Open Rails, la pantalla de inicio pregunta
        // cuál cargar ANTES de leer nada: así se carga directamente la elegida (y no primero otra).
        bool _asking;
        ThemeCombo _askCombo;
        RoundButton _askBtn;

        /// <summary>Muestra en la pantalla de inicio un desplegable con las carpetas de contenido y
        /// devuelve el índice elegido. Si la pantalla no está disponible, devuelve <paramref name="preselect"/>.</summary>
        public static async System.Threading.Tasks.Task<int> AskFolder(string[] names, int preselect)
        {
            if (names == null || names.Length == 0) return preselect;
            // La ventana puede estar aún creándose en su hilo: se espera un momento a que exista.
            SplashScreen s = null;
            for (int i = 0; i < 50; i++)
            {
                lock (_lock) s = _instance;
                if (s != null && s.IsHandleCreated) break;
                await System.Threading.Tasks.Task.Delay(100);
            }
            if (s == null || !s.IsHandleCreated) return preselect;
            var tcs = new System.Threading.Tasks.TaskCompletionSource<int>();
            try { s.BeginInvoke((Action)(() => s.ShowFolderChooser(names, preselect, tcs))); }
            catch { return preselect; }
            return await tcs.Task;
        }

        void ShowFolderChooser(string[] names, int preselect, System.Threading.Tasks.TaskCompletionSource<int> tcs)
        {
            _asking = true;
            int pad = Theme.Px(46), btnW = Theme.Px(120), gap = Theme.Px(10), y = Height - Theme.Px(78);
            _askCombo = new ThemeCombo { DropDownStyle = ComboBoxStyle.DropDownList, Width = Width - pad * 2 - btnW - gap, DropDownHeight = Theme.Px(240) };
            // Mismo aspecto que el desplegable «Contenido» del menú (oscuro, elegido en verde).
            _askCombo.BackColor = Theme.Surface2; _askCombo.ForeColor = Theme.Text;
            _askCombo.ItemHeight = Theme.Px(22); _askCombo.Font = Theme.Font(10f);
            _askCombo.DrawItem += (o, e) =>
            {
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
                if (e.Index < 0) return;
                var r = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, _askCombo.GetItemText(_askCombo.Items[e.Index]), _askCombo.Font, r, sel ? Color.White : Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            try { Native.UseDarkScrollBars(_askCombo); } catch { }
            foreach (var n in names) _askCombo.Items.Add(n);
            _askCombo.SelectedIndex = Math.Max(0, Math.Min(names.Length - 1, preselect));
            _askCombo.Location = new Point(pad, y);
            _askBtn = new RoundButton
            {
                Text = I18n.T("Cargar"), Radius = 8, Width = btnW, Height = Math.Max(_askCombo.Height, Theme.Px(30)),
                BaseColor = Theme.Accent, HoverColor = Theme.AccentHi, TextColor = Color.White, FontSize = 10f, FontStyle = FontStyle.Bold,
                Location = new Point(Width - pad - btnW, y - Math.Max(0, (Math.Max(_askCombo.Height, Theme.Px(30)) - _askCombo.Height) / 2))
            };
            void Done()
            {
                if (!_asking) return;
                int i = _askCombo.SelectedIndex;
                _asking = false;
                Controls.Remove(_askCombo); Controls.Remove(_askBtn);
                _askCombo.Dispose(); _askBtn.Dispose();
                Invalidate();
                tcs.TrySetResult(i < 0 ? preselect : i);
            }
            _askBtn.Click += (s, e) => Done();
            _askCombo.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Done(); } };
            Controls.Add(_askCombo); Controls.Add(_askBtn);
            Invalidate();
            try { Activate(); _askCombo.Focus(); } catch { }
        }

        SplashScreen()
        {
            Text = "SelectOR";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;    // mientras carga, en la barra de tareas solo está esta ventana
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); } catch { }
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

            int w = Theme.Px(520), h = Theme.Px(300);
            Size = new Size(w, h);
            var area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2);
            try { using var path = Theme.Round(new Rectangle(0, 0, w, h), Theme.Px(16)); Region = new Region(path); } catch { }

            _step = "Preparando SelectOR…";
            _timer = new System.Windows.Forms.Timer { Interval = 16 };
            _timer.Tick += (s, e) => Animate();
            _timer.Start();
        }

        // La barra persigue al valor pedido; cuando lo alcanza y el paso en curso sigue trabajando,
        // se acerca muy despacio a su final (sin llegar) para que nunca parezca detenida.
        void Animate()
        {
            float diff = _target - _shown;
            if (Math.Abs(diff) > 0.05f)
            {
                _shown += Math.Max(0.35f, Math.Abs(diff) * 0.12f) * Math.Sign(diff);
                if ((diff > 0 && _shown > _target) || (diff < 0 && _shown < _target)) _shown = _target;
                Invalidate();
            }
            else if (_closing)
            {
                _timer.Stop();
                Close();
            }
            else if (_creepTo > _target)
            {
                float techo = _target + (_creepTo - _target) * 0.85f;
                if (_shown < techo) { _shown = Math.Min(techo, _shown + 0.06f); Invalidate(); }
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var r = ClientRectangle;

            using (var b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, r);
            using (var path = Theme.Round(new Rectangle(0, 0, r.Width - 1, r.Height - 1), Theme.Px(16)))
            using (var pen = new Pen(Theme.Border, 1.4f)) g.DrawPath(pen, path);

            // marca: placa verde + «SelectOR»
            int plate = Theme.Px(84);
            var box = new Rectangle((r.Width - plate) / 2, Theme.Px(38), plate, plate);
            SelectorLogo.DrawRing(g, box);

            using (var f = Theme.Font(26f, FontStyle.Bold))
            {
                var wSel = TextRenderer.MeasureText(g, "Select", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                var wOr = TextRenderer.MeasureText(g, "OR", f, Size.Empty, TextFormatFlags.NoPadding).Width;
                int x = (r.Width - (wSel + wOr)) / 2, y = box.Bottom + Theme.Px(16);
                TextRenderer.DrawText(g, "Select", f, new Point(x, y), Theme.Text, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "OR", f, new Point(x + wSel, y), Theme.Accent, TextFormatFlags.NoPadding);
            }

            if (_asking)
            {
                // Mientras se elige el contenido no hay barra: solo la pregunta (el desplegable va debajo).
                using var fq = Theme.Font(10f, FontStyle.Bold);
                int pq = Theme.Px(46);
                TextRenderer.DrawText(g, I18n.T("¿Qué contenido quieres cargar?"), fq,
                    new Rectangle(pq, r.Height - Theme.Px(112), r.Width - pq * 2, Theme.Px(24)), Theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            // barra de progreso
            int pad = Theme.Px(46), barH = Theme.Px(8);
            int barY = r.Height - Theme.Px(62);
            var track = new Rectangle(pad, barY, r.Width - pad * 2, barH);
            Theme.FillRound(g, track, barH / 2, Theme.Surface2);
            int fill = (int)Math.Round(track.Width * Math.Max(0f, Math.Min(100f, _shown)) / 100f);
            if (fill > 2)
            {
                var fr = new Rectangle(track.X, track.Y, fill, track.Height);
                using var path = Theme.Round(fr, barH / 2);
                using var lg = new LinearGradientBrush(new Rectangle(track.X, track.Y, Math.Max(track.Width, 2), track.Height),
                                                       Theme.AccentHi, Theme.Accent2, LinearGradientMode.Horizontal);
                g.FillPath(lg, path);
            }

            // paso actual (izquierda) y porcentaje (derecha)
            using (var f = Theme.Font(9f))
            {
                int y = track.Bottom + Theme.Px(10);
                string pct = (int)Math.Round(_shown) + " %";
                var wPct = TextRenderer.MeasureText(g, pct, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, I18n.T(_step ?? ""), f,
                    new Rectangle(track.X, y, track.Width - wPct - Theme.Px(10), Theme.Px(18)), Theme.Subtle,
                    TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, pct, f, new Point(track.Right - wPct, y), Theme.Text, TextFormatFlags.NoPadding);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _timer?.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        // Sin parpadeo al aparecer.
        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x02000000; /* WS_EX_COMPOSITED */ return cp; }
        }
    }
}
