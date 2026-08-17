<#
.SYNOPSIS
    Возвращает демонстрационный репозиторий к состоянию последнего пуша.

.DESCRIPTION
    Между сценариями показа правки надо откатывать, иначе второй сценарий уезжает
    вместе с первым и в диффе оказывается смесь из двух проблем.

.EXAMPLE
    ./samples/demo/reset.ps1 -Repo C:/Projects/demo-workspace/shop-demo
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repo
)

$ErrorActionPreference = 'Stop'

Push-Location $Repo
try {
    git reset --hard origin/main
    git clean -fd
    Write-Host 'Репозиторий возвращён к состоянию origin/main.' -ForegroundColor Green
    git log --oneline -1
}
finally {
    Pop-Location
}
