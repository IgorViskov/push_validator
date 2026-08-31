namespace ReviewAgent.Core.Git;

/// <summary>
/// Текст хука <c>pre-push</c>.
///
/// Скрипт на POSIX sh, а не на PowerShell или C#: git запускает хуки через sh на всех
/// платформах, включая Windows (внутри Git for Windows идёт своя sh). Единственный
/// внешний инструмент — curl, он тоже входит в поставку git.
///
/// Дифф уезжает сырым телом запроса, а не полем JSON. Экранировать произвольный текст
/// под JSON средствами sh — это ручной разбор кавычек, переводов строк и обратных слешей;
/// на первом же диффе с кавычкой в строковом литерале такая обёртка ломается. Поэтому
/// метаданные идут заголовками, а тело остаётся тем, чем является, — текстом.
/// </summary>
public static class HookScript
{
    /// <summary>Маркер во второй строке файла: по нему установщик узнаёт свой хук в чужом.</summary>
    public const string Marker = "# review-agent-pre-push";

    // Интерполяция двойными скобками: в тексте на sh одинарные фигурные скобки встречаются
    // на каждом шагу ($1, ${var}, тела функций), и удваивать их все — верный способ
    // получить сломанный скрипт из-за пропущенной пары.
    public static string Build(HookOptions options, string repoKey, string token) =>
        $$"""
        #!/bin/sh
        {{Marker}}
        # Сгенерирован ReviewAgent. Правки будут потеряны при переустановке хука из админки:
        # {{Url(options)}}

        AGENT_URL="{{Url(options)}}"
        REPO_KEY="{{repoKey}}"
        REPO_TOKEN="{{token}}"
        AUTOSTART="{{options.AutostartCommand.Trim()}}"
        STARTUP_WAIT={{Math.Max(0, options.StartupWaitSeconds)}}
        REVIEW_TIMEOUT={{Math.Max(30, options.ReviewTimeoutSeconds)}}
        BLOCK_WHEN_DOWN={{Flag(options.OnUnavailable.Equals("block", StringComparison.OrdinalIgnoreCase))}}
        ALLOW_FORCE={{Flag(options.AllowForceOverride)}}

        say() { printf '%s\n' "$1" >&2; }

        if [ "$ALLOW_FORCE" = "1" ] && [ -n "$REVIEW_AGENT_FORCE" ]; then
          say "ReviewAgent: проверка пропущена (REVIEW_AGENT_FORCE)."
          exit 0
        fi

        if ! command -v curl >/dev/null 2>&1; then
          say "ReviewAgent: в PATH нет curl — проверить изменения нечем."
          [ "$BLOCK_WHEN_DOWN" = "1" ] && exit 1
          exit 0
        fi

        alive() { curl -fsS --max-time 5 "$AGENT_URL/api/health" >/dev/null 2>&1; }

        # ── Живость агента и попытка поднять его ─────────────────────────────────────
        if ! alive && [ -n "$AUTOSTART" ]; then
          say "ReviewAgent: агент не отвечает, запускаю ($AUTOSTART)…"
          $AUTOSTART >/dev/null 2>&1

          waited=0
          while [ "$waited" -lt "$STARTUP_WAIT" ]; do
            alive && break
            sleep 2
            waited=$((waited + 2))
          done
        fi

        if ! alive; then
          say "ReviewAgent: агент недоступен на $AGENT_URL."
          if [ "$BLOCK_WHEN_DOWN" = "1" ]; then
            say "Пуш остановлен: в настройках агента OnUnavailable=block."
            exit 1
          fi
          say "Пуш пропущен без проверки."
          exit 0
        fi

        # ── Дифф проверяемого диапазона ──────────────────────────────────────────────
        # pre-push получает на stdin строки «<local ref> <local sha> <remote ref> <remote sha>».
        # Нулевой идентификатор вычисляем, а не пишем константой: у репозитория может быть
        # sha256, и там он длиной 64 символа.
        zero=$(git hash-object --stdin </dev/null | tr '0-9a-f' '0')
        empty_tree=$(git hash-object -t tree /dev/null)
        diff_file=$(mktemp)
        branch=""
        local_sha=""

        while read -r local_ref local_oid remote_ref remote_oid; do
          [ -z "$local_ref" ] && continue
          # Удаление ветки на сервере: проверять нечего.
          [ "$local_oid" = "$zero" ] && continue

          branch="${local_ref#refs/heads/}"
          local_sha="$local_oid"

          if [ "$remote_oid" = "$zero" ]; then
            # Ветки на сервере ещё нет: берём коммиты, которых нет ни в одной удалённой.
            base=$(git rev-list "$local_oid" --not --remotes 2>/dev/null | tail -n 1)
            if [ -n "$base" ] && git rev-parse --verify --quiet "$base^" >/dev/null 2>&1; then
              git diff --no-color "$base^" "$local_oid" >>"$diff_file" 2>/dev/null
            else
              # Корневой коммит: сравниваем с пустым деревом.
              git diff --no-color "$empty_tree" "$local_oid" >>"$diff_file" 2>/dev/null
            fi
          else
            git diff --no-color "$remote_oid" "$local_oid" >>"$diff_file" 2>/dev/null
          fi
        done

        if [ ! -s "$diff_file" ]; then
          rm -f "$diff_file"
          say "ReviewAgent: изменений для анализа нет — пуш разрешён."
          exit 0
        fi

        say "ReviewAgent: отправляю $(wc -l <"$diff_file" | tr -d ' ') строк диффа на проверку…"

        # ── Проверка ─────────────────────────────────────────────────────────────────
        report_file=$(mktemp)
        http_code=$(curl -sS --max-time "$REVIEW_TIMEOUT" -o "$report_file" -w '%{http_code}' \
          -X POST "$AGENT_URL/api/review" \
          -H "Content-Type: text/plain; charset=utf-8" \
          -H "X-Review-Repo: $REPO_KEY" \
          -H "X-Review-Token: $REPO_TOKEN" \
          -H "X-Review-Branch: $branch" \
          -H "X-Review-Sha: $local_sha" \
          --data-binary "@$diff_file" 2>/dev/null)

        rm -f "$diff_file"

        if [ "$http_code" != "200" ]; then
          say "ReviewAgent: агент ответил HTTP $http_code."
          cat "$report_file" >&2
          rm -f "$report_file"
          if [ "$BLOCK_WHEN_DOWN" = "1" ]; then exit 1; fi
          say "Пуш пропущен без проверки."
          exit 0
        fi

        cat "$report_file" >&2
        decision=$(grep -a '^REVIEW_DECISION=' "$report_file" | tail -n 1 | cut -d= -f2)
        rm -f "$report_file"

        if [ "$decision" = "blocked" ]; then
          say ""
          say "Пуш остановлен агентом. Разберите находки и повторите."
          if [ "$ALLOW_FORCE" = "1" ]; then
            say "Продавить осознанно: REVIEW_AGENT_FORCE=1 git push"
          fi
          exit 1
        fi

        exit 0

        """;

    private static string Url(HookOptions options) => options.PublicUrl.TrimEnd('/');

    private static string Flag(bool value) => value ? "1" : "0";
}
