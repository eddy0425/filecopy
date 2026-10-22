# FileCopy

一个轻量的私有云盘：Go 服务端 + Windows WPF 客户端。

适用场景：远程桌面连到海外电脑时，用 RDP 拖拽传文件很慢。两台电脑各装一个客户端，连接同一台服务器，文件经服务器中转，大文件用多连接并行分块传输，跨洋链路也能跑满带宽。

## 功能

- **资源管理器风格的文件列表**：系统文件图标、类型、大小，OneDrive 风格的状态图标
- **始终保留在此设备上 / 释放空间**：保留的文件下载到本地保留文件夹并保持双向同步；释放空间只删除本机副本，云端文件不受影响
- **上传**：按钮选择，或直接把文件 / 文件夹从资源管理器拖进窗口
- **管理**：新建文件夹、重命名、删除（本机副本进回收站）、下载到任意位置
- **快速传输**：4 MB 分块，每个文件 4 条连接并行，同时传 3 个文件，失败分块自动重试
- **冲突保护**：两边同时修改同一文件时，服务器把旧版本另存为 `name (conflict 时间).ext`，不会互相覆盖
- 关闭窗口最小化到托盘，可设置开机自动连接

### 状态图标

| 图标 | 含义 |
|---|---|
| 实心绿色 ✔ | 始终在此设备上可用 |
| 空心绿色 ✔ | 在此设备上可用（双击打开时下载的） |
| 蓝色 ☁ | 联机时可用，不占用本机空间 |
| 蓝色 ⟳ | 正在同步 / 等待上传或下载 |

## 目录结构

```
server/                 Go 服务端（仅用标准库）
  main.go               配置加载、路由、启动
  auth.go               Bearer Token 认证
  fs.go                 路径安全检查、列目录、下载、删除、移动、新建文件夹
  upload.go             分块上传会话
  server_test.go        测试
  config.example.json   配置示例
  filecopy.service      systemd 服务示例
client/FileCopy.Client/ WPF 客户端（.NET 9）
  Api/ServerApi.cs      HTTP 客户端，并行分块上传 / 下载
  Local/SyncService.cs  同步逻辑：刷新对比、保留、释放空间、上传下载
  Local/LocalState.cs   本机保留状态记录
  MainWindow.xaml       主界面
```

## 服务端部署（Linux）

### 1. 编译

需要 Go 1.22+。在 `server/` 目录下交叉编译出单个静态二进制（约 7 MB）：

```bash
GOOS=linux GOARCH=amd64 CGO_ENABLED=0 go build -ldflags="-s -w" -o bin/filecopy-server .
```

### 2. 配置

参考 `config.example.json` 写一个 `config.json`：

```json
{
  "listen": ":8080",
  "dataDir": "./data",
  "token": "换成一串足够长的随机字符串",
  "tlsCert": "",
  "tlsKey": ""
}
```

- `token` 至少 16 个字符，可以用 `openssl rand -hex 32` 生成
- `dataDir` 是服务器上存放文件的目录
- 填了 `tlsCert` / `tlsKey` 就直接提供 HTTPS；否则建议在前面加 Caddy（见下文）

### 3. 安装为 systemd 服务

```bash
sudo useradd -r -s /usr/sbin/nologin filecopy
sudo mkdir -p /opt/filecopy
sudo cp bin/filecopy-server config.json /opt/filecopy/
sudo chown -R filecopy: /opt/filecopy
sudo cp filecopy.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now filecopy
```

### 4. HTTPS（推荐）

否则 Token 和文件会在网络上明文传输。用 Caddy 自动申请证书，`/etc/caddy/Caddyfile`：

```
files.example.com {
    reverse_proxy 127.0.0.1:8080
}
```

此时把 `config.json` 里的 `listen` 改成 `127.0.0.1:8080`，只让 Caddy 访问。

## 客户端

### 运行要求

- Windows 10 / 11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)

### 编译

```bash
cd client
dotnet build -c Release
```

输出在 `client/FileCopy.Client/bin/Release/net9.0-windows/FileCopy.exe`。

### 使用

1. 首次启动会打开设置窗口，填写：
   - **服务器地址**：如 `https://files.example.com` 或 `http://1.2.3.4:8080`
   - **Token**：与服务端 `config.json` 中一致
   - **本地保留文件夹**："保留在本机" 的文件存放在这里
2. 双击文件夹进入，双击文件打开（未下载的会先下载）
3. 右键文件或文件夹：
   - **始终保留在此设备上**：下载并保持同步；对文件夹设置后，服务器上新加进来的文件也会自动下载
   - **释放空间**：删除本机副本，只保留在云端
4. 已保留的文件每 30 秒自动同步一次，也可点 "刷新" 或按 F5 立即同步

快捷键：`Enter` 打开，`Backspace` 返回上级，`F2` 重命名，`Delete` 删除，`F5` 刷新。

### 本机数据位置

| 文件 | 位置 |
|---|---|
| 设置（Token 用 DPAPI 加密） | `%AppData%\FileCopy\settings.json` |
| 保留状态 | `%AppData%\FileCopy\state-*.json` |
| 日志 | `%AppData%\FileCopy\log.txt` |

## 服务端 API

所有请求都需要请求头 `Authorization: Bearer <token>`，路径参数均为相对于数据目录的 `/` 分隔路径。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/ping` | 连通性检查 |
| GET | `/api/tree` | 递归列出全部条目 `[{path, isDir, size, mtime}]`，`mtime` 为 Unix 毫秒 |
| GET | `/api/file?path=` | 下载，支持 `Range`（断点续传、多连接分块） |
| DELETE | `/api/file?path=` | 删除文件或文件夹 |
| POST | `/api/move` | `{from, to}` 重命名 / 移动 |
| POST | `/api/mkdir` | `{path}` 新建文件夹 |
| POST | `/api/upload/init` | `{path, size, mtime}` → `{id, chunkSize}`，开始分块上传 |
| PUT | `/api/upload/{id}?offset=N` | 上传一个分块，可并行、乱序、重试 |
| POST | `/api/upload/{id}/complete` | `{baseMtime}` 完成上传；服务器上的文件若已被别人修改（mtime ≠ baseMtime），旧版本另存为冲突副本 |

## 开发与测试

服务端测试：

```bash
cd server
go test ./...
```

本地联调：用一个本地配置启动服务端，再把客户端的服务器地址设为 `http://127.0.0.1:8080`：

```bash
cd server
go run . -config config.json
```
