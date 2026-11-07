# GlassLink 最终交付说明

本目录是可直接交付的完整整理版。所有内容按用途分开，中文目录名对应如下。

## 一、目录说明

| 目录 / 文件 | 内容 |
|---|---|
| `1-源代码` | 完整 C# / Web 前端 / 构建脚本 / 安装器源码 |
| `2-Windows安装包` | 已编译的 `GlassLink-Setup-1.0.0.exe` |
| `3-免安装构建产物` | 已发布的自包含 Windows x64 程序，解压即用 |
| `4-输入驱动安装` | Interception / vmulti 安装脚本与驱动说明 |
| `5-文档` | 协议、架构、驱动文档 |
| `使用说明.md` | 本文件 |

---

# 二、开发模式启动

## 1. 环境要求

- Windows 10 22H2 / Windows 11 x64
- .NET 10 SDK（验证版本 `10.0.401`）
- Visual Studio 2022/2026（仅当需要重新编译 `vmulticlient.dll`）
- NVIDIA / AMD / Intel 支持 H.264 硬件编码的 GPU

## 2. 构建

在 `1-源代码` 目录打开 PowerShell：

```powershell
dotnet build GlassLink.sln -c Release
```

可选：重新编译 vmulti 客户端 DLL（需要 C++ 工具链）：

```powershell
powershell -ExecutionPolicy Bypass -File native\vmulticlient-shim\build.ps1
```

## 3. 启动开发版

```powershell
dotnet run --project src\GlassLink\GlassLink.csproj -c Release -- --controller=auto
```

也可以直接运行：

```text
src\GlassLink\bin\Release\net10.0-windows\win-x64\GlassLink.exe
```

程序是 **WinExe**，不会弹出控制台窗口；启动后驻留系统托盘。

托盘菜单：

- 打开设置面板
- 当前状态（客户端数、FPS、码率、输入层）
- 打开手机端页面
- 打开日志目录
- 退出程序

## 4. 访问地址

默认端口：

- 电脑端：<http://localhost:5180/>
- 手机端 HTTP：`http://<电脑局域网IP>:5180/mobile.html`
- 手机端 HTTPS：`https://<电脑局域网IP>:5443/mobile.html`
- 根证书下载：`http://<电脑局域网IP>:5180/cert/root-ca.cer`

手机首次使用：

1. 手机浏览器打开根证书下载地址并安装 CA；
2. Android 在“设置 → 安全 → 加密与凭据 → 安装证书”中安装；
3. iOS 安装描述文件后，在“通用 → 关于本机 → 证书信任设置”中开启完全信任；
4. 用 HTTPS 打开手机端页面。

> 手机通过局域网 HTTP 访问时不是安全上下文，WebCodecs 会被浏览器禁用；必须使用 HTTPS。

## 5. 日志

```text
%LOCALAPPDATA%\GlassLink\logs\glasslink.log
```

如果该目录不可写，会回退到程序目录下的 `logs\glasslink.log`。

---

# 三、安装包安装

1. 双击 `2-Windows安装包\GlassLink-Setup-1.0.0.exe`；
2. 允许管理员权限；
3. 选择是否创建桌面快捷方式、是否开机自启；
4. 需要硬件输入层时勾选「安装 Interception / vmulti 输入驱动」；
5. 安装完成，启动 GlassLink；
6. 托盘出现图标即表示后台服务已启动。

安装包特性：

- 自包含 .NET 10 x64，无需另装运行时
- 桌面快捷方式
- 可选开机自启
- 可选驱动安装
- 安装目录内包含 `drivers\install-input-drivers.cmd`

---

# 四、输入驱动安装

## 1. 两种输入层

- **软件层**：`SendInput` + Windows Touch Injection，无需驱动。
- **虚拟硬件层**：Interception（键鼠）+ vmulti-win11（虚拟多点触摸），用于全屏游戏 / UAC 安全桌面。
- **混合模式**：只安装 Interception 也能启用硬件键鼠；vmulti 缺失时，触摸单独回退到软件层，不会导致整个硬件层失效。

## 2. 安装方式

优先使用安装包勾选驱动任务；也可以手动。先关闭 GlassLink 托盘程序，然后：

1. 右键 `4-输入驱动安装\install-input-drivers.cmd` →「以管理员身份运行」；
2. 如果要安装到指定程序目录，在命令行传入路径：

```text
install-input-drivers.cmd "C:\Program Files\GlassLink"
```

3. Interception 官方运行库已离线放在：

```text
4-输入驱动安装\payload\Interception\
```

不需要联网下载。

如果 testsigning 已经开启，脚本会直接安装；如果刚开启，脚本会注册一个登录后自动续装的计划任务，重启后继续。

## 3. 签名与重启流程（重要）

驱动使用测试签名，必须按两阶段执行：

1. 首次运行脚本：启用 testsigning、导入测试证书、注册登录后自动续装任务；
2. 重启电脑；
3. 重新登录后，计划任务自动以管理员权限继续安装；
4. 安装完成会删除计划任务。

### 当前建议的下一步

1. 关闭托盘中的 GlassLink；
2. 右键管理员运行安装脚本并传入程序目录；
3. 重启；
4. 启动 GlassLink，在电脑端网页把输入层切到「虚拟硬件层」；
5. 此时键盘/鼠标应走 Interception；触摸仍会回退软件层/鼠标，直到安装 vmulti。

验证 Interception：

```powershell
sc.exe query interception
dir "<安装目录>\native\interception.dll"
```

注意：

- **Secure Boot 必须关闭**，否则 testsigning 无法生效；
- 安装后需要重启；
- Windows 可能显示“测试模式”水印，这是正常的；
- 正式发布建议购买正式驱动签名证书或使用商业签名虚拟触摸驱动。

## 4. vmulti 驱动包（已内置，微软签名）

本次已把 `driver-VMulti.Driver` 集成到：

```text
4-输入驱动安装\payload\vmulti-win11\driver_dist\
```

其中的 `vmulti.sys` 和 `pentablethid.cat` 已用微软 WHQL 证书签名，`Get-AuthenticodeSignature` 返回 `Valid`。安装时脚本会用随包的 `devcon.exe install vmulti.inf pentablet\hid` 创建虚拟 HID 设备，而不是只执行 `pnputil`。因此 vmulti 本身通常不再依赖测试模式。

安装脚本会自动识别：

- 已签名 catalog：直接安装；
- 未签名/测试签名驱动：才启用 testsigning 并走重启续装流程。

设备创建后可验证：

```powershell
Get-PnpDevice | Where-Object FriendlyName -match 'Pentablet|vmulti'
```

> 如果该签名包在你的 Windows 11 版本上不兼容，再回退到自行构建的 vmulti-win11（旧流程仍保留）。

需要的文件：

```text
vmulti.inf
vmulti.sys
testvmulti.exe
vmulticlient.dll（可选）
WDKTestCert.cer
```

安装完成后可运行：

```text
testvmulti.exe /multitouch
```

确认虚拟多点触摸设备。

## 5. 当前电脑的触摸限制

本机 `SM_MAXIMUMTOUCHES = 0`，没有触摸数字化仪，Windows 的 `InjectTouchInput` 会返回错误 87。

已实现兜底：

- **单指触摸 → 鼠标点击 / 拖动**，无触摸屏也可用；
- **多指手势**必须依赖 vmulti 虚拟触摸屏。

---

# 五、音频转发

## 当前实现（无需驱动）

- PC 系统声音通过 WASAPI Loopback 捕获；
- 以 PCM16 通过 WebSocket `GLA1` 包发送；
- 浏览器用 Web Audio 播放；
- 首次点击或触摸页面后启用音频（浏览器自动播放限制）；
- 音频与视频独立队列，慢网络下丢旧音频包，不阻塞视频。

## 是否需要音频驱动

不需要。WASAPI Loopback 直接抓取默认播放设备的系统混音。

如果需要“独立音频设备”或按应用捕获，有两种路线：

1. **进程回环捕获**（推荐，无驱动）：Windows 10 2004+ 支持按进程捕获音频，可扩展为只转发指定应用；
2. **虚拟音频设备驱动**（PortCls/AVStream 或 APO）：创建 “GlassLink Audio” 端点，需要额外内核驱动与签名，开发量和签名成本高，目前未集成。

当前“转发到浏览器”的目标用系统回环已经可以完成；安装 VB-CABLE 等签名虚拟声卡也可以作为默认播放设备配合本程序使用。

---

# 六、常见问题

## 托盘没有图标

查看日志：

```text
%LOCALAPPDATA%\GlassLink\logs\glasslink.log
```

常见原因：端口 5180/5443 被占用。可用参数修改：

```powershell
GlassLink.exe --port=5180 --https-port=5443
```

## 手机没有画面

- 确认手机和电脑在同一局域网；
- 确认使用 HTTPS 地址；
- 确认已安装并信任根证书；
- 检查 Windows 防火墙是否允许 5180/5443。

## 触摸没有反应

- 无触摸屏电脑：单指应走鼠标兜底，检查是否已开始串流；
- 多指：需要 vmulti 驱动和虚拟触摸设备；
- 查看日志中 `SM_MAXIMUMTOUCHES`、`Touch Injection`、`vmulti` 相关行。

## 硬件输入层不可用

日志会显示：

```text
找不到 interception.dll
vmulti_connect 失败：未找到 vmulti-win11 虚拟 HID 设备
```

说明驱动尚未安装或 DLL 未复制到 `native\`。按第四节安装并重启。

## 音频没有声音

- 先点击/触摸一次页面；
- 检查 Windows 默认播放设备是否有声音输出；
- 查看日志 `Audio loopback started`；
- 手机媒体音量是否开启。

---

# 七、已知限制

- 浏览器无法截获 `Alt+Tab`、`Ctrl+Alt+Del`、`Win+L` 等系统保留组合键；
- 无触摸屏时多指手势不可用；
- vmulti 测试签名需要关闭 Secure Boot 并重启；
- 驱动安装需要管理员权限，无法在普通用户权限下完成；
- 正式分发建议对 `GlassLink.exe` 和安装包做代码签名。
