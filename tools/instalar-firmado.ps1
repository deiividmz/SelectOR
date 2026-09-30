<#
  Instala en una carpeta de Open Rails los archivos de SelectOR FIRMADOS por SignPath, para publicar
  desde ahí la actualización (SelectOR → Ajustes → Actualizaciones empaqueta los archivos que se están
  ejecutando).

  Uso:
    .\instalar-firmado.ps1 -Destino "C:\ruta\a\Open Rails" [-Zip "SelectOR-1.2.41-firmado.zip"]

  · -Zip: el .zip que deja el flujo «Compilar y firmar» de GitHub (en el borrador de release o en los
    artefactos del flujo). Si no se indica, se usa el más reciente de la carpeta Descargas.
  · Comprueba que SelectOR.exe y SelectOR.dll tienen una firma Authenticode VÁLIDA antes de copiar nada.
  · Guarda los archivos que había en <Destino>\SelectOR-antes-de-firmar (por si hay que volver atrás).
#>
param(
    [Parameter(Mandatory = $true)][string]$Destino,
    [string]$Zip
)
$ErrorActionPreference = 'Stop'
$archivos = 'SelectOR.exe', 'SelectOR.dll', 'SelectOR.deps.json', 'SelectOR.runtimeconfig.json', 'shape.mgfx'

if (-not (Test-Path (Join-Path $Destino 'OpenRails.exe')) -and -not (Test-Path (Join-Path $Destino 'RunActivity.exe'))) {
    throw "«$Destino» no parece una carpeta de Open Rails (no tiene OpenRails.exe ni RunActivity.exe)."
}
if (Get-Process -Name SelectOR -ErrorAction SilentlyContinue) {
    throw 'Cierra SelectOR antes de instalar los archivos firmados.'
}

if (-not $Zip) {
    $descargas = Join-Path $env:USERPROFILE 'Downloads'
    $Zip = Get-ChildItem $descargas -Filter 'SelectOR-*-firmado.zip' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Zip) { throw "No hay ningún SelectOR-*-firmado.zip en $descargas. Indícalo con -Zip." }
}
if (-not (Test-Path $Zip)) { throw "No existe $Zip." }
"Paquete: $Zip"

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('SelectOR-firmado-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -Path $Zip -DestinationPath $tmp
    # El .zip del flujo puede traer los archivos en la raíz o dentro de una carpeta.
    $raiz = (Get-ChildItem $tmp -Recurse -Filter 'SelectOR.exe' | Select-Object -First 1).DirectoryName
    if (-not $raiz) { throw 'El paquete no contiene SelectOR.exe.' }

    foreach ($f in $archivos) { if (-not (Test-Path (Join-Path $raiz $f))) { throw "Al paquete le falta $f." } }
    foreach ($f in 'SelectOR.exe', 'SelectOR.dll') {
        $s = Get-AuthenticodeSignature (Join-Path $raiz $f)
        if ($s.Status -ne 'Valid') { throw "$f no tiene una firma válida ($($s.Status)). No se ha copiado nada." }
        "$f firmado por: $($s.SignerCertificate.Subject)"
    }
    $fv = [version](Get-Item (Join-Path $raiz 'SelectOR.exe')).VersionInfo.FileVersion
    "Versión: $($fv.Major).$($fv.Minor).$($fv.Build)"

    $copia = Join-Path $Destino 'SelectOR-antes-de-firmar'
    New-Item -ItemType Directory -Force $copia | Out-Null
    foreach ($f in $archivos) {
        $actual = Join-Path $Destino $f
        if (Test-Path $actual) { Copy-Item $actual $copia -Force }
    }
    foreach ($f in $archivos) { Copy-Item (Join-Path $raiz $f) $Destino -Force }
    "Listo: archivos firmados instalados en $Destino (los anteriores, en $copia)."
    'Ahora abre SelectOR y publica desde Ajustes → Actualizaciones.'
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
