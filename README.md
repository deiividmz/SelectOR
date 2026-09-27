# SelectOR

Menú de selección de tren y ruta para [Open Rails](https://www.openrails.org/) (Windows 10/11, .NET 8).

## Compilar

Necesitas el **SDK de .NET 8** y una instalación de **Open Rails**. SelectOR usa sus DLLs ya compiladas
(`Orts.*.dll`, `MonoGame.Framework.dll`), que no se incluyen aquí, y se compila dentro de esa carpeta,
junto a `OpenRails.exe`:

```bash
cd src
dotnet build -c Release -p:ORDir="C:\Open Rails\Program"
```

## Licencia

© 2026 David MZP. GNU General Public License v3 (ver [`LICENSE`](LICENSE)), la misma que Open Rails,
cuyas bibliotecas reutiliza. SelectOR es independiente del proyecto Open Rails. «Open Rails» y «MSTS»
son marcas de sus titulares; las rutas, trenes y demás contenido pertenecen a sus autores y no forman
parte de este proyecto.
