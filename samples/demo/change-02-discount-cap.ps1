<#
.SYNOPSIS
    Демо №2: тихая логическая ошибка внутри одного метода.

.DESCRIPTION
    Убирает потолок суммарной скидки. Ни один контракт не меняется, ни один
    вызывающий не ломается, сборка проходит. Но правила скидок складываются,
    и на заказе с высоким уровнем покупателя и большим объёмом сумма уходит в минус.

    Это находка для этапа анализа, а не для графа: ошибка целиком внутри диффа,
    и увидеть её должна модель, читающая код, а не обход связей. Хороший контраст
    к демо №1 — там наоборот.

    Ожидаемое поведение агента: критическая или предупреждающая находка про
    отсутствие ограничения скидки; проверка ссылки на MaxDiscountRate, ставшую
    неиспользуемой.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repo
)

$ErrorActionPreference = 'Stop'

# Сравнение с нормализацией переводов строк. Без неё скрипт ломается на Windows:
# git отдаёт файл с CRLF (core.autocrlf), а текст в этом скрипте хранится с LF,
# и подстрока просто не находится — при полностью исправном файле.
function Get-NormalizedContent([string]$Path) {
    return (Get-Content $Path -Raw).Replace("`r`n", "`n")
}

function Set-NormalizedContent([string]$Path, [string]$Content) {
    # Пишем с LF: файл всё равно уедет в git, а тот применит core.autocrlf сам.
    [System.IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

$file = Join-Path $Repo 'src/ShopDemo.Domain/Pricing/PriceCalculator.cs'
if (-not (Test-Path $file)) { throw "Не найден $file" }

$content = Get-NormalizedContent $file

$old = 'rate = Math.Min(rate, MaxDiscountRate);'
$new = '// Потолок снят: маркетинг просил складывать акции без ограничений.'

if ($content -notmatch [regex]::Escape($old.Replace("`r`n", "`n"))) {
    throw 'Строка с потолком скидки не найдена — правка уже внесена или файл изменён.'
}

Set-NormalizedContent $file $content.Replace($old, $new)

Write-Host 'Правка внесена: потолок суммарной скидки снят.' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Что должно сломаться:' -ForegroundColor Cyan
Write-Host '  Gold (10%) + объём 50+ (12%) = 22% — пока в порядке.'
Write-Host '  Добавление третьего правила уводит сумму в минус.'
Write-Host '  CheckoutService уже проверяет amount <= 0 — то есть заказ будет отвергнут в рантайме.'
Write-Host ''
Write-Host 'Дальше:' -ForegroundColor Cyan
Write-Host "  cd $Repo"
Write-Host "  git add -A; git commit -m 'Снять потолок скидки'; git push"
