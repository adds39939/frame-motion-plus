#!/usr/bin/env bash
# Regenerates the checked-in PTX (native/kernels/ptx) from the kernel sources in this directory. A normal
# `dotnet build` never runs this: FrameMotionPlus.Cuda embeds the checked-in PTX.
#
# Usage: native/kernels/build.sh [--toolchain clang|nvcc] [--clang PATH] [--cuda-path DIR] [--out DIR] [--check]
#
#   --toolchain clang  (default) clang's NVPTX backend. Needs only a clang built with the nvptx64 target, taken
#                      from --clang, $FMP_CLANG or PATH. No NVIDIA headers or tools are used.
#   --toolchain nvcc   A CUDA toolkit's nvcc, from --cuda-path or $CUDA_PATH (the directory holding bin/nvcc).
#                      On the dev machine that is lib/VideoFX/external/cuda (CUDA 12.8 from the VFX SDK).
#   --out DIR          Write the PTX and manifest to DIR instead of native/kernels/ptx.
#   --check            Build into a temporary directory and fail if the result differs from native/kernels/ptx.
#
# Also read: $FMP_KERNEL_TOOLCHAIN (default toolchain), $FMP_NVCC_CCBIN (nvcc host compiler, default clang++).
#
# ptx/manifest.txt records the toolchain, its version and SHA-256 hashes of every source and PTX file. The tests
# check the hashes on every run (no toolchain needed) and, where the recorded toolchain and version are installed,
# rebuild and compare byte for byte.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
toolchain="${FMP_KERNEL_TOOLCHAIN:-clang}"
clang_bin="${FMP_CLANG:-clang}"
cuda_path="${CUDA_PATH:-}"
out="$here/ptx"
check=0

# Oldest architecture and PTX ISA the kernels need. The driver JIT-compiles this PTX for the GPU it runs on;
# PTX ISA 7.0 loads on any driver from CUDA 11.0 (R450) onwards.
arch=sm_75
ptx_isa=ptx70

while [[ $# -gt 0 ]]; do
    case "$1" in
        --toolchain) toolchain="$2"; shift 2 ;;
        --clang) clang_bin="$2"; shift 2 ;;
        --cuda-path) cuda_path="$2"; shift 2 ;;
        --out) out="$2"; shift 2 ;;
        --check) check=1; shift ;;
        -h|--help) sed -n '2,22p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "build.sh: unknown argument '$1'" >&2; exit 2 ;;
    esac
done

if [[ $check -eq 1 ]]; then
    tmp="$(mktemp -d)"
    trap 'rm -rf "$tmp"' EXIT
    args=(--toolchain "$toolchain" --out "$tmp")
    [[ -n "$cuda_path" ]] && args+=(--cuda-path "$cuda_path")
    args+=(--clang "$clang_bin")
    "${BASH_SOURCE[0]}" "${args[@]}"
    if diff -r "$here/ptx" "$tmp" > /dev/null; then
        echo "build.sh: checked-in PTX is up to date"
        exit 0
    fi
    echo "build.sh: checked-in PTX differs from a fresh $toolchain build:" >&2
    diff -r "$here/ptx" "$tmp" | head -40 >&2 || true
    exit 1
fi

sources=(fmp_cuda.h)
modules=()
for f in "$here"/*.cu; do
    name="$(basename "$f")"
    sources+=("$name")
    modules+=("${name%.cu}")
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

case "$toolchain" in
    clang)
        command -v "$clang_bin" > /dev/null || { echo "build.sh: clang not found ('$clang_bin')" >&2; exit 3; }
        "$clang_bin" -print-targets 2> /dev/null | grep -q nvptx64 ||
            { echo "build.sh: '$clang_bin' has no nvptx64 target" >&2; exit 3; }
        version="$("$clang_bin" --version | sed -n '1s/.*version \([0-9][0-9.]*\).*/\1/p')"
        compile() {
            "$clang_bin" -x cuda --cuda-device-only -nocudainc -nocudalib --cuda-gpu-arch="$arch" \
                --cuda-feature=+"$ptx_isa" -O3 -Wall -Wextra -Werror -S -o "$2" "$1"
        }
        ;;
    nvcc)
        [[ -n "$cuda_path" ]] || { echo "build.sh: nvcc needs --cuda-path or CUDA_PATH" >&2; exit 3; }
        cuda_path="$(cd "$cuda_path" && pwd)"
        nvcc="$cuda_path/bin/nvcc"
        [[ -x "$nvcc" ]] || { echo "build.sh: no nvcc at $nvcc" >&2; exit 3; }
        # nvcc runs cicc from <toolkit>/nvvm/bin. The VFX SDK ships a flattened toolkit with cicc in bin/, so give
        # nvcc a toolkit-shaped view of it through symlinks (nvcc reads nvcc.profile next to the path it was run by).
        if [[ ! -x "$cuda_path/nvvm/bin/cicc" && -x "$cuda_path/bin/cicc" ]]; then
            view="$work/toolkit"
            mkdir -p "$view/bin" "$view/nvvm/bin"
            for tool in "$cuda_path"/bin/*; do
                [[ "$(basename "$tool")" == nvcc.profile ]] || ln -s "$tool" "$view/bin/$(basename "$tool")"
            done
            cp "$cuda_path/bin/nvcc.profile" "$view/bin/nvcc.profile"
            ln -s "$cuda_path/bin/cicc" "$view/nvvm/bin/cicc"
            ln -s "$cuda_path/include" "$view/include"
            nvcc="$view/bin/nvcc"
        fi
        version="$("$nvcc" --version | sed -n 's/.*release [0-9.]*, V\([0-9.]*\).*/\1/p')"
        ccbin="${FMP_NVCC_CCBIN:-clang++}"
        # nvcc preprocesses with the host compiler and force-includes cuda_runtime.h, which pulls in host libc and
        # libstdc++ headers. CUDA 12.8 predates gcc 16, glibc 2.42 and libstdc++ 16: its EDG front end rejects
        # libstdc++ 16's builtin traits under gcc emulation, its math_functions.h conflicts with glibc's C23 math
        # declarations (cospi, sinpi, rsqrt) that _GNU_SOURCE enables, and libstdc++ 16's C++17 headers use syntax
        # it cannot parse. Hence clang++ as host compiler, -U_GNU_SOURCE and -std=c++14. Kernel code needs none of
        # those headers.
        compile() {
            "$nvcc" -ptx -arch="compute_${arch#sm_}" -O3 -Werror all-warnings \
                --allow-unsupported-compiler -ccbin "$ccbin" -std=c++14 -Xcompiler -U_GNU_SOURCE \
                -o "$2" "$1"
        }
        ;;
    *)
        echo "build.sh: unknown toolchain '$toolchain' (clang or nvcc)" >&2
        exit 2
        ;;
esac

[[ -n "$version" ]] || { echo "build.sh: cannot read the $toolchain version" >&2; exit 3; }

for module in "${modules[@]}"; do
    compile "$here/$module.cu" "$work/$module.ptx"
done

mkdir -p "$out"
rm -f "$out"/*.ptx "$out/manifest.txt"
{
    echo "# Written by native/kernels/build.sh. Do not edit; the tests check it against the sources and the PTX."
    echo "toolchain $toolchain $version"
    # As written into the PTX: nvcc always emits its own (newest) ISA version, clang the one requested above.
    echo "target $(sed -n 's/^\.target[[:space:]]*//p' "$work/${modules[0]}.ptx") ptx $(sed -n 's/^\.version[[:space:]]*//p' "$work/${modules[0]}.ptx")"
    for s in "${sources[@]}"; do
        echo "source $s $(sha256sum "$here/$s" | cut -d' ' -f1)"
    done
    for module in "${modules[@]}"; do
        cp "$work/$module.ptx" "$out/$module.ptx"
        echo "ptx $module.ptx $(sha256sum "$out/$module.ptx" | cut -d' ' -f1)"
    done
} > "$out/manifest.txt"

echo "build.sh: $toolchain $version -> ${modules[*]} in $out"
