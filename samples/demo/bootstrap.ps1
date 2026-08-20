<#
.SYNOPSIS
    Готовит демонстрационный репозиторий из ShopDemo.

.DESCRIPTION
    Копирует samples/ShopDemo в отдельный каталог, делает из него git-репозиторий
    и создаёт локальный bare-репозиторий как удалённый. Локальный «сервер» нужен
    потому, что pre-push срабатывает только на настоящем `git push`: без remote
    хук не запускается вообще, и показывать было бы нечего.

    Копия, а не сам ShopDemo: демонстрация вносит в код заведомо ломающие правки,
    и делать это в отслеживаемом каталоге решения — значит мусорить в истории агента.

.PARAMETER Target
    Куда положить рабочую копию. По умолчанию — каталог demo-workspace рядом
    с корнем ReviewAgent, чтобы он попадал под тот же REPOS_ROOT.

.EXAMPLE
    ./samples/demo/bootstrap.ps1
    ./samples/demo/bootstrap.ps1 -Target C:/Projects/demo-workspace
#>
[CmdletBinding()]
param(
    [string]$Target = ""
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '../..')
$source = Join-Path $repoRoot 'samples/ShopDemo'

if (-not $Target) {
    $Target = Join-Path (Split-Path $repoRoot -Parent) 'demo-workspace'
}

$workTree = Join-Path $Target 'shop-demo'
$origin = Join-Path $Target 'shop-demo-origin.git'

Write-Host "Источник:        $source"
Write-Host "Рабочая копия:   $workTree"
Write-Host "Удалённый repo:  $origin"
Write-Host ''

if (Test-Path $workTree) {
    $answer = Read-Host "Каталог $workTree уже есть. Пересоздать? (y/N)"
    if ($answer -notin @('y', 'Y', 'да')) {
        Write-Host 'Отменено.' -ForegroundColor Yellow
        exit 1
    }

    Remove-Item -Recurse -Force $workTree
    if (Test-Path $origin) { Remove-Item -Recurse -Force $origin }
}

New-Item -ItemType Directory -Force -Path $Target | Out-Null

# ── Рабочая копия ────────────────────────────────────────────────────────────────
Copy-Item -Recurse $source $workTree
Get-ChildItem $workTree -Recurse -Directory -Include bin, obj -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

@'
bin/
obj/
'@ | Set-Content (Join-Path $workTree '.gitignore') -Encoding utf8

# ── Локальный «сервер» ───────────────────────────────────────────────────────────
git init --bare --quiet $origin
if ($LASTEXITCODE -ne 0) { throw 'git init --bare не удался' }

Push-Location $workTree
try {
    git init --quiet --initial-branch=main
    git config user.name  'Demo Developer'
    git config user.email 'demo@example.com'
    git add -A
    git commit --quiet -m 'ShopDemo: расчёт суммы заказа, скидки, оформление и отчётность'
    git remote add origin $origin

    # Первый пуш до установки хука: базовое состояние должно оказаться «на сервере»,
    # иначе демонстрационная правка уедет вместе с ним и проверять будет нечего.
    git push --quiet -u origin main
    if ($LASTEXITCODE -ne 0) { throw 'первый git push не удался' }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Готово.' -ForegroundColor Green
Write-Host ''
Write-Host 'Дальше:' -ForegroundColor Cyan
Write-Host "  1. В .env укажите REPOS_ROOT=$($Target -replace '\\','/')"
Write-Host '  2. docker compose up -d'
Write-Host '  3. В админке подключите путь /repos/shop-demo'
Write-Host "  4. Внесите правку: ./samples/demo/change-01-breaking-signature.ps1 -Repo $workTree"
Write-Host "  5. cd $workTree; git add -A; git commit -m 'правка'; git push"
