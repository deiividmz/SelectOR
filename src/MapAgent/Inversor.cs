// (2026-11-10, petición) El inversor de tu tren, al despachador de la sala al momento: las señales de DETRÁS de un tren solo se
// abren si invierte la marcha (SelectOR Despachador/Detras.cs), y el sentido que llega con el MOVE de Open Rails solo viaja con el
// tren en marcha (y un segundo tarde): al empezar a retroceder, la señal de detrás seguiría en rojo. Aquí, en el Open Rails del
// jugador: «SELECTORDIR F|R|N» (adelante, atrás, neutro, según MUDirection: el sentido de marcha del tren, ya contando la cabina
// desde la que se conduce) en cuanto cambia, y cada 20 s por si se ha perdido. El servidor de la sala se lo pasa al despachador.
// Solo si el servidor lo pide («@selector:dir?»: ver On).

using System;
using ORTS.Common;
using Orts.MultiPlayer;

namespace SelectOR.MapAgent
{
    public static class Inversor
    {
        static char _sent = '?'; static int _at;
        // solo con un servidor de la sala que lo conoce (lo pide con «TEXT SelectOR yo @selector:dir?», Agent.BeforeText): a uno
        // viejo o a un servidor de Open Rails, una trama que no conoce le cuenta como error del jugador (y con 5 seguidas, fuera)
        public static volatile bool On;

        // (Agent.AfterUpdate, cada vuelta del multijugador)
        public static void Check()
        {
            if (!On || !MPManager.IsMultiPlayer() || !MPManager.IsClient()) return;
            var t = MPManager.Simulator?.PlayerLocomotive?.Train; if (t == null) return;
            char d = t.MUDirection == Direction.Reverse ? 'R' : t.MUDirection == Direction.Forward ? 'F' : 'N';
            int now = Environment.TickCount;
            if (d == _sent && unchecked(now - _at) < 20000) return;
            bool changed = d != _sent;
            _sent = d; _at = now;
            string body = "SELECTORDIR " + d;
            MPManager.Notify(" " + body.Length + ": " + body);
            if (changed) Agent.Log("inversor: " + (d == 'R' ? "marcha atrás" : d == 'F' ? "adelante" : "neutro") + " (al despachador de la sala)");
        }
    }
}
