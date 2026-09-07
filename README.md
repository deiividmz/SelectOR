<div align="center">

<img src="docs/logo.png" alt="SelectOR" width="460">

### Un menú moderno y fácil para elegir tren y ruta en **Open Rails**

Vista previa **3D y 2D** de los trenes · Mapa del recorrido · Multijugador · **Empresas** · Todo en una pantalla.

![Licencia](https://img.shields.io/badge/Licencia-GPL%20v3-4c9a2a)
![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D6)
![.NET](https://img.shields.io/badge/.NET-8-512BD4)
![Open Rails](https://img.shields.io/badge/Para-Open%20Rails-brightgreen)

</div>

---

## 🚆 ¿Qué es SelectOR?

**SelectOR** es un **menú alternativo para Open Rails**: eliges la **ruta**, el **tren**, la **actividad**
y las condiciones (hora, estación, clima), ves una **vista previa 3D y 2D** del tren, consultas el **mapa
del recorrido** y lanzas la simulación. Todo con una interfaz clara y moderna.

Es un **complemento independiente**: **no modifica Open Rails**, solo reutiliza lo que ya tienes
instalado. Si quitas SelectOR, Open Rails sigue igual.

> ℹ️ SelectOR **no** está desarrollado ni avalado por el equipo de Open Rails. Es un proyecto de aficionado.

---

## ✨ Características

- 🚆 **Elige ruta y tren** con buscador y favoritos (doble clic para marcar favorito).
- 🧊 **Vista previa 3D** del tren — **arrástrala para girarla**.
- 📏 **Composición 2D** del tren completo, en orden y con la orientación correcta.
- 🗺️ **Mapa del recorrido** con origen, destino y trazado resaltado.
- 🌦️ **Condiciones**: hora del día, estación del año y clima.
- 🌐 **Multijugador**: aloja o únete a partidas, con **lista de servidores públicos**.
- ⏱️ **Horarios (timetable)** y **Reanudar** partidas guardadas.
- 🌍 **Español e inglés** (según el idioma configurado en Open Rails).
- ✅ **Compatible con varias versiones de Open Rails** (oficial, Testing, *New Year*…).
- 🏢 **Empresas** *(en línea, opcional)*: crea o únete a una **empresa ferroviaria** con otros
  maquinistas y lleva la contabilidad de vuestros viajes — banca, rangos, insignias y rankings.

---

## 🏢 Empresas *(modo en línea — novedad 1.1)*

Una sección **opcional** para jugar en comunidad: forma o únete a una **empresa ferroviaria**
compartida con otros maquinistas y lleva la **contabilidad de todos vuestros viajes reales**.

- 🚂 **Servicios automáticos**: cada viaje que conduces para tu empresa se registra solo al volver,
  con los **kilómetros medidos en directo** desde Open Rails y el **tiempo real** de conducción.
- 💶 **Economía y banca**: ingreso por km, cánon al administrador de infraestructuras, energía,
  mantenimiento y salario; tesorería, historial de movimientos y panel financiero.
- 🎖️ **Rangos e insignias**: progresa de *Aprendiz* a *Leyenda* y desbloquea logros.
- 🏆 **Rankings**: público de empresas (por km) y de maquinistas dentro de tu empresa.
- 👥 **Roles**: Maquinista, Gestor y Gerente, cada uno con sus permisos.
- 🛡️ **Antitrampas**: los viajes con velocidades imposibles se marcan y no cuentan.

> 🔐 Requiere **registrarse** (correo + contraseña) desde la propia pestaña y **conexión a internet**.
> Es opcional: si no usas Empresas, el resto de SelectOR funciona igual. Tu contraseña, si eliges
> recordarla, se guarda **cifrada** en tu equipo (nunca en texto plano).

---

## ✅ Requisitos

- **Open Rails** ya instalado y funcionando, con tu contenido (rutas y trenes) configurado.
- **Windows 10 u 11** (64 bits).
- **.NET Desktop Runtime 8** (gratuito) — si al abrir SelectOR te avisa de que falta, descárgalo aquí:
  <https://dotnet.microsoft.com/download/dotnet/8.0> → *Desktop Runtime*, x64.

---

## ⬇️ Instalación (¡en 30 segundos!)

1. **Descarga** la última versión desde la sección **[Releases](../../releases)** y descomprímela.
2. Abre la carpeta **`Folder Open Rails`**.
3. **Copia todo** su contenido dentro de la carpeta de tu Open Rails
   (la que tiene `OpenRails.exe`).
4. Ejecuta **`SelectOR.exe`**. ¡Listo! 🎉

> 💡 Consejo: crea un acceso directo de `SelectOR.exe` en el Escritorio.
> Para desinstalarlo, borra los archivos que copiaste. No deja nada más.

---

## 🎮 Cómo se usa

1. Arriba, en **Contenido**, elige tu carpeta (si tienes varias).
2. A la izquierda, selecciona una **ruta**.
3. Elige el modo con las pestañas: **Ruta**, **Actividad**, **Exploración**, **Horarios**,
   **Multijugador** o **Empresas**.
4. Ajusta hora, estación y clima; mira la **vista 3D** (arrástrala) o la **composición 2D**.
5. Pulsa **CONDUCIR** (o **CONECTAR** en multijugador). 🚀

---

## ❓ Preguntas frecuentes

**No abre y menciona “.NET”.**
Instala el *.NET Desktop Runtime 8* (ver Requisitos).

**No aparecen rutas o trenes (“Sin rutas” / “Sin trenes”).**
Esa carpeta de contenido no tiene rutas/trenes o no está dada de alta en Open Rails.
Añádela desde *Open Rails → Opciones → Contenido*.

**Un tren se ve gris o incompleto en la vista previa.**
Suele ser porque a ese modelo le faltan archivos (texturas o el `.s`) en tu instalación:
es cosa del contenido descargado, no del menú.

---

## 🛠️ Compilar desde el código (desarrolladores)

SelectOR referencia las DLLs ya compiladas de Open Rails (no las incluye).

```bash
dotnet build -c Release
```

- **Framework:** `net8.0-windows` (Windows Forms).
- **Gráficos:** MonoGame (DirectX 11) para la vista previa; GDI+ para la interfaz.
- **Salida:** `SelectOR.exe` + `SelectOR.dll` + `shape.mgfx`.
- Tolerante a distintas versiones de Open Rails/MonoGame (reflexión para las clases de OR y
  respaldo a `BasicEffect` si el shader propio no carga).

---

## 📜 Licencia

**GNU General Public License v3 (GPL v3)** — por reutilizar las bibliotecas de Open Rails, que usan esa
misma licencia. Ver [`LICENCIA.txt`](LICENCIA.txt) y [`LICENSE`](LICENSE).

## 🙌 Créditos

- Desarrollo: **David Muñoz Primo (David MZP)**, 2026.
- Hecho para [**Open Rails**](https://www.openrails.org/) (© su equipo, GPL v3).
- «Open Rails» y «MSTS» son marcas de sus titulares; se citan solo para identificar.
- Las rutas, trenes y contenidos pertenecen a sus autores y **no** forman parte de este proyecto.

<div align="center">

**¡Gracias por probar SelectOR y buen viaje! 🚂💨**

</div>
