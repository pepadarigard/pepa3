# Сборка Pepa на Windows: интерфейс, тесты, готовое приложение в out\Pepa.
# Нужны: .NET 10 SDK и Node.js 22.  Запуск:  powershell -ExecutionPolicy Bypass -File build\build.ps1
# Файл сохранён в UTF-8 с BOM: без него Windows PowerShell 5.1 читает русские строки в кодировке системы и не может разобрать сценарий.
param(
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Windows PowerShell не прерывает сценарий, когда внешняя команда (npm, dotnet) завершается с ошибкой, — проверяем код сами,
# чтобы после упавших тестов или сборки не появилась «готовая» программа.
function Invoke-Checked([string]$Name, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name — ошибка (код $LASTEXITCODE). Сборка остановлена." }
}

# Прежние результаты убираем заранее: при ошибке не должны остаться старый интерфейс или старое приложение.
foreach ($dir in @("out/Pepa", "src/Pepa.Host/wwwroot")) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}

Write-Host "== Интерфейс ==" -ForegroundColor Cyan
Push-Location ui
try {
    Invoke-Checked "npm ci" { npm ci }
    if (-not $SkipTests) { Invoke-Checked "npm test" { npm test } }
    Invoke-Checked "npm run build" { npm run build }
}
finally {
    Pop-Location
}

if (-not $SkipTests) {
    Write-Host "== Тесты ==" -ForegroundColor Cyan
    Invoke-Checked "dotnet test" { dotnet test Pepa.slnx -c Release }
}

Write-Host "== Публикация ==" -ForegroundColor Cyan
Invoke-Checked "dotnet publish" {
    dotnet publish src/Pepa.Desktop/Pepa.Desktop.csproj -c Release -r win-x64 --self-contained true -o out/Pepa -p:DebugType=None -p:GenerateDocumentationFile=false
}

Write-Host "Готово: out\Pepa\Pepa.exe" -ForegroundColor Green
