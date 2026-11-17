# Pruebas del kit (tools/make_kit.py) sobre COPIAS del juego en una carpeta temporal: nunca toca la instalación real.
# Requisitos: juego 1.5.78 con la Modding API v77 instalada en la ruta de Steam por defecto,
# reference/vanilla-managed-backup/ (Assembly-CSharp.dll y mscorlib.dll originales) y dist/HKTelemetry_P99.zip
# (python tools/make_kit.py P99). Uso: powershell -ExecutionPolicy Bypass -File tools\test_kit.ps1
# Nota: cada instalación de prueba hace una copia de las partidas en ...\Hollow Knight\HKTelemetry_saves_backup\.
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
$S = Join-Path $env:TEMP 'hktelemetry_kit_test'
if (-not (Test-Path -LiteralPath $S)) { [void](New-Item -ItemType Directory -Path $S) }
$real = 'C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight'
$Bk = Join-Path $Repo 'reference\vanilla-managed-backup'
$K = "$S\kittest"
if (Test-Path $K) { Remove-Item $K -Recurse -Force }
Expand-Archive (Join-Path $Repo 'dist\HKTelemetry_P99.zip') $K
$inst = "$K\HKTelemetry_P99\scripts\install.ps1"
$unin = "$K\HKTelemetry_P99\scripts\uninstall.ps1"
$apiAdded = 'MMHOOK_Assembly-CSharp.dll','MMHOOK_PlayMaker.dll','MonoMod.RuntimeDetour.dll','MonoMod.Utils.dll','Mono.Cecil.dll','Assembly-CSharp.xml','mscorlib.xml','unityscenerepacker.dll'
$fail = 0

function New-FakeGame([string]$name) {
    $G = "$S\juegos [SSD] ñ\$name\Hollow Knight"
    if (Test-Path -LiteralPath "$S\juegos [SSD] ñ\$name") { Remove-Item -LiteralPath "$S\juegos [SSD] ñ\$name" -Recurse -Force }
    New-Item -ItemType Directory "$G\hollow_knight_Data" -Force | Out-Null
    Copy-Item -LiteralPath "$real\hollow_knight_Data\Managed" -Destination "$G\hollow_knight_Data\Managed" -Recurse
    Copy-Item -LiteralPath "$real\UnityPlayer.dll" -Destination $G
    Copy-Item -LiteralPath "$Bk\Assembly-CSharp.dll" -Destination "$G\hollow_knight_Data\Managed\Assembly-CSharp.dll" -Force
    Copy-Item -LiteralPath "$Bk\mscorlib.dll" -Destination "$G\hollow_knight_Data\Managed\mscorlib.dll" -Force
    foreach ($n in $apiAdded) { Remove-Item -LiteralPath "$G\hollow_knight_Data\Managed\$n" -Force -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath "$G\hollow_knight_Data\Managed\Mods" -Recurse -Force -ErrorAction SilentlyContinue
    return $G
}
function Snap([string]$G) {
    Get-ChildItem -LiteralPath "$G\hollow_knight_Data\Managed" -File | ForEach-Object { "{0} {1}" -f $_.Name, (Get-FileHash -LiteralPath $_.FullName).Hash }
}
function Run([string]$script, [string]$G) {
    $o = powershell.exe -NoProfile -ExecutionPolicy Bypass -File $script -Yes -GameDir $G 2>&1
    $script:lastExit = $LASTEXITCODE
    return ($o | Out-String)
}
function Check([string]$what, [bool]$ok) {
    if ($ok) { Write-Host "  OK   $what" -ForegroundColor Green } else { Write-Host "  FALLO $what" -ForegroundColor Red; $script:fail++ }
}
function Get-AcHash([string]$G) { (Get-FileHash -LiteralPath "$G\hollow_knight_Data\Managed\Assembly-CSharp.dll").Hash.Substring(0,8) }

"== 1. Instalación a medias y reparación =="
$G = New-FakeGame 'medias'; $orig = Snap $G
$o = Run $inst $G; Check "instala" ($lastExit -eq 0 -and $o -match 'Modding API v77 instalada')
Remove-Item -LiteralPath "$G\hollow_knight_Data\Managed\MonoMod.RuntimeDetour.dll" -Force
Copy-Item -LiteralPath "$Bk\mscorlib.dll" -Destination "$G\hollow_knight_Data\Managed\mscorlib.dll" -Force
$o = Run $inst $G; Check "reinstalar repara (2 ficheros)" ($o -match 'reparada \(2 ficheros')
Check "RuntimeDetour vuelve" (Test-Path -LiteralPath "$G\hollow_knight_Data\Managed\MonoMod.RuntimeDetour.dll")
$o = Run $unin $G; Check "desinstalar OK" ($o -match 'vuelve a estar como antes')
$d = Compare-Object $orig (Snap $G); Check "Managed idéntico al original" (-not $d); if ($d) { $d | Format-Table -AutoSize | Out-String | Write-Host }

"== 2. 'Verificar integridad' entre instalación y reinstalación =="
$G = New-FakeGame 'verificar'; $orig = Snap $G
[void](Run $inst $G)
Copy-Item -LiteralPath "$Bk\Assembly-CSharp.dll" -Destination "$G\hollow_knight_Data\Managed\Assembly-CSharp.dll" -Force
Copy-Item -LiteralPath "$Bk\mscorlib.dll" -Destination "$G\hollow_knight_Data\Managed\mscorlib.dll" -Force
$o = Run $inst $G
$st = Get-Content -LiteralPath "$G\hollow_knight_Data\HKTelemetry_kit\state.json" -Raw | ConvertFrom-Json
Write-Host ("added=" + ($st.api_files_added -join ',') + " | replaced=" + ($st.api_files_replaced -join ','))
Check "estado conserva 8 añadidos / 3 sustituidos" (@($st.api_files_added).Count -eq 8 -and @($st.api_files_replaced).Count -eq 3)
Check "backup no contiene ficheros de la API" (@(Get-ChildItem -LiteralPath "$G\hollow_knight_Data\HKTelemetry_kit\backup").Count -eq 3)
$o = Run $unin $G; Check "desinstalar OK" ($o -match 'vuelve a estar como antes')
$d = Compare-Object $orig (Snap $G); Check "Managed idéntico al original" (-not $d); if ($d) { $d | Format-Table -AutoSize | Out-String | Write-Host }

"== 3. Steam actualiza el juego antes de desinstalar =="
$G = New-FakeGame 'actualizado'
[void](Run $inst $G)
# Simula la versión nueva: otro UnityPlayer.dll (otra versión) y otros Assembly-CSharp/mscorlib.
Copy-Item -LiteralPath "$real\UnityCrashHandler64.exe" -Destination "$G\UnityPlayer.dll" -Force
[IO.File]::WriteAllBytes("$G\hollow_knight_Data\Managed\Assembly-CSharp.dll", [byte[]](1..200))
[IO.File]::WriteAllBytes("$G\hollow_knight_Data\Managed\mscorlib.dll", [byte[]](5..250))
$newAc = Get-AcHash $G
$o = Run $unin $G
Check "avisa del cambio de versión" ($o -match 'cambiado el juego de versión')
Check "no pone el Assembly-CSharp antiguo" ((Get-AcHash $G) -eq $newAc)
Check "quita los ficheros añadidos" (-not (Test-Path -LiteralPath "$G\hollow_knight_Data\Managed\MMHOOK_Assembly-CSharp.dll"))

"== 4. Fichero sustituido cambiado por otro (misma versión) =="
$G = New-FakeGame 'cambiado'
[void](Run $inst $G)
[IO.File]::WriteAllBytes("$G\hollow_knight_Data\Managed\Newtonsoft.Json.dll", [byte[]](9..99))
$o = Run $unin $G
Check "no dice que está como antes" ($o -notmatch 'vuelve a estar como antes' -and $o -match 'No he podido dejar')
Check "conserva la copia de seguridad" (Test-Path -LiteralPath "$G\hollow_knight_Data\HKTelemetry_kit\backup\Newtonsoft.Json.dll")

"== 5. API de Lumafly incompleta =="
$G = New-FakeGame 'lumafly'
foreach ($n in $apiAdded) { Copy-Item -LiteralPath "$real\hollow_knight_Data\Managed\$n" -Destination "$G\hollow_knight_Data\Managed\$n" }
Copy-Item -LiteralPath "$real\hollow_knight_Data\Managed\Assembly-CSharp.dll" -Destination "$G\hollow_knight_Data\Managed\Assembly-CSharp.dll" -Force
Copy-Item -LiteralPath "$real\hollow_knight_Data\Managed\mscorlib.dll" -Destination "$G\hollow_knight_Data\Managed\mscorlib.dll" -Force
Remove-Item -LiteralPath "$G\hollow_knight_Data\Managed\MonoMod.Utils.dll" -Force
$o = Run $inst $G
Check "avisa de API distinta" ($o -match 'no coincide con la v77 oficial en: MonoMod.Utils.dll')
Check "no la toca" (-not (Test-Path -LiteralPath "$G\hollow_knight_Data\Managed\MonoMod.Utils.dll"))
$o = Run $unin $G; Check "desinstalar deja la API" ($o -match 'no la instaló este kit')

"== 6. Copia que falta, con corchetes en la ruta: mensaje en español =="
$G = New-FakeGame 'corchetes'
Remove-Item -LiteralPath "$K\HKTelemetry_P99\files\api\Mono.Cecil.dll" -Force
$o = Run $inst $G
Check "mensaje claro, sin error técnico" ($o -match 'incompleto o da.ado .api/Mono.Cecil.dll.' -and $o -notmatch 'Error inesperado')
Check "no ha tocado el juego" (-not (Test-Path -LiteralPath "$G\hollow_knight_Data\HKTelemetry_kit"))
Write-Host ($o -split "`n" | Select-String 'X\]' | Out-String)
Expand-Archive (Join-Path $Repo 'dist\HKTelemetry_P99.zip') "$S\kit_tmp" -Force
Copy-Item "$S\kit_tmp\HKTelemetry_P99\files\api\Mono.Cecil.dll" "$K\HKTelemetry_P99\files\api\Mono.Cecil.dll"
Remove-Item "$S\kit_tmp" -Recurse -Force

Remove-Item -LiteralPath "$S\juegos [SSD] ñ" -Recurse -Force
""
if ($fail -eq 0) { "TODOS LOS ESCENARIOS OK"; exit 0 } else { "$fail FALLOS"; exit 1 }
