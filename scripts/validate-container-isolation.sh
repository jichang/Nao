#!/usr/bin/env bash
set -euo pipefail

image_tag="${NAO_CONTAINER_TEST_IMAGE:-alpine:3.22}"
broker_image_tag="${NAO_CONTAINER_BROKER_TEST_IMAGE:-alpine/socat:latest}"
docker pull "$image_tag" >/dev/null
docker pull "$broker_image_tag" >/dev/null
image_reference="$(docker image inspect "$image_tag" --format '{{index .RepoDigests 0}}')"
broker_image_reference="$(docker image inspect "$broker_image_tag" --format '{{index .RepoDigests 0}}')"

for resolved_image in "$image_reference" "$broker_image_reference"; do
  if [[ ! "$resolved_image" =~ @sha256:[0-9a-fA-F]{64}$ ]]; then
    printf 'error: container test image did not resolve to a repository digest: %s\n' "$resolved_image" >&2
    exit 1
  fi
done

workspace="$(mktemp -d)"
identifier="$(date +%s%N)"
container_name="nao-isolation-test-$identifier"
allowlisted_server="nao-allowlisted-server-$identifier"
undeclared_server="nao-undeclared-server-$identifier"
broker_name="nao-egress-broker-$identifier"
allowlist_worker="nao-allowlist-worker-$identifier"
worker_network="nao-worker-internal-$identifier"
upstream_network="nao-broker-upstream-$identifier"

cleanup() {
  docker rm --force --volumes \
    "$container_name" \
    "$allowlist_worker" \
    "$broker_name" \
    "$allowlisted_server" \
    "$undeclared_server" >/dev/null 2>&1 || true
  docker network rm "$worker_network" "$upstream_network" >/dev/null 2>&1 || true
  rm -rf "$workspace"
}

report_failure() {
  status=$?
  printf 'error: container isolation regression failed\n' >&2
  docker ps --all --filter "name=nao-.*-$identifier" --format 'container={{.Names}} status={{.Status}}' >&2 || true
  docker logs "$broker_name" >&2 || true
  exit "$status"
}

trap cleanup EXIT
trap report_failure ERR
printf 'declared\n' > "$workspace/declared"
printf 'undeclared\n' > "$workspace/undeclared"

docker run --name "$container_name" --rm \
  --user 65532:65532 \
  --read-only \
  --cap-drop=ALL \
  --security-opt=no-new-privileges \
  --network=none \
  --memory=33554432 \
  --memory-swap=33554432 \
  --pids-limit=8 \
  --mount "type=bind,src=$workspace/declared,dst=/input/declared,readonly" \
  "$image_reference" \
  sh -euc '
    test "$(id -u):$(id -g)" = "65532:65532"
    test "$(awk "/^CapEff:/ { print \$2 }" /proc/self/status)" = "0000000000000000"
    test "$(awk "/^NoNewPrivs:/ { print \$2 }" /proc/self/status)" = "1"
    test "$(cat /sys/fs/cgroup/memory.max)" = "33554432"
    test "$(cat /sys/fs/cgroup/pids.max)" = "8"
    test "$(cat /input/declared)" = "declared"
    ! sh -c "printf x > /input/declared" 2>/dev/null
    ! sh -c "printf x > /root-escape" 2>/dev/null
    test ! -e /input/undeclared
    test ! -e /var/run/docker.sock
    ! wget -q -T 2 -O /dev/null https://example.com 2>/dev/null
  '

test -z "$(docker ps --all --filter "name=^/${container_name}$" --format '{{.ID}}')"

docker network create --internal "$worker_network" >/dev/null
docker network create --internal "$upstream_network" >/dev/null

docker run --detach --name "$allowlisted_server" \
  --network "$upstream_network" \
  --network-alias allowlisted.test \
  "$broker_image_reference" \
  TCP-LISTEN:8080,fork,reuseaddr EXEC:/bin/uname >/dev/null

docker run --detach --name "$undeclared_server" \
  --network "$upstream_network" \
  --network-alias undeclared.test \
  "$broker_image_reference" \
  TCP-LISTEN:8080,fork,reuseaddr EXEC:/bin/uname >/dev/null

docker create --name "$broker_name" \
  --network "$upstream_network" \
  "$broker_image_reference" \
  TCP-LISTEN:8080,fork,reuseaddr TCP:allowlisted.test:8080 >/dev/null

docker network connect --alias egress-broker "$worker_network" "$broker_name"
docker start "$broker_name" >/dev/null

docker run --name "$allowlist_worker" --rm \
  --user 65532:65532 \
  --read-only \
  --cap-drop=ALL \
  --security-opt=no-new-privileges \
  --network "$worker_network" \
  --memory=33554432 \
  --memory-swap=33554432 \
  --pids-limit=8 \
  "$image_reference" \
  sh -euc '
    attempts=0
    body=""
    until body="$(nc -w 2 egress-broker 8080 2>/dev/null)"; do
      attempts=$((attempts + 1))
      test "$attempts" -lt 5
    done
    test "$body" = "Linux"
    ! nc -w 2 undeclared.test 8080 </dev/null 2>/dev/null
    ! nc -w 2 example.com 80 </dev/null 2>/dev/null
    test "$(id -u):$(id -g)" = "65532:65532"
    test "$(awk "/^CapEff:/ { print \$2 }" /proc/self/status)" = "0000000000000000"
    test "$(awk "/^NoNewPrivs:/ { print \$2 }" /proc/self/status)" = "1"
  '

docker rm --force --volumes "$broker_name" "$allowlisted_server" "$undeclared_server" >/dev/null
docker network rm "$worker_network" "$upstream_network" >/dev/null

for removed_name in "$allowlist_worker" "$broker_name" "$allowlisted_server" "$undeclared_server"; do
  test -z "$(docker ps --all --filter "name=^/${removed_name}$" --format '{{.ID}}')"
done

for removed_network in "$worker_network" "$upstream_network"; do
  test -z "$(docker network ls --filter "name=^${removed_network}$" --format '{{.ID}}')"
done

printf 'Validated container isolation with %s and egress broker %s\n' "$image_reference" "$broker_image_reference"