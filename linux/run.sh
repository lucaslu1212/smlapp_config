#!/usr/bin/env bash
# 启动教室一体机管家服务端（Linux / Python）
#   ./run.sh         生产（443/80，需要 root 或 CAP_NET_BIND_SERVICE）
#   ./run.sh --dev   本地调试（8443/8080，数据在 data-dev/）
set -e
cd "$(dirname "$0")"
exec python3 sml_server.py "$@"
