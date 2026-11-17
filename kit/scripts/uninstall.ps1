# HKTelemetry - desinstalación. Lo lanza 3_DESINSTALAR.bat.
param([switch]$Elevated, [switch]$Yes, [string]$GameDir)
. (Join-Path $PSScriptRoot 'common.ps1')
$Script:PauseOnExit = [bool]$Elevated
$Script:AssumeYes = [bool]$Yes
$Script:GameDirOverride = $GameDir

try {
    $man = Read-Manifest
    Write-Title "HKTelemetry - desinstalación"

    $game = Resolve-GameDir $GameDir
    $gameDir = $game.Dir
    $managed = Get-ManagedDir $gameDir
    Wait-GameClosed

    # Antes de pedir permisos de administrador: en esa ventana la carpeta de usuario podría ser otra.
    $dataRoot = Join-Path (Get-DataRoot) 'HKTelemetry'
    if (-not $Elevated -and (Test-Path -LiteralPath $dataRoot) -and
        @(Get-ChildItem -LiteralPath $dataRoot -Directory -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-Warn "Tienes datos grabados. Si aún no los has enviado, cancela y ejecuta antes 2_ENVIAR_DATOS.bat."
        if (-not (Read-YesNo "¿Seguir con la desinstalación?")) { Stop-Kit "Cancelado. No se ha tocado nada." }
    }

    if (-not (Test-Writable $managed)) {
        if ($Elevated) { Stop-Kit "Ni siquiera como administrador puedo escribir en la carpeta del juego. Avisa a Alex." }
        Restart-Elevated $PSCommandPath
    }

    # ---- 1. Mod ----
    $modDir = Join-Path $managed 'Mods\HKTelemetry'
    if (Test-Path -LiteralPath $modDir) {
        Remove-Item -LiteralPath $modDir -Recurse -Force
        Write-Ok "Mod HKTelemetry eliminado."
    } else {
        Write-Info "El mod HKTelemetry no estaba instalado."
    }

    # ---- 2. Modding API (solo si la instaló este kit) ----
    $state = Read-State $gameDir
    $stateDir = Get-KitStateDir $gameDir
    $backup = Join-Path $stateDir 'backup'
    if (Get-Prop $state 'api_installed_by_kit') {
        $other = @(Get-OtherMods $managed)
        $removeApi = $true
        if ($other.Count -gt 0) {
            Write-Warn ("Hay otros mods que necesitan la Modding API: " + ($other -join ', '))
            $removeApi = Read-YesNo "¿Quitar también la Modding API? (esos mods dejarán de funcionar)"
        }
        if ($removeApi) {
            $apiHashes = @{}
            foreach ($f in $man.api_files) { $apiHashes[$f.name] = $f.sha256 }
            $problems = 0
            # Si Steam ha cambiado el juego de versión, los originales guardados (1.5.78) ya no valen:
            # no se restaura nada, solo se quitan los ficheros que añadió la API.
            $sameVersion = (Get-UnityVersion $gameDir).StartsWith($man.game.unity_prefix)
            if (-not $sameVersion) {
                Write-Warn "Steam ha cambiado el juego de versión desde la instalación: no restauro los ficheros antiguos."
            } else {
                foreach ($name in @(Get-Prop $state 'api_files_replaced' | Where-Object { $_ })) {
                    $bak = Join-Path $backup $name
                    $dst = Join-Path $managed $name
                    $cur = Get-Sha256 $dst
                    if (-not (Test-Path -LiteralPath $bak)) {
                        Write-Warn ("Falta la copia de seguridad de " + $name); $problems++
                    } elseif ($cur -eq (Get-Sha256 $bak)) {
                        # ya es el original (por ejemplo, tras 'Verificar integridad')
                    } elseif ($apiHashes.ContainsKey($name) -and $cur -eq $apiHashes[$name]) {
                        Copy-Item -LiteralPath $bak -Destination $dst -Force
                        if ((Get-Sha256 $dst) -ne (Get-Sha256 $bak)) { Write-Warn ("No he podido restaurar " + $name); $problems++ }
                    } else {
                        Write-Warn ($name + " ha cambiado desde la instalación (¿Steam?): lo dejo como está."); $problems++
                    }
                }
            }
            foreach ($name in @(Get-Prop $state 'api_files_added' | Where-Object { $_ })) {
                $dst = Join-Path $managed $name
                # Solo se borra si sigue siendo el fichero que puso el kit.
                if ($apiHashes.ContainsKey($name) -and (Get-Sha256 $dst) -eq $apiHashes[$name]) {
                    Remove-Item -LiteralPath $dst -Force
                }
            }
            $acHash = Get-Sha256 (Join-Path $managed 'Assembly-CSharp.dll')
            if (-not $sameVersion) {
                Remove-Item -LiteralPath $stateDir -Recurse -Force
                Write-Ok "Ficheros de la Modding API quitados."
                Write-Info "Por si acaso: Steam > Propiedades > Archivos instalados > 'Verificar integridad de los archivos'."
            } elseif ($problems -eq 0 -and $acHash -eq $man.game.vanilla_assembly_csharp_sha256) {
                Remove-Item -LiteralPath $stateDir -Recurse -Force
                Write-Ok "Modding API quitada: el juego vuelve a estar como antes."
            } else {
                # Se conserva la copia de seguridad: no se ha podido volver al estado original.
                Write-Warn "No he podido dejar el juego exactamente como estaba."
                Write-Info "Para arreglarlo: Steam > clic derecho en Hollow Knight > Propiedades > Archivos instalados >"
                Write-Info "'Verificar integridad de los archivos'."
            }
        } else {
            Write-Info "La Modding API se queda instalada."
        }
    } else {
        Write-Info "La Modding API no la instaló este kit: se queda como estaba."
        if (Test-Path -LiteralPath $stateDir) { Remove-Item -LiteralPath $stateDir -Recurse -Force }
    }

    Write-Title "Listo"
    if (Test-Path -LiteralPath $dataRoot) {
        Write-Info "Tus datos grabados siguen en:"
        Write-Info ("  " + $dataRoot)
        Write-Info "Puedes borrar esa carpeta cuando ya los hayas enviado."
    }
    Write-Info "Si quieres volver a la última versión de Hollow Knight: Steam > Propiedades >"
    Write-Info "'Versiones de juego y betas' > 'Ninguna'."
}
catch {
    Write-Bad ("Error inesperado: " + $_.Exception.Message)
    Write-Info "Haz una captura de esta ventana y envíasela a Alex."
    Write-Info "Para dejar el juego como estaba: Steam > Propiedades > Archivos instalados > 'Verificar integridad'."
    if ($Script:PauseOnExit) { [void](Read-Host "  Pulsa Enter para cerrar") }
    exit 1
}
if ($Script:PauseOnExit) { [void](Read-Host "  Pulsa Enter para cerrar") }
