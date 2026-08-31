<#
.SYNOPSIS
    Демо №1: сломанный контракт, которого не видно в диффе.

.DESCRIPTION
    Меняет сигнатуру PriceCalculator.CalculateTotal — добавляет обязательный параметр.
    Дифф затрагивает один файл и выглядит безобидно: метод стал «гибче».

    Проверить это по диффу нельзя: четыре вызывающих места лежат в других файлах
    и в дифф не попадают. Найти их можно только по связям — этим и занимается
    состояние impact, спрашивая граф кода «кто вызывает CalculateTotal».

    Ожидаемое поведение агента: критическая находка о сломанных вызывающих,
    пуш остановлен.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repo
)

$ErrorActionPreference = 'Stop'

# Чтение и запись через .NET, а не Get-Content/Set-Content. Причин две, и обе про
# Windows PowerShell 5.1, где умолчания отличаются от pwsh 7:
#
#   кодировка — Get-Content без -Encoding читает файл как ANSI, и кириллица в исходниках
#               превращается в мусор; сравнение с текстом скрипта не находит совпадения
#               при полностью исправном файле;
#   переводы строк — git отдаёт файл с CRLF (core.autocrlf), а текст в этом скрипте
#               хранится с LF, поэтому обе стороны сравнения приводятся к LF.
function Get-NormalizedContent([string]$Path) {
    $utf8 = [System.Text.UTF8Encoding]::new($false)
    return [System.IO.File]::ReadAllText($Path, $utf8).Replace("`r`n", "`n")
}

function Set-NormalizedContent([string]$Path, [string]$Content) {
    # Пишем с LF: файл всё равно уедет в git, а тот применит core.autocrlf сам.
    [System.IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

$file = Join-Path $Repo 'src/ShopDemo.Domain/Pricing/PriceCalculator.cs'
if (-not (Test-Path $file)) { throw "Не найден $file" }

$content = Get-NormalizedContent $file

$old = @'
    /// <summary>Только сумма к оплате — короткий путь для тех, кому разбивка не нужна.</summary>
    public decimal CalculateTotal(Order order) => Calculate(order).Total;
'@

$new = @'
    /// <summary>Сумма к оплате с доставкой.</summary>
    public decimal CalculateTotal(Order order, decimal shippingCost) =>
        Calculate(order).Total + shippingCost;
'@

if ($content -notmatch [regex]::Escape(($old.Trim().Replace("`r`n", "`n")))) {
    throw 'Исходный текст CalculateTotal не найден — правка уже внесена или файл изменён.'
}

Set-NormalizedContent $file $content.Replace(($old.Trim().Replace("`r`n", "`n")), $new.Trim())

Write-Host 'Правка внесена: CalculateTotal(Order) -> CalculateTotal(Order, decimal).' -ForegroundColor Yellow
Write-Host ''
Write-Host 'Вызывающие места, которых нет в диффе:' -ForegroundColor Cyan
Write-Host '  CheckoutService.CheckoutAsync      (Checkout/CheckoutService.cs)'
Write-Host '  InvoiceService.AmountForPayment    (Billing/InvoiceService.cs)'
Write-Host '  SalesReportBuilder.BuildAsync      (Reporting/SalesReportBuilder.cs)'
Write-Host ''
Write-Host 'Дальше:' -ForegroundColor Cyan
Write-Host "  cd $Repo"
Write-Host "  git add -A; git commit -m 'Доставка в расчёте суммы'; git push"
