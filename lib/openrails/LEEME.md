# Bibliotecas de referencia de Open Rails

Estas DLL **solo se usan para compilar** SelectOR en GitHub Actions (el servidor no tiene Open Rails
instalado). No se publican con SelectOR: en el equipo de cada usuario, SelectOR usa las de su propia
instalación de Open Rails.

| Archivo | Procede de | Licencia |
|---|---|---|
| `Orts.Menu.dll`, `Orts.Settings.dll`, `Orts.Common.dll`, `Orts.Formats.Msts.dll`, `Orts.Formats.OR.dll` | Open Rails Testing **T1.6.1-438-g3a4d79804** | GNU GPL v3 — código fuente en <https://github.com/openrails/openrails> (commit `3a4d79804`) |
| `MonoGame.Framework.dll` | MonoGame **3.8.1.303** (la que acompaña a esa versión de Open Rails) | Microsoft Public License (Ms-PL) — <https://github.com/MonoGame/MonoGame> |

Si se cambia la versión de Open Rails con la que se compila SelectOR, hay que sustituir aquí estas seis
DLL por las de la nueva versión (las de la carpeta de Open Rails, junto a `OpenRails.exe`).
