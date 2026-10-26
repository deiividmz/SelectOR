// Vehículos que faltan, en tu Open Rails: la lista para sustituirlos, ya hecha (1.2.80). Open Rails la hace la primera vez
// que le falta un vehículo (MPManager.GetList: abre todos los .eng/.wag de la carpeta de trenes), en el hilo de la red, y
// con muchos trenes tarda minutos: no lee la conexión con la sala y se cae al entrar. Aquí se prepara al arrancar Open Rails,
// en segundo plano, con la lista guardada en disco (VehiculosCache.cs: solo se leen los archivos nuevos o cambiados; SelectOR
// la deja hecha al abrirse), y se le pone a Open Rails en sus listas (engList, coachList) antes de que la necesite.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using Orts.MultiPlayer;
using SelectOR.Comun;

namespace SelectOR.MapAgent
{
    public static class Vehiculos
    {
        static FieldInfo _eng, _wag;
        static Task _task;
        static SortedList<double, string> _engL, _wagL;

        public static int Install(Harmony h)
        {
            try
            {
                _eng = AccessTools.Field(typeof(MPManager), "engList"); _wag = AccessTools.Field(typeof(MPManager), "coachList");
                var m = AccessTools.Method(typeof(MPManager), "SubMissingCar");
                if (_eng == null || _wag == null || m == null) return 0;
                h.Patch(m, prefix: new HarmonyMethod(typeof(Vehiculos), nameof(Before)));
                _task = Task.Run((Action)Prepare);
                return 1;
            }
            catch (Exception ex) { Agent.Log("vehículos que faltan: " + ex.GetType().Name + " " + ex.Message); return 0; }
        }

        static void Prepare()
        {
            try
            {
                string basePath = null;
                for (int i = 0; i < 1200 && basePath == null; i++) { basePath = MPManager.Simulator?.BasePath; if (basePath == null) Thread.Sleep(500); }
                if (basePath == null) return;
                // (solo en multijugador: es donde llegan trenes de otros)
                for (int i = 0; i < 120 && !MPManager.IsMultiPlayer(); i++) Thread.Sleep(500);
                if (!MPManager.IsMultiPlayer()) return;
                var r = VehiculosCache.Build(basePath);
                if (r == null) return;
                _engL = r.Value.eng; _wagL = r.Value.wag;
                Agent.Log("vehículos para sustituir los que falten, ya preparados: " + r.Value.info);
                Give();
            }
            catch (Exception ex) { Agent.Log("vehículos que faltan (lista): " + ex.GetType().Name + " " + ex.Message); }
        }

        // a Open Rails, si aún no tiene las suyas
        static void Give()
        {
            try
            {
                var mp = MPManager.Instance(); if (mp == null || _engL == null) return;
                lock (mp)
                {
                    if (_eng.GetValue(mp) == null) _eng.SetValue(mp, _engL);
                    if (_wag.GetValue(mp) == null) _wag.SetValue(mp, _wagL);
                }
            }
            catch { }
        }

        // (Open Rails va a buscar un sustituto) si la lista aún se está preparando, se espera a ella (como mucho 45 s): sale
        // antes que leer otra vez todos los archivos
        static void Before(MPManager __instance)
        {
            try
            {
                if (_eng.GetValue(__instance) != null && _wag.GetValue(__instance) != null) return;
                if (_engL == null && _task != null && !_task.IsCompleted)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _task.Wait(TimeSpan.FromSeconds(45));
                    Agent.Log($"vehículo que falta: se esperan {sw.Elapsed.TotalSeconds:F1} s a la lista");
                }
                Give();
            }
            catch { }
        }
    }
}
