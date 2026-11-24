# HKTelemetry - funciones comunes del kit (instalar, enviar datos, desinstalar).
# PowerShell 5.1 (viene con Windows 10/11). Guardado en UTF-8 con BOM para que se vean las tildes.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Script:KitRoot = Split-Path -Parent $PSScriptRoot
$Script:FilesDir = Join-Path $Script:KitRoot 'files'
$Script:AppId = '367520'

function Write-Title([string]$text) {
    Write-Host ''
    Write-Host ('=' * 64) -ForegroundColor DarkCyan
    Write-Host ("  " + $text) -ForegroundColor Cyan
    Write-Host ('=' * 64) -ForegroundColor DarkCyan
    Write-Host ''
}
function Write-Ok([string]$text)   { Write-Host ("  [OK] " + $text) -ForegroundColor Green }
function Write-Info([string]$text) { Write-Host ("  " + $text) }
function Write-Warn([string]$text) { Write-Host ("  [!] " + $text) -ForegroundColor Yellow }
function Write-Bad([string]$text)  { Write-Host ("  [X] " + $text) -ForegroundColor Red }

# En la ventana elevada (administrador) no hay .bat que haga 'pause': se pausa desde aquí.
$Script:PauseOnExit = $false
# -Yes: responde «sí» a todo y no espera (solo para pruebas automáticas del kit).
$Script:AssumeYes = $false

# Error que el usuario tiene que ver: se muestra y se sale sin traza técnica.
function Stop-Kit([string]$text) {
    Write-Host ''
    Write-Bad $text
    Write-Host ''
    if ($Script:PauseOnExit) { [void](Read-Host "  Pulsa Enter para cerrar") }
    exit 1
}

function Read-YesNo([string]$question) {
    if ($Script:AssumeYes) { Write-Info ($question + " -> S (automático)"); return $true }
    while ($true) {
        $a = Read-Host ("  " + $question + " (S/N)")
        if ($a -match '^\s*[sSyY]') { return $true }
        if ($a -match '^\s*[nN]') { return $false }
    }
}

# '' si el fichero no existe (así una copia que falta se trata como «distinta», con su mensaje en español).
function Get-Sha256([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return '' }
    $h = Get-FileHash -Algorithm SHA256 -LiteralPath $path -ErrorAction Stop
    if (-not $h) { return '' }
    return $h.Hash.ToLowerInvariant()
}

# Propiedad de un objeto de ConvertFrom-Json sin romper con Set-StrictMode si no existe.
function Get-Prop($obj, [string]$name) {
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($p) { return $p.Value }
    return $null
}

# Ficheros de la API (del manifiesto) que faltan o no coinciden en Managed.
function Get-ApiMismatches([string]$managed, $man) {
    $bad = @()
    foreach ($f in $man.api_files) {
        if ((Get-Sha256 (Join-Path $managed $f.name)) -ne $f.sha256) { $bad += $f.name }
    }
    return $bad
}

function Read-Manifest {
    $path = Join-Path $Script:FilesDir 'manifest.json'
    if (-not (Test-Path -LiteralPath $path)) {
        Stop-Kit ("No encuentro la carpeta 'files' del kit. ¿Has descomprimido el zip entero? " +
                  "Haz clic derecho en el zip > 'Extraer todo...' y ejecuta el .bat desde la carpeta extraída.")
    }
    return (Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json)
}

# El kit entero tiene que estar bien antes de tocar nada (zip mal descomprimido, antivirus...).
function Test-KitFiles($man) {
    $bad = @()
    foreach ($f in $man.api_files) {
        if ((Get-Sha256 (Join-Path (Join-Path $Script:FilesDir 'api') $f.name)) -ne $f.sha256) { $bad += ('api/' + $f.name) }
    }
    foreach ($f in $man.mod_files) {
        if ((Get-Sha256 (Join-Path (Join-Path $Script:FilesDir 'mod') $f.name)) -ne $f.sha256) { $bad += ('mod/' + $f.name) }
    }
    if ($bad.Count -gt 0) {
        Stop-Kit ("El kit está incompleto o dañado (" + ($bad -join ', ') + "). No se ha tocado nada. " +
                  "Vuelve a descomprimir el zip; si sigue pasando, puede que el antivirus haya quitado algún fichero: avisa a Alex.")
    }
}

# ---------------------------------------------------------------- búsqueda del juego

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $keys = @(
        @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
        @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' }
    )
    foreach ($k in $keys) {
        try {
            $v = (Get-ItemProperty -Path $k.Path -Name $k.Name -ErrorAction Stop).($k.Name)
            if ($v) { $roots.Add(($v -replace '/', '\')) }
        } catch { }
    }
    $pf86 = [Environment]::GetFolderPath('ProgramFilesX86')
    if ($pf86) { $roots.Add((Join-Path $pf86 'Steam')) }
    return $roots | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -Unique
}

function Get-SteamLibraries {
    $libs = New-Object System.Collections.Generic.List[string]
    foreach ($root in Get-SteamRoots) {
        $libs.Add($root)
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($line in Get-Content -LiteralPath $vdf -Encoding UTF8) {
                if ($line -match '^\s*"path"\s*"(.+)"\s*$') {
                    $libs.Add(($Matches[1] -replace '\\\\', '\'))
                }
            }
        }
    }
    return $libs | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -Unique
}

function Test-GameDir([string]$dir) {
    if (-not $dir) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir 'hollow_knight_Data\Managed\Assembly-CSharp.dll')) -and
           (Test-Path -LiteralPath (Join-Path $dir 'UnityPlayer.dll'))
}

# ¿Copia de Steam? Solo por la ruta (...\steamapps\common\...): decide qué instrucciones se enseñan
# (las de Steam o las genéricas) y si se acepta un Assembly-CSharp desconocido.
function Test-SteamGameDir([string]$dir) { return [bool]($dir -match '[\\/]steamapps[\\/]common[\\/]') }

# Copias fuera de Steam (GOG u otras): el registro de GOG y sus carpetas por defecto.
function Get-NonSteamCandidates {
    $c = New-Object System.Collections.Generic.List[string]
    foreach ($base in @('HKLM:\SOFTWARE\WOW6432Node\GOG.com\Games', 'HKLM:\SOFTWARE\GOG.com\Games')) {
        try {
            foreach ($k in @(Get-ChildItem -Path $base -ErrorAction Stop)) {
                try {
                    $p = Get-ItemProperty -Path $k.PSPath -ErrorAction Stop
                    $name = [string](Get-Prop $p 'gameName')
                    $path = [string](Get-Prop $p 'path')
                    if ($name -like '*Hollow Knight*' -and $path) { $c.Add($path) }
                } catch { }
            }
        } catch { }
    }
    foreach ($env in @('ProgramFiles(x86)', 'ProgramFiles')) {
        $pf = [Environment]::GetEnvironmentVariable($env)
        if ($pf) { $c.Add((Join-Path $pf 'GOG Galaxy\Games\Hollow Knight')) }
    }
    $c.Add('C:\GOG Games\Hollow Knight')
    return $c | Select-Object -Unique
}

# Devuelve @{ Dir = ...; Manifest = <ruta appmanifest o $null>; Steam = $true/$false } o $null.
function Find-GameDir {
    foreach ($lib in Get-SteamLibraries) {
        $acf = Join-Path $lib ("steamapps\appmanifest_" + $Script:AppId + ".acf")
        $installdir = 'Hollow Knight'
        if (Test-Path -LiteralPath $acf) {
            foreach ($line in Get-Content -LiteralPath $acf -Encoding UTF8) {
                if ($line -match '^\s*"installdir"\s*"(.+)"\s*$') { $installdir = $Matches[1] }
            }
        }
        $dir = Join-Path $lib ("steamapps\common\" + $installdir)
        if (Test-GameDir $dir) {
            $m = $null
            if (Test-Path -LiteralPath $acf) { $m = $acf }
            return @{ Dir = $dir; Manifest = $m; Steam = $true }
        }
    }
    foreach ($dir in Get-NonSteamCandidates) {
        if (Test-GameDir $dir) { return @{ Dir = $dir; Manifest = $null; Steam = (Test-SteamGameDir $dir) } }
    }
    return $null
}

# $override: ruta indicada a mano (-GameDir), para instalaciones fuera de Steam o pruebas.
function Resolve-GameDir([string]$override) {
    if ($override) {
        if (-not (Test-GameDir $override)) { Stop-Kit ("En " + $override + " no está Hollow Knight (falta hollow_knight_Data\Managed).") }
        Write-Ok ("Hollow Knight (ruta indicada): " + $override)
        return @{ Dir = $override; Manifest = $null; Steam = (Test-SteamGameDir $override) }
    }
    $found = Find-GameDir
    if ($found) {
        Write-Ok ("Hollow Knight encontrado en: " + $found.Dir)
        return $found
    }
    Write-Warn "No he encontrado Hollow Knight automáticamente."
    Write-Info "Busca la carpeta del juego (la que tiene hollow_knight.exe):"
    Write-Info "  - Si es de Steam: Steam > clic derecho en Hollow Knight > Administrar > Ver archivos locales."
    Write-Info "  - Si no es de Steam: la carpeta donde lo tengas instalado o descomprimido."
    Write-Info "ARRASTRA esa carpeta (o hollow_knight.exe) a esta ventana y pulsa Enter."
    Write-Info "(También vale copiar la ruta de la barra de direcciones y pegarla con clic derecho.)"
    while ($true) {
        $p = (Read-Host "  Carpeta de Hollow Knight (vacío para salir)").Trim().Trim('"').Trim("'").Trim()
        if (-not $p) { Stop-Kit "Cancelado." }
        if ((Test-Path -LiteralPath $p -PathType Leaf) -and ($p -like '*.exe')) { $p = Split-Path -Parent $p }
        if (Test-GameDir $p) { return @{ Dir = $p; Manifest = $null; Steam = (Test-SteamGameDir $p) } }
        # Carpeta de más arriba (p. ej. la del zip descomprimido): mirar un nivel por debajo.
        $sub = $null
        try {
            $sub = @(Get-ChildItem -LiteralPath $p -Directory -ErrorAction Stop | Where-Object { Test-GameDir $_.FullName }) | Select-Object -First 1
        } catch { }
        if ($sub) {
            Write-Ok ("Hollow Knight encontrado en: " + $sub.FullName)
            return @{ Dir = $sub.FullName; Manifest = $null; Steam = (Test-SteamGameDir $sub.FullName) }
        }
        Write-Bad "En esa carpeta no está el juego (tiene que tener hollow_knight.exe y hollow_knight_Data). Prueba otra vez."
    }
}

function Get-ManagedDir([string]$gameDir) { return (Join-Path $gameDir 'hollow_knight_Data\Managed') }
function Get-KitStateDir([string]$gameDir) { return (Join-Path $gameDir 'hollow_knight_Data\HKTelemetry_kit') }

function Get-UnityVersion([string]$gameDir) {
    try { return [string]((Get-Item -LiteralPath (Join-Path $gameDir 'UnityPlayer.dll')).VersionInfo.FileVersion) } catch { return '' }
}

function Get-BetaKey([string]$acf) {
    if (-not $acf -or -not (Test-Path -LiteralPath $acf)) { return '' }
    $key = ''
    foreach ($line in Get-Content -LiteralPath $acf -Encoding UTF8) {
        if ($line -match '^\s*"BetaKey"\s*"(.*)"\s*$') { $key = $Matches[1] }
    }
    return $key
}

function Show-WrongVersionHelp([bool]$steam = $true) {
    Write-Info ""
    if ($steam) {
        Write-Info "El mod solo funciona con la versión 1.5.78 de Hollow Knight. Para cambiarla en Steam:"
        Write-Info "  1. Biblioteca > clic derecho en Hollow Knight > Propiedades."
        Write-Info "  2. 'Versiones de juego y betas' > elige la versión anterior '1.5.78.11833'."
        Write-Info "  3. Espera a que Steam termine de descargarla y vuelve a ejecutar 1_INSTALAR.bat."
    } else {
        Write-Info "El mod solo funciona con la versión 1.5.78 de Hollow Knight (la anterior a la última"
        Write-Info "actualización; es la que usan casi todos los mods). La tuya es otra."
        Write-Info "Necesitas una copia de la 1.5.78 (por ejemplo, la de GOG). Cuando la tengas, vuelve a"
        Write-Info "ejecutar 1_INSTALAR.bat y elige esa carpeta."
    }
    Write-Info "Tus partidas guardadas no se tocan."
}

function Test-GameRunning {
    return [bool](Get-Process -Name 'hollow_knight' -ErrorAction SilentlyContinue)
}

function Wait-GameClosed {
    while (Test-GameRunning) {
        if ($Script:AssumeYes) { Stop-Kit "Hollow Knight está abierto." }
        Write-Warn "Hollow Knight está abierto. Ciérralo del todo y pulsa Enter para seguir."
        [void](Read-Host "  ")
    }
}

function Get-DataRoot {
    $localLow = Join-Path $env:USERPROFILE 'AppData\LocalLow'
    return (Join-Path $localLow 'Team Cherry\Hollow Knight')
}

function Test-Writable([string]$dir) {
    $probe = Join-Path $dir ('.hktelemetry_probe_' + [Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($probe, 'x')
        Remove-Item -LiteralPath $probe -Force
        return $true
    } catch { return $false }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Vuelve a lanzar el script actual como administrador (solo si hace falta para escribir en la carpeta del juego).
function Restart-Elevated([string]$scriptPath) {
    Write-Warn "No tengo permiso para escribir en la carpeta del juego. Voy a pedir permisos de administrador."
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'), '-Elevated')
    if ($Script:GameDirOverride) { $argList += @('-GameDir', ('"' + $Script:GameDirOverride + '"')) }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList
    exit 0
}

function Read-State([string]$gameDir) {
    $p = Join-Path (Get-KitStateDir $gameDir) 'state.json'
    if (Test-Path -LiteralPath $p) { return (Get-Content -LiteralPath $p -Raw -Encoding UTF8 | ConvertFrom-Json) }
    return $null
}

function Write-State([string]$gameDir, $state) {
    $dir = Get-KitStateDir $gameDir
    if (-not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir) }
    $json = $state | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $dir 'state.json'), $json, (New-Object Text.UTF8Encoding($false)))
}

# Otras carpetas de mods (fuera de HKTelemetry y Disabled): pueden cambiar la jugabilidad.
function Get-OtherMods([string]$managed) {
    $mods = Join-Path $managed 'Mods'
    if (-not (Test-Path -LiteralPath $mods)) { return @() }
    $other = @()
    foreach ($d in Get-ChildItem -LiteralPath $mods -Directory) {
        if ($d.Name -ne 'HKTelemetry' -and $d.Name -ne 'Disabled') { $other += $d.Name }
    }
    foreach ($f in Get-ChildItem -LiteralPath $mods -File -Filter '*.dll') { $other += $f.Name }
    return $other
}

function Pause-End {
    Write-Host ''
    [void](Read-Host "  Pulsa Enter para cerrar")
}
