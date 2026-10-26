# Сборка Pepa на Windows: интерфейс, тесты, готовое приложение в out\Pepa.
# Нужны: .NET 10 SDK и Node.js 22.  Запуск:  powershell -ExecutionPolicy Bypass -File build\build.ps1
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== Интерфейс ==" -ForegroundColor Cyan
Push-Location ui
npm ci
if (-not $SkipTests) { npm test }
npm run build
Pop-Location

if (-not $SkipTests) {
    Write-Host "== Тесты ==" -ForegroundColor Cyan
    dotnet test Pepa.slnx -c Release
}

Write-Host "== Публикация ==" -ForegroundColor Cyan
dotnet publish src/Pepa.Desktop/Pepa.Desktop.csproj -c Release -r win-x64 --self-contained true -o out/Pepa -p:DebugType=None

Write-Host "Готово: out\Pepa\Pepa.exe" -ForegroundColor Green
