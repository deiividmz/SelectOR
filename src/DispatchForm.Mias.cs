// PUESTO DE MANDO: «Solo las mías» (petición 2026-11-08). Con el mando de jefe de circulación, un botón encima de la lista
// de dependencias la deja solo con las tuyas (y el plano general y el tablero se acercan a ellas). Se recuerda para la
// próxima vez (MineOnlyGet/MineOnlySet: las preferencias de SelectOR). Sin ninguna tuya, la lista entera.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        public Func<bool> MineOnlyGet;     // (MainMenuForm) lo que se eligió la última vez
        public Action<bool> MineOnlySet;
        Label _mineBtn;
        bool _mineOnly, _mineLoaded;
        string _mineShown = "";            // las dependencias tuyas con las que se hizo la lista (para rehacerla si cambian)

        void BuildMineBtn(Panel side)
        {
            _mineBtn = new Label
            {
                Dock = DockStyle.Top, Height = Theme.Px(28), TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand,
                Font = Theme.Font(8.75f, FontStyle.Bold), Margin = Padding.Empty, Visible = false
            };
            _mineBtn.Click += (s, e) =>
            {
                _mineOnly = !_mineOnly;
                try { MineOnlySet?.Invoke(_mineOnly); } catch { }
                _mineShown = null;
                MineRefresh();
                if (_mineOnly && Overview) FitMine();
            };
            new ToolTip().SetToolTip(_mineBtn, Tr("Deja en la lista solo las dependencias que tienes con el mando, y acerca el plano a ellas."));
            side.Controls.Add(_mineBtn);
        }

        List<string> MyZones() => _zones == null || Me == null ? new List<string>() : _zones.Where(z => Mine(z.Name)).Select(z => z.Name).ToList();

        // (cada segundo y al tomar o dejar el mando) el botón y, si cambian tus dependencias, la lista
        void MineRefresh()
        {
            if (_mineBtn == null || _zones == null) return;
            if (!_mineLoaded) { _mineLoaded = true; try { _mineOnly = MineOnlyGet?.Invoke() ?? false; } catch { } }   // (MainMenuForm lo pone después de crear la ventana)
            var mine = MyZones();
            _mineBtn.Visible = mine.Count > 0 && !_folded;
            bool on = _mineOnly && mine.Count > 0;
            _mineBtn.Text = on ? "✓ " + Tr("Solo las mías") + " (" + mine.Count + ")" : Tr("Solo las mías");
            _mineBtn.BackColor = on ? Color.FromArgb(30, 90, 45) : Theme.Surface2;
            _mineBtn.ForeColor = on ? Color.FromArgb(220, 245, 225) : Theme.Text;
            string key = on ? string.Join("|", mine) : "*";
            if (key == _mineShown) return;
            bool first = _mineShown == null || _mineShown == "";
            _mineShown = key;
            string sel = _list.SelectedItem as string;
            FillList(on, mine);   // (2026-11-09) con las bandas (DispatchForm.Bandas.cs)
            if (sel != null && _list.Items.Contains(sel)) { int i = _list.Items.IndexOf(sel); if (_list.SelectedIndex != i) { _filling = true; try { _list.SelectedIndex = i; } finally { _filling = false; } } }
            else if (sel != null) Select(General);
            if (on && Overview && !first) FitMine();
        }

        // el plano general (o el tablero) con tus dependencias a la vista
        void FitMine()
        {
            var mine = MyZones();
            if (mine.Count == 0 || _canvas.Width < 50) return;
            if (Board && _board != null)
            {
                RectangleF u = RectangleF.Empty;
                foreach (var zone in mine)
                {
                    RectangleF r = RectangleF.Empty;
                    var st = _board.Stations.FirstOrDefault(s => s.name == zone);
                    if (st.name != null) r = RectangleF.Inflate(st.box, 220, 160);
                    else if (_zones?.FirstOrDefault(z => z.Name == zone) is { Anchors.Count: > 0 } z)
                    {
                        var pts = z.Anchors.Select(a => _board.W(a.x, a.y)).ToList();
                        r = RectangleF.FromLTRB(pts.Min(q => q.X), pts.Min(q => q.Y), pts.Max(q => q.X), pts.Max(q => q.Y)); r.Inflate(150, 150);
                    }
                    if (!r.IsEmpty) u = u.IsEmpty ? r : RectangleF.Union(u, r);
                }
                if (!u.IsEmpty) CenterOn(u);
                return;
            }
            if (_g == null) return;
            var xs = new List<(double x, double y)>();
            foreach (var zone in mine)
            {
                if (_stations != null) foreach (var st in _stations.Where(s => s.name == zone)) xs.Add((st.x, st.y));
                if (_zones?.FirstOrDefault(z => z.Name == zone) is { Anchors.Count: > 0 } zz) xs.AddRange(zz.Anchors);
            }
            if (xs.Count == 0) return;
            double x0 = xs.Min(q => q.x), x1 = xs.Max(q => q.x), y0 = xs.Min(q => q.y), y1 = xs.Max(q => q.y);
            double w = Math.Max(800, x1 - x0), h = Math.Max(600, y1 - y0), cx = (x0 + x1) / 2, cy = (y0 + y1) / 2;
            float pad = Theme.Px(80);
            _ovZoom = (float)Math.Min((_canvas.Width - 2 * pad) / w, (_canvas.Height - 2 * pad) / h);
            _ovOrigin = new PointF((float)(_canvas.Width / 2.0 - cx * _ovZoom), (float)(_canvas.Height / 2.0 - cy * _ovZoom));
            _ovFitted = true; _canvas.Invalidate();
        }
    }
}
