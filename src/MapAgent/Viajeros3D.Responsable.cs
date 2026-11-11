// RESPONSABLE DE CIRCULACIÓN EN EL ANDÉN (2026-11-10, petición): un objeto de la sala más («responsable», figura
// «obj_responsable» de Gente.pack: el vigilante con gorra roja de franja negra). Solo se ve: no toca señales.
//  · Su indicación la cambian el jefe de circulación de su dependencia y el administrador desde el Puesto de Mando; el
//    servidor de la sala la reparte al momento y SelectOR se la pasa al agente por el enlace local:
//      «OBJSTATE id estado nueva»   estado: reposo · parada · paso · marche   nueva: 1 si se acaba de dar (0: al conectarse)
//  · La postura: reposo, o la indicación con banderín o bandera (de día) o con la linterna encendida (de noche, con
//    NightLevel: al anochecer ya con la linterna): «<indicación>_d» / «<indicación>_n».
//  · «Marche el tren» recién dada: el silbato suena a quien conduce a menos de 200 m (más fuerte cuanto más cerca). Un
//    pitido largo de silbato de bola hecho aquí mismo (sin archivos), por el sonido de Windows (no el de Open Rails).

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;

namespace SelectOR.MapAgent
{
    public static partial class Viajeros3D
    {
        static readonly ConcurrentDictionary<int, string> _btState = new ConcurrentDictionary<int, string>();
        static readonly ConcurrentQueue<int> _btWhistle = new ConcurrentQueue<int>();
        const float WhistleDist = 200;

        // (enlace local) «id estado nueva»
        public static void ObjState(string s)
        {
            var t = s.Split(' ');
            if (t.Length < 2 || !int.TryParse(t[0], out int id)) return;
            string st = t[1] == "parada" || t[1] == "paso" || t[1] == "marche" ? t[1] : "reposo";
            _btState[id] = st;
            if (st == "marche" && t.Length > 2 && t[2] == "1") _btWhistle.Enqueue(id);
        }

        // la postura de un responsable: su indicación, con la luz que toque
        static string BtAnim(Obj o, ref int night)
        {
            string st = _btState.TryGetValue(o.Id, out var x) ? x : "reposo";
            if (st == "reposo") return "reposo";
            if (night < 0) night = NightLevel();
            return st + (night > 0 ? "_n" : "_d");
        }

        // (hilo de actualización, ObjDraw) el silbato de las «Marche el tren» recién dadas: el del más cercano (a menos de 200 m;
        // varios a la vez, uno solo: si no, sonaban encima)
        static void BtWhistles(System.Collections.Generic.List<Obj> objs, ORTS.Common.WorldPosition lead)
        {
            Obj best = null; float bd = WhistleDist;
            while (_btWhistle.TryDequeue(out int id))
            {
                var o = objs.FirstOrDefault(q => q.Id == id && q.Kind == "responsable");
                if (o == null) continue;
                float d = DistTo(o, lead);
                if (d <= bd) { bd = d; best = o; }
            }
            if (best == null) return;
            float vol = (float)Math.Max(0.08, Math.Pow(1 - bd / WhistleDist, 1.4) * 0.85);
            Agent.Log($"responsable de circulación #{best.Id}: «Marche el tren» a {bd:0} m: silbato");
            try { var wav = Whistle(vol); System.Threading.Tasks.Task.Run(() => { try { using (var ms = new MemoryStream(wav)) using (var p = new System.Media.SoundPlayer(ms)) p.PlaySync(); } catch { } }); }
            catch { }
        }

        // un pitido largo de silbato de bola (unos 1,3 s): tono de ~2,9 kHz que vibra (la bola) y sube y baja un poco
        static byte[] Whistle(float vol)
        {
            const int rate = 22050; double secs = 1.3; int n = (int)(rate * secs);
            var rnd = new Random(7);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + n * 2);
                w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(n * 2);
                double ph = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = (double)i / rate;
                    double trill = Math.Sin(2 * Math.PI * 34 * t);                     // (la bola: ~34 veces por segundo)
                    double f = 2880 + 140 * trill + 60 * Math.Sin(2 * Math.PI * 1.7 * t);
                    ph += 2 * Math.PI * f / rate;
                    double env = Math.Min(1, t / 0.035) * Math.Min(1, (secs - t) / 0.14);
                    double am = 0.72 + 0.28 * (0.5 + 0.5 * trill);
                    double s = (Math.Sin(ph) * 0.82 + Math.Sin(2 * ph) * 0.12 + (rnd.NextDouble() * 2 - 1) * 0.06) * env * am * vol;
                    w.Write((short)Math.Max(-32767, Math.Min(32767, s * 32767)));
                }
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
