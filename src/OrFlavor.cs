// Qué variante de Open Rails es la carpeta en la que está SelectOR (SelectOR vive dentro de la de cada Open Rails).
// CYZYRails: una variante de Open Rails con un motor gráfico propio (HDR, bloom, auto-exposición, cielo astronómico,
// farolas que iluminan…); se reconoce por sus archivos de Content (AtmosphereLut.mgfx, el de su cielo físico).

using System;
using System.IO;

namespace SelectOR
{
    public static class OrFlavor
    {
        public static bool IsCyzy(string orDir = null)
        {
            try { return File.Exists(Path.Combine(orDir ?? AppContext.BaseDirectory, "Content", "AtmosphereLut.mgfx")); }
            catch { return false; }
        }
    }
}
