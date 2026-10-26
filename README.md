# FileCopy

一个轻量的私有云盘：Go 服务端 + Windows WPF 客户端。

适用场景：远程桌面连到海外电脑时，用 RDP 拖拽传文件很慢。两台电脑各装一个客户端，连接同一台服务器，文件经服务器中转，大文件用多连接并行分块传输，跨洋链路也能跑满带宽。

服务端一键安装（Linux，需 root 和 Docker，详见 [服务端部署](#服务端部署linux)）：

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/eddy0425/filecopy/refs/heads/main/filecopy.sh)
```

## 功能

- **资源管理器风格的文件列表**：系统文件图标、类型、大小，OneDrive 风格的状态图标
- **始终保留在此设备上 / 释放空间**：保留的文件下载到本地保留文件夹并保持双向同步；释放空间只删除本机副本，云端文件不受影响
- **上传**：按钮选择，或直接把文件 / 文件夹从资源管理器拖进窗口
- **管理**：新建文件夹、重命名、删除（本机副本进回收站）、下载到任意位置
- **快速传输**：4 MB 分块，每个文件 4 条连接并行，同时传 3 个文件，失败分块自动重试
- **冲突保护**：两边同时修改同一文件时，服务器把旧版本另存为 `name (conflict 时间).ext`，不会互相覆盖
- **一键备份**：把 `D:\Config`、`D:\Recipes`、`D:\MechanicalParameters` 压缩成 `backup/<设备 ID>.7z` 并上传，按设备区分
- 关闭窗口最小化到托盘，可设置开机自动连接
- **控制面板**（浏览器访问 `/admin`）：一键允许 / 断开所有客户端连接；按设备 ID 查看在线状态、实时速度和累计流量（可重置、可在线修改设备 ID）；二步认证（TOTP）；云端文件管理（上传、下载、复制、移动、删除、回收站、只读文件夹）。详见 [控制面板](#控制面板)

### 状态图标

| 图标 | 含义 |
|---|---|
| 实心绿色 ✔ | 始终在此设备上可用 |
| 空心绿色 ✔ | 在此设备上可用（双击打开时下载的） |
| 蓝色 ☁ | 联机时可用，不占用本机空间 |
| 蓝色 ⟳ | 正在同步 / 等待上传或下载 |

## 目录结构

```
filecopy.sh             服务端一键安装 / 升级脚本（Docker）
server/                 Go 服务端（仅用标准库）
  main.go               配置加载、路由、启动
  auth.go               客户端接口：Token 认证、连接开关、设备识别与流量统计
  fs.go                 路径安全检查、列目录、下载、删除、移动、复制、新建文件夹
  upload.go             分块上传会话
  state.go              服务端状态（.filecopy/state.json）：连接开关、设备、只读文件夹
  admin.go              控制面板接口；admin/index.html 为面板网页（编译进二进制）
  totp.go / qr.go       二步认证与二维码生成
  trash.go              回收站
  server_test.go        测试
  config.example.json   配置示例
  filecopy.service      systemd 服务示例
client/FileCopy.Client/ WPF 客户端（.NET 9）
  Api/ServerApi.cs      HTTP 客户端，并行分块上传 / 下载
  Local/SyncService.cs  同步逻辑：刷新对比、保留、释放空间、上传下载
  Local/LocalState.cs   本机保留状态记录
  MainWindow.xaml       主界面
client/FileCopy.Client.Tests/ 客户端测试（xUnit，集成测试会自动编译并启动真实服务端）
```

## 服务端部署（Linux）

### 一键安装（Docker，推荐）

以 root 运行，需已安装 Docker（`curl -fsSL https://get.docker.com | sh`）：

```bash
bash <(curl -fsSL https://raw.githubusercontent.com/eddy0425/filecopy/refs/heads/main/filecopy.sh)
```

- 自动下载源码并用 `golang` 镜像编译，安装到 `/root/data/docker_data/filecopy`，文件存放在其下的 `data/`
- 首次安装自动生成 Token，结束时打印服务器地址和 Token，填入客户端即可
- 选项写在 `bash` 前面，如 `PORT=9000 bash <(curl ...)`：
  - `PORT=9000`：对外端口，默认 8240
  - `BIND=127.0.0.1`：只监听本机，配合 Caddy 提供 HTTPS（见下文）
  - `GO_IMAGE=...`：Docker Hub 访问慢时换成镜像源里的 golang 镜像
- 升级：重新运行同一条命令，配置和数据保留
- 也可以克隆仓库后在根目录运行 `bash filecopy.sh`；若已在本地编译好 `server/bin/filecopy-server`，会直接使用，不再拉取 golang 镜像

以下是不用 Docker 的手动部署步骤。

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
  "adminPassword": "控制面板初始密码",
  "tlsCert": "",
  "tlsKey": ""
}
```

- `token` 至少 16 个字符，可以用 `openssl rand -hex 32` 生成
- `adminPassword` 是控制面板的初始密码，不填则用 `token` 登录面板；在面板中修改密码后以面板设置为准
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

此时把 `config.json` 里的 `listen` 改成 `127.0.0.1:8080`，只让 Caddy 访问。Docker 部署则不改 `config.json`：用 `BIND=127.0.0.1` 重新运行安装命令，并把 `reverse_proxy` 改成 `127.0.0.1:8240`（或你指定的 `PORT`）；脚本会让服务端信任经 Docker 网桥转发的 `X-Forwarded-For`，面板显示的仍是访问者的真实 IP。

反向代理不在同一台机器、而是从内网地址访问服务端时，在 `config.json` 中加上 `"trustProxy": true`，否则面板显示和失败锁定都按代理的 IP 计算。不要在服务端直接对外（没有反向代理）时开启：内网中任何人都能伪造 IP，绕过失败锁定。

## 控制面板

浏览器打开 `http://服务器地址:端口/admin`（配置了 HTTPS 则用 `https://`），用管理密码登录。面板与客户端共用同一端口，可从公网访问，由管理密码、二步认证和失败锁定（同一 IP 连续输错 5 次锁定 5 分钟）保护；**强烈建议配置 HTTPS**，否则密码以明文传输，面板顶部也会提示。

- **连接**：
  - 开关 "允许连接"：关闭后所有客户端请求被拒绝（客户端显示 "服务器已暂停连接"），正在进行的传输立即中断；面板不受影响，服务器重启后保持关闭
  - 设备列表：按客户端设置里的设备 ID 区分，显示 IP、最后活动、在线状态（60 秒内有请求）、实时上传 / 下载速度、累计流量；可单个或全部重置流量
  - 点击设备 ID 可修改，客户端下次连接服务器时自动更新自己的设置（客户端确认前显示 "待同步"）；已有的备份包不改名，下次备份使用新 ID
  - 同一设备 ID 同时从多个 IP 连接时标红 "ID 冲突"
  - 顶部显示数据占用、回收站占用和磁盘剩余空间（约每分钟更新）
- **文件**：上传（文件、文件夹、拖放）、下载、新建文件夹、重命名、复制到、移动到、删除、设为只读 / 取消只读
  - **只读文件夹**：客户端只能下载，不能上传、新建、重命名、删除，也不能删除或移动包含它的上级文件夹；客户端在本地保留文件夹里修改的文件会被恢复为云端版本（修改的版本移到 Windows 回收站），新建的文件会被移到回收站，不会上传。面板不受限制
- **回收站**：客户端和面板删除的文件都进入服务器回收站，可还原（原位置被占用时还原为 `name (restored 时间).ext`）或永久删除；超过 30 天自动清理
- **安全**：
  - 启用二步认证（TOTP）：用 Google Authenticator、Microsoft Authenticator 等验证器 App 扫码。启用后登录面板需要 6 位验证码；客户端**关闭到托盘后**再次打开（或打开同步文件夹）也需要验证码，解锁后 5 分钟内重新打开免验证，连不上服务器时无法解锁
  - 修改管理密码（需当前密码和验证码），修改后其他已登录的会话被退出

面板的状态（连接开关、设备与流量、二步认证密钥、修改后的密码、只读文件夹）保存在数据目录下的 `.filecopy/state.json`，回收站在 `.filecopy/trash/`，客户端看不到这个目录。

## 客户端

### 运行要求

- Windows 10 / 11
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)
- 使用备份功能需要 [7-Zip](https://www.7-zip.org/)（安装到默认位置，或把 `7za.exe` 放到 `FileCopy.exe` 同目录）

### 编译

```bash
cd client
dotnet build -c Release
```

输出在 `client/FileCopy.Client/bin/Release/net9.0-windows/FileCopy.exe`。

### 使用

1. 首次启动会打开设置窗口，填写：
   - **服务器地址**：如 `https://files.example.com` 或 `http://1.2.3.4:8240`
   - **Token**：与服务端 `config.json` 中一致
   - **本地保留文件夹**："保留在本机" 的文件存放在这里（默认 `文档\FileCopy\data`；如果 "文档" 已被 OneDrive 接管同步，建议改到 OneDrive 之外的位置，避免两边同步互相干扰）
   - **设备 ID**（必填）：区分不同电脑，用于控制面板的设备列表和备份包命名，如 `XZT1-CB`；可在控制面板中在线修改
2. 双击文件夹进入，双击文件打开（未下载的会先下载）
3. 右键文件或文件夹：
   - **始终保留在此设备上**：下载并保持同步；对文件夹设置后，服务器上新加进来的文件也会自动下载
   - **释放空间**：删除本机副本，只保留在云端
4. 已保留的文件在打开软件、点 "刷新" / 按 F5、点 "备份" 时同步（不会定时自动扫描）
5. 点 "备份"：把三个设备文件夹压缩为 `<本地保留文件夹>\backup\<设备 ID>.7z` 并上传到服务器 `backup/`，同名覆盖只保留最新

快捷键：`Enter` 打开，`Backspace` 返回上级，`F2` 重命名，`Delete` 删除，`F5` 刷新。

服务端启用二步认证后，窗口关闭到托盘再从托盘打开时需要输入验证码；控制面板设置的只读文件夹带 🔒 标记，其中的上传、新建、重命名、删除不可用。删除的文件进入服务器回收站，可在控制面板中还原。

### 本机数据位置

| 文件 | 位置 |
|---|---|
| 设置（Token 用 DPAPI 加密） | `%AppData%\FileCopy\settings.json` |
| 保留状态 | `%AppData%\FileCopy\state-*.json` |
| 日志 | `%AppData%\FileCopy\log.txt` |
| 本地保留文件夹（默认） | `文档\FileCopy\data` |
| 备份包（默认） | `文档\FileCopy\data\backup\<设备 ID>.7z` |
| 程序 | `文档\FileCopy\app` |

## 服务端 API

所有请求都需要请求头 `Authorization: Bearer <token>` 和 `X-Equipment-Id: <设备 ID>`（URL 编码，缺少时返回 400），路径参数均为相对于数据目录的 `/` 分隔路径。连接被控制面板断开时所有接口返回 503；在只读文件夹中做修改返回 403。

| 方法 | 路径 | 说明 |
|---|---|---|
| GET | `/api/ping` | 连通性检查，返回 `{status, totp, equipmentId}`：是否启用二步认证，以及服务器认定的设备 ID（在面板中改名后与请求头不同） |
| POST | `/api/totp/verify` | `{code}` 校验二步验证码（客户端解锁用）：正确 204，错误 403，尝试过多 429 |
| GET | `/api/tree` | 递归列出全部条目 `[{path, isDir, size, mtime, readOnly}]`，`mtime` 为 Unix 毫秒，`readOnly` 表示位于只读文件夹中 |
| GET | `/api/file?path=` | 下载，支持 `Range`（断点续传、多连接分块） |
| DELETE | `/api/file?path=` | 删除文件或文件夹（移入服务器回收站） |
| POST | `/api/move` | `{from, to}` 重命名 / 移动；目标已存在时返回 409（仅大小写不同的同一项除外） |
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

客户端测试（需要 Go，测试会编译服务端并在随机端口启动；删除类测试会把临时文件移到回收站）：

```bash
cd client
dotnet test FileCopy.Client.Tests
```

本地联调：用一个本地配置启动服务端，再把客户端的服务器地址设为 `http://127.0.0.1:8080`：

```bash
cd server
go run . -config config.json
```
