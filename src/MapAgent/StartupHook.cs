// Punto de entrada que .NET llama antes de arrancar Open Rails cuando la variable DOTNET_STARTUP_HOOKS apunta a esta
// DLL (SelectOR la pone solo al lanzar una partida en red). Tiene que llamarse así y no llevar espacio de nombres.
// Si algo falla, Open Rails arranca igual, sin el agente del mapa.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

internal class StartupHook
{
    public static void Initialize()
    {
        try
        {
            string exe = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
            if (!exe.Equals("RunActivity", StringComparison.OrdinalIgnoreCase)) return;   // solo el simulador
            // Harmony va junto a esta DLL (Open Rails no lo conoce): se le enseña dónde está
            string dir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location);
            AssemblyLoadContext.Default.Resolving += (ctx, name) =>
            {
                string p = Path.Combine(dir, name.Name + ".dll");
                return File.Exists(p) ? ctx.LoadFromAssemblyPath(p) : null;
            };
            Install();
        }
        catch (Exception ex)
        {
            try { SelectOR.MapAgent.Agent.Log("no se ha podido instalar: " + ex.Message); } catch { }
        }
    }

    // (aparte: así, si falta algo de Open Rails, el fallo salta dentro del try de arriba)
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Install() => SelectOR.MapAgent.Agent.Install();
}
