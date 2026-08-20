#!/bin/sh
# Возвращает демонстрационный репозиторий к состоянию последнего пуша.
#
#   ./reset.sh <путь к репозиторию>
set -eu

[ $# -eq 1 ] || { echo "Использование: ./reset.sh <путь к репозиторию>" >&2; exit 2; }

repo=$1
[ -d "$repo/.git" ] || { echo "Не похоже на git-репозиторий: $repo" >&2; exit 1; }

git -C "$repo" reset --hard origin/main
git -C "$repo" clean -fd

echo "Репозиторий возвращён к состоянию origin/main."
git -C "$repo" log --oneline -1
