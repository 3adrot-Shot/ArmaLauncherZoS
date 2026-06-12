# publish-and-deploy.ps1
# Публикация и деплой на сервер 176.124.222.151

param(
    [string]$ServerIP = "176.124.222.151",
    [string]$ServerUser = "root",
    [string]$ServerPath = "/opt/launcher-server"
)

$ErrorActionPreference = "Stop"

Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host "?     AI Model Launcher - Build & Deploy Script               ?" -ForegroundColor Cyan
Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host ""

$SolutionDir = Split-Path -Parent $PSScriptRoot
$ServerProject = Join-Path $SolutionDir "LauncherServer"
$ClientProject = Join-Path $SolutionDir "ArmaLauncherClient"
$PublishDir = Join-Path $SolutionDir "publish"

# 1. Очистка
Write-Host "[1/5] Очистка предыдущей сборки..." -ForegroundColor Yellow
if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
}
New-Item -ItemType Directory -Path "$PublishDir\server" | Out-Null
New-Item -ItemType Directory -Path "$PublishDir\client" | Out-Null

# 2. Публикация сервера
Write-Host "[2/5] Публикация LauncherServer..." -ForegroundColor Yellow
dotnet publish $ServerProject `
    -c Release `
    -o "$PublishDir\server" `
    --self-contained false `
    -r linux-x64

# 3. Публикация клиента
Write-Host "[3/5] Публикация ArmaLauncherClient..." -ForegroundColor Yellow
dotnet publish $ClientProject `
    -c Release `
    -o "$PublishDir\client" `
    --self-contained true `
    -r win-x64 `
    -p:PublishSingleFile=true

# 4. Копирование публичного ключа в клиент (если существует)
$ServerKeysDir = Join-Path $ServerProject "keys"
$PublicKeyPath = Join-Path $ServerKeysDir "signing.pub"
if (Test-Path $PublicKeyPath) {
    Write-Host "[4/5] Копирование публичного ключа..." -ForegroundColor Yellow
    Copy-Item $PublicKeyPath "$PublishDir\client\"
} else {
    Write-Host "[4/5] Публичный ключ не найден (будет создан при первом запуске сервера)" -ForegroundColor DarkYellow
}

# 5. Деплой на сервер (требует SSH ключ или пароль)
Write-Host "[5/5] Деплой на сервер $ServerIP..." -ForegroundColor Yellow
Write-Host ""
Write-Host "Для деплоя выполните:" -ForegroundColor White
Write-Host ""
Write-Host "  scp -r $PublishDir\server\* ${ServerUser}@${ServerIP}:${ServerPath}/" -ForegroundColor Green
Write-Host ""
Write-Host "Или через rsync:" -ForegroundColor White
Write-Host ""
Write-Host "  rsync -avz --progress $PublishDir/server/ ${ServerUser}@${ServerIP}:${ServerPath}/" -ForegroundColor Green
Write-Host ""

Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host "?                    Сборка завершена!                         ?" -ForegroundColor Cyan
Write-Host "????????????????????????????????????????????????????????????????" -ForegroundColor Cyan
Write-Host ""
Write-Host "Сервер:  $PublishDir\server" -ForegroundColor White
Write-Host "Клиент:  $PublishDir\client" -ForegroundColor White
Write-Host ""
Write-Host "После деплоя на сервер выполните:" -ForegroundColor Yellow
Write-Host "  ssh ${ServerUser}@${ServerIP}" -ForegroundColor Green
Write-Host "  systemctl restart launcher-server" -ForegroundColor Green
Write-Host "  curl http://${ServerIP}:5000/health" -ForegroundColor Green
Write-Host ""
