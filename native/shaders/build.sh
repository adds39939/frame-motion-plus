#!/usr/bin/env bash
# Regenerates the checked-in SPIR-V for the Vulkan presenter.
#
#   native/shaders/build.sh            write native/shaders/spirv/*.spv and spirv/COMPILER
#   native/shaders/build.sh <outdir>   write into <outdir> instead (the up-to-date test uses this)
#
# Needs glslangValidator (Arch: glslang). The build never runs this; the .spv files are embedded as they are.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="${1:-$here/spirv}"
mkdir -p "$out"

compiler="$(command -v glslangValidator || true)"
if [[ -z "$compiler" ]]; then
    echo "glslangValidator not found (install glslang)" >&2
    exit 2
fi

for shader in present.vert present.frag copy.comp; do
    "$compiler" --quiet -V --target-env vulkan1.3 -o "$out/$shader.spv" "$here/$shader"
done

# The up-to-date test only compares bytes when the installed compiler is the one recorded here.
"$compiler" --version | head -n 1 > "$out/COMPILER"
