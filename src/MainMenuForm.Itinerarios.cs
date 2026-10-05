// Itinerarios guardados (mapa grande → «Guardar» y pestaña «Guardados»): el itinerario marcado en el mapa, con sus
// puntos y sus paradas, se guarda en las preferencias (AppPrefs.SavedItineraries, por ruta) para cargarlo en otra
// conducción sin marcarlo a mano. Al cargarlo, si el tren ya está en mitad del itinerario (de A a F y el tren en
// C), la hoja de ruta se calcula desde donde está el tren (RoadBook.SkipReachedFrom).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace SelectOR
{
    public partial class MainMenuForm
    {
        SavedItinerary _roadSavedPending;   // pedido antes de que el grafo de vías esté listo

        static string RouteKey(string routeDir) => string.IsNullOrEmpty(routeDir) ? "" : Path.GetFileName(routeDir.TrimEnd('\\', '/'));

        // Los guardados de la ruta que se conduce, el más reciente primero.
        IReadOnlyList<SavedItinerary> SavedItinerariesHere()
        {
            string key = RouteKey(_road.RouteDir);
            if (_prefs?.SavedItineraries == null || key.Length == 0) return Array.Empty<SavedItinerary>();
            return _prefs.SavedItineraries.Where(s => string.Equals(s.Route, key, StringComparison.OrdinalIgnoreCase))
                                          .OrderByDescending(s => s.Created).ToList();
        }

        // Nombre propuesto: «primera estación → última» (o «Itinerario N»).
        string SuggestItineraryName()
        {
            var names = _road.Stops.Where(s => !s.Synthetic && !string.IsNullOrWhiteSpace(s.Name)).Select(s => s.Name).ToList();
            string n = names.Count >= 2 ? names[0] + " → " + names[^1] : names.Count == 1 ? names[0]
                     : string.Format(Tr("Itinerario {0}"), SavedItinerariesHere().Count + 1);
            string baseName = n; int k = 2;
            while (SavedItinerariesHere().Any(s => string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase))) n = $"{baseName} ({k++})";
            return n;
        }

        // «Guardar» del mapa grande. Devuelve el mensaje para la cabecera del mapa (null = cancelado).
        string SaveCurrentItinerary(IWin32Window owner)
        {
            if (_road.Points.Count == 0) return Tr("Marca primero un itinerario.");
            string name;
            using (var dlg = new TextPromptDialog(Tr("Guardar itinerario"), Tr("Nombre del itinerario (lo encontrarás en la pestaña «Guardados» del mapa grande):"),
                       SuggestItineraryName(), "", Tr("Guardar")))
            {
                dlg.TopMost = true; dlg.StartPosition = FormStartPosition.CenterScreen;
                if (dlg.ShowDialog(owner) != DialogResult.OK) return null;
                name = (dlg.Value ?? "").Trim();
            }
            if (name.Length == 0) name = SuggestItineraryName();
            if (name.Length > 80) name = name.Substring(0, 80);
            var it = new SavedItinerary
            {
                Name = name, Route = RouteKey(_road.RouteDir),
                Km = Math.Round(_road.TotalLen / 1000.0, 1),
                Halts = _road.HaltsForSave()
            };
            foreach (var p in _road.Points)
                it.Points.Add(new SavedItineraryPoint { Lat = p.Lat, Lon = p.Lon, Edge = p.Edge, Off = p.Off, Guide = p.Guide, Reverse = p.Reverse });
            // mismo nombre en la misma ruta: se sustituye
            _prefs.SavedItineraries.RemoveAll(s => string.Equals(s.Route, it.Route, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(s.Name, it.Name, StringComparison.OrdinalIgnoreCase));
            _prefs.SavedItineraries.Add(it);
            try { _prefs.Save(); } catch { }
            return string.Format(Tr("Itinerario guardado: {0}"), it.Name);
        }

        void DeleteSavedItinerary(SavedItinerary it)
        {
            if (it == null || _prefs?.SavedItineraries == null) return;
            _prefs.SavedItineraries.RemoveAll(s => s.Id == it.Id);
            try { _prefs.Save(); } catch { }
        }

        // Carga un guardado como itinerario de la conducción en curso. Devuelve el mensaje para la cabecera del mapa.
        string LoadSavedItinerary(SavedItinerary it)
        {
            if (it == null) return null;
            if (_road.Graph == null)
            {
                _roadSavedPending = it;
                EnsureRoadGraph();
                return _road.Graph == null && !_road.Building && _road.Problem != null ? _road.Problem : Tr("Preparando el esquema de vías…");
            }
            return ApplySavedItinerary(it);
        }

        string ApplySavedItinerary(SavedItinerary it)
        {
            _roadSavedPending = null;
            _road.ClearAll();
            _road.Schedule = null; _road.FromPlan = false; _road.ScheduleFirstPass = false;
            _roadPatPending = null; _roadPlanWaiting = null;
            _road.Editing = false;
            _road.SetHaltChoices(it.Halts);
            int ok = 0;
            foreach (var p in it.Points) if (_road.AddSavedPoint(p.Lat, p.Lon, p.Edge, p.Off, p.Guide, p.Reverse)) ok++;
            if (ok == 0) { _road.ClearAll(); return Tr("Ese itinerario no encaja en las vías de esta ruta."); }
            var t = RoadTrain();
            int skipped = _road.SkipReachedFrom(t.has, t.lat, t.lon);
            RoadReplan();
            int visibles(int upto) => _road.Points.Take(upto).Count(p => !p.Guide);
            return skipped > 0 && visibles(skipped) > 0
                ? string.Format(Tr("Cargado «{0}»: el tren ya está en mitad del itinerario, se calcula desde aquí."), it.Name)
                : string.Format(Tr("Cargado «{0}»."), it.Name);
        }
    }
}
