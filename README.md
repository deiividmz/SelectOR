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

En `lib/openrails/` están las seis bibliotecas de Open Rails contra las que se compila en GitHub Actions
(ver [`lib/openrails/LEEME.md`](lib/openrails/LEEME.md)); también sirven para compilar sin tener Open Rails:

```bash
dotnet build src/SelectOR.csproj -c Release -p:ORDir="lib\openrails\" -p:OutputPath="out\"
```

## Firma de código

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

Las versiones publicadas de SelectOR (`SelectOR.exe` y `SelectOR.dll`) se compilan en GitHub Actions a
partir de este repositorio ([flujo «Compilar y firmar»](.github/workflows/firmar.yml)) y se firman con
Authenticode a través de SignPath. Cada firma la aprueba a mano el responsable del proyecto.

- Autores y revisores del código: [deiividmz](https://github.com/deiividmz)
- Aprobación de las firmas: [deiividmz](https://github.com/deiividmz)

### Privacidad

SelectOR no recoge telemetría ni muestra publicidad. Solo se conecta a internet para:

- **Comprobar si hay una versión nueva** (al arrancar y cada 3 horas): es una consulta pública al servidor
  de SelectOR que no envía datos personales. Las actualizaciones solo se instalan si el usuario lo acepta.
- **Las funciones de Empresas** (cuenta, servicios, flota, liga, megafonía, avisos): usan el servidor de
  SelectOR únicamente si el usuario crea una cuenta e inicia sesión.
- **El mapa en vivo**: con la sesión iniciada, mientras se conduce, la posición del tren se comparte con los
  demás usuarios de SelectOR. Se puede desactivar en *Mi perfil*.

Esos datos se guardan en el servidor de SelectOR (alojado en Supabase) y no se ceden a nadie más.

## Licencia

© 2026 David MZP. GNU General Public License v3 (ver [`LICENSE`](LICENSE)), la misma que Open Rails,
cuyas bibliotecas reutiliza. SelectOR es independiente del proyecto Open Rails. «Open Rails» y «MSTS»
son marcas de sus titulares; las rutas, trenes y demás contenido pertenecen a sus autores y no forman
parte de este proyecto.
