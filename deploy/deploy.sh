#!/usr/bin/env bash
# Сборка образа на этой машине и запуск на сервере.
#
#   deploy/deploy.sh [контекст docker]      по умолчанию — manager
#
# Образ собирается локально (здесь есть кэш и доступ к пакетам), передаётся на
# сервер по ssh и запускается там через docker compose. Секреты — в deploy/.env.
#
# Переменные:
#   BUILD_PROXY — прокси для загрузки пакетов при сборке, если NuGet недоступен напрямую.
set -euo pipefail

cd "$(dirname "$0")/.."
CONTEXT=${1:-manager}
ENV_FILE=deploy/.env
COMPOSE="docker --context $CONTEXT compose -f deploy/docker-compose.prod.yml --env-file $ENV_FILE"

[ -f "$ENV_FILE" ] || { echo "Нет $ENV_FILE — скопируйте deploy/.env.example и заполните." >&2; exit 1; }

# Метка образа — короткий хеш коммита: по ней видно, что именно запущено,
# и к предыдущей версии можно вернуться, указав её в SMENA_IMAGE.
TAG=$(git rev-parse --short HEAD)$(git diff --quiet HEAD -- . ':!deploy' || echo -dirty)
IMAGE=smenav2:$TAG

echo "=== сборка $IMAGE"
BUILD_ARGS=()
if [ -n "${BUILD_PROXY:-}" ]; then
  BUILD_ARGS+=(--build-arg "HTTPS_PROXY=$BUILD_PROXY" --build-arg "HTTP_PROXY=$BUILD_PROXY")
fi
DOCKER_HOST=unix:///var/run/docker.sock docker build "${BUILD_ARGS[@]}" -t "$IMAGE" -t smenav2:latest .

echo "=== передача образа на $CONTEXT"
DOCKER_HOST=unix:///var/run/docker.sock docker save "$IMAGE" | gzip | docker --context "$CONTEXT" load

echo "=== запуск"
SMENA_IMAGE=$IMAGE $COMPOSE up -d --remove-orphans --wait --wait-timeout 120
SMENA_IMAGE=$IMAGE $COMPOSE ps --format 'table {{.Name}}\t{{.Image}}\t{{.Status}}'
