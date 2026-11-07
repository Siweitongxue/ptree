#!/bin/sh
# 构建互斥器：多个智能体同时改 C# 时，用 mkdir 做原子锁，避免 obj/bin 互相踩。
# 用法：bash build-lock.sh <相对解决方案目录的项目路径> [dotnet build 参数...]
# 例：bash build-lock.sh src/PTree.Cli
# 锁目录固定为仓内 .build-lock，超过 20 分钟自动视为陈旧锁。
set -u
HERE=$(cd "$(dirname "$0")" && pwd)
LOCK="$HERE/.build-lock"
STALE=1200
if [ $# -lt 1 ]; then echo "用法：build-lock.sh <项目相对路径> [参数...]" >&2; exit 2; fi
PROJ="$1"; shift
n=0
while [ $n -lt 600 ]; do
  if mkdir "$LOCK" 2>/dev/null; then
    trap 'rmdir "$LOCK" 2>/dev/null' EXIT INT TERM
    break
  fi
  if [ -f "$LOCK/pid" ]; then
    age=$(( $(date +%s) - $(stat -c %Y "$LOCK" 2>/dev/null || echo 0) ))
    if [ "$age" -gt "$STALE" ]; then rmdir "$LOCK" 2>/dev/null; fi
  fi
  n=$((n+1)); sleep 2
done
if [ ! -d "$LOCK" ]; then echo "拿不到构建锁，20 分钟超时" >&2; exit 3; fi
echo $$ > "$LOCK/pid" 2>/dev/null || true
cd "$HERE" || exit 4
dotnet build "$PROJ" -c Release -v q --nologo "$@"
rc=$?
rm -rf "$LOCK"
exit $rc
