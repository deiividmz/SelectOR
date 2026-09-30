// Aviso de nueva versión: versión actual → nueva, fecha, novedades en lista y, al pulsar «Descargar e
// instalar», el progreso por fases (descarga con MB, comprobación de la firma, instalación). Todo el
// trabajo va en segundo plano (Updater), así que la ventana siempre responde; la descarga se puede
// cancelar y, si la conexión se queda parada, avisa en vez de esperar para siempre.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace SelectOR
{
    public class UpdateDialog : FancyDialog
    {
        readonly ReleaseInfo _r;
        readonly Func<string> _blockReason;   // p. ej. «termina el servicio antes de actualizar»
        readonly RoundButton _ok, _later;
        readonly SlimProgress _bar;
        readonly NotesView _notes;
        Icon _appIcon;
        string _phase = "", _detail = "";
        bool _error, _busy, _installing;
        CancellationTokenSource _cts;

        /// <summary>true si la instalación terminó y la app debe cerrarse (Program arranca la nueva).</summary>
        public bool Installed { get; private set; }

        static int W => Theme.Px(580);
        static int Pad => Theme.Px(24);
        static int IconS => Theme.Px(52);
        static int HeaderH => Theme.Px(96);
        static int NotesH => Theme.Px(240);
        static int StatusH => Theme.Px(52);

        public UpdateDialog(ReleaseInfo r, Func<string> blockReason = null)
        {
            _r = r; _blockReason = blockReason;
            Text = I18n.T("Nueva versión de SelectOR");
            try { using var ic = Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? AppContext.BaseDirectory); _appIcon = new Icon(ic, IconS, IconS); } catch { }

            int yNotes = Band + Pad + HeaderH + Theme.Px(24);
            int yStatus = yNotes + NotesH + Theme.Px(12);
            int h = yStatus + StatusH + Theme.Px(8) + 38 + Pad;
            ClientSize = new Size(W, h);

            _notes = new NotesView(r.Notes) { Location = new Point(Pad, yNotes), Size = new Size(W - Pad * 2, NotesH) };
            _bar = new SlimProgress { Location = new Point(Pad, yStatus + StatusH - Theme.Px(12)), Size = new Size(W - Pad * 2, Theme.Px(10)), Visible = false };

            _ok = PrimaryButton(I18n.T("Descargar e instalar"), Theme.Px(210));
            _later = SecondaryButton(I18n.T("Más tarde"), Theme.Px(130));
            _ok.Location = new Point(W - Pad - _ok.Width, h - Pad - _ok.Height);
            _later.Location = new Point(_ok.Left - 10 - _later.Width, _ok.Top);
            _ok.Click += async (s, e) => await Install();
            _later.Click += (s, e) =>
            {
                if (_busy) { if (!_installing) _cts?.Cancel(); return; }   // durante la descarga: «Cancelar»
                DialogResult = DialogResult.Cancel; Close();
            };
            Controls.Add(_notes); Controls.Add(_bar); Controls.Add(_ok); Controls.Add(_later);

            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };   // no cerrar a mitad de la instalación
        }

        protected override bool CanEscape => !_busy;

        protected override void PaintContent(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var flL = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            int y0 = Band + Pad;

            // Icono de la app en su pastilla
            var ir = new Rectangle(Pad, y0, IconS, IconS);
            using (var p = Theme.Round(ir, 14)) using (var b = new SolidBrush(Color.FromArgb(50, Theme.Accent))) g.FillPath(b, p);
            if (_appIcon != null) { int m = Theme.Px(8); g.DrawIcon(_appIcon, new Rectangle(ir.X + m, ir.Y + m, ir.Width - 2 * m, ir.Height - 2 * m)); }

            int tx = ir.Right + Theme.Px(16), tw = W - tx - Pad;
            using (var fT = Theme.Font(15f, FontStyle.Bold))
                TextRenderer.DrawText(g, I18n.T("Nueva versión de SelectOR"), fT, new Rectangle(tx, y0, tw, Theme.Px(30)), Theme.Text, flL);

            // Versiones: actual → nueva
            using var fChip = Theme.Font(10f, FontStyle.Bold);
            int cy = y0 + Theme.Px(36), ch = Theme.Px(26);
            int x = Chip(g, tx, cy, ch, Updater.CurrentVersionText, fChip, Theme.Surface2, Theme.Subtle);
            using (var fA = Theme.Font(11f, FontStyle.Bold))
                TextRenderer.DrawText(g, "→", fA, new Rectangle(x + 6, cy, Theme.Px(22), ch), Theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            x = Chip(g, x + Theme.Px(32), cy, ch, _r.Version ?? "", fChip, Theme.Accent, Color.White);
            if (_r.PublishedAt > DateTime.MinValue)
                using (var fD = Theme.Font(9f))
                    TextRenderer.DrawText(g, string.Format(I18n.T("Publicada el {0}"), _r.PublishedAt.ToLocalTime().ToString("dd-MM-yyyy")), fD,
                        new Rectangle(x + Theme.Px(12), cy, W - x - Pad, ch), Theme.Subtle, flL);

            // «Novedades»
            using (var fN = Theme.Font(9f, FontStyle.Bold))
                TextRenderer.DrawText(g, I18n.T("NOVEDADES"), fN, new Rectangle(Pad + 2, _notes.Top - Theme.Px(22), W - Pad * 2, Theme.Px(20)), Theme.Subtle, flL);

            // Estado de la instalación (fase + detalle)
            if (!string.IsNullOrEmpty(_phase))
            {
                int sy = _notes.Bottom + Theme.Px(12);
                var col = _error ? Color.FromArgb(229, 115, 115) : Theme.Text;
                using var fP = Theme.Font(10f, FontStyle.Bold);
                using var fDt = Theme.Font(9f);
                var wrap = TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding;
                int dw = TextRenderer.MeasureText(_detail ?? "", fDt, Size.Empty, TextFormatFlags.NoPadding).Width;
                TextRenderer.DrawText(g, _phase, fP, new Rectangle(Pad + 2, sy, W - Pad * 2 - dw - 10, _bar.Visible ? Theme.Px(24) : StatusH), col, wrap);
                if (!string.IsNullOrEmpty(_detail))
                    TextRenderer.DrawText(g, _detail, fDt, new Rectangle(Pad, sy, W - Pad * 2, Theme.Px(22)), Theme.Subtle,
                        TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        static int Chip(Graphics g, int x, int y, int h, string text, Font f, Color bg, Color fg)
        {
            int w = TextRenderer.MeasureText(text, f, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.Px(22);
            var r = new Rectangle(x, y, w, h);
            Theme.FillRound(g, r, h / 2, bg);
            TextRenderer.DrawText(g, text, f, r, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            return r.Right;
        }

        void SetStatus(string phase, string detail = "", bool error = false)
        {
            _phase = phase ?? ""; _detail = detail ?? ""; _error = error;
            Invalidate();
        }

        static string Mb(long b) => (b / 1048576.0).ToString(b < 10 * 1048576 ? "0.0" : "0", I18n.English ? System.Globalization.CultureInfo.InvariantCulture : new System.Globalization.CultureInfo("es-ES"));

        async System.Threading.Tasks.Task Install()
        {
            if (_busy) return;
            string block = _blockReason?.Invoke();
            if (!string.IsNullOrEmpty(block)) { SetStatus(block, "", true); return; }

            _busy = true; _installing = false; _error = false;
            _ok.Enabled = false; _later.Text = I18n.T("Cancelar"); _later.Invalidate();
            _bar.Visible = true; _bar.Indeterminate = true; _bar.Value = 0;
            SetStatus(I18n.T("Conectando…"));
            _cts = new CancellationTokenSource();
            var prog = new Progress<UpdateStep>(st =>
            {
                if (IsDisposed) return;
                switch (st.Phase)
                {
                    case UpdatePhase.Downloading:
                        if (st.Total > 0)
                        {
                            _bar.Indeterminate = false; _bar.Value = st.Got / (double)st.Total;
                            SetStatus(string.Format(I18n.T("Descargando… {0} %"), (int)Math.Round(100.0 * st.Got / st.Total)),
                                string.Format(I18n.T("{0} de {1} MB"), Mb(st.Got), Mb(st.Total)));
                        }
                        else if (st.Got > 0) SetStatus(I18n.T("Descargando…"), Mb(st.Got) + " MB");
                        break;
                    case UpdatePhase.Verifying:
                        _installing = true; _later.Enabled = false; _later.Text = I18n.T("Más tarde"); _later.Invalidate();
                        _bar.Indeterminate = true;
                        SetStatus(I18n.T("Comprobando la firma del paquete…"));
                        break;
                    case UpdatePhase.Installing:
                        _bar.Indeterminate = true;
                        SetStatus(I18n.T("Instalando…"));
                        break;
                    case UpdatePhase.Done:
                        _bar.Indeterminate = false; _bar.Value = 1;
                        SetStatus(I18n.T("Actualización instalada. Reiniciando SelectOR…"));
                        break;
                }
            });

            string err;
            try { err = await Updater.DownloadAndInstallAsync(_r, prog, _cts.Token); }
            catch (Exception e) { err = I18n.T("No se pudo instalar la actualización: ") + e.Message; }
            _busy = false;
            try { _cts.Dispose(); } catch { }
            _cts = null;
            if (IsDisposed) return;
            if (err != null)
            {
                _bar.Indeterminate = false; _bar.Visible = false;
                SetStatus(err, "", true);
                _ok.Enabled = true; _later.Enabled = true; _later.Text = I18n.T("Más tarde"); _later.Invalidate();
                _ok.Text = I18n.T("Reintentar"); _ok.Invalidate();
                return;
            }
            Installed = true;
            DialogResult = DialogResult.OK;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _appIcon?.Dispose(); } catch { }
            base.OnFormClosed(e);
        }

        // ---- Novedades: lista con viñetas y cabeceras de versión (con desplazamiento) ----
        sealed class NotesView : Panel
        {
            readonly NotesCanvas _c;
            public NotesView(string notes)
            {
                BackColor = Theme.Surface2; AutoScroll = true;
                Padding = new Padding(0);
                _c = new NotesCanvas(notes) { Location = new Point(0, 0) };
                Controls.Add(_c);
            }
            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                if (Width <= 0) return;
                using var p = Theme.Round(new Rectangle(0, 0, Width, Height), 10);
                Region = new Region(p);
                _c.Width = ClientSize.Width - (VerticalScroll.Visible ? 0 : 0);
                _c.Relayout(ClientSize.Width);
            }
        }

        sealed class NotesCanvas : Control
        {
            readonly List<(int kind, string text)> _lines = new();   // 0 texto · 1 viñeta · 2 cabecera de versión · 3 hueco
            readonly List<(int kind, string text, Rectangle r)> _lay = new();
            static int P => Theme.Px(14);

            public NotesCanvas(string notes)
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                BackColor = Theme.Surface2;
                string t = string.IsNullOrWhiteSpace(notes) ? I18n.T("Mejoras y correcciones.") : notes.Replace("\r\n", "\n");
                foreach (var raw in t.Split('\n'))
                {
                    string l = raw.TrimEnd();
                    string s = l.TrimStart();
                    if (s.Length == 0) { _lines.Add((3, "")); continue; }
                    if (s.StartsWith("▸")) { _lines.Add((2, s.TrimStart('▸').Trim())); continue; }
                    if (s.StartsWith("- ") || s.StartsWith("• ") || s.StartsWith("* ") || s.StartsWith("· ")) { _lines.Add((1, s.Substring(2).Trim())); continue; }
                    _lines.Add((0, s));
                }
            }

            public void Relayout(int width)
            {
                _lay.Clear();
                int y = P, w = Math.Max(50, width - P * 2 - SystemInformation.VerticalScrollBarWidth);
                using var fB = Theme.Font(9.75f);
                using var fH = Theme.Font(10f, FontStyle.Bold);
                var fl = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                int bullet = Theme.Px(18);
                foreach (var (kind, text) in _lines)
                {
                    if (kind == 3) { y += Theme.Px(6); continue; }
                    var f = kind == 2 ? fH : fB;
                    int indent = kind == 1 ? bullet : 0;
                    if (kind == 2 && _lay.Count > 0) y += Theme.Px(6);
                    int h = TextRenderer.MeasureText(text, f, new Size(w - indent, 9999), fl).Height;
                    _lay.Add((kind, text, new Rectangle(P + indent, y, w - indent, h)));
                    y += h + Theme.Px(kind == 2 ? 6 : 5);
                }
                Height = y + P;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Theme.Surface2)) g.FillRectangle(b, ClientRectangle);
                using var fB = Theme.Font(9.75f);
                using var fH = Theme.Font(10f, FontStyle.Bold);
                var fl = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
                foreach (var (kind, text, r) in _lay)
                {
                    if (kind == 1)
                    {
                        int d = Theme.Px(6);
                        using var b = new SolidBrush(Theme.Accent);
                        g.FillEllipse(b, r.X - Theme.Px(14), r.Y + Theme.Px(7), d, d);
                    }
                    TextRenderer.DrawText(g, text, kind == 2 ? fH : fB, r, kind == 2 ? Theme.AccentHi : Theme.Text, fl);
                }
            }
        }
    }
}
