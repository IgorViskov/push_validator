# AGENTS.md

Инструкции для AI-агентов и контрибьюторов, работающих с этим репозиторием.

## Что это

CLI-агент, который **проверяет изменения перед `git push`**: анализирует дифф с помощью
нескольких LLM и либо разрешает пуш (`exit 0`), либо приостанавливает его (`exit 1`).
Задуман как pre-push git-hook.

Две ключевые идеи:
- **ReAct** — на этапе анализа модель сама вызывает инструменты (`read_file`, `git_log`,
  `search_files`), чтобы дочитать контекст вне диффа. Цикл Reason→Act→Observe крутит
  `FunctionInvokingChatClient` из Microsoft.Extensions.AI.
- **Когнитивный роутер** — из заданных в конфиге моделей выбирает подходящую под задачу:
  по роли (Orchestration / Execution / Validation) и, для анализа, по соотношению
  «сложность изменений ↔ стоимость/ум модели» (`CostEfficiency`).

## Стек

- .NET 10 (`net10.0`), C# с `Nullable` и `ImplicitUsings`.
- Microsoft.Extensions.AI (+ `.OpenAI`) поверh OpenAI SDK — общается с любым
  OpenAI-совместимым endpoint (OpenRouter, LM Studio, корпоративные шлюзы).
- Microsoft.Extensions.{DependencyInjection, Configuration, Logging}.
- Один проект, без сторонних библиотек для git — обёртка над процессом `git`.

## Команды

```bash
# Сборка (из корня; решение LLMAgent.sln подхватывается автоматически)
dotnet build

# Запуск проверки конкретного репозитория
dotnet run --project LLMAgent/LLMAgent.csproj -- "C:\path\to\target\repo"
# Без аргумента берётся текущий каталог.

# Коды выхода: 0 — пуш разрешён; 1 — пуш приостановлен либо ошибка.
```

**Автотестов в проекте нет** (единственный `.csproj` — сам агент). «Тест работоспособности» =
запуск бинаря против репозитория, в котором есть незапушенные изменения, и проверка вывода/кода
выхода. Нужен хотя бы один доступный LLM-endpoint (см. ниже).

## Конфигурация

Источник — `LLMAgent/appsettings.json`, секция `Apis` (массив API, у каждого — список моделей):

```jsonc
{
  "Apis": [{
    "Name": "OpenRouter",
    "ApiKey": "",                 // пустой ключ → ApiKeyCredential бросит ArgumentException
    "Endpoint": "https://openrouter.ai/api/v1",
    "Models": [{
      "Name": "openai/gpt-oss-120b:free",
      "Role": "Orchestration",    // Orchestration | Execution | Validation
      "Priority": 1,              // для Orchestration/Validation берётся модель с макс. Priority
      "CostEfficiency": 4         // для Execution: чем выше — тем «умнее и дороже»
    }]
  }]
}
```

- **Ключи через переменные окружения**: `Apis__0__ApiKey`, `Apis__1__Endpoint`, … перекрывают
  JSON (`AddEnvironmentVariables`). Локально это делает `LLMAgent/Properties/launchSettings.json`
  (профиль `Local`).
- **Секреты не коммитить.** `launchSettings.json` в `.gitignore`. Реальные ключи держать только
  в env / секрет-хранилище, не в `appsettings.json`.

## Архитектура

Поток запроса (`Agent.Run`) — пайплайн middleware, собираемый `AgentEngine`:

```
Diff (GitService)
  └─> Orchestration ─> Execution ─> Validation ─> Report
        оценка          ReAct +       текстовые      вывод + решение
        сложности       инструменты   проверки       (блокировка пуша)
        (1..10)         + structured  + structured
```

| Компонент | Файл | Роль |
|-----------|------|------|
| Точка входа, DI | `LLMAgent/Program.cs` | читает конфиг, регистрирует сервисы, ловит коды выхода |
| Оркестратор пайплайна | `Modules/Agent/Agent.cs` | проверки репозитория + `Use<…>().Run()` |
| Движок пайплайна | `Modules/Agent/AgentEngine.cs` | строит цепочку middleware |
| Шаги | `Modules/Agent/Middelwares/*Step.cs` | Orchestration/Execution/Validation/Report |
| Регистрация шагов | `Modules/Agent/Middelwares/MiddlewareInstaller.cs` | фабрики `Func<AgentEngineDelegate, TStep>` |
| Когнитивный роутер | `Modules/Router/CognitiveRouter.cs` | выбор модели по роли/сложности |
| Чат-обёртка | `Modules/Chats/Chat.cs` | `IChatClient` + function-calling + structured output |
| Git | `Modules/Git/GitService.cs` | дифф/лог/проверка репозитория через процесс `git` |
| Инструменты | `Modules/Tools/RepoToolFactory.cs` | `read_file`, `git_log`, `search_files` |
| Промпты | `Prompts/Prompt.cs` | все системные и пользовательские сообщения |
| Модели данных | `Models/*.cs` | `ApiSettings`, `ModelSetting`, `Finding`, `*Result` |

Контекст между шагами передаётся через мутируемый `LlmContext` (дифф → сложность → модель →
находки → решение `AllowPush`).

## Контракт поведения

- **Выбор диффа** (`GitService.GetLatestChanges`): сначала незапушенные коммиты (`@{u}..HEAD`),
  иначе последний коммит (`HEAD~1..HEAD`), иначе рабочее дерево (`diff HEAD`). Пусто → `exit 0`.
- **Гейт fail-closed**: если обращение к модели упало или ответ не распарсился, шаг добавляет
  **критическую находку** (не «тихо разрешает» пуш). Любая критическая находка приостанавливает
  пуш до явного `y/yes/да` от пользователя в `ReportStep`.
- **Severity**: `Critical` блокирует пуш; `Warning`/`Info` — информативны.

## Соглашения

- **Язык — русский**: комментарии, XML-док, промпты, логи, сообщения пользователю. Держим единообразие.
- Ошибки — через `Modules/ErrorsModule/Errors.cs` и типизированные исключения
  (`ExitException`, `NoChatException`).
- Структурный вывод и инструменты намеренно **разнесены по разным запросам** (function-calling +
  принудительная JSON-схема конфликтуют на многих моделях) — см. `Chat.GetAnswer` vs `GetAnswer<T>`.
- Опечатка в имени каталога/неймспейса `Middelwares` сохраняется во всех `using` — при добавлении
  файлов держите ту же форму, чтобы не плодить разнобой (или переименуйте целиком отдельным PR).

## Как расширять

**Добавить шаг пайплайна** (частый источник ошибок — забыть один из трёх пунктов):
1. Класс `…Step : IAgentMiddleware` с конструктором, первым параметром которого идёт
   `AgentEngineDelegate next`.
2. Зарегистрировать фабрику в `MiddlewareInstaller.AddMiddlewares` (там же, через `Register<T>()`).
3. Вставить `.Use<…Step>()` в нужное место цепочки в `Agent.Run`.

**Добавить инструмент ReAct**: новый `AIFunctionFactory.Create(...)` в `RepoToolFactory.Build`;
схема выводится из сигнатуры C#-метода, ручной JSON-Schema не нужен. Доступ за пределы репозитория
обязан спрашивать `IUserPermission`.

**Добавить модель/провайдера**: запись в `Apis` (`appsettings.json`) с нужными `Role`/`Priority`/
`CostEfficiency`; ключ — через env.

## Известные ограничения (на момент написания)

- **Validation требует своей модели.** Если для роли `Validation` не задано ни одной модели —
  `CognitiveRouter` бросит `NoChatException` (пуш будет заблокирован через общий catch). Удобной
  деградации «нет роли — пропустить шаг» пока нет.
- **Нет фолбэка между моделями одной роли** при недоступности endpoint (выбирается одна модель).
- В примере конфигурации `Validation` указывает на **локальную LM Studio** (`localhost:1234`) —
  без запущенного локального сервера этот шаг даёт критическую находку (это корректный fail-closed,
  но «зелёный» прогон требует поднятой LM Studio либо validation-модели у облачного провайдера).
- `AgentEngine` зарегистрирован как singleton с мутабельным состоянием — рассчитан на один прогон
  за процесс.
