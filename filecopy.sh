#!/usr/bin/env bash
# FileCopy 服务端一键安装 / 升级脚本（Docker 部署）
#
# 用法（在服务器上，以 root 运行）：
#   bash <(curl -fsSL https://raw.githubusercontent.com/eddy0425/filecopy/refs/heads/main/filecopy.sh)
#                                 # 一键安装 / 升级，无需克隆仓库，自动下载源码
#   bash filecopy.sh              # 在仓库根目录（与 server/ 同级）运行，使用本地代码
#   PORT=9000 bash filecopy.sh    # 指定对外端口，默认 8240
#   BIND=127.0.0.1 bash filecopy.sh  # 只监听本机，前面用 Caddy / Nginx 反代 HTTPS
#
# 二进制来源（按顺序）：
#   1. server/bin/filecopy-server —— 本地交叉编译好再上传，服务器无需拉取 golang 镜像
#   2. 否则用 golang 镜像在容器里编译 server/ 源码（可用 GO_IMAGE 指定镜像加速地址）
#   3. 脚本旁边没有 server/ 时（如一键安装），从 GitHub 下载源码到安装目录的 src/ 再编译
#
# 安装后的目录：
#   /root/data/docker_data/filecopy/
#     filecopy-server      服务端二进制
#     Dockerfile
#     docker-compose.yml
#     config.json          配置（含 Token，首次安装自动生成，升级时保留）
#     data/                用户文件
set -euo pipefail

APP_DIR=/root/data/docker_data/filecopy
PORT="${PORT:-8240}"
BIND="${BIND:-0.0.0.0}"
GO_IMAGE="${GO_IMAGE:-golang:1.23-alpine}"
REPO_TARBALL="${REPO_TARBALL:-https://github.com/eddy0425/filecopy/archive/refs/heads/main.tar.gz}"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_DIR="$SCRIPT_DIR/server"

info() { echo -e "\033[32m[INFO]\033[0m $*"; }
die()  { echo -e "\033[31m[ERROR]\033[0m $*" >&2; exit 1; }

# ---------- 环境检查 ----------
[ "$(id -u)" -eq 0 ] || die "请用 root 运行"
command -v docker >/dev/null || die "未安装 Docker，可执行：curl -fsSL https://get.docker.com | sh"
command -v curl >/dev/null || die "未安装 curl，请先安装（apt install -y curl / yum install -y curl）"
if docker compose version >/dev/null 2>&1; then
    COMPOSE="docker compose"
elif command -v docker-compose >/dev/null; then
    COMPOSE="docker-compose"
else
    die "未找到 docker compose 插件或 docker-compose"
fi

mkdir -p "$APP_DIR/data"

# ---------- 准备源码 ----------
# 通过 bash <(curl ...) 运行时脚本旁边没有 server/，改为下载源码
if [ ! -f "$SRC_DIR/main.go" ] && [ ! -f "$SRC_DIR/bin/filecopy-server" ]; then
    info "未找到本地源码，从 $REPO_TARBALL 下载"
    rm -rf "$APP_DIR/src" && mkdir -p "$APP_DIR/src"
    curl -fsSL "$REPO_TARBALL" | tar -xz -C "$APP_DIR/src" --strip-components=1 \
        || die "下载源码失败，可克隆仓库后在仓库根目录运行 bash filecopy.sh"
    SRC_DIR="$APP_DIR/src/server"
    [ -f "$SRC_DIR/main.go" ] || die "下载的源码中没有 server/main.go：$REPO_TARBALL"
fi

# ---------- 准备二进制 ----------
PREBUILT="$SRC_DIR/bin/filecopy-server"
# 源码比二进制新（留 60 秒余量，避免同一次上传的文件时间先后造成误判）则改为重新编译
if [ -f "$PREBUILT" ] && [ -f "$SRC_DIR/main.go" ] && [ -n "$(find "$SRC_DIR" -maxdepth 1 -name '*.go' \
        -newermt "@$(( $(stat -c %Y "$PREBUILT") + 60 ))" 2>/dev/null)" ]; then
    info "预编译二进制比源码旧，改为从源码编译"
    PREBUILT=""
fi
if [ -n "$PREBUILT" ] && [ -f "$PREBUILT" ]; then
    info "使用预编译二进制 $PREBUILT"
    install -m 755 "$PREBUILT" "$APP_DIR/filecopy-server.new"
elif [ -f "$SRC_DIR/main.go" ]; then
    info "用 $GO_IMAGE 编译 $SRC_DIR"
    docker run --rm \
        -v "$SRC_DIR":/src:ro \
        -v "$APP_DIR":/out \
        -w /src \
        -e CGO_ENABLED=0 -e GOFLAGS=-buildvcs=false -e GOCACHE=/tmp/gocache \
        "$GO_IMAGE" \
        go build -ldflags="-s -w" -o /out/filecopy-server.new .
    chmod 755 "$APP_DIR/filecopy-server.new"
else
    die "找不到 $SRC_DIR，请把脚本放在仓库根目录（与 server/ 同级）运行"
fi
mv -f "$APP_DIR/filecopy-server.new" "$APP_DIR/filecopy-server"

# ---------- 配置 ----------
if [ -f "$APP_DIR/config.json" ]; then
    info "保留已有配置 $APP_DIR/config.json"
    # 容器内端口固定为 8080，HTTPS 由反向代理提供
    if ! grep -Eq '"listen"[[:space:]]*:[[:space:]]*"[^"]*:8080"' "$APP_DIR/config.json"; then
        die "config.json 的 listen 必须是 \":8080\"（容器内端口），对外端口请用 PORT=xxx 指定"
    fi
    if grep -Eq '"tlsCert"[[:space:]]*:[[:space:]]*"[^"]+' "$APP_DIR/config.json"; then
        die "Docker 部署请用反向代理提供 HTTPS，不要在 config.json 中填写 tlsCert / tlsKey"
    fi
else
    if command -v openssl >/dev/null; then
        TOKEN="$(openssl rand -hex 32)"
    else
        TOKEN="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"
    fi
    # 容器内的路径；对外端口由 docker-compose.yml 映射
    cat > "$APP_DIR/config.json" <<EOF
{
  "listen": ":8080",
  "dataDir": "/app/data",
  "token": "$TOKEN"
}
EOF
    chmod 600 "$APP_DIR/config.json"
    info "已生成配置和随机 Token"
fi

cat > "$APP_DIR/Dockerfile" <<'EOF'
FROM scratch
LABEL app=filecopy
COPY filecopy-server /app/filecopy-server
WORKDIR /app
EXPOSE 8080
ENTRYPOINT ["/app/filecopy-server", "-config", "/app/config.json"]
EOF

cat > "$APP_DIR/docker-compose.yml" <<EOF
services:
  filecopy:
    build: .
    image: filecopy-server:latest
    container_name: filecopy
    restart: unless-stopped
    ports:
      - "$BIND:$PORT:8080"
    volumes:
      - ./config.json:/app/config.json:ro
      - ./data:/app/data
      - /etc/localtime:/etc/localtime:ro   # 冲突副本文件名使用本地时间
    logging:
      driver: json-file
      options:
        max-size: "10m"
        max-file: "3"
EOF

# ---------- 启动 ----------
cd "$APP_DIR"
info "构建镜像并启动容器"
$COMPOSE up -d --build --force-recreate
docker image prune -f --filter label=app=filecopy >/dev/null 2>&1 || true

# ---------- 健康检查 ----------
TOKEN="$(sed -n 's/.*"token"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' config.json)"
CHECK_HOST="$BIND"; [ "$CHECK_HOST" = "0.0.0.0" ] && CHECK_HOST=127.0.0.1
OK=0
for _ in $(seq 1 10); do
    if curl -fsS -H "Authorization: Bearer $TOKEN" "http://$CHECK_HOST:$PORT/api/ping" >/dev/null 2>&1; then
        OK=1; break
    fi
    sleep 1
done
[ "$OK" -eq 1 ] || die "服务未响应，查看日志：cd $APP_DIR && $COMPOSE logs"

PUBLIC_IP="$(curl -fsS --max-time 3 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}')"
echo
info "安装完成"
echo "  安装目录：$APP_DIR"
echo "  数据目录：$APP_DIR/data"
if [ "$BIND" = "127.0.0.1" ]; then
    echo "  本机地址：http://127.0.0.1:$PORT（请配置反向代理提供 HTTPS）"
else
    echo "  服务器地址：http://$PUBLIC_IP:$PORT"
fi
echo "  Token：$TOKEN"
echo
echo "  常用命令（在 $APP_DIR 下）："
echo "    $COMPOSE logs -f      查看日志"
echo "    $COMPOSE restart      重启"
echo "    $COMPOSE down         停止"
echo "  升级：更新代码后重新运行本脚本，配置和数据会保留"
