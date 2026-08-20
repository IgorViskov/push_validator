<#
.SYNOPSIS
    Справка по demo.cmd.

.DESCRIPTION
    Вынесена в отдельный скрипт, потому что консоль cmd работает в OEM-кодировке
    и кириллицу из .cmd отрисовать не может, а PowerShell может.
#>

Write-Host 'Демонстрационные сценарии ReviewAgent' -ForegroundColor Cyan
Write-Host ''
Write-Host '  demo run [каталог]           весь рабочий путь целиком: скрипт делает всё сам,' -ForegroundColor White
Write-Host '                               от вас — Enter между шагами' -ForegroundColor White
Write-Host ''
Write-Host '  demo bootstrap [каталог]     подготовить демонстрационный репозиторий'
Write-Host '                               по умолчанию — demo-workspace рядом с корнем'
Write-Host ''
Write-Host '  demo 01 <репозиторий>        сломанный публичный контракт:'
Write-Host '                               правка на три строки ломает четырёх вызывающих'
Write-Host '  demo 02 <репозиторий>        снятый потолок скидки:'
Write-Host '                               тихая логическая ошибка внутри диффа'
Write-Host '  demo 03 <репозиторий>        обращение к анализирующей модели в комментарии:'
Write-Host '                               проверка границы доверия'
Write-Host ''
Write-Host '  demo reset <репозиторий>     откатить к состоянию последнего пуша'
Write-Host ''
Write-Host 'Проще всего:' -ForegroundColor Cyan
Write-Host '  demo run'
Write-Host ''
Write-Host 'По шагам вручную:' -ForegroundColor Cyan
Write-Host '  demo bootstrap C:/Projects/demo-workspace'
Write-Host '  demo 01 C:/Projects/demo-workspace/shop-demo'
Write-Host '  cd C:/Projects/demo-workspace/shop-demo'
Write-Host '  git add -A; git commit -m ''правка''; git push'
