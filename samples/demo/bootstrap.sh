#!/bin/sh
# Готовит демонстрационный репозиторий из samples/ShopDemo.
#
# Копирует решение в отдельный каталог, делает из него git-репозиторий и создаёт локальный
# bare-репозиторий как удалённый. Локальный «сервер» нужен потому, что pre-push срабатывает
# только на настоящем `git push`: без remote хук не запускается вообще.
#
# Копия, а не сам ShopDemo: демонстрация вносит заведомо ломающие правки, и делать это
# в отслеживаемом каталоге решения — значит мусорить в истории агента.
#
#   ./bootstrap.sh [каталог]
#
# По умолчанию — demo-workspace рядом с корнем ReviewAgent, чтобы каталог попадал
# под тот же REPOS_ROOT.
set -eu

here=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
root=$(CDPATH= cd -- "$here/../.." && pwd)
source="$root/samples/ShopDemo"

target=${1:-"$(dirname -- "$root")/demo-workspace"}
worktree="$target/shop-demo"
origin="$target/shop-demo-origin.git"

echo "Источник:        $source"
echo "Рабочая копия:   $worktree"
echo "Удалённый repo:  $origin"
echo

[ -d "$source" ] || { echo "Не найден $source" >&2; exit 1; }

if [ -e "$worktree" ]; then
    printf 'Каталог %s уже есть. Пересоздать? (y/N): ' "$worktree"
    read -r answer
    case "$answer" in
        y|Y|да) ;;
        *) echo 'Отменено.'; exit 1 ;;
    esac
    rm -rf "$worktree" "$origin"
fi

mkdir -p "$target"
cp -r "$source" "$worktree"
find "$worktree" -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} + 2>/dev/null || true

printf 'bin/\nobj/\n' > "$worktree/.gitignore"

git init --bare --quiet "$origin"

git -C "$worktree" init --quiet --initial-branch=main
git -C "$worktree" config user.name  'Demo Developer'
git -C "$worktree" config user.email 'demo@example.com'
git -C "$worktree" add -A
git -C "$worktree" commit --quiet -m 'ShopDemo: расчёт суммы заказа, скидки, оформление и отчётность'
git -C "$worktree" remote add origin "$origin"

# Первый пуш до установки хука: базовое состояние должно оказаться «на сервере»,
# иначе демонстрационная правка уедет вместе с ним и проверять будет нечего.
git -C "$worktree" push --quiet -u origin main

echo
echo 'Готово.'
echo
echo 'Дальше:'
echo "  1. В .env укажите REPOS_ROOT=$target"
echo '  2. docker compose up -d'
echo '  3. В админке подключите путь /repos/shop-demo'
echo "  4. Внесите правку: $here/change.sh 01 $worktree"
echo "  5. cd $worktree && git add -A && git commit -m 'правка' && git push"
