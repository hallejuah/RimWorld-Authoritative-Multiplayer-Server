#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$root/out/AuthoritativeProbeMod}"
publish="$(mktemp -d)"
trap 'rm -rf "$publish"' EXIT

# SDK compilation uses only reference assemblies. Never include any licensed
# RimWorld/Unity/Verse binaries in the packaged mod.
dotnet publish "$root/Source/AuthoritativeProbeMod/AuthoritativeProbeMod.csproj" \
    -c Release -o "$publish"

mkdir -p "$out/About" "$out/Assemblies"
cp "$root/Experimental/AuthoritativeProbe/About/About.xml" "$out/About/About.xml"

# Explicit allowlist: exclude all RimWorld, Unity, Assembly-CSharp and Harmony
# reference assemblies. Harmony is supplied by the player's Harmony mod.
for assembly in \
    MultiplayerAuthoritativeProbe \
    MultiplayerAuthoritativeRimWorldBridge \
    AuthoritativeCore \
    AuthoritativeTransport
do
    if [[ ! -f "$publish/$assembly.dll" ]]; then
        echo "Required assembly missing from publish output: $assembly" >&2
        exit 1
    fi
    cp "$publish/$assembly.dll" "$out/Assemblies/"
done

# Only copy the redistributable NuGet runtime libraries actually present.
# A real game install still must validate their Unity/Mono compatibility.
for dependency in \
    System.Text.Json \
    System.Text.Encodings.Web \
    System.Buffers \
    System.Memory \
    System.Runtime.CompilerServices.Unsafe \
    System.Threading.Tasks.Extensions \
    System.IO.Pipelines \
    System.Numerics.Vectors \
    Microsoft.Bcl.AsyncInterfaces
do
    if [[ -f "$publish/$dependency.dll" ]]; then
        cp "$publish/$dependency.dll" "$out/Assemblies/"
    fi
done

# Defense in depth: never stage proprietary engine reference assemblies.
if find "$out/Assemblies" -maxdepth 1 -type f \
    \( -name 'Assembly-CSharp.dll' -o -name 'UnityEngine*.dll' \
       -o -name 'Verse.dll' -o -name '0Harmony.dll' \) | grep -q .; then
    echo "Proprietary/game/runtime assemblies detected in staging folder" >&2
    exit 1
fi

echo "Isolated local test mod staged in $out (NOT game-tested)"
echo "Review game-runtime dependencies and third-party license notices before redistribution."
