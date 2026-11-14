#!/usr/bin/env bash
# 在 Linux 服务器上安装「每 30 分钟自动 Git 备份」的 systemd timer。
# 用法：sudo ./setup-linux.sh [/opt/sml-server]
set -euo pipefail

APP_DIR="${1:-/opt/sml-server}"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

if [ ! -d "$APP_DIR/.git" ]; then
  echo "错误：$APP_DIR 不是 Git 仓库。请先初始化仓库并配置 remote。"
  exit 1
fi

sed "s#/opt/sml-server#$APP_DIR#g" "$SCRIPT_DIR/sml-backup.service" \
  | sudo tee /etc/systemd/system/sml-backup.service > /dev/null
sudo cp "$SCRIPT_DIR/sml-backup.timer" /etc/systemd/system/sml-backup.timer

sudo systemctl daemon-reload
sudo systemctl enable --now sml-backup.timer

echo "已安装。查看状态：systemctl status sml-backup.timer"
echo "立即执行一次：sudo systemctl start sml-backup.service"
echo "查看日志：journalctl -u sml-backup.service -n 50"
