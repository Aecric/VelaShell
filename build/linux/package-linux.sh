#!/usr/bin/env bash
# Build installable Linux packages from an existing VelaShell self-contained publish directory.
#
# Usage:
#   build/linux/package-linux.sh <version> <linux-x64|linux-arm64> <publish-dir> <dist-dir>
#
# The tar.gz remains the portable/in-app-update payload and is produced by the caller. This script
# only creates the package-manager assets; it never mutates the publish directory.
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: $0 <version> <linux-x64|linux-arm64> <publish-dir> <dist-dir>" >&2
  exit 64
fi

velashell_version=$1
velashell_rid=$2
velashell_publish_dir=$3
velashell_dist_dir=$4

if [[ ! $velashell_version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  echo "invalid SemVer version: $velashell_version" >&2
  exit 64
fi

case "$velashell_rid" in
  linux-x64)
    velashell_goarch=amd64
    velashell_tool_arch=x86_64
    velashell_nfpm_sha256=0660ca602b2d2d2ae4781a06c692b3eeb9d437ffea05b831d76e41f4a3188783
    ;;
  linux-arm64)
    velashell_goarch=arm64
    velashell_tool_arch=arm64
    velashell_nfpm_sha256=1c0f5f2999b9a974bfb04fdb0cc3306096de530ac5dbb25d739cc5f5219c919c
    ;;
  *)
    echo "unsupported RID: $velashell_rid" >&2
    exit 64
    ;;
esac

if [[ ! -d $velashell_publish_dir ]]; then
  echo "publish directory does not exist: $velashell_publish_dir" >&2
  exit 66
fi

for velashell_executable in VelaShell VelaShell.PluginHost; do
  if [[ ! -x $velashell_publish_dir/$velashell_executable ]]; then
    echo "required executable is missing or not executable: $velashell_publish_dir/$velashell_executable" >&2
    exit 66
  fi
done

if [[ ! -d $velashell_publish_dir/plugins ]] \
  || [[ -z $(find "$velashell_publish_dir/plugins" -type f -name plugin.json -print -quit) ]]; then
  echo "publish directory contains no packaged plugin manifest" >&2
  exit 66
fi

velashell_script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
velashell_repo_root=$(cd -- "$velashell_script_dir/../.." && pwd -P)
velashell_publish_dir=$(cd -- "$velashell_publish_dir" && pwd -P)
mkdir -p -- "$velashell_dist_dir"
velashell_dist_dir=$(cd -- "$velashell_dist_dir" && pwd -P)

# Pin the packaging tool and verify the upstream digest before executing it. A caller may provide
# NFPM_BIN to use an already provisioned binary (useful for offline/reproducible build images).
if [[ -n ${NFPM_BIN:-} ]]; then
  velashell_nfpm=$NFPM_BIN
else
  velashell_nfpm_version=2.47.0
  velashell_tool_root=${RUNNER_TEMP:-${TMPDIR:-/tmp}}/velashell-nfpm-$velashell_nfpm_version-$velashell_tool_arch
  velashell_nfpm=$velashell_tool_root/nfpm
  if [[ ! -x $velashell_nfpm ]]; then
    velashell_download_dir=$(mktemp -d)
    trap 'rm -rf -- "$velashell_download_dir"' EXIT
    velashell_archive=$velashell_download_dir/nfpm.tar.gz
    curl --fail --location --silent --show-error \
      "https://github.com/goreleaser/nfpm/releases/download/v$velashell_nfpm_version/nfpm_${velashell_nfpm_version}_Linux_${velashell_tool_arch}.tar.gz" \
      --output "$velashell_archive"
    printf '%s  %s\n' "$velashell_nfpm_sha256" "$velashell_archive" | sha256sum --check --status
    mkdir -p -- "$velashell_tool_root"
    tar -xzf "$velashell_archive" -C "$velashell_tool_root" nfpm
    chmod +x "$velashell_nfpm"
  fi
fi

if [[ ! -x $velashell_nfpm ]]; then
  echo "nFPM is not executable: $velashell_nfpm" >&2
  exit 69
fi

export VELASHELL_ARCH=$velashell_goarch
export VELASHELL_INPUT=$velashell_publish_dir
export VELASHELL_VERSION=$velashell_version

velashell_asset_base="VelaShell-$velashell_version-$velashell_rid"
cd -- "$velashell_repo_root"
for velashell_packager in deb rpm; do
  "$velashell_nfpm" package \
    --config "$velashell_script_dir/nfpm.yaml" \
    --packager "$velashell_packager" \
    --target "$velashell_dist_dir/$velashell_asset_base.$velashell_packager"
done
