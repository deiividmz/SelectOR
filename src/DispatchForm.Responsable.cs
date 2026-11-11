// (2026-11-10, petición) EL RESPONSABLE DE CIRCULACIÓN EN EL ANDÉN, desde el Puesto de Mando. Es un objeto de la sala (lo pone
// el administrador en «🚧 Objetos»); aquí sale con 🚩 en el trazado real y en el esquema (capa «Objetos de la sala»), con el
// color de su indicación. Clic derecho en él → «Señal de parada», «Señal de paso», «Marche el tren» o «Sin indicación»:
// solo el jefe de circulación de la dependencia donde está y el administrador. El servidor de la sala la reparte al momento
// a todos (SwitchPilot.SetBt → «SELECTOR BT»), y cada SelectOR se la pasa a su Open Rails (el agente cambia la postura: de
// día con banderín o bandera, de noche con la linterna; en «Marche el tren», el silbato a quien esté cerca). Solo se ve: no
// toca señales.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        readonly List<(RectangleF r, ObjetosSalaForm.RoomObj o, PointF p)> _btHits = new();
        bool _btHooked;

        static readonly (string st, string label)[] BtOptions =
        {
            ("parada", "Señal de parada"), ("paso", "Señal de paso"), ("marche", "Marche el tren"), ("reposo", "Sin indicación"),
        };

        void BtHook()
        {
            if (_btHooked || _pilot == null) return;
            _btHooked = true;
            _pilot.BtChanged += (id, st) => { try { if (!IsDisposed) BeginInvoke(new Action(() => _canvas.Invalidate())); } catch { } };
            _pilot.BtSnapshot += () => { try { if (!IsDisposed) BeginInvoke(new Action(() => _canvas.Invalidate())); } catch { } };
            _pilot.BtError += e => { try { if (!IsDisposed) BeginInvoke(new Action(() => ItinSay(Tr("La indicación de este responsable de circulación la cambian el jefe de circulación de su dependencia y el administrador."), false))); } catch { } };
        }

        string BtState(long id) { BtHook(); return _pilot?.BtStates != null && _pilot.BtStates.TryGetValue(id, out var s) ? s : "reposo"; }
        Color BtColor(long id) => BtState(id) switch
        {
            "parada" => Color.FromArgb(232, 60, 50), "paso" or "marche" => Color.FromArgb(92, 196, 104), _ => Color.FromArgb(170, 172, 178),
        };
        string BtLabel(long id)
        {
            string st = BtState(id);
            return Tr("Responsable de circulación") + (st == "reposo" ? "" : " · " + Tr(BtOptions.First(x => x.st == st).label));
        }

        // la dependencia donde está (la de la vía más cercana)
        string BtZone(ObjetosSalaForm.RoomObj o)
        {
            try
            {
                if (_zones == null) return null;
                if (_board != null && _zoneSpans != null && _board.Locate(o.Lat, o.Lon, out int e, out double off))
                    foreach (var kv in _zoneSpans) if (kv.Value.Any(s => s.e == e && off >= s.a - 5 && off <= s.b + 5)) return kv.Key;
                foreach (var z in _zones)
                    if (_cache.TryGetValue(z.Name, out var sc) && sc != null && sc.Locate(o.Lat, o.Lon, out int e2, out _) && sc.Drawn(e2)) return z.Name;
            }
            catch { }
            return null;
        }

        // (clic derecho) el menú del responsable; true si era uno
        bool BtRightClick(Point p)
        {
            if (!_showObjs) return false;
            var hit = _btHits.LastOrDefault(h => h.r.Contains(p));
            if (hit.o == null) return false;
            BtHook();
            var o = hit.o; string zone = BtZone(o), cur = BtState(o.Id);
            bool can = _pilot != null && _pilot.Ready && (IsAdmin || (Me != null && zone != null && Mine(zone)));
            var m = MenuStyle.Apply(new ContextMenuStrip());
            m.Items.Add(new ToolStripMenuItem(Tr("Responsable de circulación") + (zone != null ? " · " + zone : "")) { Enabled = false });
            m.Items.Add(new ToolStripSeparator());
            foreach (var (st, label) in BtOptions)
            {
                var it = new ToolStripMenuItem(Tr(label), null, (s, e) => { _pilot?.SetBt(o.Id, st, zone); }) { Checked = st == cur, Enabled = can && st != cur };
                if (st == "parada") it.ForeColor = Color.FromArgb(232, 80, 70); else if (st != "reposo") it.ForeColor = Color.FromArgb(110, 205, 120);
                m.Items.Add(it);
            }
            if (!can) m.Items.Add(new ToolStripMenuItem(zone != null ? string.Format(Tr("La cambian el jefe de circulación de {0} y el administrador"), zone) : Tr("La cambian el jefe de circulación de su dependencia y el administrador")) { Enabled = false });
            m.Show(_canvas, p);
            return true;
        }
    }
}
