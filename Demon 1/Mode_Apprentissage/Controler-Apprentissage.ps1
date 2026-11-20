# SentinelCore — contrôle manuel du mode apprentissage D1
# À lancer explicitement depuis PowerShell. Ce script ne démarre pas lʼapprentissage automatiquement.

$ErrorActionPreference = 'Stop'
$moduleRoot = $PSScriptRoot
$projectFile = Join-Path $moduleRoot 'src\SentinelCore.Demon1.Learning.Collector\SentinelCore.Demon1.Learning.Collector.csproj'
$dataRoot = Join-Path $env:LOCALAPPDATA 'SentinelCore\Demon1Learning'
$binaryRoot = Join-Path $dataRoot 'app'
$databasePath = Join-Path $dataRoot 'learning.db'
$statusPath = Join-Path $dataRoot 'status.json'
$stopPath = Join-Path $dataRoot 'stop.signal'
$workerDll = Join-Path $binaryRoot 'SentinelCore.Demon1.Learning.Collector.dll'
$currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$script:worker = $null

New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

function Quote-Argument([string]$Value) {
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Ensure-WorkerBuilt {
    if (Test-Path -LiteralPath $workerDll) { return }
    Write-Host 'Préparation du collecteur de mesures...'
    & dotnet build $projectFile --configuration Release --output $binaryRoot --nologo
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $workerDll)) {
        throw 'La compilation du collecteur a échoué. Consulte les messages ci-dessus.'
    }
}

function Start-Learning {
    if ($script:worker -and -not $script:worker.HasExited) {
        Write-Host 'Le mode apprentissage est déjà actif.' -ForegroundColor Yellow
        return
    }
    $answer = Read-Host 'Confirme que tu es le propriétaire et veux commencer la calibration (O/N)'
    if ($answer -notin @('O','o','Oui','oui')) { Write-Host 'Démarrage annulé.'; return }

    Ensure-WorkerBuilt
    Remove-Item -LiteralPath $stopPath -Force -ErrorAction SilentlyContinue
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = (Get-Command dotnet).Source
    $psi.Arguments = (Quote-Argument $workerDll) + ' collect --db ' + (Quote-Argument $databasePath) +
        ' --sid ' + (Quote-Argument $currentSid) + ' --context ' + (Quote-Argument 'LOCAL|DEFAULT_KB|DEFAULT_MOUSE') +
        ' --status ' + (Quote-Argument $statusPath) + ' --stop ' + (Quote-Argument $stopPath) +
        ' --owner-confirmed true --ignore-pid ' + $PID
    $psi.WorkingDirectory = $moduleRoot
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $script:worker = [System.Diagnostics.Process]::new()
    $script:worker.StartInfo = $psi
    if (-not $script:worker.Start()) { throw 'Le collecteur nʼa pas démarré.' }
    $script:worker.BeginOutputReadLine()
    $script:worker.BeginErrorReadLine()
    Start-Sleep -Milliseconds 800
    if ($script:worker.HasExited) { throw 'Le collecteur sʼest arrêté au démarrage. Vérifie la configuration Windows et le fichier status.json.' }
    Write-Host "Apprentissage démarré. Les résumés vont dans la base locale : $databasePath" -ForegroundColor Green
}

function Stop-Learning {
    if (-not $script:worker -or $script:worker.HasExited) { Write-Host 'Aucun apprentissage actif.'; return }
    Set-Content -LiteralPath $stopPath -Value 'stop' -Encoding ascii
    if (-not $script:worker.WaitForExit(5000)) { $script:worker.Kill(); $script:worker.WaitForExit() }
    Write-Host 'Apprentissage arrêté. Les mesures brutes temporaires ont été abandonnées; les résumés enregistrés restent dans la base.' -ForegroundColor Green
}

function Get-WorkerData([string]$Command) {
    Ensure-WorkerBuilt
    if (-not (Test-Path -LiteralPath $databasePath)) { return @() }
    $jsonText = & dotnet $workerDll list --db $databasePath --sid $currentSid --take 100
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($jsonText -join ''))) { return @() }
    return ,(($jsonText -join "`n") | ConvertFrom-Json)
}

function Show-Database {
    $rows = Get-WorkerData 'list'
    Clear-Host
    Write-Host 'BASE DʼAPPRENTISSAGE — 100 dernières périodes' -ForegroundColor Cyan
    Write-Host "Fichier : $databasePath"
    if (-not $rows -or $rows.Count -eq 0) { Write-Host 'La base ne contient encore aucune période.'; [void](Read-Host 'Entrée pour revenir au menu'); return }
    $table = foreach ($row in $rows) {
        [pscustomobject]@{
            Date = $row.atLocal
            Résultat = $row.result
            Profil = $row.profile
            Fenêtres = $row.acceptedWindows
            Qualité = $row.quality
            Mesures = ($row.measures | ConvertTo-Json -Compress)
        }
    }
    $table | Format-Table -AutoSize -Wrap | Out-Host
    [void](Read-Host 'Entrée pour revenir au menu')
}

function Show-Live {
    if (-not $script:worker -or $script:worker.HasExited) { Write-Host 'Démarre dʼabord lʼapprentissage avec lʼoption 1.' -ForegroundColor Yellow; [void](Read-Host 'Entrée pour revenir'); return }
    Write-Host 'Suivi en direct. Appuie sur Q pour revenir au menu; tes frappes dans cette fenêtre sont exclues du profil.' -ForegroundColor Cyan
    while ($script:worker -and -not $script:worker.HasExited) {
        if (Test-Path -LiteralPath $statusPath) {
            try { $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json } catch { Start-Sleep -Milliseconds 250; continue }
            Clear-Host
            Write-Host 'SENTINELCORE — APPRENTISSAGE EN DIRECT' -ForegroundColor Cyan
            Write-Host "Collecte active : $($status.active)    Mise à jour : $($status.updatedLocal)"
            Write-Host "Profil : $($status.State)    Progression : $([math]::Round($status.CalibrationProgressPercent)) %"
            Write-Host "Périodes acceptées : $($status.AcceptedWindows)    Jours : $($status.DistinctDays)"
            Write-Host "Activité sur la période : frappes=$($status.KeyPresses), mouvements souris=$($status.MouseMoves), clics=$($status.Clicks)"
            Write-Host "Mesures apprises dans la dernière période :"
            if ($status.latestMeasures) { $status.latestMeasures | Format-List | Out-Host } else { Write-Host 'Aucune période complète pour le moment.' }
            if ($status.note) { Write-Host "Note : $($status.note)" -ForegroundColor Yellow }
            Write-Host 'Q : retour au menu'
        }
        $until = [DateTime]::UtcNow.AddMilliseconds(1500)
        while ([DateTime]::UtcNow -lt $until) {
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                if ($key.Key -eq [ConsoleKey]::Q) { return }
            }
            Start-Sleep -Milliseconds 100
        }
    }
    Write-Host 'Le collecteur nʼest plus actif.'
    [void](Read-Host 'Entrée pour revenir au menu')
}

try {
    do {
        Clear-Host
        Write-Host 'SENTINELCORE — CONTRÔLE DU MODE APPRENTISSAGE D1' -ForegroundColor Cyan
        $active = $script:worker -and -not $script:worker.HasExited
        Write-Host "État : $(if ($active) {'ACTIF'} else {'ARRÊTÉ'})"
        Write-Host "Base : $databasePath"
        Write-Host ''
        Write-Host '1. Démarrer lʼapprentissage (confirmation manuelle)'
        Write-Host '2. Suivre en temps réel'
        Write-Host '3. Voir la base dʼapprentissage'
        Write-Host '4. Arrêter lʼapprentissage'
        Write-Host '0. Quitter'
        $choice = Read-Host 'Choix'
        switch ($choice) {
            '1' { Start-Learning; [void](Read-Host 'Entrée pour continuer') }
            '2' { Show-Live }
            '3' { Show-Database }
            '4' { Stop-Learning; [void](Read-Host 'Entrée pour continuer') }
            '0' { }
            default { Write-Host 'Choix inconnu.'; Start-Sleep -Seconds 1 }
        }
    } while ($choice -ne '0')
}
finally {
    if ($script:worker -and -not $script:worker.HasExited) {
        $answer = Read-Host 'La collecte fonctionne encore. Lʼarrêter maintenant (O/N)'
        if ($answer -in @('O','o','Oui','oui')) { Stop-Learning }
    }
}

