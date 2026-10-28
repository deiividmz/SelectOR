// Farolas de verdad en CYZYRails (OrFlavor): CYZYRails ilumina el entorno con las farolas y focos de la ruta, pero
// necesita que se le diga cuáles son y dónde está su luz (Ruta\OpenRails\CyzyLamps.txt: forma .s, posición de la luz en
// la forma, lúmenes, temperatura de color y tipo). Las de cualquier ruta las busca y las mide LampScanner (en sus
// archivos de mundo y sus .s) y se guardan por ruta (%AppData%\Open Rails\SelectOR\cyzy-farolas\): solo se vuelven a
// buscar si cambian sus archivos de mundo. En CGL, además, van las medidas a mano (Routes: mandan sobre las buscadas):
//  · al lanzar Open Rails desde SelectOR, si es CYZYRails, se pone el archivo en la ruta que se va a conducir (si ya
//    hay uno que no es de SelectOR, no se toca); si hay que buscarlas, antes, en segundo plano (Prepare);
//  · al volver (y al abrir SelectOR) se quita. Así la ruta queda como estaba y los demás Open Rails ni lo ven.
// CYZYRails usa como mucho las 24 luces más cercanas (a menos de 800 m) y solo con sus gráficos mejorados.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SelectOR
{
    public static class CyzyLamps
    {
        const string Mark = "# SelectOR: farolas de CYZYRails";

        // por el principio del nombre de la carpeta de la ruta
        static readonly (string prefix, string text)[] Routes =
        {
            ("CGL", Mark + @" para CGL (se pone al conducir con CYZYRails y se quita al volver)
# forma .s                 x      y      z      lumen  kelvin  tipo   (1.2.77: túneles a 30 lm; el resto, 1000 lm menos y después a la mitad: peticiones del 25-10)
# focos de túnel (bombilla naranja, 382 en la ruta; luz tenue: 30 lm desde la 1.2.77)
focotunel.s                0.22   0.23   0.20     30   2000    omni
# fluorescentes de túnel (tubo de 1,5 m; 128 en la ruta): blancos, 5000 K, 30 lm (1.2.77, petición)
fluortunel.s              -0.08   0.00   0.40     30   5000    omni
# farolas de forja (las tres del mismo modelo; la cabeza arriba del poste)
farola-gb.s                0.00   4.20   0.00   2000   3000    down
farola-gris-forjado.s      0.00   4.20   0.00   2000   3000    down
farola-roja.s              0.00   4.20   0.00   2000   3000    down
# farola de andén de Cercanías (fluorescente arriba; blanca, 5500 K desde la 1.2.75)
farolasud.s                0.00   4.25   0.00   1500   5500    down
# báculos con brazo (el poste en x = 2; la luminaria al final del brazo, hacia x = 0,3)
cplamp1ti.s                0.45   6.80   0.00   3500   2200    down
cplamp3ti.s                0.45   3.96   0.00   1500   2200    down
# fluorescentes del techo de las estaciones (tiras de 29 y 21 m: unas cuantas luces a lo largo; omni: también alumbran el techo)
piezaluzcipriano.s         0.20   0.00   0.50   1500   5500    omni
piezaluzcipriano.s         0.20   0.00  -6.00   1500   5500    omni
piezaluzcipriano.s         0.20   0.00 -12.50   1500   5500    omni
piezaluzcipriano.s         0.20   0.00 -19.00   1500   5500    omni
piezaluzcipriano.s         0.20   0.00 -24.50   1500   5500    omni
piezaluzcolombia3.s        0.20   0.00   0.50   1500   5500    omni
piezaluzcolombia3.s        0.20   0.00  -5.50   1500   5500    omni
piezaluzcolombia3.s        0.20   0.00 -11.50   1500   5500    omni
piezaluzcolombia3.s        0.20   0.00 -16.50   1500   5500    omni
# marquesinas de estaciones (1.2.72): luz hacia abajo, cada 4-14 m; blanca (5500 K) desde la 1.2.75
# MarqueSur (25 en la ruta): el fluorescente, 98 m a lo largo
marquesur.s                          6.00   4.15   -0.10   1250   5500    down
marquesur.s                         18.57   4.15   -0.10   1250   5500    down
marquesur.s                         31.14   4.15   -0.10   1250   5500    down
marquesur.s                         43.71   4.15   -0.10   1250   5500    down
marquesur.s                         56.29   4.15   -0.10   1250   5500    down
marquesur.s                         68.86   4.15   -0.10   1250   5500    down
marquesur.s                         81.43   4.15   -0.10   1250   5500    down
marquesur.s                         94.00   4.15   -0.10   1250   5500    down
# marquepiziasvia1_3.s: bajo la marquesina, sobre el andén (el fluorescente del borde cuelga encima de la vía; 1.2.77)
marquepiziasvia1_3.s                 2.00   4.68    2.60   1000   5500    down
marquepiziasvia1_3.s                 2.00   4.68    6.90   1000   5500    down
# marquepiziasvia1_2.s: bajo la marquesina, sobre el andén (el fluorescente del borde cuelga encima de la vía; 1.2.77)
marquepiziasvia1_2.s                 2.00   4.68    2.60   1000   5500    down
marquepiziasvia1_2.s                 2.00   4.68    6.90   1000   5500    down
# marquepiziasvia1.s: bajo la marquesina, sobre el andén (el fluorescente del borde cuelga encima de la vía; 1.2.77)
marquepiziasvia1.s                   2.00   4.68    2.60   1000   5500    down
marquepiziasvia1.s                   2.00   4.68    6.90   1000   5500    down
# marquesina_caf.s: bajo la cubierta (sin fluorescente)
marquesina_caf.s                     0.10   3.15    2.50   1000   5500    down
marquesina_caf.s                     0.10   3.15   10.00   1000   5500    down
marquesina_caf.s                     0.10   3.15   17.50   1000   5500    down
# marquesina_caf_2.s: bajo la cubierta (sin fluorescente)
marquesina_caf_2.s                   0.10   3.15    2.50   1000   5500    down
marquesina_caf_2.s                   0.10   3.15   10.00   1000   5500    down
marquesina_caf_2.s                   0.10   3.15   17.50   1000   5500    down
# marquesinasantaana_2.s: el fluorescente, 155 m a lo largo
marquesinasantaana_2.s               1.58   6.50    6.00   1250   5500    down
marquesinasantaana_2.s               1.58   6.50   18.33   1250   5500    down
marquesinasantaana_2.s               1.58   6.50   30.67   1250   5500    down
marquesinasantaana_2.s               1.58   6.50   43.00   1250   5500    down
marquesinasantaana_2.s               1.58   6.50   55.33   1250   5500    down
marquesinasantaana_2.s               1.58   6.50   67.67   1250   5500    down
marquesinasantaana_2.s               1.58   6.50   80.00   1250   5500    down
marquesinasantaana_2.s               1.58   6.50   92.33   1250   5500    down
marquesinasantaana_2.s               1.58   6.50  104.67   1250   5500    down
marquesinasantaana_2.s               1.58   6.50  117.00   1250   5500    down
marquesinasantaana_2.s               1.58   6.50  129.33   1250   5500    down
marquesinasantaana_2.s               1.58   6.50  141.67   1250   5500    down
marquesinasantaana_2.s               1.58   6.50  154.00   1250   5500    down
# marquesinasantaana_1.s: el fluorescente, 155 m a lo largo
marquesinasantaana_1.s               1.58   6.50    6.00   1250   5500    down
marquesinasantaana_1.s               1.58   6.50   18.33   1250   5500    down
marquesinasantaana_1.s               1.58   6.50   30.67   1250   5500    down
marquesinasantaana_1.s               1.58   6.50   43.00   1250   5500    down
marquesinasantaana_1.s               1.58   6.50   55.33   1250   5500    down
marquesinasantaana_1.s               1.58   6.50   67.67   1250   5500    down
marquesinasantaana_1.s               1.58   6.50   80.00   1250   5500    down
marquesinasantaana_1.s               1.58   6.50   92.33   1250   5500    down
marquesinasantaana_1.s               1.58   6.50  104.67   1250   5500    down
marquesinasantaana_1.s               1.58   6.50  117.00   1250   5500    down
marquesinasantaana_1.s               1.58   6.50  129.33   1250   5500    down
marquesinasantaana_1.s               1.58   6.50  141.67   1250   5500    down
marquesinasantaana_1.s               1.58   6.50  154.00   1250   5500    down
# Colomé: bajo la cubierta entre sus postes (sin fluorescente)
colome.s                             2.80   4.20   27.00   1250   5500    down
colome.s                             2.80   4.20   38.43   1250   5500    down
colome.s                             2.80   4.20   49.86   1250   5500    down
colome.s                             2.80   4.20   61.29   1250   5500    down
colome.s                             2.80   4.20   72.71   1250   5500    down
colome.s                             2.80   4.20   84.14   1250   5500    down
colome.s                             2.80   4.20   95.57   1250   5500    down
colome.s                             2.80   4.20  107.00   1250   5500    down
# marqueaerop.s: bajo la cubierta (18 × 10 m), fuera de las dos vías que cubre (x≈5 y 10; 1.2.77)
marqueaerop.s                        2.00   3.45    3.00   1000   5500    down
marqueaerop.s                       14.00   3.45    3.00   1000   5500    down
marqueaerop.s                       16.00   3.45    3.00   1000   5500    down
marqueaerop.s                        2.00   3.45    7.00   1000   5500    down
marqueaerop.s                       14.00   3.45    7.00   1000   5500    down
marqueaerop.s                       16.00   3.45    7.00   1000   5500    down
# marqueaeropcc.s: bajo la cubierta (18 × 10 m), fuera de las dos vías que cubre (x≈5 y 10; 1.2.77)
marqueaeropcc.s                      2.00   3.45    3.00   1000   5500    down
marqueaeropcc.s                     14.00   3.45    3.00   1000   5500    down
marqueaeropcc.s                     16.00   3.45    3.00   1000   5500    down
marqueaeropcc.s                      2.00   3.45    7.00   1000   5500    down
marqueaeropcc.s                     14.00   3.45    7.00   1000   5500    down
marqueaeropcc.s                     16.00   3.45    7.00   1000   5500    down
# marqueaeropsc.s: bajo la cubierta (18 × 10 m), fuera de las dos vías que cubre (x≈5 y 10; 1.2.77)
marqueaeropsc.s                      2.00   3.45    3.00   1000   5500    down
marqueaeropsc.s                     14.00   3.45    3.00   1000   5500    down
marqueaeropsc.s                     16.00   3.45    3.00   1000   5500    down
marqueaeropsc.s                      2.00   3.45    7.00   1000   5500    down
marqueaeropsc.s                     14.00   3.45    7.00   1000   5500    down
marqueaeropsc.s                     16.00   3.45    7.00   1000   5500    down
# marqueaeropfinal.s: bajo las vigas de la cubierta (18 × 10 m)
marqueaeropfinal.s                   3.00   3.45    3.00   1000   5500    down
marqueaeropfinal.s                   9.00   3.45    3.00   1000   5500    down
marqueaeropfinal.s                  15.00   3.45    3.00   1000   5500    down
marqueaeropfinal.s                   3.00   3.45    7.00   1000   5500    down
marqueaeropfinal.s                   9.00   3.45    7.00   1000   5500    down
marqueaeropfinal.s                  15.00   3.45    7.00   1000   5500    down
# apd_filella_edif.s: el techo de fluorescentes (13 m de ancho); luces sobre los dos andenes (x 0-4,2 y 12,3-16,2), no sobre las vías (1.2.77)
apd_filella_edif.s                   2.20   5.00    2.00   1250   5500    down
apd_filella_edif.s                  14.20   5.00    2.00   1250   5500    down
apd_filella_edif.s                   2.20   5.00   15.29   1250   5500    down
apd_filella_edif.s                  14.20   5.00   15.29   1250   5500    down
apd_filella_edif.s                   2.20   5.00   28.57   1250   5500    down
apd_filella_edif.s                  14.20   5.00   28.57   1250   5500    down
apd_filella_edif.s                   2.20   5.00   41.86   1250   5500    down
apd_filella_edif.s                  14.20   5.00   41.86   1250   5500    down
apd_filella_edif.s                   2.20   5.00   55.14   1250   5500    down
apd_filella_edif.s                  14.20   5.00   55.14   1250   5500    down
apd_filella_edif.s                   2.20   5.00   68.43   1250   5500    down
apd_filella_edif.s                  14.20   5.00   68.43   1250   5500    down
apd_filella_edif.s                   2.20   5.00   81.71   1250   5500    down
apd_filella_edif.s                  14.20   5.00   81.71   1250   5500    down
apd_filella_edif.s                   2.20   5.00   95.00   1250   5500    down
apd_filella_edif.s                  14.20   5.00   95.00   1250   5500    down
# apd_filella.s: el techo de fluorescentes (13 m de ancho); luces sobre los dos andenes (x 0-4,2 y 12,3-16,2), no sobre las vías (1.2.77)
apd_filella.s                        2.20   5.00    5.00   1250   5500    down
apd_filella.s                       14.20   5.00    5.00   1250   5500    down
apd_filella.s                        2.20   5.00   20.17   1250   5500    down
apd_filella.s                       14.20   5.00   20.17   1250   5500    down
apd_filella.s                        2.20   5.00   35.33   1250   5500    down
apd_filella.s                       14.20   5.00   35.33   1250   5500    down
apd_filella.s                        2.20   5.00   50.50   1250   5500    down
apd_filella.s                       14.20   5.00   50.50   1250   5500    down
apd_filella.s                        2.20   5.00   65.67   1250   5500    down
apd_filella.s                       14.20   5.00   65.67   1250   5500    down
apd_filella.s                        2.20   5.00   80.83   1250   5500    down
apd_filella.s                       14.20   5.00   80.83   1250   5500    down
apd_filella.s                        2.20   5.00   96.00   1250   5500    down
apd_filella.s                       14.20   5.00   96.00   1250   5500    down
# andenpgrandeconmarq.s: el fluorescente
andenpgrandeconmarq.s               -0.53   5.15    2.60   1000   5500    down
andenpgrandeconmarq.s               -0.53   5.15    7.30   1000   5500    down
# andenpgrandeconmarq_carteleria1.s: el fluorescente
andenpgrandeconmarq_carteleria1.s   -0.53   5.15    2.60   1000   5500    down
andenpgrandeconmarq_carteleria1.s   -0.53   5.15    7.30   1000   5500    down
# andenpgrandeconmarq_carteleria2.s: el fluorescente
andenpgrandeconmarq_carteleria2.s   -0.53   5.15    2.60   1000   5500    down
andenpgrandeconmarq_carteleria2.s   -0.53   5.15    7.30   1000   5500    down
# andenpgrandeconmarq_carteleria3.s: el fluorescente
andenpgrandeconmarq_carteleria3.s   -0.53   5.15    2.60   1000   5500    down
andenpgrandeconmarq_carteleria3.s   -0.53   5.15    7.30   1000   5500    down
# andenpgrandeconmarq_carteleria4.s: el fluorescente
andenpgrandeconmarq_carteleria4.s   -0.53   5.15    2.60   1000   5500    down
andenpgrandeconmarq_carteleria4.s   -0.53   5.15    7.30   1000   5500    down
# Susana, brazo corto: bajo la cubierta
susana_marquesina_brazo_corto.s      2.20   4.95    4.50   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   16.25   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   28.00   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   39.75   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   51.50   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   63.25   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   75.00   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   86.75   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95   98.50   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95  110.25   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95  122.00   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95  133.75   1250   5500    down
susana_marquesina_brazo_corto.s      2.20   4.95  145.50   1250   5500    down
# Susana, brazo largo (10 m de ancho): dos filas bajo la cubierta
susana_marquesina_brazo_largo.s     -2.40   4.95    5.00   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95    5.00   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   16.67   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   16.67   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   28.33   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   28.33   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   40.00   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   40.00   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   51.67   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   51.67   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   63.33   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   63.33   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   75.00   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   75.00   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   86.67   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   86.67   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95   98.33   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95   98.33   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95  110.00   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95  110.00   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95  121.67   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95  121.67   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95  133.33   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95  133.33   1000   5500    down
susana_marquesina_brazo_largo.s     -2.40   4.95  145.00   1000   5500    down
susana_marquesina_brazo_largo.s      2.80   4.95  145.00   1000   5500    down
# SusanaEdif: la marquesina del andén
susanaedif.s                        12.00   5.25    3.00   1000   5500    down
susanaedif.s                        16.00   5.25    3.00   1000   5500    down
susanaedif.s                        12.00   5.25   11.67   1000   5500    down
susanaedif.s                        16.00   5.25   11.67   1000   5500    down
susanaedif.s                        12.00   5.25   20.33   1000   5500    down
susanaedif.s                        16.00   5.25   20.33   1000   5500    down
susanaedif.s                        12.00   5.25   29.00   1000   5500    down
susanaedif.s                        16.00   5.25   29.00   1000   5500    down
# SusanaEdif: los soportales
susanaedif.s                         6.30   4.50   12.00   1000   5500    down
susanaedif.s                         6.30   4.50   20.00   1000   5500    down
susanaedif.s                         6.30   4.50   28.00   1000   5500    down
# Marquesina de Cercanías sencilla (7 en la ruta): su fluorescente, 3,5 m
marquesina_cercanias_simple.s       -0.02   2.90    1.40    250   5500    down
marquesina_cercanias_simple.s       -0.02   2.90    3.50    250   5500    down
# Marquesina de Cercanías larga (2 en la ruta): su fluorescente, 8 m
marquesina_cercanias_larga.s         0.00   2.83   -2.80    250   5500    down
marquesina_cercanias_larga.s         0.00   2.83    0.20    250   5500    down
marquesina_cercanias_larga.s         0.00   2.83    3.20    250   5500    down
# Marquesina de Cercanías larga C4 (4 en la ruta): su fluorescente, 8 m
marquesina_cercanias_larga_c4.s      0.00   2.83   -2.80    250   5500    down
marquesina_cercanias_larga_c4.s      0.00   2.83    0.20    250   5500    down
marquesina_cercanias_larga_c4.s      0.00   2.83    3.20    250   5500    down
# NovoEdificio: la marquesina delante del edificio (19 × 4 m, a 4,5 m)
novoedificio.s                       2.00   4.40   11.20   1000   5500    down
novoedificio.s                       7.00   4.40   11.20   1000   5500    down
novoedificio.s                      12.00   4.40   11.20   1000   5500    down
novoedificio.s                      17.00   4.40   11.20   1000   5500    down
# Central (apeadero): bajo el alero de la caseta blanca
central_apeadero.s                   1.20   2.25   25.00    500   5500    down
# EdifSur: la gran marquesina (22 × 112 m), fila x = 10
edifsur.s                           10.00   4.75    7.00   1250   5500    down
edifsur.s                           10.00   4.75   20.75   1250   5500    down
edifsur.s                           10.00   4.75   34.50   1250   5500    down
edifsur.s                           10.00   4.75   48.25   1250   5500    down
edifsur.s                           10.00   4.75   62.00   1250   5500    down
edifsur.s                           10.00   4.75   75.75   1250   5500    down
edifsur.s                           10.00   4.75   89.50   1250   5500    down
edifsur.s                           10.00   4.75  103.25   1250   5500    down
edifsur.s                           10.00   4.75  117.00   1250   5500    down
# EdifSur: la gran marquesina (22 × 112 m), fila x = 20
edifsur.s                           20.00   4.75    7.00   1250   5500    down
edifsur.s                           20.00   4.75   20.75   1250   5500    down
edifsur.s                           20.00   4.75   34.50   1250   5500    down
edifsur.s                           20.00   4.75   48.25   1250   5500    down
edifsur.s                           20.00   4.75   62.00   1250   5500    down
edifsur.s                           20.00   4.75   75.75   1250   5500    down
edifsur.s                           20.00   4.75   89.50   1250   5500    down
edifsur.s                           20.00   4.75  103.25   1250   5500    down
edifsur.s                           20.00   4.75  117.00   1250   5500    down
# HermanosEdif: marquesina del andén x = 0.7 (a 2,4 m)
hermanosedif.s                       0.70   2.35   25.00    750   5500    down
hermanosedif.s                       0.70   2.35   31.00    750   5500    down
hermanosedif.s                       0.70   2.35   37.00    750   5500    down
hermanosedif.s                       0.70   2.35   43.00    750   5500    down
# HermanosEdif: marquesina del andén x = 27.7 (a 2,4 m)
hermanosedif.s                      27.70   2.35   25.00    750   5500    down
hermanosedif.s                      27.70   2.35   31.00    750   5500    down
hermanosedif.s                      27.70   2.35   37.00    750   5500    down
hermanosedif.s                      27.70   2.35   43.00    750   5500    down
# MarqueGenzor (12 en la ruta): su fluorescente
marquegenzor.s                       2.05   3.42    2.50    750   5500    down
marquegenzor.s                       2.05   3.42    7.20    750   5500    down
# Aranda: marquesina del andén x = 4.9 (6 × 80 m, a 6 m)
aranda.s                             4.90   5.90   17.00   1250   5500    down
aranda.s                             4.90   5.90   27.86   1250   5500    down
aranda.s                             4.90   5.90   38.71   1250   5500    down
aranda.s                             4.90   5.90   49.57   1250   5500    down
aranda.s                             4.90   5.90   60.43   1250   5500    down
aranda.s                             4.90   5.90   71.29   1250   5500    down
aranda.s                             4.90   5.90   82.14   1250   5500    down
aranda.s                             4.90   5.90   93.00   1250   5500    down
# Aranda: marquesina del andén x = 22.9 (6 × 80 m, a 6 m)
aranda.s                            22.90   5.90   17.00   1250   5500    down
aranda.s                            22.90   5.90   27.86   1250   5500    down
aranda.s                            22.90   5.90   38.71   1250   5500    down
aranda.s                            22.90   5.90   49.57   1250   5500    down
aranda.s                            22.90   5.90   60.43   1250   5500    down
aranda.s                            22.90   5.90   71.29   1250   5500    down
aranda.s                            22.90   5.90   82.14   1250   5500    down
aranda.s                            22.90   5.90   93.00   1250   5500    down
# Pere_Comas: el fluorescente de la marquesina del andén x = 2.8 (80 m, a 4,3 m)
pere_comas.s                         2.82   4.22    7.00   1000   5500    down
pere_comas.s                         2.82   4.22   17.86   1000   5500    down
pere_comas.s                         2.82   4.22   28.71   1000   5500    down
pere_comas.s                         2.82   4.22   39.57   1000   5500    down
pere_comas.s                         2.82   4.22   50.43   1000   5500    down
pere_comas.s                         2.82   4.22   61.29   1000   5500    down
pere_comas.s                         2.82   4.22   72.14   1000   5500    down
pere_comas.s                         2.82   4.22   83.00   1000   5500    down
# Pere_Comas: el fluorescente de la marquesina del andén x = 14.8 (80 m, a 4,3 m)
pere_comas.s                        14.85   4.22    7.00   1000   5500    down
pere_comas.s                        14.85   4.22   17.86   1000   5500    down
pere_comas.s                        14.85   4.22   28.71   1000   5500    down
pere_comas.s                        14.85   4.22   39.57   1000   5500    down
pere_comas.s                        14.85   4.22   50.43   1000   5500    down
pere_comas.s                        14.85   4.22   61.29   1000   5500    down
pere_comas.s                        14.85   4.22   72.14   1000   5500    down
pere_comas.s                        14.85   4.22   83.00   1000   5500    down
# carteles publicitarios (1.2.75, 6100 K): focos a 4 m de cada cara del cartel de tres caras, a media altura
# Mercadona: el cartel del poste (8 m de alto, 55 m por cara, a 68-76 m)
mercadona.s                         126.67  72.10   -5.90   2500   6100    omni
mercadona.s                         126.67  72.10    5.20   2500   6100    omni
mercadona.s                         126.67  72.10   16.29   2500   6100    omni
mercadona.s                         126.67  72.10   27.39   2500   6100    omni
mercadona.s                         126.67  72.10   38.49   2500   6100    omni
mercadona.s                          89.97  72.10   20.59   2500   6100    omni
mercadona.s                          96.65  72.10   26.47   2500   6100    omni
mercadona.s                         103.33  72.10   32.35   2500   6100    omni
mercadona.s                         110.01  72.10   38.23   2500   6100    omni
mercadona.s                         116.69  72.10   44.10   2500   6100    omni
mercadona.s                          90.15  72.10    8.89   2500   6100    omni
mercadona.s                          96.83  72.10    3.67   2500   6100    omni
mercadona.s                         103.51  72.10   -1.55   2500   6100    omni
mercadona.s                         110.19  72.10   -6.77   2500   6100    omni
mercadona.s                         116.87  72.10  -11.99   2500   6100    omni
# PovedaCartel: el cartel del poste (16 m de alto, 55 m por cara, a 68-84 m)
povedacartel.s                       16.48  76.10  -20.02   2500   6100    omni
povedacartel.s                       16.48  76.10   -8.92   2500   6100    omni
povedacartel.s                       16.48  76.10    2.17   2500   6100    omni
povedacartel.s                       16.48  76.10   13.27   2500   6100    omni
povedacartel.s                       16.48  76.10   24.37   2500   6100    omni
povedacartel.s                      -20.23  76.10    6.47   2500   6100    omni
povedacartel.s                      -13.55  76.10   12.35   2500   6100    omni
povedacartel.s                       -6.87  76.10   18.23   2500   6100    omni
povedacartel.s                       -0.18  76.10   24.11   2500   6100    omni
povedacartel.s                        6.50  76.10   29.98   2500   6100    omni
povedacartel.s                      -20.05  76.10   -5.23   2500   6100    omni
povedacartel.s                      -13.37  76.10  -10.45   2500   6100    omni
povedacartel.s                       -6.69  76.10  -15.67   2500   6100    omni
povedacartel.s                       -0.01  76.10  -20.89   2500   6100    omni
povedacartel.s                        6.68  76.10  -26.11   2500   6100    omni
"),
            ("Via Estrecha", Mark + @" para Via Estrecha (se pone al conducir con CYZYRails y se quita al volver)
# forma .s                 x      y      z      lumen  kelvin  tipo   (1.2.77, petición 2026-10-25: blancas y con los lúmenes de CGL, salvo lo indicado)
# estaciones: sobre los andenes (su textura ANDEN…), cada 10 m, bajo el techo; ninguna a menos de 1,6 m de una vía (comprobado en la ruta)
# Espanya (andén central, subterránea): sobre el andén, cada 10 m, bajo el techo
EspanyaCentral.S               2.50   3.55    4.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   14.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   24.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   34.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   44.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   54.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   64.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   74.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   84.50   1250   5500    down
EspanyaCentral.S               2.50   3.55   94.50   1250   5500    down
EspanyaCentral.S               2.50   3.55  104.50   1250   5500    down
EspanyaCentral.S               2.50   3.55  114.50   1250   5500    down
# Espanya (andenes laterales): sobre los dos andenes, cada 10 m
EspanyaNormal.S                3.00   3.60   14.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   14.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   24.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   24.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   34.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   34.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   44.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   44.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   54.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   54.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   64.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   64.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   74.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   74.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   84.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   84.50   1250   5500    down
EspanyaNormal.S                3.00   3.60   94.50   1250   5500    down
EspanyaNormal.S               12.00   3.60   94.50   1250   5500    down
EspanyaNormal.S                3.00   3.60  104.50   1250   5500    down
EspanyaNormal.S               12.00   3.60  104.50   1250   5500    down
EspanyaNormal.S                3.00   3.60  114.50   1250   5500    down
EspanyaNormal.S               12.00   3.60  114.50   1250   5500    down
EspanyaNormal.S                3.00   3.60  124.50   1250   5500    down
EspanyaNormal.S               12.00   3.60  124.50   1250   5500    down
EspanyaNormal.S                3.00   3.60  134.50   1250   5500    down
EspanyaNormal.S               12.00   3.60  134.50   1250   5500    down
# Magòria (andén central): sobre el andén, cada 10 m
Magoria.S                      9.00   3.42   45.50   1250   5500    down
Magoria.S                      9.00   3.42   55.50   1250   5500    down
Magoria.S                      9.00   3.42   65.50   1250   5500    down
Magoria.S                      9.00   3.42   75.50   1250   5500    down
Magoria.S                      9.00   3.42   85.50   1250   5500    down
Magoria.S                      9.00   3.42   95.50   1250   5500    down
Magoria.S                      9.00   3.42  105.50   1250   5500    down
Magoria.S                      9.00   3.42  115.50   1250   5500    down
Magoria.S                      9.00   3.42  125.50   1250   5500    down
Magoria.S                      9.00   3.42  135.50   1250   5500    down
Magoria.S                      9.00   3.42  145.50   1250   5500    down
Magoria.S                      9.00   3.42  155.50   1250   5500    down
Magoria.S                      9.00   3.59  165.50   1250   5500    down
Magoria.S                      9.00   3.59  175.50   1250   5500    down
# Sant Ildefons: sobre los dos andenes, cada 10 m (a la altura de los fluorescentes de las de su tipo)
Ildefons.S                     2.00   3.55    2.50   1250   5500    down
Ildefons.S                    12.50   3.55    2.50   1250   5500    down
Ildefons.S                     2.00   3.55   12.50   1250   5500    down
Ildefons.S                    12.50   3.55   12.50   1250   5500    down
Ildefons.S                     2.00   3.55   22.50   1250   5500    down
Ildefons.S                    12.50   3.55   22.50   1250   5500    down
Ildefons.S                     2.00   3.55   32.50   1250   5500    down
Ildefons.S                    12.50   3.55   32.50   1250   5500    down
Ildefons.S                     2.00   3.55   42.50   1250   5500    down
Ildefons.S                    12.50   3.55   42.50   1250   5500    down
Ildefons.S                     2.00   3.55   52.50   1250   5500    down
Ildefons.S                    12.50   3.55   52.50   1250   5500    down
Ildefons.S                     2.00   3.55   62.50   1250   5500    down
Ildefons.S                    12.50   3.55   62.50   1250   5500    down
Ildefons.S                     2.00   3.55   72.50   1250   5500    down
Ildefons.S                    12.50   3.55   72.50   1250   5500    down
Ildefons.S                     2.00   3.55   82.50   1250   5500    down
Ildefons.S                    12.50   3.55   82.50   1250   5500    down
Ildefons.S                    -0.50   3.55   92.50   1250   5500    down
Ildefons.S                    15.00   3.55   92.50   1250   5500    down
Ildefons.S                     2.50   3.55  102.50   1250   5500    down
Ildefons.S                    12.00   3.55  102.50   1250   5500    down
# Gornal: sobre los dos andenes, a la altura de sus fluorescentes (3,2 m)
Gornal.S                       2.00   3.20    2.50   1250   5500    down
Gornal.S                      12.50   3.20    2.50   1250   5500    down
Gornal.S                       2.00   3.16   12.50   1250   5500    down
Gornal.S                      12.50   3.16   12.50   1250   5500    down
Gornal.S                       2.00   3.16   22.50   1250   5500    down
Gornal.S                      12.50   3.16   22.50   1250   5500    down
Gornal.S                       2.00   3.16   32.50   1250   5500    down
Gornal.S                      12.50   3.16   32.50   1250   5500    down
Gornal.S                       2.00   3.16   42.50   1250   5500    down
Gornal.S                      12.50   3.16   42.50   1250   5500    down
Gornal.S                       2.00   3.16   52.50   1250   5500    down
Gornal.S                      12.50   3.16   52.50   1250   5500    down
Gornal.S                       2.00   3.16   62.50   1250   5500    down
Gornal.S                      12.50   3.16   62.50   1250   5500    down
Gornal.S                       2.00   3.16   72.50   1250   5500    down
Gornal.S                      12.50   3.16   72.50   1250   5500    down
Gornal.S                       2.00   3.16   82.50   1250   5500    down
Gornal.S                      12.50   3.16   82.50   1250   5500    down
Gornal.S                      -0.50   3.55   92.50   1250   5500    down
Gornal.S                      15.00   3.55   92.50   1250   5500    down
Gornal.S                      -0.50   3.55  102.50   1250   5500    down
Gornal.S                      15.00   2.79  102.50   1250   5500    down
# Sant Josep: sobre los dos andenes, a la altura de sus fluorescentes
SantJosep.S                    2.00   3.20    2.50   1250   5500    down
SantJosep.S                   12.50   3.20    2.50   1250   5500    down
SantJosep.S                    2.00   3.16   12.50   1250   5500    down
SantJosep.S                   12.50   3.16   12.50   1250   5500    down
SantJosep.S                    2.00   3.16   22.50   1250   5500    down
SantJosep.S                   12.50   3.16   22.50   1250   5500    down
SantJosep.S                    2.00   3.16   32.50   1250   5500    down
SantJosep.S                   12.50   3.16   32.50   1250   5500    down
SantJosep.S                    2.00   3.16   42.50   1250   5500    down
SantJosep.S                   12.50   3.16   42.50   1250   5500    down
SantJosep.S                    2.00   3.16   52.50   1250   5500    down
SantJosep.S                   12.50   3.16   52.50   1250   5500    down
SantJosep.S                    2.00   3.16   62.50   1250   5500    down
SantJosep.S                   12.50   3.16   62.50   1250   5500    down
SantJosep.S                    2.00   3.16   72.50   1250   5500    down
SantJosep.S                   12.50   3.16   72.50   1250   5500    down
SantJosep.S                    2.00   3.16   82.50   1250   5500    down
SantJosep.S                   12.50   3.16   82.50   1250   5500    down
SantJosep.S                   -3.50   3.55   92.50   1250   5500    down
SantJosep.S                   15.00   3.55   92.50   1250   5500    down
# L'Hospitalet (andenes): sobre los andenes, cada 10 m
HospiAndenes.S                 8.00   2.42    3.50   1250   5500    down
HospiAndenes.S                -1.50   2.42   13.50   1250   5500    down
HospiAndenes.S                 9.00   2.42   13.50   1250   5500    down
HospiAndenes.S                -2.00   2.42   23.50   1250   5500    down
HospiAndenes.S                 8.00   2.42   23.50   1250   5500    down
HospiAndenes.S                11.00   2.42   23.50   1250   5500    down
HospiAndenes.S                -2.50   2.42   33.50   1250   5500    down
HospiAndenes.S                 9.50   2.42   33.50   1250   5500    down
HospiAndenes.S                -2.50   2.42   43.50   1250   5500    down
HospiAndenes.S                 8.00   2.42   43.50   1250   5500    down
HospiAndenes.S                -2.50   2.42   53.50   1250   5500    down
HospiAndenes.S                 9.50   2.42   53.50   1250   5500    down
HospiAndenes.S                -2.50   2.42   63.50   1250   5500    down
HospiAndenes.S                 9.50   2.42   63.50   1250   5500    down
HospiAndenes.S                -2.50   2.42   73.50   1250   5500    down
HospiAndenes.S                 9.50   2.42   73.50   1250   5500    down
HospiAndenes.S                -2.50   2.42   83.50   1250   5500    down
HospiAndenes.S                 9.50   2.42   83.50   1250   5500    down
HospiAndenes.S                -2.50   2.42   93.50   1250   5500    down
HospiAndenes.S                 9.50   2.42   93.50   1250   5500    down
HospiAndenes.S                -2.50   2.42  103.50   1250   5500    down
HospiAndenes.S                 9.50   2.42  103.50   1250   5500    down
HospiAndenes.S                -2.00   2.42  113.50   1250   5500    down
HospiAndenes.S                 9.50   2.42  113.50   1250   5500    down
HospiAndenes.S                -2.00   2.42  123.50   1250   5500    down
HospiAndenes.S                 9.50   2.42  123.50   1250   5500    down
HospiAndenes.S                -1.50   2.42  133.50   1250   5500    down
HospiAndenes.S                 9.00   2.42  133.50   1250   5500    down
# Sant Boi: bajo las marquesinas de los andenes
SantBoi.s                     -6.00   3.20  -30.50   1000   5500    down
SantBoi.s                    -10.50   3.46  -20.50   1000   5500    down
SantBoi.s                    -11.00   2.80  -10.50   1000   5500    down
SantBoi.s                     -5.50   3.60   -0.50   1000   5500    down
SantBoi.s                     18.50   3.60   -0.50   1000   5500    down
SantBoi.s                      3.00   3.60    9.50   1000   5500    down
SantBoi.s                     19.00   3.60    9.50   1000   5500    down
SantBoi.s                      3.00   3.60   19.50   1000   5500    down
SantBoi.s                     19.00   3.60   19.50   1000   5500    down
SantBoi.s                      3.00   3.60   29.50   1000   5500    down
SantBoi.s                     16.00   3.60   29.50   1000   5500    down
SantBoi.s                      3.00   3.60   39.50   1000   5500    down
SantBoi.s                     16.00   3.60   39.50   1000   5500    down
# Colònia Güell: bajo la parte cubierta de los andenes
ColoniaGuell.S                -2.50   3.55    4.50   1000   5500    down
ColoniaGuell.S                -1.00   3.55   14.50   1000   5500    down
ColoniaGuell.S                 9.50   3.55   14.50   1000   5500    down
ColoniaGuell.S                -1.00   3.55   24.50   1000   5500    down
ColoniaGuell.S                 9.50   3.55   24.50   1000   5500    down
ColoniaGuell.S                -1.00   3.55   34.50   1000   5500    down
ColoniaGuell.S                 9.50   3.55   34.50   1000   5500    down
ColoniaGuell.S                -1.00   3.55   44.50   1000   5500    down
ColoniaGuell.S                 9.50   3.55   44.50   1000   5500    down
# Manresa Alta: bajo las marquesinas
manresaalta.S                 38.50   2.80   -8.50   1000   5500    down
manresaalta.S                 38.50   2.80    1.50   1000   5500    down
manresaalta.S                 38.50   2.80   11.50   1000   5500    down
manresaalta.S                 38.50   2.80   21.50   1000   5500    down
manresaalta.S                 38.50   2.80   31.50   1000   5500    down
manresaalta.S                 38.50   2.80   41.50   1000   5500    down
manresaalta.S                 38.50   2.80   51.50   1000   5500    down
manresaalta.S                 39.00   2.80   61.50   1000   5500    down
manresaalta.S                 15.00   4.25   71.50   1000   5500    down
manresaalta.S                 44.00   2.80   71.50   1000   5500    down
manresaalta.S                 13.00   4.25   81.50   1000   5500    down
manresaalta.S                 13.00   4.25   91.50   1000   5500    down
manresaalta.S                 16.00   4.00  101.50   1000   5500    down
manresaalta.S                 -2.50   3.50  121.50   1000   5500    down
# marquesina pequeña (3 × 9 m): dos luces bajo el techo
MarquesinaB.s                 -1.40   2.90   -2.20    500   5500    down
MarquesinaB.s                 -1.40   2.90    2.20    500   5500    down
# marquesina FGC (35 m): cada 9 m, por el centro
MARQUESINAFGC.S                2.00   2.85    4.40   1000   5500    down
MARQUESINAFGC.S                2.00   2.85   13.20   1000   5500    down
MARQUESINAFGC.S                2.00   2.85   22.00   1000   5500    down
MARQUESINAFGC.S                2.00   2.85   30.90   1000   5500    down
# marquesina FGC moderna: en sus tubos de luz
MarquesinaFGCModerna.S         2.70   3.78    3.00   1000   5500    down
MarquesinaFGCModerna.S         2.70   3.78   11.00   1000   5500    down
MarquesinaFGCModerna.S         2.70   3.78   19.00   1000   5500    down
MarquesinaFGCModerna.S         2.70   3.78   27.00   1000   5500    down
MarquesinaFGCModerna.S         2.70   3.78   35.00   1000   5500    down
MarquesinaFGCModerna.S         2.70   3.78   43.00   1000   5500    down
MarquesinaFGCModerna.S         2.70   3.78   50.00   1000   5500    down
# marquesina de Molí Nou: cada 8 m, por el centro
MarqueMoli.S                   1.50   2.25    4.00   1000   5500    down
MarqueMoli.S                   1.50   2.25   12.00   1000   5500    down
MarqueMoli.S                   1.50   2.25   20.00   1000   5500    down
MarqueMoli.S                   1.50   2.25   28.00   1000   5500    down
MarqueMoli.S                   1.50   2.25   35.00   1000   5500    down
# Claramunt: bajo la marquesina
Claramunt.S                   -2.50   2.45    3.00   1000   5500    down
Claramunt.S                   -2.50   2.45   11.00   1000   5500    down
Claramunt.S                   -2.50   2.45   19.00   1000   5500    down
# edificio de Molí Nou: luz en el interior (vestíbulo, planta superior y pasillo)
EdifMoliNou.S                 34.00   4.60    0.00   1000   5500    down
EdifMoliNou.S                 34.00   4.60    8.00   1000   5500    down
EdifMoliNou.S                 50.00   4.60    0.00   1000   5500    down
EdifMoliNou.S                 50.00   4.60    8.00   1000   5500    down
EdifMoliNou.S                 36.00  10.20    3.00   1000   5500    down
EdifMoliNou.S                 48.00  10.20    3.00   1000   5500    down
EdifMoliNou.S                -57.00  13.40    3.35   1000   5500    down
EdifMoliNou.S                -44.00  13.40    3.35   1000   5500    down
EdifMoliNou.S                -31.00  13.40    3.35   1000   5500    down
EdifMoliNou.S                -18.00  13.40    3.35   1000   5500    down
EdifMoliNou.S                 -5.00  13.40    3.35   1000   5500    down
EdifMoliNou.S                  8.00  13.40    3.35   1000   5500    down
EdifMoliNou.S                 21.00  13.40    3.35   1000   5500    down
# lámpara colgada: la bombilla
LamparaFira.S                  0.32   0.05    0.33   1500   5500    down
# farola con brazo: luz amarilla, como las farolas
VLILRU25PSV.s                  0.00   7.60   -0.30   2000   2200    down
# farola alta: blanca
VLILYT40DMV.s                  0.00  11.30   -0.36   2000   5500    down
# farola de poste: blanca
BKDSlupWZOUS400.s              0.00  11.10   -2.50   2000   5500    down
"),
            ("Línea 9", Mark + @" para Línea 9 Metro de Madrid (se pone al conducir con CYZYRails y se quita al volver)
# forma .s                    x      y        z    lumen  kelvin  tipo   (1.2.81, petición 2026-10-27: todo omni, al triple de lúmenes y blancas: 7000 K en estaciones y piezas)
# estaciones (andenes de 0 a 4 m y de 13 a 17 m, 114 m de largo): las dos filas de fluorescentes sobre el borde de los
# andenes (bóveda, 6,5 m), una luz cada 10 m
PAVONES.s                       3.75   6.30     5.00   8500   7000    omni
PAVONES.s                      13.25   6.30     5.00   8500   7000    omni
PAVONES.s                       3.75   6.30    15.00   8500   7000    omni
PAVONES.s                      13.25   6.30    15.00   8500   7000    omni
PAVONES.s                       3.75   6.30    25.00   8500   7000    omni
PAVONES.s                      13.25   6.30    25.00   8500   7000    omni
PAVONES.s                       3.75   6.30    35.00   8500   7000    omni
PAVONES.s                      13.25   6.30    35.00   8500   7000    omni
PAVONES.s                       3.75   6.30    45.00   8500   7000    omni
PAVONES.s                      13.25   6.30    45.00   8500   7000    omni
PAVONES.s                       3.75   6.30    55.00   8500   7000    omni
PAVONES.s                      13.25   6.30    55.00   8500   7000    omni
PAVONES.s                       3.75   6.30    65.00   8500   7000    omni
PAVONES.s                      13.25   6.30    65.00   8500   7000    omni
PAVONES.s                       3.75   6.30    75.00   8500   7000    omni
PAVONES.s                      13.25   6.30    75.00   8500   7000    omni
PAVONES.s                       3.75   6.30    85.00   8500   7000    omni
PAVONES.s                      13.25   6.30    85.00   8500   7000    omni
PAVONES.s                       3.75   6.30    95.00   8500   7000    omni
PAVONES.s                      13.25   6.30    95.00   8500   7000    omni
PAVONES.s                       3.75   6.30   105.00   8500   7000    omni
PAVONES.s                      13.25   6.30   105.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30     5.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30     5.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    15.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    15.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    25.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    25.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    35.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    35.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    45.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    45.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    55.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    55.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    65.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    65.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    75.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    75.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    85.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    85.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30    95.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30    95.00   8500   7000    omni
ARTILLEROS.s                    3.75   6.30   105.00   8500   7000    omni
ARTILLEROS.s                   13.25   6.30   105.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30     5.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30     5.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    15.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    15.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    25.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    25.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    35.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    35.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    45.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    45.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    55.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    55.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    65.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    65.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    75.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    75.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    85.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    85.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30    95.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30    95.00   8500   7000    omni
ESTRELLA.s                      3.75   6.30   105.00   8500   7000    omni
ESTRELLA.s                     13.25   6.30   105.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30     5.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30     5.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    15.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    15.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    25.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    25.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    35.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    35.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    45.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    45.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    55.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    55.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    65.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    65.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    75.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    75.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    85.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    85.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30    95.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30    95.00   8500   7000    omni
SAINZBARANDA.s                  3.75   6.30   105.00   8500   7000    omni
SAINZBARANDA.s                 13.25   6.30   105.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30     5.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30     5.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    15.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    15.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    25.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    25.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    35.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    35.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    45.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    45.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    55.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    55.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    65.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    65.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    75.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    75.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    85.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    85.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30    95.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30    95.00   8500   7000    omni
IBIZA2021.s                     3.75   6.30   105.00   8500   7000    omni
IBIZA2021.s                    13.25   6.30   105.00   8500   7000    omni
VERGARA.s                       3.75   6.30     5.00   8500   7000    omni
VERGARA.s                      13.25   6.30     5.00   8500   7000    omni
VERGARA.s                       3.75   6.30    15.00   8500   7000    omni
VERGARA.s                      13.25   6.30    15.00   8500   7000    omni
VERGARA.s                       3.75   6.30    25.00   8500   7000    omni
VERGARA.s                      13.25   6.30    25.00   8500   7000    omni
VERGARA.s                       3.75   6.30    35.00   8500   7000    omni
VERGARA.s                      13.25   6.30    35.00   8500   7000    omni
VERGARA.s                       3.75   6.30    45.00   8500   7000    omni
VERGARA.s                      13.25   6.30    45.00   8500   7000    omni
VERGARA.s                       3.75   6.30    55.00   8500   7000    omni
VERGARA.s                      13.25   6.30    55.00   8500   7000    omni
VERGARA.s                       3.75   6.30    65.00   8500   7000    omni
VERGARA.s                      13.25   6.30    65.00   8500   7000    omni
VERGARA.s                       3.75   6.30    75.00   8500   7000    omni
VERGARA.s                      13.25   6.30    75.00   8500   7000    omni
VERGARA.s                       3.75   6.30    85.00   8500   7000    omni
VERGARA.s                      13.25   6.30    85.00   8500   7000    omni
VERGARA.s                       3.75   6.30    95.00   8500   7000    omni
VERGARA.s                      13.25   6.30    95.00   8500   7000    omni
VERGARA.s                       3.75   6.30   105.00   8500   7000    omni
VERGARA.s                      13.25   6.30   105.00   8500   7000    omni
BALBOA.s                        3.75   6.30     5.00   8500   7000    omni
BALBOA.s                       13.25   6.30     5.00   8500   7000    omni
BALBOA.s                        3.75   6.30    15.00   8500   7000    omni
BALBOA.s                       13.25   6.30    15.00   8500   7000    omni
BALBOA.s                        3.75   6.30    25.00   8500   7000    omni
BALBOA.s                       13.25   6.30    25.00   8500   7000    omni
BALBOA.s                        3.75   6.30    35.00   8500   7000    omni
BALBOA.s                       13.25   6.30    35.00   8500   7000    omni
BALBOA.s                        3.75   6.30    45.00   8500   7000    omni
BALBOA.s                       13.25   6.30    45.00   8500   7000    omni
BALBOA.s                        3.75   6.30    55.00   8500   7000    omni
BALBOA.s                       13.25   6.30    55.00   8500   7000    omni
BALBOA.s                        3.75   6.30    65.00   8500   7000    omni
BALBOA.s                       13.25   6.30    65.00   8500   7000    omni
BALBOA.s                        3.75   6.30    75.00   8500   7000    omni
BALBOA.s                       13.25   6.30    75.00   8500   7000    omni
BALBOA.s                        3.75   6.30    85.00   8500   7000    omni
BALBOA.s                       13.25   6.30    85.00   8500   7000    omni
BALBOA.s                        3.75   6.30    95.00   8500   7000    omni
BALBOA.s                       13.25   6.30    95.00   8500   7000    omni
BALBOA.s                        3.75   6.30   105.00   8500   7000    omni
BALBOA.s                       13.25   6.30   105.00   8500   7000    omni
RAYO2022.s                      3.75   6.30     5.00   8500   7000    omni
RAYO2022.s                     13.25   6.30     5.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    15.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    15.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    25.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    25.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    35.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    35.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    45.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    45.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    55.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    55.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    65.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    65.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    75.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    75.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    85.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    85.00   8500   7000    omni
RAYO2022.s                      3.75   6.30    95.00   8500   7000    omni
RAYO2022.s                     13.25   6.30    95.00   8500   7000    omni
RAYO2022.s                      3.75   6.30   105.00   8500   7000    omni
RAYO2022.s                     13.25   6.30   105.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30     5.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30     5.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    15.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    15.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    25.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    25.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    35.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    35.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    45.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    45.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    55.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    55.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    65.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    65.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    75.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    75.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    85.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    85.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30    95.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30    95.00   8500   7000    omni
CONCHA2022.s                    3.75   6.30   105.00   8500   7000    omni
CONCHA2022.s                   13.25   6.30   105.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30     5.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30     5.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    15.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    15.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    25.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    25.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    35.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    35.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    45.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    45.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    55.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    55.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    65.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    65.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    75.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    75.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    85.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    85.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30    95.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30    95.00   8500   7000    omni
PIOXII2022.s                    3.75   6.30   105.00   8500   7000    omni
PIOXII2022.s                   13.25   6.30   105.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30     5.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30     5.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    15.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    15.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    25.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    25.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    35.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    35.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    45.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    45.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    55.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    55.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    65.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    65.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    75.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    75.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    85.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    85.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30    95.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30    95.00   8500   7000    omni
PASTRANA2022.s                  3.75   6.30   105.00   8500   7000    omni
PASTRANA2022.s                 13.25   6.30   105.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30     5.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30     5.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    15.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    15.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    25.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    25.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    35.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    35.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    45.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    45.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    55.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    55.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    65.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    65.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    75.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    75.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    85.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    85.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30    95.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30    95.00   8500   7000    omni
VENTILLA22.s                    3.75   6.30   105.00   8500   7000    omni
VENTILLA22.s                   13.25   6.30   105.00   8500   7000    omni
PILAR22.s                       3.75   6.30     5.00   8500   7000    omni
PILAR22.s                      13.25   6.30     5.00   8500   7000    omni
PILAR22.s                       3.75   6.30    15.00   8500   7000    omni
PILAR22.s                      13.25   6.30    15.00   8500   7000    omni
PILAR22.s                       3.75   6.30    25.00   8500   7000    omni
PILAR22.s                      13.25   6.30    25.00   8500   7000    omni
PILAR22.s                       3.75   6.30    35.00   8500   7000    omni
PILAR22.s                      13.25   6.30    35.00   8500   7000    omni
PILAR22.s                       3.75   6.30    45.00   8500   7000    omni
PILAR22.s                      13.25   6.30    45.00   8500   7000    omni
PILAR22.s                       3.75   6.30    55.00   8500   7000    omni
PILAR22.s                      13.25   6.30    55.00   8500   7000    omni
PILAR22.s                       3.75   6.30    65.00   8500   7000    omni
PILAR22.s                      13.25   6.30    65.00   8500   7000    omni
PILAR22.s                       3.75   6.30    75.00   8500   7000    omni
PILAR22.s                      13.25   6.30    75.00   8500   7000    omni
PILAR22.s                       3.75   6.30    85.00   8500   7000    omni
PILAR22.s                      13.25   6.30    85.00   8500   7000    omni
PILAR22.s                       3.75   6.30    95.00   8500   7000    omni
PILAR22.s                      13.25   6.30    95.00   8500   7000    omni
PILAR22.s                       3.75   6.30   105.00   8500   7000    omni
PILAR22.s                      13.25   6.30   105.00   8500   7000    omni
HERRERA22.s                     3.75   6.30     5.00   8500   7000    omni
HERRERA22.s                    13.25   6.30     5.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    15.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    15.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    25.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    25.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    35.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    35.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    45.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    45.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    55.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    55.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    65.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    65.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    75.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    75.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    85.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    85.00   8500   7000    omni
HERRERA22.s                     3.75   6.30    95.00   8500   7000    omni
HERRERA22.s                    13.25   6.30    95.00   8500   7000    omni
HERRERA22.s                     3.75   6.30   105.00   8500   7000    omni
HERRERA22.s                    13.25   6.30   105.00   8500   7000    omni
# Avenida de América: sus fluorescentes van en las paredes (4,6 m)
AVENIDADAMERICA.s               0.40   4.40     5.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40     5.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    15.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    15.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    25.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    25.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    35.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    35.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    45.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    45.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    55.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    55.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    65.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    65.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    75.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    75.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    85.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    85.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40    95.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40    95.00   8500   7000    omni
AVENIDADAMERICA.s               0.40   4.40   105.00   8500   7000    omni
AVENIDADAMERICA.s              16.60   4.40   105.00   8500   7000    omni
# piezas de luz de los techos (tubo fluorescente a lo largo): una luz cada ~6 m
PIEZALUZARGANDA.s               0.20   0.00     1.63   8500   7000    omni
PIEZALUZARGANDA2.s              0.20   0.00     1.63   8500   7000    omni
PIEZALUZARGANDASUBIDA.s         0.12   1.03     0.52   8500   7000    omni
PIEZALUZCIPRIANO.s              0.20   0.00     0.50   8500   7000    omni
PIEZALUZCIPRIANO.s              0.20   0.00    -6.00   8500   7000    omni
PIEZALUZCIPRIANO.s              0.20   0.00   -12.50   8500   7000    omni
PIEZALUZCIPRIANO.s              0.20   0.00   -19.00   8500   7000    omni
PIEZALUZCIPRIANO.s              0.20   0.00   -24.50   8500   7000    omni
PIEZALUZCOLOMBIA.s              0.20   0.00     0.50   8500   7000    omni
PIEZALUZCOLOMBIA.s              0.20   0.00    -5.50   8500   7000    omni
PIEZALUZCOLOMBIA.s              0.20   0.00   -11.50   8500   7000    omni
PIEZALUZCOLOMBIA.s              0.20   0.00   -16.50   8500   7000    omni
PIEZALUZCOLOMBIA2.s             0.20   0.00     0.50   8500   7000    omni
PIEZALUZCOLOMBIA2.s             0.20   0.00    -5.50   8500   7000    omni
PIEZALUZCOLOMBIA2.s             0.20   0.00   -11.50   8500   7000    omni
PIEZALUZCOLOMBIA2.s             0.20   0.00   -16.50   8500   7000    omni
PIEZALUZCOLOMBIA3.s             0.20   0.00     0.50   8500   7000    omni
PIEZALUZCOLOMBIA3.s             0.20   0.00    -5.50   8500   7000    omni
PIEZALUZCOLOMBIA3.s             0.20   0.00   -11.50   8500   7000    omni
PIEZALUZCOLOMBIA3.s             0.20   0.00   -16.50   8500   7000    omni
PIEZALUZCOLOMBIA4.s             0.12   0.00    -1.90   8500   7000    omni
PIEZALUZCOLOMBIA5.s             0.12   0.00    -1.90   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00    -3.40   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00    -9.60   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -15.80   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -22.00   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -28.20   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -34.40   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -40.60   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -46.80   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -53.00   8500   7000    omni
PIEZALUZCOLOMBIA6.s             0.12   0.00   -59.20   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00    -3.40   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00    -9.60   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -15.80   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -22.00   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -28.20   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -34.40   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -40.60   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -46.80   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -53.00   8500   7000    omni
PIEZALUZCOLOMBIA8.s             0.12   0.00   -59.20   8500   7000    omni
PIEZALUZCOLOMBIA7.s             0.12   0.00    -3.50   8500   7000    omni
PIEZALUZCOLOMBIA7.s             0.12   0.00   -10.00   8500   7000    omni
PIEZALUZCOLOMBIA7.s             0.12   0.00   -16.50   8500   7000    omni
PIEZALUZCOLOMBIA7.s             0.12   0.00   -23.00   8500   7000    omni
# la mina (fluorescente redondo del techo, a 6 m) y el enlace con su pared
MINAENCENDIDO.s                 9.66   5.90    -9.74    900   5000    omni
MINAENCENDIDO2.s                9.66   5.90    -9.74    1200   5000    omni
MINAENCENDIDO.s                 9.66   5.90    -4.15    900   5000    omni
MINAENCENDIDO2.s                9.66   5.90    -4.15    1200   5000    omni
ENLACE89.s                      9.66   5.90   -18.50    1200   7500    omni
PAREDENLACE.s                  13.80   5.50   -18.50    1200   7500    omni
PAREDENLACEbaja.s              13.80   4.50   -18.50    1200   7500    omni
ENLACE89.s                      9.66   5.90   -24.50    1200   7500    omni
PAREDENLACE.s                  13.80   5.50   -24.50    1200   7500    omni
PAREDENLACEbaja.s              13.80   4.50   -24.50    1200   7500    omni
# el proyector (el que encuentra el buscador, pero en omni)
proyector.s                     0.24  10.42     3.16  85000   3000    omni
"),
        };

        // ── farolas buscadas (LampScanner), guardadas por ruta
        static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR", "cyzy-farolas");
        static string CacheFile(string route)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            var h = sha.ComputeHash(Encoding.UTF8.GetBytes(Path.GetFullPath(route).TrimEnd('\\').ToLowerInvariant()));
            return Path.Combine(CacheDir, Regex.Replace(Path.GetFileName(route), @"[^\w\-]", "_") + "-" + BitConverter.ToString(h, 0, 4).Replace("-", "") + ".txt");
        }

        // la huella de la ruta: cuántos archivos de mundo hay y el más reciente (y la versión de la búsqueda)
        static string Fingerprint(string route)
        {
            try
            {
                var w = new DirectoryInfo(Path.Combine(route, "WORLD")).GetFiles("*.w");
                long last = w.Length == 0 ? 0 : w.Max(f => f.LastWriteTimeUtc.Ticks);
                return $"# huella v{LampScanner.Version} {w.Length} {last}";
            }
            catch { return "# huella ?"; }
        }

        static string Cached(string route)
        {
            try
            {
                string f = CacheFile(route);
                if (!File.Exists(f)) return null;
                string text = File.ReadAllText(f);
                int nl = text.IndexOf('\n');
                return nl > 0 && text.Substring(0, nl).TrimEnd('\r') == Fingerprint(route) ? text.Substring(nl + 1) : null;
            }
            catch { return null; }
        }

        // ¿Hay que buscar las farolas de la ruta de estos argumentos? (para avisar «Preparando…» mientras tanto)
        public static bool NeedsScan(string args, string orDir = null)
        {
            if (!OrFlavor.IsCyzy(orDir)) return false;
            string route = RouteOf(args);
            return route != null && Directory.Exists(route) && Cached(route) == null;
        }

        // Busca las farolas de la ruta y las guarda (en segundo plano: con las rutas grandes, unos segundos).
        public static string Prepare(string args)
        {
            try
            {
                string route = RouteOf(args);
                if (route == null || !Directory.Exists(route)) return null;
                var r = LampScanner.Scan(route);
                var sb = new StringBuilder();
                sb.AppendLine(Fingerprint(route));
                sb.AppendLine($"# {Path.GetFileName(route)}: {r.Shapes} formas de farola, {r.Lamps} luces, {r.Placed} colocadas en la ruta (buscadas por SelectOR)");
                sb.Append(r.Text);
                foreach (var n in r.Notes) sb.AppendLine("# (fuera) " + n);
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(CacheFile(route), sb.ToString(), new UTF8Encoding(false));
                return $"farolas de CYZYRails: {Path.GetFileName(route)}: {r.Shapes} formas, {r.Lamps} luces";
            }
            catch (Exception ex) { return "farolas de CYZYRails: " + ex.GetType().Name + " " + ex.Message; }
        }

        static string StateFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Open Rails", "SelectOR", "cyzy-farolas.txt");

        // La carpeta de la ruta que se va a conducir, sacada de los argumentos de RunActivity (el .pat, la actividad o el
        // horario están dentro de ROUTES\<ruta>\…).
        public static string RouteOf(string args)
        {
            foreach (Match m in Regex.Matches(args ?? "", "\"([^\"]+)\""))
            {
                string p = m.Groups[1].Value;
                int k = p.IndexOf(@"\ROUTES\", StringComparison.OrdinalIgnoreCase);
                if (k < 0) continue;
                int end = p.IndexOf('\\', k + 8);
                return end > 0 ? p.Substring(0, end) : p;
            }
            return null;
        }

        // Al lanzar. Devuelve qué ha hecho (para el registro) o null si nada.
        public static string Install(string args, string orDir = null)
        {
            try
            {
                Remove();
                if (!OrFlavor.IsCyzy(orDir)) return null;
                string route = RouteOf(args);
                if (route == null || !Directory.Exists(route)) return null;
                string name = Path.GetFileName(route);
                string dir = Path.Combine(route, "OpenRails"), file = Path.Combine(dir, "CyzyLamps.txt");
                if (File.Exists(file) && !Ours(file)) return "farolas de CYZYRails: la ruta ya tiene su CyzyLamps.txt (no se toca)";
                // las medidas a mano de la ruta (si las hay) y las buscadas de las demás formas
                var def = Routes.FirstOrDefault(r => name.StartsWith(r.prefix, StringComparison.OrdinalIgnoreCase));
                string found = Cached(route);
                if (found == null) { Prepare(args); found = Cached(route) ?? ""; }
                var hand = def.text == null ? new string[0] : def.text.Replace("\r\n", "\n").Split('\n');
                var handShapes = new HashSet<string>(hand.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).Select(l => l.Split(' ')[0].ToLowerInvariant()));
                var sb = new StringBuilder();
                sb.AppendLine(Mark + $" para {name} (se pone al conducir con CYZYRails y se quita al volver)");
                foreach (var l in hand.Skip(1)) if (l.Trim().Length > 0) sb.AppendLine(l);
                int lamps = handShapes.Count == 0 ? 0 : hand.Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#"));
                foreach (var l in found.Replace("\r\n", "\n").Split('\n'))
                {
                    string t = l.Trim();
                    if (t.Length == 0) continue;
                    if (!t.StartsWith("#"))
                    {
                        if (handShapes.Contains(t.Split(' ')[0].ToLowerInvariant())) continue;   // (manda la medida a mano)
                        lamps++;
                    }
                    sb.AppendLine(l);
                }
                if (lamps == 0) return $"farolas de CYZYRails: {name}: ninguna farola reconocida";
                Directory.CreateDirectory(dir);
                File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
                Directory.CreateDirectory(Path.GetDirectoryName(StateFile));
                File.WriteAllText(StateFile, file);
                return $"farolas de CYZYRails: puestas en {name} ({lamps} luces)";
            }
            catch (Exception ex) { return "farolas de CYZYRails: " + ex.Message; }
        }

        // Al volver de conducir y al abrir SelectOR: fuera el archivo que se puso (solo si sigue siendo el de SelectOR).
        public static void Remove()
        {
            try
            {
                if (!File.Exists(StateFile)) return;
                string file = File.ReadAllText(StateFile).Trim();
                if (file.Length > 0 && File.Exists(file) && Ours(file)) File.Delete(file);
                File.Delete(StateFile);
            }
            catch { }
        }

        static bool Ours(string file)
        {
            try { using var r = new StreamReader(file); return (r.ReadLine() ?? "").StartsWith(Mark, StringComparison.Ordinal); }
            catch { return false; }
        }
    }
}
