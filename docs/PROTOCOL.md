# GlassLink 线协议

WebSocket 端点：

```text
ws(s)://<host>:<port>/ws?client=desktop|mobile[&token=...]
```

- `client=desktop`：电脑端网页（也是默认角色）
- `client=mobile`：手机端网页
- 可选 `token`：启动时 `--token` 设置后必需

## 控制消息（文本 JSON）

### 客户端 → 服务端

#### hello

```json
{ "type": "hello", "role": "desktop", "clientName": "电脑浏览器" }
```

#### 启动/停止串流

```json
{ "type": "stream.start" }
{ "type": "stream.stop" }
```

#### 切换输入控制器层

```json
{ "type": "controller.set", "layer": "auto" }
```

`layer`：`auto` | `software` | `hardware`。

#### 输入事件（统一模型）

所有输入都放在 `event` 中发送：

```json
{ "type": "input", "event": { ... } }
```

键盘：

```json
{
  "type": "keyboard",
  "action": "down",
  "code": "KeyW",
  "key": "w",
  "virtualKey": 87
}
```

鼠标相对移动（游戏视角旋转）：

```json
{ "type": "mouseMove", "mode": "relative", "dx": 8.5, "dy": -2.0 }
```

鼠标绝对定位（普通远程桌面；x/y 归一化到被捕获输出）：

```json
{ "type": "mouseMove", "mode": "absolute", "x": 0.42, "y": 0.61 }
```

鼠标按键 / 滚轮：

```json
{ "type": "mouseButton", "button": "left", "action": "down" }
{ "type": "mouseWheel", "wheelDelta": -120, "wheelDeltaX": 0 }
```

多指触摸：

```json
{
  "type": "touch",
  "action": "down",
  "touches": [
    { "id": 1, "x": 0.25, "y": 0.5, "phase": "down" },
    { "id": 2, "x": 0.75, "y": 0.4, "phase": "move" }
  ]
}
```

- `phase`：`down` | `move` | `up` | `cancel`
- `action=cancel` 或 `phase=cancel` 会释放所有触点
- 服务端会 clamp 坐标、限制最多 20 触点、限制单次位移范围

这些事件**与使用哪一层控制器无关**。前端没有 software/hardware 分支。

#### 动态画质/帧率

```json
{ "type": "video.settings", "fps": 60, "bitrate": 16000000 }
```

服务端动态调整编码码率和丢帧间隔，不重启视频流。变更通过 `videoConfig` 和
`status` 广播。

#### 虚拟键盘文本

```json
{ "type": "input", "event": { "type": "text", "key": "Hello, GlassLink!" } }
```

软件层用 Windows Unicode SendInput；硬件层把字符解析为当前键盘布局的扫描码 +
Shift/Ctrl/Alt 组合，再由 Interception 注入。无法映射的字符会被忽略。

#### 关键帧请求 / 心跳 / 状态

```json
{ "type": "keyframe" }
{ "type": "ping", "t": 1730000000000 }
{ "type": "status" }
```

### 服务端 → 客户端

- `hello`：连接建立，含客户端 ID、服务端版本、完整 `status`
- `status`：完整状态（串流、控制器、编码、客户端数、stats）
- `streamState`：`idle|starting|streaming|recovering|stopping|error`
- `controllerStatus`：当前输入层、请求层、硬件可用性、回退原因、诊断
- `videoConfig`：WebCodecs 配置（codec、宽高、fps、编码器等）
- `videoStats`：fps / Mbps / 帧数 / 字节数
- `cursor`：宿主光标归一化坐标与可见性
- `pong`：心跳回复
- `error`：可读错误

示例：

```json
{ "type": "videoConfig", "video": {
  "codec": "avc1.4D4033",
  "width": 2560, "height": 1440, "fps": 60,
  "bitrate": 16000000,
  "encoderName": "NVIDIA H.264 Encoder MFT",
  "hardwareAccelerated": true,
  "format": "annexb"
} }
```

```json
{ "type": "cursor", "cursor": { "visible": true, "x": 0.48, "y": 0.73 } }
```

## 视频二进制帧

WebSocket binary message，固定 24 字节头 + Annex-B H.264 payload：

| 偏移 | 长度 | 内容 |
|---|---|---|
| 0 | 4 | ASCII `GLV1` |
| 4 | 1 | flags；bit0=关键帧 |
| 5 | 1 | codec；1=H.264 Annex-B |
| 6 | 2 | 保留 |
| 8 | 8 | timestamp，微秒，little-endian |
| 16 | 4 | duration，微秒，little-endian |
| 20 | 4 | payload 长度 |
| 24 | N | H.264 Annex-B access unit |

浏览器：

1. 解析头；
2. `EncodedVideoChunk({type: key?'key':'delta', timestamp, duration, data})`；
3. 提交给 `VideoDecoder`；
4. 输出 `VideoFrame` 绘制到 Canvas。

`videoConfig` 使用：

```js
await decoder.configure({
  codec,
  codedWidth,
  codedHeight,
  optimizeForLatency: true,
  hardwareAcceleration: 'prefer-hardware',
  avc: { format: 'annexb' }
});
```

## HTTP API

- `GET /api/status` → 状态 JSON
- `POST /api/stream/start`
- `POST /api/stream/stop`
- `POST /api/controller` body `{ "layer": "hardware" }`
- `GET /cert/root-ca.cer` → 本地根证书
- `GET /` → 电脑端
- `GET /mobile.html` → 手机端

设置 `--token` 后，API 需要 query `?token=` 或请求头 `X-GlassLink-Token`。

## 传输抽象

`IStreamTransport`（`src/GlassLink/Streaming/IStreamTransport.cs`）定义了
control/video 广播边界。当前实现为 `WebSocketHub`。视频包格式与控制消息均不是
WebSocket 特有，未来 WebTransport 实现可复用捕获、编码、输入模型和前端解码器。
