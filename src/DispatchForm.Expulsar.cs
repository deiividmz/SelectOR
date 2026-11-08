// PUESTO DE MANDO: expulsiones (petición 2026-11-08). Con el mando de jefe de circulación, un clic (o el botón derecho) en un
// tren de la sala:
//   · un usuario: «Desconectar a X de la sala…» con un motivo obligatorio, que le sale a él. Se le corta la conexión y su
//     tren desaparece; puede volver a entrar. No a otro jefe ni al administrador; queda apuntado para el administrador.
//   · unos vagones sueltos de un desacople: «Retirar estos vagones sueltos».
// Solo en tus dependencias (el administrador, en toda la sala): lo comprueba la sala (Expulsar.cs del servidor y del
// despachador), que contesta KICKOK / KICKERR.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SelectOR
{
    public sealed partial class DispatchForm
    {
        readonly List<(string raw, bool player, PointF[] body)> _trainSeen = new();   // (Live / LiveBoard, en cada repintado)

        // el tren bajo el ratón (su nombre en la sala: el usuario, «SUELTO-dueño-número» o «IA-…»), o null
        (string raw, bool player)? TrainAt(Point p)
        {
            PointF S(PointF q) => Board ? Bp(q) : new PointF(_origin.X + q.X * _zoom, _origin.Y + q.Y * _zoom);
            static float Seg(PointF a, PointF b, Point p)
            {
                float dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
                float t = l2 <= 0.01f ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
                float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y; return (float)Math.Sqrt(x * x + y * y);
            }
            (string, bool)? best = null; float bd = Theme.Px(9);
            foreach (var (raw, player, body) in _trainSeen)
            {
                if (body == null || body.Length == 0 || RoomAi.Is(raw)) continue;
                var pts = body.Select(S).ToArray();
                float d = pts.Length == 1 ? Seg(pts[0], pts[0], p) : Enumerable.Range(0, pts.Length - 1).Min(i => Seg(pts[i], pts[i + 1], p));
                var mid = pts[pts.Length / 2];   // (y su rótulo, encima)
                if (Math.Abs(p.X - mid.X) < Theme.Px(55) && p.Y > mid.Y - Theme.Px(32) && p.Y < mid.Y - Theme.Px(6)) d = Math.Min(d, Theme.Px(4));
                if (d < bd) { bd = d; best = (raw, player); }
            }
            return best;
        }

        // (MouseUp, con el mando) clic en un tren: su menú
        bool TrainClick(Point p)
        {
            if (Me == null || _pilot == null) return false;
            if (TrainAt(p) is not { } hit) return false;
            var m = MenuStyle.Apply(new ContextMenuStrip());
            if (RoomLoose.Is(hit.raw))
            {
                string owner = RoomLoose.Owner(hit.raw).Replace('_', ' ');
                int k = hit.raw.LastIndexOf('-');
                if (k < 0 || !int.TryParse(hit.raw.Substring(k + 1), out int num)) return false;
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Vagones sueltos de {0}"), owner)) { Enabled = false });
                m.Items.Add(new ToolStripMenuItem(Tr("Retirar estos vagones sueltos"), null, async (o, e) => await RetireLoose(num)) { ForeColor = Color.FromArgb(255, 183, 77) });
            }
            else if (hit.player)
            {
                m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Tren de {0}"), hit.raw)) { Enabled = false });
                if (hit.raw == Me) m.Items.Add(new ToolStripMenuItem(Tr("(es tu tren)")) { Enabled = false });
                else m.Items.Add(new ToolStripMenuItem(string.Format(Tr("Desconectar a {0} de la sala…"), hit.raw), null, (o, e) => KickDialog(hit.raw)) { ForeColor = Color.FromArgb(239, 83, 80) });
            }
            else return false;
            m.Show(_canvas, p);
            return true;
        }

        // espera la respuesta de la sala a una expulsión (null: hecho; si no, por qué no)
        async Task<string> KickWait(string what, Action send)
        {
            var tcs = new TaskCompletionSource<(bool ok, string text)>(TaskCreationOptions.RunContinuationsAsynchronously);
            void H(string w, bool ok, string text) { if (w == what) tcs.TrySetResult((ok, text)); }
            _pilot.KickReply += H;
            try
            {
                send();
                var done = await Task.WhenAny(tcs.Task, Task.Delay(15000));
                if (done != tcs.Task) return Tr("La sala no contesta. Inténtalo otra vez dentro de un rato.");
                var (ok, text) = tcs.Task.Result;
                return ok ? null : KickWhy(text);
            }
            finally { _pilot.KickReply -= H; }
        }

        static string KickWhy(string code) => code switch
        {
            "notjefe" => Tr("Necesitas tener el mando de alguna dependencia."),
            "nodisp" => Tr("La sala no tiene ahora mismo el despachador fijo de SelectOR."),
            "reason" => Tr("Escribe el motivo (de 3 a 300 caracteres)."),
            "self" => Tr("No puedes desconectarte a ti mismo."),
            "jefe" => Tr("No se puede desconectar a un jefe de circulación."),
            "notin" => Tr("Ya no está en la sala."),
            "admin" => Tr("No se puede desconectar al administrador."),
            "offline" => Tr("La sala no puede apuntarlo ahora mismo. Inténtalo dentro de un rato."),
            "out" => Tr("No está en tus dependencias."),
            _ => ItinText(code),
        };

        void KickDialog(string user)
        {
            using var dlg = new FormDialog(Tr("Desconectar de la sala"), Tr("Desconectar"), 560, "⛔", user);
            dlg.AddMultiline("reason", string.Format(Tr("Motivo (lo verá {0})"), user), "", 90);
            dlg.AddInfo(Tr("Se le corta la conexión y su tren desaparece de la sala; puede volver a entrar. Solo si su tren está en tus dependencias. Queda apuntado para el administrador."));
            string done = null;
            dlg.Validate = async f =>
            {
                string reason = f.Get("reason").Trim();
                if (reason.Length < 3) return Tr("Escribe el motivo (de 3 a 300 caracteres).");
                if (reason.Length > 300) return Tr("El motivo es demasiado largo (300 caracteres como mucho).");
                string err = await KickWait(user, () => _pilot.JefeKick(user, reason));
                if (err != null) return err;
                done = string.Format(Tr("{0} ha sido desconectado de la sala."), user);
                return null;
            };
            if (dlg.ShowDialog(this) == DialogResult.OK && done != null) ItinSay(done, true);
        }

        async Task RetireLoose(int num)
        {
            string err = await KickWait("#" + num, () => _pilot.JefeLoose(num));
            ItinSay(err ?? Tr("Vagones sueltos retirados."), err == null);
        }
    }
}
