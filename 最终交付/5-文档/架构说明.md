# 架构与实现说明

## 1. 进程与线程

- **WinForms 托盘主线程（STA）**：运行托盘图标/菜单的 WinForms 消息循环；Kestrel 在后台运行，程序是 `WinExe`，无控制台窗口。
- **Kestrel HTTP/WebSocket 线程池**：页面、状态 API、WebSocket 收发。
- **Capture/Encode 专用线程**：最高优先级；创建 D3D11 设备和 Media Foundation MFT；执行捕获、颜色转换、编码。
- **WebSocket 每客户端发送线程**：控制消息优先；视频采用 latest-frame-wins 邮箱（一个待发关键帧 + 一个最新 P 帧），绝不阻塞抓屏/编码线程。

捕获线程与 WebSocket 完全解耦。控制器层切换只修改 `ControllerManager` 内部引用，不触碰流会话或视频管线。

### 采集/编码节流

`IDXGIOutputDuplication::AcquireNextFrame` 本身就是事件等待。捕获循环使用 500ms
超时在静止桌面等待，因此没有 `Thread.Sleep` 限帧轮询。当用户设置目标 FPS 时，
循环比较当前帧与上一编码帧的时间间隔，直接跳过整帧编码，不 sleep 主循环。
静态桌面不更新时不产生编码帧，CPU/GPU 处于等待状态。

## 2. GPU 视频路径

```text
IDXGIOutputDuplication::AcquireNextFrame
  → ID3D11Texture2D (BGRA)
  → CopyResource 到稳定中间纹理
  → ID3D11VideoProcessor::VideoProcessorBlt (BGRA → NV12, BT.709 limited)
  → MFCreateDXGISurfaceBuffer
  → IMFTransform::ProcessInput (hardware H.264 MFT)
  → ProcessOutput (compressed sample copy)
  → Annex-B + GLV1
```

- 捕获帧不 `Map`/`Unmap`。
- 颜色转换由 D3D11 Video Processor 完成，不经过 CPU。
- 编码器通过 `MFCreateDXGIDeviceManager` + `MFT_MESSAGE_SET_D3D_MANAGER` 使用同一个 D3D11 device。
- D3D11 device 启用 `ID3D11Multithread::SetMultithreadProtected(true)`。
- 编码输出样本很小（压缩后的 NAL），复制到 CPU 是网络发送所必需的，不属于捕获回读。

### 动态画质/FPS

- FPS：`StreamSession.ApplyVideoSettings` 修改 `_fps`，采集线程在下一帧采用新的丢帧间隔，不重启流。
- 码率：`MediaFoundationEncoder.UpdateBitrate` 通过 `ICodecAPI` 更新 mean/max bitrate，在下一帧编码前生效。
- 分辨率不动态改变，避免重建 DXGI/编码管线；画质预设调整码率和帧率。

### 同步与异步 MFT

硬件 MFT 通常是异步的：

1. 读取 `MF_TRANSFORM_ASYNC`，设置 `MF_TRANSFORM_ASYNC_UNLOCK=TRUE`；
2. 查询 `IMFMediaEventGenerator`；
3. 等待 `METransformNeedInput`；
4. `ProcessInput`；
5. 等待 `METransformHaveOutput`；
6. **每个 HaveOutput 事件只调用一次 ProcessOutput**（NVIDIA MFT 在重复调用时会返回 `0x8000FFFF`）。

### 输出类型协商

NVIDIA 硬件 MFT 对输出媒体类型的附加属性较敏感。GlassLink 使用最小但完整的输出类型：

- Major=Video，Subtype=H.264
- bitrate、frame size、frame rate、progressive

编码参数（CBR、GOP、B 帧=0、low latency、强制关键帧）通过 `ICodecAPI` 设置；不支持的项目只记录日志，不阻断启动。

### 编码器回退

- 默认只枚举 `MFT_ENUM_FLAG_HARDWARE`，没有硬件 MFT 时串流启动失败并显示原因。
- 只有显式传入 `--allow-software-encoder` 才尝试软件 MFT；该模式仅用于调试/兼容，不满足项目的 GPU 编码要求。
- 状态中的 `HardwareAccelerated` 会明确展示当前是否 GPU。

## 3. 输入控制器

统一接口：

```csharp
public interface IInputController
{
    string LayerId { get; }
    CaptureBounds Bounds { get; }
    bool Inject(InputEvent evt);
    Task<ControllerActivation> ActivateAsync(CaptureBounds bounds, CancellationToken ct);
}
```

`ControllerManager`：

- 默认 `auto`：先试硬件；失败记录 diagnostics 并回退软件。
- 显式 `hardware` 不可用时也回退，但网页显示真实原因。
- 显式 `software` 直接初始化软件层。
- 切换流程：构造/初始化新层 → 锁内交换引用 → 释放旧层。视频线程不受影响。
- 注入时只锁引用读取，避免长持有锁。

### 软件层

- `SendInput` 扫描码键盘；
- 相对/绝对鼠标、按键、滚轮；
- `InitializeTouchInjection`/`InjectTouchInput` 维护最多 20 个联系人；
- 局限在代码注释、诊断和 UI 中明确说明。

### 虚拟硬件层

- Interception：API 调用均在 `InterceptionNative`，设备 ID 枚举/热身/发送在 `InterceptionSession`。
- vmulti：`vmulticlient.dll` 的 P/Invoke 在 `VMultiNative`；多指槽位分配、释放、归一化坐标在 `HardwareInputController`。
- 所有触摸批次通过 vmulti 每 2 个触点分包上报。
- 绝对鼠标坐标映射到虚拟桌面；相对位移用于游戏视角。
- 初始化失败不影响视频流，控制器管理器回退。

## 4. 托盘与无控制台宿主

`Program.Main` 标记 `[STAThread]`。启动流程：

1. 初始化文件日志；
2. 启动 Kestrel / 串流运行时；
3. 创建 WinForms `TrayApplicationContext`；
4. `Application.Run(tray)` 进入托盘消息循环。

托盘菜单中的「打开设置面板」打开电脑端网页；「当前状态」显示客户端数、串流状态、
FPS、码率、输入层；「退出程序」结束消息循环，主流程停止 Kestrel、串流和输入层。
因为没有控制台，诊断日志写入 `%LOCALAPPDATA%\GlassLink\logs\glasslink.log`。

## 5. 浏览器端

`common.js` 提供：

- `GlassLinkClient`：WebSocket 重连、心跳、JSON 控制、WebCodecs 配置/解码、Canvas 绘制、光标消息。
- `RemoteCursor`：把宿主光标坐标映射到 letterbox 后的视频区域。

视频输出回调不直接绘制，而是保留最新 `VideoFrame`，用 `requestAnimationFrame` 合并绘制，降低主线程压力。

手机游戏控件：

- 独立 `<canvas id="controlCanvas">`。
- 只有触摸/编辑/尺寸变化时才 `requestAnimationFrame` 重绘，不随 WebCodecs 帧循环重绘。
- 指针事件按 pointerId 复用，支持多指同时按住按钮 + 摇杆 + 视角区。
- 配置存于 `localStorage`。

### 电脑端全屏与系统按键接管

`desktop.js` 的「全屏显示电脑画面」按钮只把 `#stage` 视频容器全屏化，隐藏网页外壳与侧栏。
进入全屏后会：

1. 自动开启键鼠捕获并聚焦视频容器；
2. 在安全上下文中调用 `navigator.keyboard.lock()`（不支持时退回显式键列表）；
3. 将浏览器可截获的 Meta/Windows、Alt、Tab、Esc 等按键通过统一输入事件发往被控机；
4. 退出全屏时调用 `navigator.keyboard.unlock()`，并释放所有按住的键。

Keyboard Lock 无法拦截由操作系统在浏览器之前处理的组合键，例如 Alt+Tab、Ctrl+Alt+Del、
Win+L；这是浏览器安全模型的限制。

## 6. HTTPS 与证书

`LocalCertificate`：

- 首次生成一个 10 年本地根 CA（优先保存在 `%LOCALAPPDATA%\GlassLink\certs`；不可写时回退到应用目录 `certs`）。
- 每次启动生成新的 2 年服务器证书，SAN 自动包含 localhost、主机名、所有局域网 IP。
- 根证书导出为 `.cer`，供手机安装。
- Kestrel 同时监听 HTTP 与 HTTPS。
- 也可以用 `--https-thumbprint` 指定 `CurrentUser\My` 中已有私钥的证书。
- 自动证书需要写当前用户私钥存储；受限沙箱中可能失败并禁用 HTTPS。

WebCodecs 需要安全上下文：手机只能通过 HTTPS（或 localhost）使用。

## 7. 已知限制

- H.264 编码参数使用最小媒体类型以保证 NVENC 兼容；GOP/CBR/B 帧通过 CodecAPI 控制，部分驱动可能忽略个别设置。
- 依赖桌面合成；全屏独占游戏如果绕过 Desktop Duplication，会捕获不到（需改为 WGC 备选）。
- 光标是浏览器 overlay，不是把光标合成进 GPU 帧。
- 当前没有音频。
- 不支持多显示器同时串流，一次选择一个 DXGI output。
- WebSocket/TCP 在严重丢包下会有队头阻塞；局域网优先有线/5GHz。
