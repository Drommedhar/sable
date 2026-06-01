#!/usr/bin/env bash
#
# build-ort-cuda.sh — build a Blackwell-capable ONNX Runtime CUDA runtime for Sable.
#
# WHY: prebuilt ONNX Runtime ships no kernels for newer NVIDIA archs (e.g. sm_120 / RTX 5090), so its
# CUDA EP fails there ("CUBLAS failure 50" / "no kernel image"). Sable builds ORT from source with the
# target archs baked in, publishes the resulting libonnxruntime*.so set, and the app downloads + loads
# it at runtime (Sable.Ai.Runtime.OrtCudaRuntime / OrtRuntimeProvisioner; GpuRuntimeCatalog points at
# the published archive). Distribution model = "Sable builds, app downloads" (NOT per-user compile).
#
# OUTPUT: dist/onnxruntime-cuda-<ort>-sm<archs>-cuda<major>-linux-x64.tar.gz
#   containing libonnxruntime.so(.<ver>), libonnxruntime_providers_shared.so,
#   libonnxruntime_providers_cuda.so — link the system CUDA toolkit + cuDNN at runtime.
#
# REQUIREMENTS on the build box: git, a C++ compiler, the CUDA toolkit (nvcc) >= 12.8 for sm_120,
# cuDNN 9 (headers+libs), ~30 GB free disk, ~16 GB RAM. cmake + ninja are auto-fetched below.
#
# USAGE:  tools/build-ort-cuda.sh [ARCHS] [ORT_TAG] [CUDA_HOME] [CUDNN_HOME]
#   ARCHS      CMAKE_CUDA_ARCHITECTURES list, default "89;90;120" (Ada/Hopper/Blackwell)
#   ORT_TAG    ONNX Runtime git tag, default v1.24.4 (MUST match Microsoft.ML.OnnxRuntime managed pkg)
#   CUDA_HOME  default /opt/cuda            CUDNN_HOME default /usr
#
# After building, upload the tarball and set the URL/Sha256/SizeBytes on the matching
# GpuRuntimeCatalog entry (src/Sable.Core/Ai/GpuRuntimeCatalog.cs).
#
# NOTE: the cmake flags below encode fixes discovered building on CachyOS (CUDA 13 + GCC 16 + a system
# protobuf/abseil): force-source all deps (CUDA bundles its own abseil/re2), bypass system protobuf,
# don't treat warnings as errors (GCC 16's -Wsfinae-incomplete), allow a newer host compiler, and cap
# parallelism so CUDA kernel compiles don't exhaust RAM.
set -euo pipefail

ARCHS="${1:-89;90;120}"
ORT_TAG="${2:-v1.24.4}"
CUDA_HOME="${3:-/opt/cuda}"
CUDNN_HOME="${4:-/usr}"
JOBS="${ORT_BUILD_JOBS:-6}"          # cap parallel jobs; CUDA kernel compiles are RAM-hungry

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${ORT_BUILD_DIR:-$ROOT/.ort-build}"
TOOLS="$WORK/tools"
mkdir -p "$TOOLS" "$WORK/dist"

echo "== build-ort-cuda: archs=$ARCHS ort=$ORT_TAG cuda=$CUDA_HOME cudnn=$CUDNN_HOME jobs=$JOBS =="

# --- portable cmake + ninja (no root needed) ---
export PATH="$TOOLS/cmake/bin:$TOOLS:$PATH"
if ! command -v cmake >/dev/null; then
  curl -sL "https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-linux-x86_64.tar.gz" -o "$TOOLS/cmake.tgz"
  mkdir -p "$TOOLS/cmake" && tar -xzf "$TOOLS/cmake.tgz" -C "$TOOLS/cmake" --strip-components=1 && rm "$TOOLS/cmake.tgz"
fi
if ! command -v ninja >/dev/null; then
  curl -sL "https://github.com/ninja-build/ninja/releases/download/v1.12.1/ninja-linux.zip" -o "$TOOLS/ninja.zip"
  (cd "$TOOLS" && unzip -oq ninja.zip && rm ninja.zip && chmod +x ninja)
fi

# --- source ---
cd "$WORK"
[ -d onnxruntime ] || git clone --recursive --depth 1 --branch "$ORT_TAG" https://github.com/microsoft/onnxruntime.git
cd onnxruntime

# --- build ---
python3 tools/ci_build/build.py \
  --build_dir build/Release --config Release \
  --parallel "$JOBS" \
  --use_cuda --cuda_home "$CUDA_HOME" --cudnn_home "$CUDNN_HOME" \
  --build_shared_lib --skip_tests \
  --cmake_generator Ninja \
  --allow_running_as_root \
  --compile_no_warning_as_error \
  --no_kleidiai \
  --cmake_extra_defines \
      "CMAKE_CUDA_ARCHITECTURES=$ARCHS" \
      onnxruntime_BUILD_UNIT_TESTS=OFF \
      CMAKE_CUDA_FLAGS=-allow-unsupported-compiler \
      CMAKE_DISABLE_FIND_PACKAGE_Protobuf=ON \
      FETCHCONTENT_TRY_FIND_PACKAGE_MODE=NEVER \
  --update --build

# --- package ---
OUT="$WORK/dist/onnxruntime-cuda-${ORT_TAG#v}-sm$(echo "$ARCHS" | tr ';' '-')-cuda$(basename "$CUDA_HOME")-linux-x64.tar.gz"
B="build/Release/Release"
stage="$WORK/stage"; rm -rf "$stage"; mkdir -p "$stage"
cp -P "$B"/libonnxruntime.so* "$stage"/ 2>/dev/null || true
cp "$B"/libonnxruntime_providers_shared.so "$stage"/
cp "$B"/libonnxruntime_providers_cuda.so "$stage"/
tar -C "$stage" -czf "$OUT" .
echo "== built: $OUT =="
echo "   sha256: $(sha256sum "$OUT" | cut -d' ' -f1)"
echo "   size:   $(stat -c%s "$OUT") bytes"
echo "Set these on the matching GpuRuntimeCatalog entry (Url/Sha256/SizeBytes)."
