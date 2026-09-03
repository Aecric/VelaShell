#!/usr/bin/env bash
# Publish one Linux RID and assemble its portable and package-manager assets.
#
# Usage:
#   build/linux/build-linux.sh <version> <linux-x64|linux-arm64> <out-dir> <dist-dir>
set -euo pipefail

if [[ $# -ne 4 ]]; then
  echo "usage: $0 <version> <linux-x64|linux-arm64> <out-dir> <dist-dir>" >&2
  exit 64
fi

velashell_version=$1
velashell_rid=$2
velashell_out_root=$3
velashell_dist_dir=$4

if [[ ! $velashell_version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$ ]]; then
  echo "invalid SemVer version: $velashell_version" >&2
  exit 64
fi
case "$velashell_rid" in
  linux-x64|linux-arm64) ;;
  *)
    echo "unsupported RID: $velashell_rid" >&2
    exit 64
    ;;
esac

velashell_script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)
velashell_repo_root=$(cd -- "$velashell_script_dir/../.." && pwd -P)
velashell_name="VelaShell-$velashell_version-$velashell_rid"
velashell_publish_dir="$velashell_out_root/$velashell_name"
mkdir -p -- "$velashell_publish_dir" "$velashell_dist_dir"

velashell_publish_args=(
  "$velashell_repo_root/src/VelaShell/VelaShell.csproj"
  -c Release
  -r "$velashell_rid"
  -o "$velashell_publish_dir"
  -p:Version="$velashell_version"
  -p:SelfContained=true
  -p:DebugType=None
  --nologo
)
if [[ -n ${VELASHELL_SIGN_ASSEMBLY:-} ]]; then
  case "$VELASHELL_SIGN_ASSEMBLY" in
    true|false) velashell_publish_args+=(-p:SignAssembly="$VELASHELL_SIGN_ASSEMBLY") ;;
    *)
      echo "VELASHELL_SIGN_ASSEMBLY must be true or false" >&2
      exit 64
      ;;
  esac
fi

dotnet publish "${velashell_publish_args[@]}"
chmod +x "$velashell_publish_dir/VelaShell" "$velashell_publish_dir/VelaShell.PluginHost"
tar -czf "$velashell_dist_dir/$velashell_name.tar.gz" -C "$velashell_publish_dir" .
"$velashell_script_dir/package-linux.sh" \
  "$velashell_version" "$velashell_rid" "$velashell_publish_dir" "$velashell_dist_dir"
