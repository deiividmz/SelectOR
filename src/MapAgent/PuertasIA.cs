// Las puertas de los trenes IA de la sala, en tu Open Rails (1.2.79). El despachador de la sala avisa en cada parada de
// viajeros con «@selector:aidoors número lado 1|0» (TEXT de «SelectOR», que Agent.BeforeText se queda) y aquí se abren o se
// cierran las de la copia del tren con Train.SetDoors (las mismas que abre Open Rails, con su sonido). El lado, por su
// nombre (Left/Right/Both): el orden del enum DoorSide puede cambiar entre versiones de Open Rails.
// Se hace en la vuelta de la simulación (MPManager.Update), no en el hilo de red.

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Orts.MultiPlayer;
using Orts.Simulation.Physics;

namespace SelectOR.MapAgent
{
    public static class PuertasIA
    {
        static readonly ConcurrentQueue<(int num, string side, bool open)> _ask = new ConcurrentQueue<(int, string, bool)>();
        static MethodInfo _set; static Type _sideType; static bool _looked;
        static int _done;

        // (Agent.BeforeText, hilo de red) true = era suyo
        public static bool Take(string msg)
        {
            if (msg == null || !msg.StartsWith("@selector:aidoors ", StringComparison.Ordinal)) return false;
            var a = msg.Substring(18).Trim().Split(' ');
            if (a.Length >= 3 && int.TryParse(a[0], out int n)) _ask.Enqueue((n, a[1], a[2] == "1"));
            return true;
        }

        // (Agent.AfterUpdate, hilo de la simulación)
        public static void Apply()
        {
            if (_ask.IsEmpty) return;
            if (!_looked)
            {
                _looked = true;
                _set = typeof(Train).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "SetDoors" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.IsEnum && m.GetParameters()[1].ParameterType == typeof(bool));
                _sideType = _set?.GetParameters()[0].ParameterType;
                if (_set == null) Agent.Log("puertas de los trenes IA: este Open Rails no tiene Train.SetDoors");
            }
            while (_ask.TryDequeue(out var q))
            {
                if (_set == null) continue;
                try
                {
                    Train t = null;
                    for (int i = 0; i < 4 && t == null; i++)
                        try { t = MPManager.Simulator?.Trains?.ToArray().FirstOrDefault(x => x.Number == q.num); } catch (InvalidOperationException) { } catch (ArgumentException) { }
                    if (t == null || t.Cars == null || t.Cars.Count == 0) continue;
                    if (t == MPManager.Simulator.PlayerLocomotive?.Train) continue;   // (nunca el tuyo)
                    object side;
                    try { side = Enum.Parse(_sideType, q.side, true); } catch { continue; }
                    _set.Invoke(t, new[] { side, (object)q.open });
                    if (_done++ < 6) Agent.Log($"puertas del tren IA «{t.Name}» ({t.Number}): {(q.open ? "abre" : "cierra")} ({q.side})");
                }
                catch (Exception ex) { Agent.Log("puertas de los trenes IA: " + (ex.InnerException ?? ex).GetType().Name + " " + (ex.InnerException ?? ex).Message); }
            }
        }
    }
}
