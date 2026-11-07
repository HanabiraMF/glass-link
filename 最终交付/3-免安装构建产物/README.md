# GlassLink

把 Windows 桌面以低延迟 GPU 编码流实时送到手机/电脑浏览器，并让浏览器反向控制 Windows。
项目基于 **.NET 10**、**DXGI Desktop Duplication**、**Media Foundation 硬件 H.264**、**WebSocket + WebCodecs**。

## 功能

- **静默托盘宿主**：`WinExe` 无控制台窗口，ASP.NET Core/Kestrel 后台运行，系统托盘菜单提供「打开设置面板」「当前状态」「退出程序」，日志写入 `%LOCALAPPDATA%\GlassLink\logs`（不可写时回退到应用目录）。
- **GPU 捕获 / 事件驱动**：DXGI Desktop Duplication，帧停留在 `ID3D11Texture2D`，不做 `Map/Unmap` CPU 回读。静止桌面由 `AcquireNextFrame` 阻塞等待，不使用 `Thread.Sleep` 轮询或定时器限帧；帧率限制通过丢帧实现。
- **背压控制**：每个 WebSocket 客户端只有「最新关键帧 + 最新 P 帧」两个槽位，发送在客户端独立循环中完成；队列积压时丢弃旧 P 帧，抓屏/编码线程从不等待网络 I/O。
- **GPU 颜色转换**：D3D11 Video Processor 将 BGRA 转 NV12，专供硬件编码器。
- **GPU 硬件编码**：Media Foundation `IMFTransform` + `IMFDXGIDeviceManager`，自动枚举 NVIDIA NVENC / AMD AMF / Intel QSV，输出 H.264 Annex-B。默认强制使用硬件编码器；仅在显式传入 `--allow-software-encoder` 时才允许软件 MFT 回退。
- **低延迟传输**：WebSocket 控制/输入 + 二进制视频帧；电脑端和手机端浏览器均使用 **WebCodecs VideoDecoder**（`optimizeForLatency: true`）解码，Canvas 渲染。
- **统一输入事件模型**：键盘、鼠标相对/绝对位移、鼠标按键、滚轮、多指触摸。前端不感知当前注入层。
- **可切换输入层**：
  - **软件层**：`SendInput`（扫描码键鼠）+ Windows Touch Injection（最多 20 触点）。免驱动。
  - **虚拟硬件层**：Interception（内核 HID 过滤器键盘/鼠标）+ vmulti-win11（多指虚拟触摸屏）。用于全屏 DirectX/Unity/Unreal、UAC/Secure Desktop 场景。
  - 支持启动参数 `--controller=auto|software|hardware`，电脑端网页可运行时切换；切换不会中断视频流。
  - 驱动检测失败时友好提示并回退软件层。
- **电脑端网页**：一键开始、开始/停止串流、连接与编码状态、输入层切换、完整键鼠捕获、隐藏本机鼠标指针、被控机光标 overlay、全屏显示电脑画面、画质/帧率实时调节、驱动安装入口。
  - 全屏时调用浏览器 **Keyboard Lock API**，尽可能接管 Windows 键、Meta、Alt、Tab、Esc 等可截获的系统按键，并转发到被控机。
  - 由操作系统底层保留的组合键（如 Alt+Tab、Ctrl+Alt+Del、Win+L）浏览器无法截获；网页全屏界面右上角提供鼠标可点的「退出全屏」。建议使用 Chrome/Edge。
- **手机端网页**：
  - 普通模式：多指触摸直通，支持全屏选择和底部应用式工具栏。
  - 虚拟键盘：呼出手机输入法，发送文本和 Esc/Tab/Enter/方向键等快捷键。
  - 串流设置：画质/帧率实时调整、编码器/输入层/全屏状态查看、驱动提示。
  - 游戏模式：进入动画 + 自动全屏 + 虚拟摇杆/按键/视角区，可增删、拖动、设置形状/大小/透明度/颜色/映射按键，配置保存在 `localStorage`。
  - 独立 Canvas 控件层，不随视频帧重绘；支持触摸多点、横屏、Wake Lock。
- **HTTPS**：自动生成本地根 CA 与 HTTPS 服务端证书。手机通过 HTTP 下载根证书安装后，使用 HTTPS 访问，WebCodecs 才能工作。

## 快速开始

### 1. 环境

- Windows 10 22H2 / Windows 11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/)（本项目在 `10.0.401` 上验证）
- 支持 H.264 硬件编码的 GPU（NVENC / AMF / QSV）与最新驱动
- 手机与电脑在同一局域网
- 浏览器：最新 Chrome / Edge（WebCodecs H.264 解码）

### 2. 构建

```powershell
git clone <this-repo> GlassLink
cd GlassLink

# 构建 C# 应用
dotnet build GlassLink.sln -c Release

# 构建 vmulti 客户端 DLL（需要 VS2022/2026 + C++ 工具）
powershell -NoProfile -ExecutionPolicy Bypass -File native\vmulticlient-shim\build.ps1
```

或者运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
```

`native\vmulticlient-shim\bin\vmulticlient.dll` 会在 C# 构建时自动复制到输出目录的 `native\`。
如果没有安装 vmulti / Interception，程序仍可运行，`auto` 会回退到软件层。

### 3. 启动

```powershell
dotnet run --project src\GlassLink\GlassLink.csproj -c Release -- --controller=auto
```

程序是 **无窗体 WinExe**：不会出现控制台黑框，启动后驻留系统托盘。

托盘右键菜单：

- **打开设置面板**：用默认浏览器打开电脑端网页
- **当前状态**：显示在线客户端数、串流状态、FPS、码率、输入层
- **打开手机端页面**
- **打开日志目录**
- **退出程序**

日志默认位置：

```text
%LOCALAPPDATA%\GlassLink\logs\glasslink.log
```

受限目录不可写时回退到应用程序目录下的 `logs`。

默认：

- 电脑端：<http://localhost:5180/>
- 手机端 HTTP：`http://<LAN-IP>:5180/mobile.html`（用于下载证书/页面）
- 手机端 HTTPS：`https://<LAN-IP>:5443/mobile.html`
- 根证书：`http://<LAN-IP>:5180/cert/root-ca.cer`

手机首次使用：

1. 手机浏览器打开 `http://<LAN-IP>:5180/cert/root-ca.cer`，下载并安装根证书。
   - Android：设置 → 安全 → 加密与凭据 → 安装证书 → CA 证书。
   - iOS：下载描述文件后到 设置 → 已下载描述文件 安装，再到 通用 → 关于本机 → 证书信任设置 中启用完全信任。
2. 用 **HTTPS** 打开 `https://<LAN-IP>:5443/mobile.html`。
3. 在电脑端网页点击「开始串流」。手机等待画面或点击「请求电脑开始串流」。

> 手机通过 LAN IP 使用 HTTP 时不是安全上下文，`VideoDecoder` 会被浏览器禁用。必须安装根证书并使用 HTTPS。
> Windows 本机的 `http://localhost:5180` 属于安全上下文，电脑端网页无需证书。

### 4. 防火墙

```powershell
# 管理员 PowerShell
New-NetFirewallRule -DisplayName "GlassLink HTTP" -Direction Inbound -Protocol TCP -LocalPort 5180 -Action Allow
New-NetFirewallRule -DisplayName "GlassLink HTTPS" -Direction Inbound -Protocol TCP -LocalPort 5443 -Action Allow
```

手机与电脑必须在同一网段，且路由器未开启 AP 隔离。

## 一键安装包

安装包使用 **Inno Setup**。构建：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

输出：

```text
installer\Output\GlassLink-Setup-1.0.0.exe
```

安装包特性：

- 自包含 .NET 10 x64 `GlassLink.exe` 及依赖
- 桌面快捷方式
- 可选开机自启
- 可选输入驱动安装（需要管理员权限和重启）
- 安装目录内提供 `drivers\install-input-drivers.cmd`，可右键「以管理员身份运行」
- 可选离线驱动 payload 放到 `installer\drivers\payload\` 后一起打包

## 输入层选择与回退

启动默认 `auto`：

1. 尝试虚拟硬件层（Interception + vmulti-win11）。
2. 若任一驱动/DLL 缺失或初始化失败，明确记录原因，回退到软件层。
3. 用户显式选择 `hardware` 但不可用时，同样回退软件层，并在网页显示原因。
4. 显式选择 `software` 则直接使用软件层。

电脑端网页的「输入控制器层」下拉框可运行时切换。视频流、WebCodecs 解码器、前端输入协议都不重启。

### 软件层（无驱动）

实现：

- 键盘：`SendInput` + Set-1 扫描码（常见键位完整映射，含扩展键）。
- 鼠标：`MOUSEEVENTF_MOVE` 相对位移、绝对定位、按键、滚轮。
- 触摸：`InitializeTouchInjection` / `InjectTouchInput`，维护触点状态，最多 20 点。

限制（网页和日志中会提示）：

- 全屏独占 DirectX / Unity / Unreal / 反作弊游戏可能忽略 `SendInput`。
- 无法注入 Secure Desktop（UAC 提示）和登录界面。
- 某些受保护内容/高完整性窗口不接受普通完整性注入。

因此**全屏游戏首选虚拟硬件层**。

### 虚拟硬件层

- 键盘/鼠标：Interception 内核 HID 过滤器，扫描码级注入，可作用于 Secure Desktop，并被全屏游戏接受。**不使用 `SendInput`**。
- 触摸：vmulti-win11 虚拟 HID 多指触摸屏，通过 `vmulticlient.dll` 导出函数提交 20 个触点。
- 启动时执行 Interception 设备 ID 学习与零位移热身，随后选择可注入的键盘/鼠标设备槽位。
- 相对鼠标位移用于视角旋转；绝对指针依赖被控机自身光标（GlassLink 还会向浏览器发送宿主光标 overlay 坐标）。

完整驱动安装与验证步骤见 [docs/DRIVERS.md](docs/DRIVERS.md)。

> vmulti-win11 的多指触摸在部分 Windows 版本上可能不稳定。若 `testvmulti.exe /multitouch` 无法产生正确的多指坐标，请使用 MultiTouchVista 兼容驱动作为触摸后备；键盘/鼠标注入仍由 Interception 提供。详见驱动文档。

## 命令行

```text
GlassLink [选项]

--port <5180>                  HTTP 端口
--https-port <5443>            HTTPS 端口
--bind <0.0.0.0>               监听地址
--controller <auto|software|hardware>
--fps <60>                     捕获/编码目标帧率（15-240）
--bitrate <16000000>           H.264 平均码率
--gop <120>                    关键帧间隔
--output <0>                   DXGI 输出序号
--no-https                     仅 HTTP（手机 WebCodecs 通常不可用）
--no-open                      不自动打开电脑端网页
--require-hardware-encoder     无 GPU 硬件编码器时直接失败（默认）
--allow-software-encoder       允许软件编码回退（不满足 GPU 编码要求，仅调试）
--token <token>                可选访问令牌
--https-thumbprint <thumbprint> 使用 CurrentUser\My 中已有证书（例如企业证书）
--help
```

> 自动生成的 HTTPS 证书需要把私钥写入当前用户证书存储；在受限容器/沙箱中可能被拒绝。
> 此时 HTTPS 会被禁用并保留 HTTP 服务。可以换成有权限的账户、手动信任证书，或用
> `--https-thumbprint` 指定 `CurrentUser\My` 中已有私钥的证书。

## 架构

```text
DXGI Desktop Duplication
        │  ID3D11Texture2D (GPU)
        ▼
D3D11 Video Processor (BGRA→NV12, GPU)
        ▼
Media Foundation H.264 Hardware MFT
        │  Annex-B NALU (CPU copy of compressed bytes only)
        ▼
GLV1 binary WebSocket packets
        ▼
browser WebCodecs VideoDecoder (optimizeForLatency)
        ▼
Canvas → VideoFrame draw

browser input (unified InputEvent)
        ▼
WebSocket input message
        ▼
ControllerManager ── software  ── SendInput / Touch Injection
        │
        └────────── hardware  ── Interception / vmulti-win11
```

详细设计：[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
协议：[docs/PROTOCOL.md](docs/PROTOCOL.md)

## 传输路线取舍

本项目选择 **WebSocket + WebCodecs**：

- .NET 10 / Kestrel 对 WebSocket 提供稳定的一等支持，纯 C# 单进程即可交付。
- 视频帧只传输压缩后的 NAL，不做图片轮询；WebCodecs 硬件解码，Canvas 渲染。
- 局域网 5GHz Wi-Fi 下 60fps/16Mbps 量级足够；端到端延迟瓶颈主要在编码/解码/显示。
- 前端通过 `IStreamTransport` 边界与 GLV1 帧格式解耦，后续可增加 WebTransport 实现，复用 WebCodecs 与输入模型。

**WebTransport + WebCodecs** 的弱网/尾延迟优势更明显，但当前 .NET/Kestrel 没有现成的服务端 WebTransport（HTTP/3 Extended CONNECT、QUIC stream/datagram）API；要稳定实现需引入 native MsQuic + HTTP/3 栈或 sidecar，显著增加部署复杂度和风险。因此本版本不把 WebTransport 作为默认路径。
纯 C# WebRTC 栈同样有高风险；没有作为主路线。

## 已验证 / 未验证

在开发机上实际验证：

- .NET 10 构建通过。
- DXGI Desktop Duplication 2560×1440 捕获。
- NVIDIA H.264 Encoder MFT 硬件编码（异步 MFT）持续输出 Annex-B；2560×1440 静态桌面实测约 55fps（目标 60fps），实际帧率取决于捕获/编码负载。
- WebSocket 二进制帧接收、输入层切换 API。
- 前端 JavaScript 语法检查。

受限开发会话中无法验证：

- HTTPS TLS 握手（沙箱拒绝 Windows 证书私钥存储与 Schannel 凭据，HTTP 服务正常）。
- Playwright/Edge 无法在受限会话中启动浏览器，因此 WebCodecs 解码渲染与触摸交互请在真实浏览器手动验证。
- 真实 Interception / vmulti 驱动注入（本机未安装驱动；代码提供检测与回退）。
- 不同 GPU / 手机浏览器组合。

运行前请按文档安装驱动、证书并实际验证。

## 目录

```text
GlassLink.sln
src/GlassLink/
  Program.cs
  App/                 CLI、Host、运行时编排、证书
  Capture/             DXGI 捕获、GPU 颜色转换、光标
  Encoding/            Media Foundation MFT、H.264 工具、CodecAPI
  Input/               统一控制器、按键映射
    Software/          SendInput + Touch Injection
    Hardware/          Interception + vmulti
  Protocol/            输入事件和 JSON 消息
  Streaming/           视频帧、WebSocket 客户端/服务、流会话、传输抽象
  Security/            本地根 CA + HTTPS 证书
  Tray/                WinForms NotifyIcon 托盘宿主
  Logging/             无控制台模式的文件日志
wwwroot/
  index.html           电脑端网页
  mobile.html          手机端网页
  css/ js/ assets/
native/
  vmulticlient-shim/   vmulti 客户端 DLL 源码与构建脚本
  interception/        放置 interception.dll（脚本安装）
scripts/               开发者驱动、验证脚本
installer/             Inno Setup 脚本、驱动安装包、构建脚本
docs/                  驱动、协议、架构文档
```

## 安全说明

- 服务默认监听所有网卡，能在局域网访问。仅在可信网络使用。
- 建议传 `--token=...`；页面/WS/API 需带上 token。手机端 URL 会透传 token。
- 本地根 CA 只用于开发/局域网；根 CA 私钥保存在 `%LOCALAPPDATA%\GlassLink\certs`，不要分发 PFX。
- 输入控制具有完全操作被控机的权限；无鉴权时不要暴露到公网。
- 驱动安装需管理员权限并启用 testsigning；生产部署应使用正式签名的驱动。

## 第三方

- [Vortice.Windows](https://github.com/amerkoleci/Vortice.Windows) — MIT
- [vmulti-win11](https://github.com/xaviersykora/vmulti-win11) — 上游许可证
- [Interception](https://github.com/oblitum/Interception) — 非商业 LGPL 3.0；商业使用需授权
- 详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)
