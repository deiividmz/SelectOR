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

## Firma de código · Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by
[SignPath Foundation](https://signpath.org).

Las versiones publicadas de SelectOR (`SelectOR.exe` y `SelectOR.dll`) se compilan en GitHub Actions a
partir de este repositorio ([flujo «Compilar y firmar»](.github/workflows/firmar.yml)) y se firman con
Authenticode a través de SignPath. Solo se firman los binarios propios del proyecto: las bibliotecas de
Open Rails se distribuyen tal cual. Cada firma la aprueba a mano el responsable del proyecto.

*Released builds of SelectOR (`SelectOR.exe` and `SelectOR.dll`) are built by GitHub Actions from this
repository and signed with Authenticode through SignPath. Only the project's own binaries are signed; the
Open Rails libraries are shipped unchanged. Every signing request is approved manually.*

### Equipo y roles · Team roles

| Rol · Role | Miembros · Members |
|---|---|
| Autores (*Committers*) | [deiividmz](https://github.com/deiividmz) (David MZP) |
| Revisores (*Reviewers*) | [deiividmz](https://github.com/deiividmz) (David MZP) |
| Aprobadores (*Approvers*) | [deiividmz](https://github.com/deiividmz) (David MZP) |

Todos los miembros usan la verificación en dos pasos en GitHub y en SignPath.
*All team members use multi-factor authentication on GitHub and SignPath.*

### Privacidad · Privacy policy

Política de privacidad completa · Full privacy policy:
**[www.select-or.app/privacidad](https://www.select-or.app/privacidad)** ([English](https://www.select-or.app/en/privacidad)).

Resumen: SelectOR no recoge telemetría ni muestra publicidad, y no vende ni cede datos. Sin cuenta, solo se
conecta a internet para **comprobar si hay una versión nueva** (consulta pública, sin datos personales; las
actualizaciones solo se instalan si el usuario lo acepta). La cuenta es opcional y solo hace falta para
**Empresas** (servicios, flota, banca, liga, carné, chat, megafonía), el **mapa en vivo** (se puede
desactivar en *Mi perfil*) y el bloqueo de servidores **multijugador**. Esos datos se guardan en el servidor
de SelectOR (Supabase) y se borran al eliminar la cuenta desde el propio programa.

*Summary: SelectOR collects no telemetry, shows no adverts and never sells or shares data. Without an
account it only goes online to check for updates. An optional account is used for Companies, the live map
and the multiplayer server lock; that data is stored on the SelectOR server (Supabase) and is erased when
the account is deleted from the program.*

## Licencia

© 2026 David MZP. GNU General Public License v3 (ver [`LICENSE`](LICENSE)), la misma que Open Rails,
cuyas bibliotecas reutiliza. SelectOR es independiente del proyecto Open Rails. «Open Rails» y «MSTS»
son marcas de sus titulares; las rutas, trenes y demás contenido pertenecen a sus autores y no forman
parte de este proyecto.
