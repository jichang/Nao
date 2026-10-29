#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
packages="${1:-$root/artifacts/packages}"
version="${2:-1.0.0}"
consumer="$(mktemp -d)"
trap 'rm -rf "$consumer"' EXIT

export FSharpCoreImplicitPackageVersion=10.1.401
dotnet new console --language F# --framework net10.0 --output "$consumer"
dotnet add "$consumer" package Nao.Extensions --version "$version" --source "$packages"
dotnet add "$consumer" package Nao.Platform --version "$version" --source "$packages"

cat > "$consumer/Program.fs" <<'EOF'
open System
open Nao.Extensions
open Nao.Platform

let compatibility =
    AdapterCompatibility.create
        (Version(1, 0, 0))
        1
        (Version(1, 0, 0))
        (Version(1, 0, 0))
        "net10.0"

let profile = DeploymentProfile.LocalDevelopment

printfn
    "clean-consumer: SDK %O, protocol %d, profile %A"
    (AdapterCompatibility.sdkVersion compatibility)
    (AdapterCompatibility.protocolVersion compatibility)
    profile
EOF

dotnet run --project "$consumer" --no-restore
