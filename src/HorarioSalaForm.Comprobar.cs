// HORARIO DE LA SALA · COMPROBAR HORARIO (2026-11-11, petición): el botón «Comprobar horario» de la ventana del horario,
// la comprobación antes de guardar un servicio (avisa, no impide) y «Aplicar sugerencia» (retrasar las salidas de un
// servicio u otra frecuencia: se guarda como siempre, con room_ai_service_save_trip). La comprobación: HorarioCheck.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class HorarioSalaForm
    {
        readonly Dictionary<string, double> _vmax = new(StringComparer.CurrentCultureIgnoreCase);
        string BaseDir => Path.GetDirectoryName(Path.GetDirectoryName((_routeDir ?? "").TrimEnd('\\', '/'))) ?? "";

        // la velocidad máxima (m/s) de una composición, de su .con (0: no se puede leer)
        double ConsistVmax(string con)
        {
            lock (_vmax)
            {
                if (_vmax.TryGetValue(con ?? "", out double v)) return v;
                string spec = string.IsNullOrWhiteSpace(con) ? null : AiConsistSpec.Build(BaseDir, con);
                v = spec != null && spec.StartsWith("v=") && double.TryParse(spec.Substring(2).Split(';')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : 0;
                _vmax[con ?? ""] = v; return v;
            }
        }

        int MaxNow => MaxVals[Math.Max(0, _max.SelectedIndex)];

        async Task<HorarioCheck.Result> RunCheck(List<Service> services)
        {
            await EnsureLists();
            var g = _g; int max = MaxNow; bool on = _enabled.Checked;
            return await Task.Run(() => HorarioCheck.Run(new HorarioCheck.Input { G = g, Services = services, Max = max, Enabled = on, ConsistVmax = ConsistVmax }));
        }

        // el botón «Comprobar horario»
        async Task CheckAll()
        {
            _status.Text = Tr("Comprobando el horario…");
            var r = await RunCheck(_services.ToList());
            if (IsDisposed) return;
            _status.Text = string.Format(Tr("{0} servicios"), _services.Count);
            using var f = new HorarioCheckForm(r, _g, _map?.Data)
            {
                Apply = ApplyFix,
                Recheck = async () => { await Reload(); return await RunCheck(_services.ToList()); }
            };
            f.ShowDialog(this);
        }

        // antes de guardar un servicio (HorarioServicioForm): lo que avisa la comprobación de ESE servicio con el resto
        async Task<List<HorarioCheck.Issue>> Precheck(Service edited)
        {
            var list = _services.Select(x => x.Id == edited.Id && edited.Id != 0 ? edited : x).ToList();
            if (edited.Id == 0) list.Add(edited);
            int me = list.IndexOf(edited);
            var r = await RunCheck(list);
            return r.Issues.Where(i => i.Sev > 0 && (i.Svc == me || i.ShiftSvc == me || i.EverySvc == me || (i.Service ?? "").Contains(edited.Name))).ToList();
        }

        // «Aplicar sugerencia»: retrasar todas las salidas de un servicio o cambiar su frecuencia
        async Task<bool> ApplyFix(HorarioCheck.Issue i)
        {
            if (i == null || !i.CanApply) return false;
            int si = i.ShiftSvc >= 0 && i.ShiftMin != 0 ? i.ShiftSvc : i.EverySvc;
            if (si < 0 || si >= _services.Count) return false;
            var s = _services[si];
            string first = s.First, last = s.Last; int every = s.Every;
            string ask;
            if (i.ShiftSvc >= 0 && i.ShiftMin != 0)
            {
                first = Add(s.First, i.ShiftMin); last = Add(s.Last, i.ShiftMin);
                ask = string.Format(Tr("¿Retrasar {0} min todas las salidas de «{1}»? Primera salida {2} → {3}, última {4} → {5}."), i.ShiftMin, s.Name, s.First, first, s.Last, last);
            }
            else
            {
                every = i.NewEvery;
                ask = string.Format(Tr("¿Cambiar la frecuencia de «{0}» de cada {1} min a cada {2} min?"), s.Name, s.Every, every);
            }
            if (ThemedBox.Show(this, ask, Tr("Aplicar sugerencia"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
            string spec = AiConsistSpec.Build(BaseDir, s.Consist);
            if (spec == null) { ThemedBox.Show(this, string.Format(Tr("No se puede leer la composición «{0}» en tu Open Rails."), s.Consist), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            var (_, err) = await Rpc("room_ai_service_save_trip", new
            {
                p_id = (long?)s.Id, p_name = s.Name, p_kind = s.Kind, p_origin = s.Origin, p_destination = s.Destination, p_consist = s.Consist,
                p_first_time = first, p_every_min = every, p_last_time = last, p_stops = s.Stops, p_active = s.Active, p_route = _route, p_consist_spec = spec, p_trip = s.Trip ?? ""
            });
            if (IsDisposed) return false;
            if (err != null) { ThemedBox.Show(this, err, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            _status.Text = Tr("Servicio guardado: la sala lo aplica en un minuto.");
            return true;
        }

        static string Add(string hm, int min)
        {
            var p = (hm ?? "").Split(':');
            if (p.Length < 2 || !int.TryParse(p[0], out int h) || !int.TryParse(p[1], out int m)) return hm;
            int t = Math.Max(0, Math.Min(23 * 60 + 59, h * 60 + m + min));
            return $"{t / 60:00}:{t % 60:00}";
        }
    }
}
