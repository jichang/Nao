#!/usr/bin/env bash
set -euo pipefail

revision="${GITHUB_SHA:-local}"
agents_image="nao-agents-worker:deployment-smoke"
documents_image="nao-documents-worker:deployment-smoke"

cleanup() {
  docker image rm --force "$agents_image" "$documents_image" >/dev/null 2>&1 || true
}

trap cleanup EXIT

docker build \
  --file deploy/images/agents-worker.Dockerfile \
  --build-arg "SOURCE_REVISION=$revision" \
  --tag "$agents_image" \
  .

docker build \
  --file deploy/images/documents-worker.Dockerfile \
  --build-arg "SOURCE_REVISION=$revision" \
  --tag "$documents_image" \
  .

for image in "$agents_image" "$documents_image"; do
  test "$(docker image inspect "$image" --format '{{.Config.User}}')" = "65532:65532"
  test "$(docker image inspect "$image" --format '{{json .Config.Entrypoint}}')" != "null"

  set +e
  output="$(
    printf '' |
      docker run --rm \
        --read-only \
        --cap-drop=ALL \
        --security-opt=no-new-privileges \
        --network=none \
        --memory=134217728 \
        --pids-limit=32 \
        "$image" 2>&1
  )"
  status=$?
  set -e

  test "$status" -eq 1
  test -n "$output"
done

printf 'Validated clean builds and hardened startup for both Nao worker images.\n'
