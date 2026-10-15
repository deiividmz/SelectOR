// Agente del mapa (MapAgent\, multijugador y Conducción libre): sus archivos van dentro de SelectOR.dll, en dos
// variantes, y se deja la que toca junto a Open Rails al lanzar una partida en red o de Conducción libre (si falta o es
// de otra versión):
//  · Open Rails de .NET 6 (trae RunActivity.runtimeconfig.json): se carga con DOTNET_STARTUP_HOOKS;
//  · Open Rails de .NET Framework (NY, NewYear MG, Estable, Testing…): con APPDOMAIN_MANAGER_ASM/TYPE.
// Así llegan con la actualización de siempre, que solo instala los archivos de SelectOR. Si no se pueden escribir,
// Open Rails arranca sin él (y nunca se pone la variable sin el archivo: .NET Framework no arrancaría).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace SelectOR
{
    static class MapAgentFiles
    {
        // Pone el agente en el arranque de Open Rails (variables de entorno de psi). false = sin agente.
        public static bool Apply(ProcessStartInfo psi)
        {
            try
            {
                string dir = AppContext.BaseDirectory;
                bool core = File.Exists(Path.Combine(dir, "RunActivity.runtimeconfig.json"));
                string kind = core ? "core" : "fx";
                foreach (var file in new[] { "SelectOR.MapAgent.dll", "0Harmony.dll" })
                    if (!Ensure($"mapagent.{kind}.{file}", Path.Combine(dir, file))) return false;
                string hook = Path.Combine(dir, "SelectOR.MapAgent.dll");
                if (core)
                {
                    string prev = psi.Environment.TryGetValue("DOTNET_STARTUP_HOOKS", out var h0) ? h0 : null;
                    if (prev != null && prev.Contains(hook, StringComparison.OrdinalIgnoreCase)) return true;   // (ya puesto: no dos veces)
                    psi.Environment["DOTNET_STARTUP_HOOKS"] = string.IsNullOrEmpty(prev) ? hook : prev + ";" + hook;
                }
                else
                {
                    psi.Environment["APPDOMAIN_MANAGER_ASM"] = "SelectOR.MapAgent, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
                    psi.Environment["APPDOMAIN_MANAGER_TYPE"] = "SelectOR.MapAgent.AgentDomainManager";
                }
                return true;
            }
            catch { return false; }
        }

        // El archivo, igual que el que va dentro (si no, se escribe). false = no se ha podido.
        static bool Ensure(string res, string path)
        {
            using var s = typeof(MapAgentFiles).Assembly.GetManifestResourceStream(res);
            if (s == null) return false;
            using var ms = new MemoryStream(); s.CopyTo(ms);
            byte[] want = ms.ToArray();
            if (File.Exists(path))
            {
                byte[] have = File.ReadAllBytes(path);
                if (have.Length == want.Length && SHA256.HashData(have).AsSpan().SequenceEqual(SHA256.HashData(want))) return true;
            }
            string tmp = path + ".new";
            File.WriteAllBytes(tmp, want);
            File.Move(tmp, path, true);
            return true;
        }
    }
}
