# syntax=docker/dockerfile:1

# ─────────────────────────────────────────────────────────────────────────────────────
# Сборка
# ─────────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Сначала только файлы проектов: слой с restore переиспользуется, пока не менялись
# зависимости. Копировать сразу всё — значит гонять restore на каждую правку кода.
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ReviewAgent.CodeGraph/ReviewAgent.CodeGraph.csproj src/ReviewAgent.CodeGraph/
COPY src/ReviewAgent.Core/ReviewAgent.Core.csproj       src/ReviewAgent.Core/
COPY src/ReviewAgent.Data/ReviewAgent.Data.csproj       src/ReviewAgent.Data/
COPY src/ReviewAgent.Web/ReviewAgent.Web.csproj         src/ReviewAgent.Web/
RUN dotnet restore src/ReviewAgent.Web/ReviewAgent.Web.csproj

COPY src/ ./src/

# Без --no-restore, хотя restore выше уже отработал. Это не забывчивость: с ключом
# --no-restore publish не раскладывает статические ресурсы Blazor из общей платформы —
# в wwwroot не появляется _framework/blazor.web.js. Страница при этом рендерится и
# выглядит целой, но интерактивной не становится: кнопки не реагируют, а в консоли
# браузера единственная строка про 404. Слой restore выше всё равно полезен — он
# прогревает кеш пакетов, и повторный restore внутри publish идёт без сети.
RUN dotnet publish src/ReviewAgent.Web/ReviewAgent.Web.csproj \
    -c Release -o /app/publish /p:UseAppHost=false

# ─────────────────────────────────────────────────────────────────────────────────────
# Рантайм
#
# Базовый образ — SDK, а не aspnet. Это осознанно и это самое дорогое решение в файле.
# Агент обязан «сам поддерживать графовую БД в актуальном состоянии», то есть разбирать
# C# по семантической модели Roslyn. MSBuildWorkspace открывает решение силами настоящего
# MSBuild и требует установленный SDK: без него решение открывается без ссылок, символы
# не разрешаются, и половина рёбер CALLS просто не появляется — молча, без ошибки.
# Разница в размере (примерно 800 МБ против 220) — цена работающего графа вызовов.
# ─────────────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime

# git нужен рантайму: им читаются дифф, история коммитов и путь к каталогу хуков.
RUN apt-get update \
 && apt-get install -y --no-install-recommends git curl \
 && rm -rf /var/lib/apt/lists/*

# Репозитории монтируются снаружи и принадлежат хостовому пользователю. Агент пишет
# в них только файл хука, но git отказывается работать в чужом каталоге без этой отметки.
RUN git config --system --add safe.directory '*'

WORKDIR /app
COPY --from=build /app/publish ./

# Каталог реестра. Том монтируется сюда же — этим и обеспечена персистентность:
# список обслуживаемых репозиториев переживает пересоздание контейнера.
RUN mkdir -p /app/data

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    Storage__DatabasePath=/app/data/review-agent.db

EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=40s --retries=5 \
    CMD curl -fsS http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "ReviewAgent.Web.dll"]
