// Entrada en los Open Rails de .NET Framework (NY, NewYear MG, Estable, Testing…): .NET Framework crea este «gestor
// del dominio» al arrancar RunActivity cuando SelectOR pone APPDOMAIN_MANAGER_ASM y APPDOMAIN_MANAGER_TYPE (solo al
// lanzar una partida en red). No hace nada más que instalar el guardián; si algo falla, Open Rails arranca igual.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SelectOR.SignalGuard
{
    public sealed class GuardDomainManager : AppDomainManager
    {
        static bool _done;

        public override void InitializeNewDomain(AppDomainSetup appDomainInfo)
        {
            base.InitializeNewDomain(appDomainInfo);
            if (_done) return;
            _done = true;
            try
            {
                string exe = "";
                try { exe = Path.GetFileNameWithoutExtension(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? ""); } catch { }
                if (!exe.Equals("RunActivity", StringComparison.OrdinalIgnoreCase)) return;   // solo el simulador
                // Harmony va junto a esta DLL
                string dir = Path.GetDirectoryName(typeof(GuardDomainManager).Assembly.Location);
                AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
                {
                    string p = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                    return File.Exists(p) ? Assembly.LoadFrom(p) : null;
                };
                Install();
            }
            catch (Exception ex)
            {
                try { Guard.Log("no se ha podido instalar: " + ex.Message); } catch { }
            }
        }

        // (aparte: si falta algo de Open Rails, el fallo salta dentro del try de arriba)
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Install() => Guard.Install();
    }
}
