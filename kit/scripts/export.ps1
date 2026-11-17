# HKTelemetry - empaquetar los datos grabados en un zip para enviárselo a Alex. Lo lanza 2_ENVIAR_DATOS.bat.
param([switch]$Yes)
. (Join-Path $PSScriptRoot 'common.ps1')
$Script:AssumeYes = [bool]$Yes

try {
    $man = Read-Manifest
    Write-Title ("HKTelemetry - enviar datos (" + $man.player_id + ")")
    Wait-GameClosed

    $base = Get-DataRoot
    $root = Join-Path $base 'HKTelemetry'
    $sessions = @()
    if (Test-Path -LiteralPath $root) {
        $sessions = @(Get-ChildItem -LiteralPath $root -Directory |
                      Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'session.json') } |
                      Sort-Object Name)
    }
    if ($sessions.Count -eq 0) {
        Stop-Kit ("No hay datos grabados todavía en " + $root + ". ¿Has jugado con el mod instalado?")
    }

    # Resumen para que el jugador vea qué envía.
    $seconds = 0.0
    $open = 0
    $recovered = 0
    foreach ($s in $sessions) {
        try {
            $j = Get-Content -LiteralPath (Join-Path $s.FullName 'session.json') -Raw -Encoding UTF8 | ConvertFrom-Json
            $end = Get-Prop $j 'end'
            $tg = Get-Prop $end 't_game'
            if ($null -ne $tg) { $seconds += [double]$tg }
            elseif ((Get-Prop $end 'reason') -eq 'recovered') { $recovered++ }
            else { $open++ }
        } catch { $open++ }
    }
    $sizeMb = [math]::Round(((Get-ChildItem -LiteralPath $root -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Info ("Sesiones: " + $sessions.Count + "   Tiempo de juego grabado: " + [math]::Round($seconds / 3600, 1) + " h   Tamaño: " + $sizeMb + " MB")
    if ($recovered -gt 0) { Write-Info ("(" + $recovered + " sesión(es) cerradas de golpe: su tiempo no cuenta en el total, pero se envían.)") }
    if ($open -gt 0) { Write-Info ("(" + $open + " sesión(es) sin cerrar del todo; se envían igual.)") }

    # Los ModLog (el actual y los de arranques anteriores) ayudan a diagnosticar problemas. Se tapan
    # rutas que pueden llevar el nombre de la persona: carpeta de usuario, carpeta del juego y de Steam.
    $tmp = Join-Path $env:TEMP ('HKTelemetry_export_' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $tmp)
    $items = @($sessions | ForEach-Object { $_.FullName })

    $redact = New-Object System.Collections.Generic.List[object]
    $redact.Add(@($env:USERPROFILE, '%USERPROFILE%'))
    try {
        $g = Find-GameDir
        if ($g) {
            $redact.Add(@($g.Dir, '%GAMEDIR%'))
            $lib = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $g.Dir))   # <biblioteca>\steamapps\common\<juego>
            if ($lib) { $redact.Add(@($lib, '%STEAMLIB%')) }
        }
    } catch { }
    function Hide-Paths([string]$text) {
        foreach ($r in $redact) {
            if (-not $r[0]) { continue }
            foreach ($form in @($r[0], ($r[0].Replace([char]92, [char]47)))) {
                $text = [Regex]::Replace($text, [Regex]::Escape($form), $r[1], 'IgnoreCase')
            }
        }
        if ($env:USERNAME -and $env:USERNAME.Length -ge 3) {
            $text = [Regex]::Replace($text, '\b' + [Regex]::Escape($env:USERNAME) + '\b', '%USERNAME%', 'IgnoreCase')
        }
        return $text
    }

    $logs = @()
    $modlog = Join-Path $base 'ModLog.txt'
    if (Test-Path -LiteralPath $modlog) { $logs += @{ Src = $modlog; Dst = 'ModLog.txt' } }
    $oldDir = Join-Path $base 'Old ModLogs'
    if (Test-Path -LiteralPath $oldDir) {
        foreach ($f in Get-ChildItem -LiteralPath $oldDir -File -Filter '*.txt') { $logs += @{ Src = $f.FullName; Dst = ('modlogs\' + $f.Name) } }
    }
    foreach ($l in $logs) {
        try {
            $text = Get-Content -LiteralPath $l.Src -Raw -Encoding UTF8
            if (-not $text) { continue }
            $out = Join-Path $tmp $l.Dst
            $dir = Split-Path -Parent $out
            if (-not (Test-Path -LiteralPath $dir)) { [void](New-Item -ItemType Directory -Path $dir -Force) }
            [IO.File]::WriteAllText($out, (Hide-Paths $text), (New-Object Text.UTF8Encoding($false)))
        } catch { }
    }
    if (Test-Path -LiteralPath (Join-Path $tmp 'ModLog.txt')) { $items += (Join-Path $tmp 'ModLog.txt') }
    if (Test-Path -LiteralPath (Join-Path $tmp 'modlogs')) { $items += (Join-Path $tmp 'modlogs') }

    $desktop = [Environment]::GetFolderPath('Desktop')
    if (-not $desktop -or -not (Test-Path -LiteralPath $desktop)) { $desktop = $env:USERPROFILE }
    $zip = Join-Path $desktop ("HKTelemetry_" + $man.player_id + "_" + (Get-Date -Format 'yyyyMMdd_HHmm') + ".zip")
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Write-Info "Comprimiendo..."
    # ZipArchive de .NET en vez de Compress-Archive: en PowerShell 5.1 este guarda las rutas con barra invertida.
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $items) {
            if (Test-Path -LiteralPath $item -PathType Container) {
                $parent = Split-Path -Parent $item
                foreach ($f in Get-ChildItem -LiteralPath $item -Recurse -File) {
                    $rel = $f.FullName.Substring($parent.Length).TrimStart([char]92).Replace([char]92, [char]47)
                    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal)
                }
            } else {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item, (Split-Path -Leaf $item), [IO.Compression.CompressionLevel]::Optimal)
            }
        }
    } finally {
        $archive.Dispose()
    }
    Remove-Item -LiteralPath $tmp -Recurse -Force

    Write-Title "Listo"
    Write-Ok ("Zip creado en tu Escritorio: " + (Split-Path -Leaf $zip))
    Write-Info "Envíaselo a Alex (WhatsApp, Drive, correo...). Te abro la carpeta."
    Write-Info "Puedes seguir jugando: la próxima vez se enviará todo otra vez (Alex quita lo repetido)."
    if (-not $Script:AssumeYes) {
        try { Start-Process -FilePath 'explorer.exe' -ArgumentList ('/select,"' + $zip + '"') } catch { }
    }
    Write-Output ("ZIP=" + $zip)
}
catch {
    Write-Bad ("Error inesperado: " + $_.Exception.Message)
    Write-Info "Haz una captura de esta ventana y envíasela a Alex."
    exit 1
}
