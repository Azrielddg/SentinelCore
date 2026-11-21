$ErrorActionPreference = 'Stop'
$moduleRoot = $PSScriptRoot
$project = Join-Path $moduleRoot 'src\SentinelCore.Demon1.Learning.Controller\SentinelCore.Demon1.Learning.Controller.csproj'
$output = Join-Path $moduleRoot 'artifacts\win-x64'
dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    --output $output --nologo
if ($LASTEXITCODE -ne 0) { throw 'La génération de SentinelCore Learning a échoué.' }
Write-Host "Application prête : $(Join-Path $output 'SentinelCore.Demon1.Learning.Controller.exe')" -ForegroundColor Green
