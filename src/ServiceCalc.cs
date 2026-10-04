// Explica el cálculo de la economía de un servicio, paso a paso, con los datos que guardó el servidor al
// registrarlo (services.calc, sql/calculo-servicio.sql). Los servicios anteriores no tienen esos datos:
// entonces solo se da lo que se puede saber de sus importes (el equivalente por km).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace SelectOR
{
    public static class ServiceCalc
    {
        public sealed class Line
        {
            public string Label;
            public double Amount;           // en positivo; Cost dice el signo
            public bool Cost;
            public List<string> Steps = new List<string>();
        }

        static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-ES");
        static string T(string s) => I18n.T(s);
        static string Eur(double v) => v.ToString("N2", Es) + " €";
        static string Rate(double v) => v.ToString(v != Math.Round(v, 2) ? "0.00##" : "0.00", Es) + " €";
        static string N(double v, int dec = 0) => v.ToString("N" + dec, Es);
        static string F(double v) => v.ToString("0.###", Es);   // factores: 1,25 · 0,6 · 2

        static double D(JsonElement e, string k, double def = 0)
        {
            if (e.TryGetProperty(k, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
                if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return x;
            }
            return def;
        }

        // calc = services.calc (null = servicio anterior: sin detalle). Los importes son los registrados.
        public static List<Line> Explain(JsonElement? calc, double km, double income, double canon, double energy,
                                         double salary, double rental, double maintenance, out bool hasDetail)
        {
            var lines = new List<Line>();
            hasDetail = calc.HasValue && calc.Value.ValueKind == JsonValueKind.Object;
            var inc = new Line { Label = T("Ingreso"), Amount = income };
            var can = new Line { Label = T("Cánon AI"), Amount = canon, Cost = true };
            var ene = new Line { Label = T("Energía"), Amount = energy, Cost = true };
            var sal = new Line { Label = T("Salario"), Amount = salary, Cost = true };
            var ren = new Line { Label = T("Alquiler de la unidad"), Amount = rental, Cost = true };
            var man = new Line { Label = T("Mantenimiento"), Amount = maintenance, Cost = true };

            if (!hasDetail)
            {
                if (km > 0)
                {
                    if (income > 0) inc.Steps.Add(string.Format(T("Equivale a {0} por km."), Rate(income / km)));
                    if (canon > 0) can.Steps.Add(string.Format(T("Equivale a {0} por km."), Rate(canon / km)));
                    if (energy > 0) ene.Steps.Add(string.Format(T("Equivale a {0} por km."), Rate(energy / km)));
                }
                sal.Steps.Add(T("Fijo por servicio."));
                ren.Steps.Add(T("Lo que cuesta la unidad alquilada en cada servicio."));
            }
            else
            {
                var c = calc.Value;
                km = D(c, "km", km);
                string kind = c.TryGetProperty("kind", out var kv) && kv.ValueKind == JsonValueKind.String ? kv.GetString() : "flat";
                string kmT = N(km, 2) + " km";
                double dist = D(c, "dist", 1);
                switch (kind)
                {
                    case "pax":
                    {
                        double pax = D(c, "pax"), fare = D(c, "fare"), fareRaw = D(c, "fare_raw", fare);
                        double board = pax * fare;
                        inc.Steps.Add(string.Format(T("Billete al subir: {0} viajeros × {1} = {2}"), N(pax), Rate(fare), Eur(board)));
                        double avg = D(c, "avg_kmh");
                        inc.Steps.Add("   " + string.Format(T("Billete: {0} de base + confort {1} × {2} + {3} km/h de media × {4}"),
                            Rate(D(c, "fare_base")), F(D(c, "comfort")), Rate(D(c, "fare_comfort_k")), N(avg, 1), Rate(D(c, "fare_speed_k")))
                            + (Math.Abs(fare - fareRaw) > 0.00005 ? "" : " = " + Rate(fare)));
                        if (Math.Abs(fare - fareRaw) > 0.00005)
                            inc.Steps.Add("   " + string.Format(T("= {0}; en los viajes de menos de {1} km el billete mínimo es {2}"),
                                Rate(fareRaw), N(D(c, "fare_short_km")), Rate(D(c, "fare_short_min"))));
                        double paxkm = D(c, "pax_km"), paid = D(c, "pax_km_paid", paxkm), perKm = D(c, "fare_per_km");
                        double byKm = paid * perKm;
                        inc.Steps.Add(string.Format(T("Por distancia: {0} viajeros·km × {1} = {2}"), N(paid, 1), Rate(perKm), Eur(byKm)));
                        inc.Steps.Add("   " + string.Format(T("{0} viajeros·km: la suma de los km que ha ido a bordo cada viajero."), N(paxkm, 1)));
                        if (paid < paxkm - 0.05)
                            inc.Steps.Add("   " + string.Format(T("Por encima del {0} % de ocupación ({1} viajeros·km) cada viajero·km cuenta al {2} %."),
                                N(D(c, "occ_full") * 100), N(D(c, "occ_limit"), 1), N(D(c, "occ_over") * 100)));
                        AddDistance(inc, c, dist, km);
                        inc.Steps.Add(dist > 1.00005
                            ? string.Format(T("Ingreso = ({0} + {1}) × {2} = {3}"), Eur(board), Eur(byKm), F(dist), Eur(income))
                            : string.Format(T("Ingreso = {0} + {1} = {2}"), Eur(board), Eur(byKm), Eur(income)));
                        break;
                    }
                    case "freight":
                    {
                        double rate = D(c, "income_per_km"), fm = D(c, "income_mass_factor", 1);
                        double mass = D(c, "mass"), cap = D(c, "mass_cap"), mbase = D(c, "mass_base"), exp = D(c, "mass_exp");
                        inc.Steps.Add(dist > 1.00005
                            ? string.Format(T("Mercancías: {0} × {1}/km × factor de masa {2} × bonificación por distancia {3} = {4}"), kmT, Rate(rate), F(fm), F(dist), Eur(income))
                            : string.Format(T("Mercancías: {0} × {1}/km × factor de masa {2} = {3}"), kmT, Rate(rate), F(fm), Eur(income)));
                        inc.Steps.Add("   " + string.Format(T("Factor de masa = ({0} t ÷ {1} t) elevado a {2}"),
                            N(Math.Min(mass, cap > 0 ? cap : mass)), N(mbase), F(exp))
                            + (cap > 0 && mass > cap ? " " + string.Format(T("(el tren pesa {0} t; cuentan como máximo {1} t)"), N(mass), N(cap)) : ""));
                        AddDistance(inc, c, dist, km);
                        break;
                    }
                    case "empty":
                        inc.Steps.Add(string.Format(T("Traslado en vacío (tren de viajeros sin plazas declaradas): {0} × {1}/km = {2}"), kmT, Rate(D(c, "income_per_km")), Eur(income)));
                        break;
                    case "nopax":
                        inc.Steps.Add(T("Tren de viajeros sin ningún viajero a bordo: no hay ingreso."));
                        break;
                    default:
                        inc.Steps.Add(string.Format(T("Tarifa por km (tren sin plazas ni masa conocidas): {0} × {1}/km = {2}"), kmT, Rate(D(c, "income_per_km")), Eur(income)));
                        break;
                }
                can.Steps.Add(string.Format(T("Uso de la vía: {0} × {1}/km = {2}"), kmT, Rate(D(c, "canon_per_km")), Eur(canon)));
                double emf = D(c, "energy_mass_factor", 1);
                if (Math.Abs(emf - 1) > 0.00005)
                {
                    ene.Steps.Add(string.Format(T("{0} × {1}/km × factor de masa {2} = {3}"), kmT, Rate(D(c, "energy_per_km")), F(emf), Eur(energy)));
                    ene.Steps.Add("   " + string.Format(T("Factor de masa = raíz cuadrada de ({0} t ÷ {1} t), entre 0,5 y 2"), N(D(c, "mass")), N(D(c, "mass_base"))));
                }
                else
                    ene.Steps.Add(string.Format(T("{0} × {1}/km = {2}"), kmT, Rate(D(c, "energy_per_km")), Eur(energy)));
                if (c.TryGetProperty("salary_per_hour", out _))   // salario por tiempo (salario-por-tiempo.sql)
                {
                    double h = D(c, "salary_hours"), maxH = D(c, "salary_max_hours"), dur = D(c, "duration_s");
                    sal.Steps.Add(string.Format(T("{0} fijos + {1} h de conducción × {2}/h = {3}"),
                        Eur(D(c, "salary_base")), h.ToString("0.##", Es), Rate(D(c, "salary_per_hour")), Eur(salary)));
                    if (maxH > 0 && dur > maxH * 3600 + 1)
                        sal.Steps.Add("   " + string.Format(T("Se pagan como máximo {0} h de conducción por servicio."), maxH.ToString("0.##", Es)));
                }
                else sal.Steps.Add(T("Fijo por servicio."));
                ren.Steps.Add(T("Lo que cuesta la unidad alquilada en cada servicio."));
            }
            lines.Add(inc); lines.Add(can); lines.Add(ene); lines.Add(sal);
            if (rental > 0) lines.Add(ren);
            if (maintenance > 0) { man.Steps.Add(T("Revisión de la unidad cargada a este servicio.")); lines.Add(man); }
            return lines;
        }

        // Bonificación por distancia: ×1 hasta «desde»; sube en línea recta hasta ×(1 + máx) en «completa».
        static void AddDistance(Line inc, JsonElement c, double dist, double km)
        {
            double from = D(c, "dist_from"), full = D(c, "dist_full"), max = D(c, "dist_max");
            if (dist > 1.00005)
                inc.Steps.Add("   " + string.Format(T("Bonificación por distancia ×{0}: a partir de {1} km sube poco a poco hasta ×{2} a los {3} km."),
                    F(dist), N(from), F(1 + max), N(full)));
            else if (max > 0)
                inc.Steps.Add("   " + string.Format(T("Sin bonificación por distancia: empieza a partir de {0} km."), N(from)));
        }
    }
}
