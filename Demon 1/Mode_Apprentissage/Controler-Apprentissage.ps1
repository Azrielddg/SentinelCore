# Lance uniquement l’interface de contrôle. L’apprentissage reste arrêté jusqu’à
# une authentification Windows Hello ou au mot de passe maître dans l’application.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'artifacts\win-x64\SentinelCore.Demon1.Learning.Controller.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "L’application n’est pas encore publiée. Exécute d’abord .\Demon 1\Mode_Apprentissage\build-release.ps1 depuis la racine du dépôt."
}
Start-Process -FilePath $exe
