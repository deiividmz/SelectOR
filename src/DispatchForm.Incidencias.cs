// (2026-11-09) Avisos de incidencias en la vía (los crea el administrador desde Objetos: ObjetosSalaForm.Avisos.cs y
// sql/avisos-sala.sql). Cada uno, un cuadro arriba a la derecha (debajo de los de rebases y Tab), con su nivel, el texto y
// el lugar (el jefe de circulación se lo dice a sus maquinistas por radio); «Ocultar» lo quita de la vista hasta que cambie. Si tiene
// sitio en el mapa, un ⚠ en el trazado y un clic en el cuadro lleva allí. Se leen cada 15 s.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        List<ObjetosSalaForm.RoomAlert> _alerts = new();
        DateTime _alertNext = DateTime.MinValue; bool _alertBusy, _alertFirst = true;
        readonly Dictionary<long, long> _alertHidden = new();             // id → versión ocultada
        readonly Dictionary<long, DateTime> _alertNew = new();            // los recién llegados (parpadean 20 s)
        readonly List<(RectangleF box, RectangleF say, RectangleF hide, ObjetosSalaForm.RoomAlert a)> _alertHit = new();
        RectangleF _alertShowAll;

        void AlertsTick()
        {
            if (_alertBusy || DateTime.UtcNow < _alertNext) return;
            _alertBusy = true; _alertNext = DateTime.UtcNow.AddSeconds(15);
            _ = Poll();
            async Task Poll()
            {
                try
                {
                    var (json, err) = await Supa.RpcAsync("room_alerts_get", new { p_route = ObjectsRoute });
                    if (err != null || json == null || IsDisposed) return;
                    var l = ObjetosSalaForm.ParseAlerts(json);
                    bool fresh = false, grave = false;
                    foreach (var a in l)
                        if (!_alerts.Any(o => o.Id == a.Id && o.At == a.At)) { if (!_alertFirst) { _alertNew[a.Id] = DateTime.UtcNow; fresh = true; grave |= a.Level == "grave"; } }
                    _alerts = l; _alertFirst = false;
                    if (fresh) IncidentSound(grave);   // (2026-11-09) su propio sonido, no el de Windows
                    _canvas.Invalidate();
                }
                catch { }
                finally { _alertBusy = false; }
            }
        }

        // (2026-11-09) el sonido de un aviso nuevo: un gong de tres notas que bajan (como la megafonía de una estación), con
        // armónicos de campana; si es grave, dos veces. Hecho aquí mismo, sin archivos (como el «din-don» y la alarma de rebase)
        static byte[] _incSound, _incSoundGrave;
        static void IncidentSound(bool grave)
        {
            try
            {
                if (_incSound == null)
                {
                    const int rate = 22050;
                    var one = new List<short>();
                    foreach (var (hz, sec) in new[] { (783.99, 0.42), (659.25, 0.42), (523.25, 0.95) })   // sol, mi, do
                    {
                        int n = (int)(rate * sec);
                        for (int i = 0; i < n; i++)
                        {
                            double t = (double)i / rate, env = Math.Min(1, i / 90.0) * Math.Exp(-t * (sec > 0.5 ? 2.6 : 3.4));
                            double v = Math.Sin(2 * Math.PI * hz * t) + 0.42 * Math.Sin(2 * Math.PI * hz * 2.0 * t) * Math.Exp(-t * 3)
                                     + 0.22 * Math.Sin(2 * Math.PI * hz * 3.01 * t) * Math.Exp(-t * 5) + 0.12 * Math.Sin(2 * Math.PI * hz * 4.2 * t) * Math.Exp(-t * 8);
                            one.Add((short)(v / 1.76 * env * 12000));
                        }
                    }
                    var two = new List<short>(one); for (int i = 0; i < rate * 0.35; i++) two.Add(0); two.AddRange(one);
                    _incSound = Wav(one, rate); _incSoundGrave = Wav(two, rate);
                }
                new System.Media.SoundPlayer(new System.IO.MemoryStream(grave ? _incSoundGrave : _incSound)).Play();
            }
            catch { }
        }
        static byte[] Wav(List<short> samples, int rate)
        {
            using var ms = new System.IO.MemoryStream(); using var w = new System.IO.BinaryWriter(ms);
            int bytes = samples.Count * 2;
            w.Write("RIFF".ToCharArray()); w.Write(36 + bytes); w.Write("WAVE".ToCharArray());
            w.Write("fmt ".ToCharArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write("data".ToCharArray()); w.Write(bytes); foreach (var v in samples) w.Write(v);
            return ms.ToArray();
        }

        PointF? AlertAt(ObjetosSalaForm.RoomAlert a)
        {
            if (a.Lat == null || a.Lon == null) return null;
            try
            {
                if (Board) { var (x, y) = _board.G.Proj(a.Lat.Value, a.Lon.Value); var w = _board.W(x, y); return new PointF(_bOrigin.X + w.X * _bZoom, _bOrigin.Y + w.Y * _bZoom); }
                if (!Overview && _cur != null && _cur.Locate(a.Lat.Value, a.Lon.Value, out int e, out double off))
                {
                    var pc = _cur.Piece(e, off - 1, off + 1); if (pc == null || pc.Length == 0) return null;
                    var q = pc[pc.Length / 2]; return new PointF(_origin.X + q.X * _zoom, _origin.Y + q.Y * _zoom);
                }
            }
            catch { }
            return null;
        }

        // (DrawNotices, al final) y0: debajo de los cuadros de rebases y Tab
        void DrawAlerts(Graphics g, Rectangle area, float y0)
        {
            _alertHit.Clear(); _alertShowAll = RectangleF.Empty;
            if (_alerts.Count == 0) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var k in _alertHidden.Keys.Where(k => !_alerts.Any(a => a.Id == k && a.At == _alertHidden[k])).ToList()) _alertHidden.Remove(k);
            // el sitio de cada uno en el trazado
            using (var fe = new Font("Segoe UI Symbol", 11f, FontStyle.Bold))
                foreach (var a in _alerts)
                {
                    var p = AlertAt(a); if (p == null || !area.Contains(Point.Round(p.Value))) continue;
                    var c = ObjetosSalaForm.AlertColor(a.Level); float r = Theme.Px(12);
                    var tri = new[] { new PointF(p.Value.X, p.Value.Y - r - 2), new PointF(p.Value.X + r, p.Value.Y + r - 4), new PointF(p.Value.X - r, p.Value.Y + r - 4) };
                    using (var b = new SolidBrush(Color.FromArgb(235, 22, 26, 32))) g.FillPolygon(b, tri);
                    using (var pen = new Pen(c, 2.2f) { LineJoin = LineJoin.Round }) g.DrawPolygon(pen, tri);
                    using (var tb = new SolidBrush(c)) { var sz = g.MeasureString("!", fe); g.DrawString("!", fe, tb, p.Value.X - sz.Width / 2, p.Value.Y - sz.Height / 2 + 1); }
                }
            float w = Theme.Px(340), x = area.Right - w - Theme.Px(12), y = y0;
            using var f0 = Theme.Font(7.8f, FontStyle.Bold); using var f1 = Theme.Font(9f, FontStyle.Bold); using var f2 = Theme.Font(8.3f); using var fb = new Font("Segoe UI Symbol", 10f);
            var cf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            int hidden = 0;
            foreach (var a in _alerts)
            {
                if (_alertHidden.TryGetValue(a.Id, out long hv) && hv == a.At) { hidden++; continue; }
                if (y > area.Bottom - Theme.Px(80)) break;
                var col = ObjetosSalaForm.AlertColor(a.Level);
                float th = Math.Min(Theme.Px(70), g.MeasureString(a.Text, f1, (int)(w - 34)).Height + 2);
                bool hasPlace = a.Place.Length > 0;
                float h = Theme.Px(24) + th + (hasPlace ? Theme.Px(18) : 0) + Theme.Px(34);
                var box = new RectangleF(x, y, w, h);
                using (var bg = new SolidBrush(Color.FromArgb(242, 26, 31, 38))) g.FillRectangle(bg, box);
                if (_alertNew.TryGetValue(a.Id, out var at) && (DateTime.UtcNow - at).TotalSeconds < 20 && (int)((DateTime.UtcNow - at).TotalSeconds * 2) % 2 == 0)
                    using (var nb = new SolidBrush(Color.FromArgb(70, col))) g.FillRectangle(nb, box);
                using (var ab = new SolidBrush(col)) g.FillRectangle(ab, box.X, box.Y, 4, box.Height);
                using (var bp = new Pen(Color.FromArgb(150, col), 1f)) g.DrawRectangle(bp, box.X, box.Y, box.Width, box.Height);
                using (var cb = new SolidBrush(col)) g.DrawString("⚠", fb, cb, box.X + 8, box.Y + 4);
                using (var cb = new SolidBrush(col)) g.DrawString(Tr("INCIDENCIA EN LA VÍA") + " · " + ObjetosSalaForm.AlertLevelLabel(a.Level).ToUpperInvariant(), f0, cb, box.X + 30, box.Y + 7);
                using (var tb = new SolidBrush(Theme.Text)) g.DrawString(a.Text, f1, tb, new RectangleF(box.X + 30, box.Y + Theme.Px(24), box.Width - 38, th));
                if (hasPlace) using (var sb = new SolidBrush(Theme.Subtle)) g.DrawString(Tr("Lugar:") + " " + a.Place + (a.Lat != null ? "  ·  " + Tr("clic: verlo en el mapa") : ""), f2, sb, new RectangleF(box.X + 30, box.Y + Theme.Px(24) + th, box.Width - 38, Theme.Px(18)));
                float by = box.Bottom - Theme.Px(30), bh = Theme.Px(24);
                // (2026-11-09, petición) sin botón para avisar: el jefe de circulación se lo dice a sus maquinistas por radio
                var hide = new RectangleF(box.Right - Theme.Px(98), by, Theme.Px(90), bh);
                using (var sb = new SolidBrush(Color.FromArgb(170, 178, 186))) g.DrawString(Tr("Informa a tus maquinistas por radio"), f2, sb, new RectangleF(box.X + 30, by, hide.X - box.X - 36, bh), new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
                using (var b2 = new SolidBrush(Color.FromArgb(52, 58, 66))) g.FillRectangle(b2, hide);
                using (var wb = new SolidBrush(Color.FromArgb(220, 226, 232))) g.DrawString(Tr("Ocultar"), f1, wb, hide, cf);
                _alertHit.Add((box, RectangleF.Empty, hide, a));
                y += h + Theme.Px(6);
            }
            if (hidden > 0)
            {
                string t = "⚠ " + string.Format(hidden == 1 ? Tr("{0} aviso oculto · mostrar") : Tr("{0} avisos ocultos · mostrar"), hidden);
                var sz = g.MeasureString(t, f2);
                _alertShowAll = new RectangleF(area.Right - sz.Width - Theme.Px(24), y, sz.Width + Theme.Px(12), sz.Height + Theme.Px(6));
                using (var bg = new SolidBrush(Color.FromArgb(230, 26, 31, 38))) g.FillRectangle(bg, _alertShowAll);
                using (var tb = new SolidBrush(Color.FromArgb(255, 176, 32))) g.DrawString(t, f2, tb, _alertShowAll.X + Theme.Px(6), _alertShowAll.Y + Theme.Px(3));
            }
        }

        // (clic en el canvas) true si era en un aviso
        bool AlertClick(Point p)
        {
            if (_alertShowAll.Contains(p)) { _alertHidden.Clear(); _canvas.Invalidate(); return true; }
            foreach (var (box, say, hide, a) in _alertHit)
            {
                if (!box.Contains(p)) continue;
                if (hide.Contains(p)) { _alertHidden[a.Id] = a.At; _canvas.Invalidate(); return true; }
                // en el cuadro: al sitio, si lo tiene (en el trazado real)
                if (a.Lat != null && a.Lon != null && _board != null)
                {
                    try
                    {
                        if (!Overview && !Board) ShowOverview();
                        var (x, y) = _board.G.Proj(a.Lat.Value, a.Lon.Value); var w = _board.W(x, y);
                        if (Board) CenterOn(new RectangleF(w.X - 150, w.Y - 100, 300, 200), 1.2f);
                    }
                    catch { }
                }
                _canvas.Invalidate();
                return true;
            }
            return false;
        }
    }
}
