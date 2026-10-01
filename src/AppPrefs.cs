// Preferencias propias del selector (favoritos y ultima seleccion).
// Se guardan en %AppData%\Open Rails\MenuParalelo.json para no tocar nada de OR.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelectOR
{
    public class AppPrefs
    {
        public string LastFolder { get; set; }
        public string LastRoute { get; set; }
        public string LastConsist { get; set; }
        public int LastTab { get; set; }
        // Empresas (Supabase): URL del proyecto + clave anon (pública). El email se recuerda para el login.
        public string SupabaseUrl { get; set; }
        public string SupabaseKey { get; set; }
        public string EmpresasEmail { get; set; }
        // "Recordar contraseña": la contraseña se guarda CIFRADA con DPAPI (por usuario de Windows).
        public bool RememberPassword { get; set; }
        public string EmpresasPasswordEnc { get; set; }
        // HUD de servicio (overlay pequeño sobre Open Rails mientras conduces un servicio de empresa).
        public bool ServiceHud { get; set; } = true;
        public bool HudCollapsed { get; set; }
        public bool HudMapOpen { get; set; } = true;   // mini-mapa desplegado por defecto
        public int HudX { get; set; } = -1;
        public int HudY { get; set; } = -1;
        // Tamaño del mini-mapa del HUD (se cambia arrastrando su esquina inferior derecha).
        public int HudMapW { get; set; } = 244;
        public int HudMapH { get; set; } = 150;
        // Zoom del mini-mapa (rueda del ratón o botones +/− sobre el mapa): 1 = el de siempre
        // (~1,2 km de ancho con el tamaño por defecto); mayor = más cerca.
        public double HudMapZoom { get; set; } = 1.0;
        // Pupitre (indicadores del tren sobre Open Rails): si el maquinista lo dejó abierto (la
        // primera vez, oculto), dónde y a qué escala. Se abre y se cierra desde la barra superior
        // o desde el HUD del mini-mapa, y la próxima conducción empieza como lo dejaste.
        public bool CabHudOn { get; set; }
        public int CabPanelX { get; set; } = -1;     // -1 = abajo a la derecha
        public int CabPanelY { get; set; } = -1;
        public float CabHudScale { get; set; } = 1f;
        // HUD del chat de empresa: si lo dejaste abierto, dónde, su tamaño y si estaba plegado.
        public bool ChatHudOn { get; set; }
        public int ChatHudX { get; set; } = -1;      // -1 = abajo a la derecha
        public int ChatHudY { get; set; } = -1;
        public int ChatHudW { get; set; } = 360;
        public int ChatHudH { get; set; } = 300;
        public bool ChatHudCollapsed { get; set; }
        // HUD de la hoja de ruta (itinerario marcado en el mapa grande)
        public int RoadHudX { get; set; } = -1;      // -1 = arriba a la derecha
        public int RoadHudY { get; set; } = -1;
        public int RoadHudW { get; set; } = 330;
        public int RoadHudH { get; set; } = 360;
        public bool RoadHudCollapsed { get; set; }
        public int RoadDwellS { get; set; } = 30;    // segundos de parada en cada estación (para la hora estimada)
        // Empresa favorita: si perteneces a varias, es la que sale elegida al abrir Empresas.
        public string FavoriteCompany { get; set; }
        public string LeagueSeenMonth { get; set; }   // último mes de la liga cuyos premios ya se avisaron (aaaa-MM)
        // Mapa en vivo: enviar mi posición mientras conduzco y ver a los demás usuarios de la misma ruta.
        public bool ShareLivePosition { get; set; } = true;
        // Teleindicador elegido por tren: clave = ruta del .con, valor = subcarpeta de Destinos ("" = original).
        public Dictionary<string, string> TeleDest { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Megafonía: si suenan los avisos, a qué volumen (0-100) y la última línea elegida por ruta.
        public bool PaOn { get; set; } = true;
        public int PaVolume { get; set; } = 90;
        [JsonIgnore]   // ya no se usa (cada conducción empieza sin línea elegida): no se guarda
        public Dictionary<string, string> PaLastLine { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Favorites { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FavoriteTrains { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Imagen personalizada por tren: clave = ruta del .con, valor = ruta de la imagen.
        public Dictionary<string, string> TrainImages { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Pupitre: fondo de escala elegido para los manómetros, por máquina (clave = .eng de cabeza en
        // minúsculas), en bar. Sin entrada = la escala de la cabina (.cvf).
        public Dictionary<string, double> CabAirScaleBar { get; set; } = new Dictionary<string, double>();
        public Dictionary<string, double> CabBcScaleBar { get; set; } = new Dictionary<string, double>();

        static string Dir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails");

        // SELECTOR_PREFS_FILE: otro archivo de preferencias (lo usan los programas de prueba para no tocar
        // NUNCA las del usuario). Sin la variable, el de siempre.
        [JsonIgnore]
        private static string FilePath =>
            Environment.GetEnvironmentVariable("SELECTOR_PREFS_FILE") is string f && f.Length > 0 ? f : Path.Combine(Dir, "SelectOR.json");
        // Nombre antiguo (versiones previas): se migra automáticamente si aún existe.
        [JsonIgnore]
        private static string LegacyFilePath => Path.Combine(Dir, "MenuParalelo.json");

        public static AppPrefs Load()
        {
            try
            {
                bool otro = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SELECTOR_PREFS_FILE"));
                var path = File.Exists(FilePath) ? FilePath : (!otro && File.Exists(LegacyFilePath) ? LegacyFilePath : null);
                if (path != null)
                {
                    var json = File.ReadAllText(path);
                    var p = JsonSerializer.Deserialize<AppPrefs>(json);
                    if (p != null)
                    {
                        p.Favorites ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        p.FavoriteTrains ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        p.TrainImages ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        p.CabAirScaleBar ??= new Dictionary<string, double>();
                        p.CabBcScaleBar ??= new Dictionary<string, double>();
                        return p;
                    }
                }
            }
            catch { }
            return new AppPrefs();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }
    }
}
