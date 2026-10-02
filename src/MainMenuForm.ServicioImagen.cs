// Composición 2D del servicio (ServiceImages.cs): al ponerse de servicio se dibuja y se sube; la lista de
// servicios en tarjetas la enseña (ServiceCards.cs).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        // Al abrirse el servicio: modelos en segundo plano, dibujo en el hilo de la interfaz (dispositivo gráfico de
        // la vista 3D) y JPEG + subida otra vez en segundo plano. Nunca retrasa ni impide el servicio.
        void PublishServiceStrip(string serviceId, string companyId, TrainItem train)
        {
            if (string.IsNullOrEmpty(serviceId) || string.IsNullOrEmpty(companyId) || string.IsNullOrEmpty(train?.FilePath)) return;
            if (!Guid.TryParse(serviceId, out _) || !Guid.TryParse(companyId, out _)) return;
            List<(string path, bool flip, string name)> models;
            try
            {
                var doc = ConsistDoc.Load(train.FilePath);
                if (doc == null || doc.Cars.Count == 0) return;
                models = doc.Cars.Select(c => (ResolveCarFile(c.Name, c.Folder), c.Flip, c.Name)).ToList();
            }
            catch { return; }
            Task.Run(async () =>
            {
                var cars = new List<(ShapeGeom geom, bool flip, string name)>();
                foreach (var (path, flip, name) in models)
                {
                    ShapeGeom g = null;
                    if (path != null)
                    {
                        lock (_geomCache) _geomCache.TryGetValue(path, out g);
                        if (g == null)
                        {
                            try { g = ShapeRenderer.BuildGeometry(path); } catch { }
                            try { ShapeRenderer.PrefetchTextures(g); } catch { }
                            if (g != null) lock (_geomCache) _geomCache[path] = g;
                        }
                    }
                    cars.Add((g, flip, name));
                }
                if (!IsHandleCreated) return;
                try
                {
                    // un modelo por turno de la interfaz (sin congelarla justo al ponerse de servicio)
                    Bitmap strip = null;
                    try { strip = await ServiceImages.ComposeStripAsync(cars, this); } catch { }
                    if (strip == null) return;
                    byte[] jpg; using (strip) jpg = ServiceImages.ToJpeg(strip);
                    ServiceImages.SaveLocal(serviceId, jpg);
                    if (await ServiceImages.UploadAsync(companyId, serviceId, jpg) == null)
                        await Supa.RpcAsync("set_service_image", new { p_service = serviceId });
                }
                catch { }
            });
        }
    }
}
