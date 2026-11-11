// (2026-11-10, petición) Las dependencias, como las estaciones: los tramos «Junto a X» y «Línea A – B» van con la estación que
// tocan (de su misma banda) y no salen sueltos en la lista. «Junto a X» es de X; «Línea A – B», de la de las dos que está más
// cerca de su mitad (por el plano). Tomar una estación toma también sus tramos.
// Y al elegir una dependencia se ve lo mismo que en la banda (el esquema de la ruta, estación a estación), encuadrado en ella
// y con lo demás apagado; mientras ese esquema se prepara, su esquema suelto.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        Dictionary<string, string> _depOf;          // zona → la dependencia de la lista que la lleva
        HashSet<Schematic> _depParts;               // viendo una dependencia en el esquema de la ruta: sus piezas
        bool _depWaiting;                           // (2026-11-10) esa dependencia espera al esquema de la ruta: mientras, su trazado real

        // la dependencia (de la lista) que lleva esta zona
        string DepOf(string z)
        {
            if (z == null || _zones == null) return z;
            if (_depOf == null) BuildDeps();
            return _depOf.TryGetValue(z, out var d) ? d : z;
        }
        // todas las zonas de una dependencia de la lista (ella y sus tramos)
        List<string> DepZones(string head)
        {
            if (head == null || _zones == null) return new List<string>();
            if (_depOf == null) BuildDeps();
            var l = _zones.Where(z => DepOf(z.Name) == head).Select(z => z.Name).ToList();
            if (!l.Contains(head)) l.Insert(0, head);
            return l;
        }
        bool IsDepHead(string z) => DepOf(z) == z;

        void BuildDeps()
        {
            _depOf = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_zones == null) return;
            var stations = new HashSet<string>(_zones.Where(z => !z.Line).Select(z => z.Name), StringComparer.Ordinal);
            var centers = new Dictionary<string, (double x, double y)>(StringComparer.Ordinal);
            if (_stations != null) foreach (var s in _stations) centers.TryAdd(s.name, (s.x, s.y));
            bool SameBand(string a, string b) => !BandsOn || BandOfZone(a) == BandOfZone(b);
            foreach (var z in _zones.Where(z => z.Line))
            {
                string to = null;
                var mj = Regex.Match(z.Name, @"^Junto a (.+?)(?: \(\d+\))?$");
                if (mj.Success)
                {
                    string x = mj.Groups[1].Value;
                    if (stations.Contains(x) && SameBand(x, z.Name)) to = x;
                }
                else
                {
                    var ml = Regex.Match(z.Name, @"^Línea (.+?) – (.+?)(?: \(\d+\))?$");
                    if (ml.Success)
                    {
                        var cand = new[] { ml.Groups[1].Value, ml.Groups[2].Value }.Where(x => stations.Contains(x) && SameBand(x, z.Name)).ToList();
                        if (cand.Count == 1) to = cand[0];
                        else if (cand.Count == 2)
                        {
                            to = cand[0];
                            if (z.Anchors != null && z.Anchors.Count > 0 && centers.TryGetValue(cand[0], out var c0) && centers.TryGetValue(cand[1], out var c1))
                            {
                                double mx = z.Anchors.Average(p => p.x), my = z.Anchors.Average(p => p.y);
                                double d0 = (c0.x - mx) * (c0.x - mx) + (c0.y - my) * (c0.y - my), d1 = (c1.x - mx) * (c1.x - mx) + (c1.y - my) * (c1.y - my);
                                to = d1 < d0 ? cand[1] : cand[0];
                            }
                        }
                    }
                }
                // (su estación es de otra banda: «Junto a Suso» es de BR3 y Suso de BR6) la de su banda más cerca
                if (to == null && BandsOn && BandOfZone(z.Name) > 0 && z.Anchors != null && z.Anchors.Count > 0)
                {
                    double mx = z.Anchors.Average(p => p.x), my = z.Anchors.Average(p => p.y);
                    to = stations.Where(x => SameBand(x, z.Name) && centers.ContainsKey(x))
                                 .OrderBy(x => (centers[x].x - mx) * (centers[x].x - mx) + (centers[x].y - my) * (centers[x].y - my)).FirstOrDefault();
                }
                if (to != null) _depOf[z.Name] = to;
            }
        }

        // ---------------------------------------------------------------- en la lista: una por estación
        string DepOwner(string head)
        {
            var o = DepZones(head).Select(OwnerOf).Where(x => x != null).Distinct().ToList();
            return o.Count == 0 ? null : o.Count == 1 ? o[0] : o[0] + " +" + (o.Count - 1);
        }
        bool DepMine(string head) { var l = DepZones(head); return l.Count > 0 && l.All(Mine); }
        bool DepUnread(string head) => DepZones(head).Any(z => _unread.Contains(z));

        // ---------------------------------------------------------------- verla en el esquema de la ruta
        // las piezas del esquema de la ruta que son de esa dependencia
        List<Schematic> DepParts(string head)
        {
            var r = _route; if (r?.Parts == null) return new List<Schematic>();
            var zs = new HashSet<string>(DepZones(head), StringComparer.Ordinal);
            return r.Parts.Where(p => p.ZoneNames != null ? p.ZoneNames.Overlaps(zs) : zs.Contains(p.Station)).ToList();
        }

        // true si se ha podido ver así (si no, ShowZone sigue como antes)
        bool ShowDepInRoute(string head)
        {
            _depParts = null; _depWaiting = false;
            if (!BandsOn || !_schema || _g == null) return false;
            if (_route == null) { BuildRouteSchema(); _depWaiting = _board != null; return false; }   // (mientras, su trazado real; al acabar de prepararse se vuelve a mostrar: BuildRouteSchema)
            var parts = DepParts(head);
            if (parts.Count == 0) return false;
            _curBand = 0;
            int zb = BandOfZone(head); if (zb > 0 && zb != _openBand) { _openBand = zb; BeginInvoke((Action)Refill); }
            _curZone = head; _cur = _route;
            _depParts = new HashSet<Schematic>(parts);
            _cur.Focus = null; _cur.FocusSet = _depParts;
            _title.Text = Tr("PUESTO DE MANDO") + "  ·  " + head;
            var zones = DepZones(head);
            bool any = false; foreach (var z in zones) any |= _unread.Remove(z);
            if (any) _list.Invalidate();
            _sel = new SchematicHeader { Name = head, Zones = zones };
            _heads = new List<SchematicHeader> { _sel };
            RefreshHeaders(); UpdateJefe();
            FitPart(Union(parts));
            UpdateViewBtn();
            TrainsTick();
            return true;
        }

        // (al pintar) viendo una dependencia en el esquema de la ruta: lo demás apagado, su recuadro y el de cada banda, tenue
        void DimOutsideDep(Graphics g, Rectangle area)
        {
            if (_depParts == null || _cur?.Parts == null || _curBand > 0) return;
            using var dim = new SolidBrush(Color.FromArgb(150, SchematicDraw.Bg));
            var st = g.Save(); g.SetClip(area);
            foreach (var p in _cur.Parts)
            {
                if (_depParts.Contains(p)) continue;
                var r = p.Bounds; r.Inflate(20, 30);
                g.FillRectangle(dim, _origin.X + r.Left * _zoom, _origin.Y + r.Top * _zoom, r.Width * _zoom, r.Height * _zoom);
            }
            var vis = new RectangleF((area.Left - _origin.X) / _zoom, (area.Top - _origin.Y) / _zoom, area.Width / _zoom, area.Height / _zoom);
            if (BandsOn)
                using (var tf = Theme.Font(8f, FontStyle.Bold))
                    for (int b = 1; b <= Bandas.Count; b++)
                    {
                        using var fo = new Pen(Color.FromArgb(55, Bandas.Colors[b]), 1.2f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                        using var tb = new SolidBrush(Color.FromArgb(120, Bandas.Colors[b]));
                        foreach (var m0 in BandBlocks(BandParts(b)))
                        {
                            var m = m0; m.Inflate(30, 40);
                            if (!m.IntersectsWith(vis)) continue;
                            g.DrawRectangle(fo, _origin.X + m.Left * _zoom, _origin.Y + m.Top * _zoom, m.Width * _zoom, m.Height * _zoom);
                            g.DrawString(Bandas.Label(b).Split(' ')[0], tf, tb, _origin.X + m.Left * _zoom + 3, _origin.Y + m.Top * _zoom + 2);
                        }
                    }
            // el de la dependencia, más marcado (con el color de su banda)
            int db = BandOfZone(_curZone);
            using (var fr = new Pen(db > 0 ? Color.FromArgb(170, Bandas.Colors[db]) : Color.FromArgb(170, SchematicDraw.StationCol), 1.6f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash })
                foreach (var m0 in BandBlocks(_depParts.ToList()))
                {
                    var m = m0; m.Inflate(30, 40);
                    g.DrawRectangle(fr, _origin.X + m.Left * _zoom, _origin.Y + m.Top * _zoom, m.Width * _zoom, m.Height * _zoom);
                }
            g.Restore(st);
        }

        // las zonas que se ven ahora (una banda, una dependencia con sus tramos; null: el plano general)
        List<string> ViewZones() => _curBand > 0 ? BandZones(_curBand) : _curZone == null ? null : DepZones(_curZone);
    }
}
