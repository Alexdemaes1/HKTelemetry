# HKTelemetry - instalación (Modding API v77 + mod). Lo lanza 1_INSTALAR.bat.
param([switch]$Elevated, [switch]$Yes, [string]$GameDir)
. (Join-Path $PSScriptRoot 'common.ps1')
$Script:PauseOnExit = [bool]$Elevated
$Script:AssumeYes = [bool]$Yes
$Script:GameDirOverride = $GameDir

try {
    $man = Read-Manifest
    Test-KitFiles $man
    Write-Title ("HKTelemetry " + $man.mod_version + " - instalación")

    # ---- 1. Aviso de datos y consentimiento ----
    Write-Info "Este mod SOLO registra cómo juegas a Hollow Knight, para un trabajo de máster de IA."
    Write-Info "No cambia nada del juego ni de tus partidas."
    Write-Info ""
    $auto = [bool](Get-Prop $man 'auto_upload')
    if ($auto) {
        Write-Info "Qué se guarda (en tu PC; al terminar cada partida se envía solo a la carpeta del proyecto):"
    } else {
        Write-Info "Qué se guarda (en tu PC, hasta que tú lo envíes con 2_ENVIAR_DATOS.bat):"
    }
    Write-Info "  - Lo que pasa en la partida: salas, combates, vida, alma, objetos, mapa..."
    Write-Info "  - Posición del personaje y botones pulsados (60 veces por segundo)."
    Write-Info "  - Horas jugadas a Hollow Knight en Steam y logros conseguidos (para saber tu experiencia)."
    Write-Info "  - Datos técnicos: fps, resolución, versión del juego y otros mods instalados."
    Write-Info "  - Progreso de tus partidas guardadas (horas, porcentaje completado)."
    Write-Info "  - Tu nombre de Steam y el número de tu cuenta de Steam, para saber de quién es cada partida"
    Write-Info "    (no tu usuario ni tu contraseña). Alex los cambia por un código anónimo antes de analizarlos."
    Write-Info "Nada más de fuera de Hollow Knight. Puedes desinstalarlo cuando quieras con 3_DESINSTALAR.bat."
    if ($auto) {
        Write-Info ""
        Write-Info "Envío: al volver al menú principal, cada partida terminada se envía por internet a una"
        Write-Info "carpeta de Google Drive del proyecto (solo la ve Alex). Puedes desactivarlo en"
        Write-Info "Opciones > Mods > HKTelemetry y enviar tú los datos con 2_ENVIAR_DATOS.bat."
    }
    Write-Info ""
    if (-not $Elevated) {
        if (-not (Read-YesNo "¿Estás de acuerdo y quieres instalarlo?")) { Stop-Kit "Instalación cancelada. No se ha tocado nada." }
    }

    # ---- 2. Juego y versión ----
    Write-Title "Comprobando el juego"
    $game = Resolve-GameDir $GameDir
    $gameDir = $game.Dir
    $isSteam = [bool]$game.Steam
    $managed = Get-ManagedDir $gameDir

    $unity = Get-UnityVersion $gameDir
    if (-not $unity.StartsWith($man.game.unity_prefix)) {
        Write-Bad ("Tu Hollow Knight no es la versión 1.5.78 (motor Unity " + $unity + ").")
        Show-WrongVersionHelp $isSteam
        Stop-Kit "Instalación cancelada. No se ha tocado nada."
    }
    $beta = Get-BetaKey $game.Manifest
    Write-Ok ("Versión del juego correcta (Unity " + $unity + $(if ($beta) { ", rama de Steam '" + $beta + "'" } else { "" }) + ").")

    Wait-GameClosed

    # Copia de las partidas guardadas por si acaso (el mod no las toca). En la ventana de administrador
    # no se repite: ya se hizo, y su carpeta de usuario podría ser otra.
    if (-not $Elevated) {
        $dataRoot = Get-DataRoot
        $saves = @()
        if (Test-Path -LiteralPath $dataRoot) { $saves = @(Get-ChildItem -LiteralPath $dataRoot -File -Filter 'user*.dat*') }
        if ($saves.Count -gt 0) {
            $savesBak = Join-Path $dataRoot ('HKTelemetry_saves_backup\' + (Get-Date -Format 'yyyyMMdd_HHmmss'))
            [void](New-Item -ItemType Directory -Path $savesBak -Force)
            foreach ($f in $saves) { Copy-Item -LiteralPath $f.FullName -Destination $savesBak }
            Write-Ok ("Copia de tus " + $saves.Count + " ficheros de partidas en " + $savesBak)
        }
    }

    if (-not (Test-Writable $managed)) {
        if ($Elevated) { Stop-Kit "Ni siquiera como administrador puedo escribir en la carpeta del juego. Avisa a Alex." }
        Restart-Elevated $PSCommandPath
    }

    # ---- 3. Modding API ----
    Write-Title "Modding API"
    $acHash = Get-Sha256 (Join-Path $managed 'Assembly-CSharp.dll')
    $state = Read-State $gameDir
    $prevByKit = [bool](Get-Prop $state 'api_installed_by_kit')
    $apiByKit = $false
    $isApi = $acHash -eq $man.game.api_assembly_csharp_sha256
    $isVanilla = $acHash -eq $man.game.vanilla_assembly_csharp_sha256

    # Copia que no es de Steam con un Assembly-CSharp que no es el de Steam (otra tienda, otra copia o
    # una API antigua): misma versión del motor, así que se ofrece instalar igual. Se guarda copia de
    # todo lo que se sustituye y 3_DESINSTALAR.bat lo devuelve tal cual.
    $acceptUnknown = $false
    if (-not $isApi -and -not $isVanilla -and -not $isSteam) {
        Write-Warn "Tu copia del juego no es idéntica a la de Steam (es la versión correcta, pero algún fichero cambia)."
        Write-Info "Normalmente funciona igual. Antes de tocar nada guardo una copia de los ficheros que cambio:"
        Write-Info "si el juego no arrancase, 3_DESINSTALAR.bat lo deja exactamente como estaba."
        if (-not (Read-YesNo "¿Instalar de todas formas?")) { Stop-Kit "Instalación cancelada. No se ha tocado nada." }
        $acceptUnknown = $true
    }

    if ($isApi -and -not $prevByKit) {
        # Puesta por otro (Lumafly...): no se toca, pero se avisa si está incompleta o es distinta.
        $bad = @(Get-ApiMismatches $managed $man)
        if ($bad.Count -gt 0) {
            Write-Warn ("La Modding API instalada no coincide con la v77 oficial en: " + ($bad -join ', '))
            Write-Info "Si el juego no arranca o el mod no aparece: Steam > Propiedades > Archivos instalados >"
            Write-Info "'Verificar integridad de los archivos' y vuelve a ejecutar 1_INSTALAR.bat."
        } else {
            Write-Ok "La Modding API v77 ya estaba instalada (por ejemplo con Lumafly). No la toco."
        }
    }
    elseif ($isVanilla -or $acceptUnknown -or ($isApi -and $prevByKit)) {
        # Instalación nueva, reparación de una instalación a medias, o reinstalación después de que Steam
        # ('Verificar integridad') haya devuelto algunos ficheros originales.
        $stateDir = Get-KitStateDir $gameDir
        $backup = Join-Path $stateDir 'backup'
        if (-not (Test-Path -LiteralPath $backup)) { [void](New-Item -ItemType Directory -Path $backup -Force) }
        $added = @()
        $replaced = @()
        if ($prevByKit) {
            $added = @(Get-Prop $state 'api_files_added' | Where-Object { $_ })
            $replaced = @(Get-Prop $state 'api_files_replaced' | Where-Object { $_ })
        }
        foreach ($f in $man.api_files) {
            if ($added -contains $f.name -or $replaced -contains $f.name) { continue } # ya clasificado antes
            $dst = Join-Path $managed $f.name
            $bak = Join-Path $backup $f.name
            if (-not (Test-Path -LiteralPath $dst)) {
                $added += $f.name
            } else {
                # Todo fichero que ya existía se guarda, aunque sea idéntico al de la API (el juego original
                # podría traer el mismo): desinstalar lo devuelve, nunca lo borra. Los que dejó una
                # instalación anterior del kit ya están clasificados arriba (estado anterior).
                if (-not (Test-Path -LiteralPath $bak)) { Copy-Item -LiteralPath $dst -Destination $bak -Force }
                $replaced += $f.name
            }
        }
        # El estado se escribe ANTES de copiar: si algo falla a medias, el desinstalador sabe qué restaurar.
        Write-State $gameDir @{
            kit_version = $man.kit_version
            api_installed_by_kit = $true
            api_files_added = $added
            api_files_replaced = $replaced
            installed_utc = (Get-Date).ToUniversalTime().ToString('o')
        }
        $copied = 0
        foreach ($f in $man.api_files) {
            $dst = Join-Path $managed $f.name
            if ((Get-Sha256 $dst) -ne $f.sha256) {
                Copy-Item -LiteralPath (Join-Path $Script:FilesDir ('api\' + $f.name)) -Destination $dst -Force
                $copied++
            }
        }
        $bad = @(Get-ApiMismatches $managed $man)
        if ($bad.Count -gt 0) {
            Stop-Kit ("No se han podido copiar bien: " + ($bad -join ', ') + ". ¿Un antivirus? Vuelve a ejecutar 1_INSTALAR.bat o, si sigue fallando, 3_DESINSTALAR.bat y avisa a Alex.")
        }
        $apiByKit = $true
        if (($isVanilla -or $acceptUnknown) -and -not $prevByKit) {
            Write-Ok ("Modding API v77 instalada (" + $man.api_files.Count + " ficheros; copia de seguridad de los originales guardada).")
        } elseif ($copied -gt 0) {
            Write-Ok ("Modding API v77 reparada (" + $copied + " ficheros vueltos a copiar).")
        } else {
            Write-Ok "La Modding API ya estaba instalada por este kit y está completa."
        }
    }
    else {
        Write-Bad "Los ficheros del juego no son ni los originales de la 1.5.78 ni la Modding API v77."
        Write-Info "Puede que tengas otra versión de la Modding API o un juego modificado."
        Write-Info "Solución: en Steam, clic derecho en Hollow Knight > Propiedades > Archivos instalados >"
        Write-Info "'Verificar integridad de los archivos', y vuelve a ejecutar 1_INSTALAR.bat."
        Stop-Kit "Instalación cancelada. No se ha tocado nada."
    }

    # ---- 4. Mod ----
    Write-Title "Mod HKTelemetry"
    $modDir = Join-Path $managed 'Mods\HKTelemetry'
    if (-not (Test-Path -LiteralPath $modDir)) { [void](New-Item -ItemType Directory -Path $modDir -Force) }
    # Sin Steam, el jugador se identifica con un id aleatorio guardado en los ajustes: conservarlo.
    $oldInstallId = $null
    $settingsPath = Join-Path $modDir 'HKTelemetry.GlobalSettings.json'
    try {
        if (Test-Path -LiteralPath $settingsPath) {
            $oldInstallId = Get-Prop (Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json) 'InstallId'
            if ($oldInstallId -notmatch '^[0-9a-f]{8}$') { $oldInstallId = $null }
        }
    } catch { $oldInstallId = $null }
    foreach ($f in $man.mod_files) {
        Copy-Item -LiteralPath (Join-Path $Script:FilesDir ('mod\' + $f.name)) -Destination (Join-Path $modDir $f.name) -Force
        if ((Get-Sha256 (Join-Path $modDir $f.name)) -ne $f.sha256) { Stop-Kit ("La copia de " + $f.name + " no ha salido bien.") }
    }
    if ($oldInstallId) {
        try {
            $js = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $js | Add-Member -NotePropertyName 'InstallId' -NotePropertyValue $oldInstallId -Force
            [IO.File]::WriteAllText($settingsPath, ($js | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        } catch { }
    }
    # Restos de compilaciones de desarrollo que no forman parte del kit.
    $pdb = Join-Path $modDir 'HKTelemetry.pdb'
    if (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb -Force }
    Get-ChildItem -LiteralPath $modDir -File | ForEach-Object { try { Unblock-File -LiteralPath $_.FullName } catch { } }
    Get-ChildItem -LiteralPath $managed -File | ForEach-Object { try { Unblock-File -LiteralPath $_.FullName } catch { } }
    Write-Ok "Mod instalado."

    $state = Read-State $gameDir
    $newState = @{
        kit_version = $man.kit_version
        api_installed_by_kit = $apiByKit
        api_files_added = @()
        api_files_replaced = @()
        installed_utc = (Get-Date).ToUniversalTime().ToString('o')
    }
    if ($apiByKit -and (Get-Prop $state 'api_installed_by_kit')) {
        $newState.api_files_added = @(Get-Prop $state 'api_files_added' | Where-Object { $_ })
        $newState.api_files_replaced = @(Get-Prop $state 'api_files_replaced' | Where-Object { $_ })
    }
    Write-State $gameDir $newState

    # ---- 5. Otros mods ----
    $other = @(Get-OtherMods $managed)
    if ($other.Count -gt 0) {
        Write-Warn ("Tienes otros mods instalados: " + ($other -join ', '))
        Write-Info "Si alguno cambia el juego (dificultad, ayudas, mapa...), desactívalo mientras grabas,"
        Write-Info "moviendo su carpeta a Mods\Disabled. El mod deja constancia de qué mods había."
    }

    Write-Title "Listo"
    Write-Info "Abre Hollow Knight como siempre y juega normal. En el menú principal, abajo, verás:"
    if ($isSteam) {
        Write-Info ("  HKTelemetry v" + $man.mod_version + " - grabando como <tu nombre de Steam>")
    } else {
        Write-Info ("  HKTelemetry v" + $man.mod_version + " - grabando como <tu nombre de Steam o un código como U1a2b3c4d>")
    }
    Write-Info "Mientras juegas, un punto verde arriba a la derecha indica que está grabando"
    Write-Info "(gris en pausa o menús; ROJO si algo falla: en ese caso avisa a Alex)."
    Write-Info ""
    if ([bool](Get-Prop $man 'auto_upload')) {
        Write-Info "Los datos se envían solos al volver al menú principal (necesita internet). En el menú verás"
        Write-Info "'datos enviados' o cuántas sesiones quedan. Si algo falla, usa 2_ENVIAR_DATOS.bat."
    } else {
        Write-Info "Cuando quieras enviar tus datos: cierra el juego y ejecuta 2_ENVIAR_DATOS.bat."
    }
}
catch {
    Write-Bad ("Error inesperado: " + $_.Exception.Message)
    Write-Info "Haz una captura de esta ventana y envíasela a Alex. Si el juego no arranca, ejecuta 3_DESINSTALAR.bat."
    if ($Script:PauseOnExit) { [void](Read-Host "  Pulsa Enter para cerrar") }
    exit 1
}
if ($Script:PauseOnExit) { [void](Read-Host "  Pulsa Enter para cerrar") }
