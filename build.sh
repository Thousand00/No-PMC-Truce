#!/usr/bin/env bash
# 本地构建脚本。用法：
#   ./build.sh                     # 用默认游戏目录
#   GAME_DIR="D:\EFT-5.0 BE" ./build.sh
#   ./build.sh /d/EFT-5.0\ BE     # 位置参数也可以
set -euo pipefail

cd "$(dirname "$0")/src"

if [[ $# -ge 1 ]]; then
    GAME_DIR_IN="$1"
else
    GAME_DIR_IN="${GAME_DIR:-D:\\EFT-5.0 BE}"
fi

echo "==> 游戏目录: ${GAME_DIR_IN}"
dotnet build -c Release -p:GameDir="${GAME_DIR_IN}"

echo
echo "==> 产物:"
ls -la bin/Release/
