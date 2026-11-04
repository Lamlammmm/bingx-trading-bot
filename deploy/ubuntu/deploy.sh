#!/usr/bin/env bash
set -Eeuo pipefail
umask 077

release_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
deploy_root="${DEPLOY_ROOT:-/opt/bingx-trading-bot}"
project="${COMPOSE_PROJECT_NAME:-bingx-bot}"
cd -- "$deploy_root"
test -f .env || { echo 'Create the server .env before deploying.' >&2; exit 1; }
exec 9>deploy.lock
flock -n 9 || { echo 'Another deployment is active.' >&2; exit 1; }

BOT_IMAGE="$(cat "$release_dir/image.txt")"
export BOT_IMAGE
[[ "$BOT_IMAGE" =~ ^bingx-trading-bot:[0-9a-f]{40}-[0-9]+-[0-9]+$ ]] || {
  echo 'Invalid release image tag.' >&2; exit 1;
}
compose=(docker compose --project-name "$project" --project-directory "$deploy_root"
  --env-file "$deploy_root/.env" --file "$release_dir/docker-compose.yml")
"${compose[@]}" config --quiet
read -r bot_enabled http_port < <("${compose[@]}" config --format json | python3 -c '
import json, sys
service = json.load(sys.stdin)["services"]["trading-bot"]
print(str(service["environment"]["BOT__ENABLED"]).lower(), service["ports"][0]["published"])
')

# Load the tested image before interrupting the existing container.
gzip --decompress --stdout "$release_dir/image.tar.gz" | docker load
docker image inspect "$BOT_IMAGE" >/dev/null
previous_container="$("${compose[@]}" ps --all --quiet trading-bot)"
if [[ -n "$previous_container" ]]; then
  docker inspect --format '{{.Image}}' "$previous_container" > previous-image.txt
  "${compose[@]}" stop --timeout 60 trading-bot
fi

# A stopped SQLite/state volume gives a consistent file backup. PostgreSQL needs its own DB backup.
if docker volume inspect "${project}_bot-data" >/dev/null 2>&1; then
  mkdir -p backups
  backup="$deploy_root/backups/$(date -u +%Y%m%dT%H%M%SZ)-${BOT_IMAGE#*:}.tgz"
  if ! docker run --rm --network none --user root --entrypoint tar \
    --volume "${project}_bot-data:/data:ro" "$BOT_IMAGE" -czf - -C /data . > "$backup"; then
    echo 'Backup failed; the new release was not started.' >&2
    if [[ -n "$previous_container" ]]; then docker start "$previous_container"; fi
    exit 1
  fi
fi

"${compose[@]}" up --detach --no-deps --no-build --pull never trading-bot
container="$("${compose[@]}" ps --all --quiet trading-bot)"
for attempt in {1..120}; do
  running="$(docker inspect --format '{{.State.Running}}' "$container")"
  restarts="$(docker inspect --format '{{.RestartCount}}' "$container")"
  if [[ "$running" != true || "$restarts" != 0 ]]; then break; fi
  if curl --fail --silent --max-time 3 "http://127.0.0.1:$http_port/health/live" >/dev/null; then
    if [[ "$bot_enabled" != true ]] || \
      curl --fail --silent --max-time 3 "http://127.0.0.1:$http_port/health/ready" >/dev/null; then
      printf '%s\n' "$release_dir" > current-release.txt
      printf '%s\n' "$BOT_IMAGE" > current-image.txt
      echo "Deployed $BOT_IMAGE"
      exit 0
    fi
  fi
  sleep 5
done

echo 'Deployment failed health verification. Inspect the container; current state was retained.' >&2
"${compose[@]}" logs --tail 100 trading-bot
# Do not restore a state backup automatically: the new bot may have placed exchange orders.
exit 1
