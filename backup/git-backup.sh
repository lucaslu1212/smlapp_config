#!/usr/bin/env bash
# 服务端自动 Git 备份（Linux / Git Bash）
#
# 行为：把仓库内变更提交，然后推送到远程；无变更则直接退出。
# 推送失败只写日志，不影响服务运行。
#
# 配置：复制 backup/backup.env.example 为 backup/backup.env（该文件不进 Git）
# 认证：
#   - 推荐使用 ssh-agent（先 ssh-add 载入密钥），或
#   - 在 backup.env 里设置 SML_SSH_KEY 指向一个无口令的部署密钥，或
#   - 使用系统计划任务自带的凭据
set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_DIR="${SML_REPO_DIR:-$(cd "$SCRIPT_DIR/.." && pwd)}"
ENV_FILE="${SML_BACKUP_ENV:-$SCRIPT_DIR/backup.env}"
LOG_FILE="${SML_BACKUP_LOG:-$REPO_DIR/backup.log}"

if [ -f "$ENV_FILE" ]; then
  # shellcheck disable=SC1090
  . "$ENV_FILE"
fi

BRANCH="${SML_BRANCH:-main}"
REMOTE="${SML_REMOTE:-origin}"

if [ -n "${SML_SSH_KEY:-}" ] && [ -f "$SML_SSH_KEY" ]; then
  export GIT_SSH_COMMAND="ssh -i \"$SML_SSH_KEY\" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new"
fi

cd "$REPO_DIR" || exit 1

# 日志超过 1 MiB 时保留最近 500 行
if [ -f "$LOG_FILE" ] && [ "$(wc -c < "$LOG_FILE" 2>/dev/null || echo 0)" -gt 1048576 ]; then
  tail -n 500 "$LOG_FILE" > "$LOG_FILE.tmp" 2>/dev/null && mv "$LOG_FILE.tmp" "$LOG_FILE"
fi

log() {
  echo "$(date '+%Y-%m-%d %H:%M:%S') $*" >> "$LOG_FILE"
}

# 防并发：记录 PID，进程不存在则视为过期锁
LOCK="$REPO_DIR/.git/sml-backup.lock"
if [ -e "$LOCK" ]; then
  PID="$(cat "$LOCK" 2>/dev/null || true)"
  if [ -n "$PID" ] && kill -0 "$PID" 2>/dev/null; then
    log "已有备份进程 $PID，跳过"
    exit 0
  fi
fi
echo $$ > "$LOCK"
trap 'rm -f "$LOCK"' EXIT

git add -A
if git diff --cached --quiet; then
  exit 0
fi

git commit -m "auto backup: $(date '+%Y-%m-%d %H:%M:%S')" >> "$LOG_FILE" 2>&1

if git push "$REMOTE" "$BRANCH" >> "$LOG_FILE" 2>&1; then
  log "push ok"
else
  log "push failed（检查远程地址、SSH key 或网络）"
fi
