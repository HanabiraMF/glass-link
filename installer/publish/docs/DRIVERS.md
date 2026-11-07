# 输入驱动安装与验证

GlassLink 的虚拟硬件层需要两个独立组件：

| 功能 | 组件 | 作用 |
|---|---|---|
| 键盘 / 鼠标 | [Interception](https://github.com/oblitum/Interception) | 内核态 HID 过滤器，扫描码注入，可穿透 UAC、Secure Desktop、全屏 DirectX 游戏 |
| 多指触摸 | [vmulti-win11](https://github.com/xaviersykora/vmulti-win11) | 虚拟 HID 多指触摸屏，20 触点，`vmulticlient.dll` 导出 API |

两者都安装并可用后，虚拟硬件层才会初始化成功；否则 GlassLink 会显示诊断原因并回退到软件层。

> 驱动安装涉及管理员权限、内核驱动、测试签名与重启。请在测试机/开发机上操作。
> Interception 非商业用途采用 LGPL 3.0；商业用途需向作者获取商业授权。
> vmulti 驱动使用自签名测试证书，不适合生产分发。

---

## 0. 准备工作

- Windows 10 22H2 / Windows 11 x64
- 管理员 PowerShell
- Visual Studio 2022/2026（Desktop development with C++）
- Windows Driver Kit（WDK）与 Windows SDK，用于编译 vmulti-win11
- 允许重启

检查当前测试签名状态：

```powershell
bcdedit /enum "{current}" | findstr /i "testsigning secureboot"
```

---

## 1. 启用测试签名

vmulti-win11 的驱动使用 WDK 测试证书签名，必须启用 testsigning：

```powershell
# 管理员 PowerShell
scripts\enable-testsigning.ps1
# 或手动：
bcdedit /set testsigning on
```

重启 Windows。桌面右下角会出现「测试模式」水印。

- 如果启用了 Secure Boot，固件可能拒绝加载测试签名驱动。需要关闭 Secure Boot，或使用正式签名驱动。
- 安装完成后可以用 `bcdedit /set testsigning off` 关闭测试模式，但 vmulti 将无法加载。

---

## 2. 安装 vmulti-win11

### 2.1 构建驱动与测试程序

```powershell
git clone https://github.com/xaviersykora/vmulti-win11.git
cd vmulti-win11
# 使用 VS 的 x64 Native Tools Command Prompt
build.bat
```

产物通常在：

```text
vmulti-win11\driver_dist\x64\
  vmulti.inf
  vmulti.sys
  vmulticlient.lib
  testvmulti.exe
vmulti-win11\driver_dist\cert\WDKTestCert.cer
```

### 2.2 信任测试证书

```powershell
# 管理员 PowerShell
Import-Certificate -FilePath .\vmulti-win11\driver_dist\cert\WDKTestCert.cer `
  -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\vmulti-win11\driver_dist\cert\WDKTestCert.cer `
  -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

或者在证书管理器中把 `WDKTestCert.cer` 导入：
- 本地计算机 → 受信任的根证书颁发机构
- 本地计算机 → 受信任的发布者

### 2.3 安装驱动

使用 GlassLink 脚本：

```powershell
# 管理员 PowerShell
scripts\install-vmulti.ps1 -DriverDist C:\path\to\vmulti-win11\driver_dist\x64
```

脚本会：
1. 检查 testsigning；
2. 导入测试证书；
3. `pnputil /add-driver vmulti.inf /install`；
4. 构建 `vmulticlient.dll`；
5. 提示重启与验证步骤。

也可以手动安装：

```powershell
pnputil /add-driver .\driver_dist\x64\vmulti.inf /install
# 或设备管理器：添加过时硬件 → 手动选择 → 从磁盘安装 → 选择 vmulti.inf
```

重启后确认设备：

```powershell
Get-PnpDevice | Where-Object { $_.FriendlyName -match 'vmulti|touch' } |
  Format-Table Status, Class, FriendlyName, InstanceId
```

### 2.4 验证多指坐标映射与 Win32k 触摸链路

**必须执行：**

```powershell
cd C:\path\to\vmulti-win11\driver_dist\x64
.\testvmulti.exe /multitouch
```

预期：程序注入 4 个同时按下的触点，并让触点移动，最后抬起。可在支持触摸的软件
（如 Paint、Windows 触摸诊断工具、或用 Win32 `WM_TOUCH` 日志程序）中观察坐标。

坐标约定：

- vmulti 触点坐标范围为 `0..32767`，映射到虚拟桌面。
- GlassLink 把浏览器中的归一化触点坐标先映射到被捕获显示的像素区域，再映射到虚拟桌面。
- 如果触点在多显示器/非主显示器上偏移，检查 GlassLink 捕获的 `--output` 和 Windows 显示排列。
- 如果 `testvmulti.exe /multitouch` 只能看到一个点、坐标乱跳或驱动蓝屏，说明 vmulti 多指触摸在当前系统不可靠。

**备选方案：MultiTouchVista 系驱动**

如果 vmulti 多指触摸不可靠：

1. 卸载 vmulti，重启。
2. 安装 MultiTouchVista 兼容 HID 触摸驱动（或其它经过验证的 Windows 10/11 多指虚拟触摸驱动）。
3. 该类驱动通常不提供 `vmulticlient.dll` 同名 API。需要在
   `src/GlassLink/Input/Hardware/` 下新增一个触摸后端适配器，把统一 `InputEvent.Touch`
   翻译成该驱动的 IOCTL/HID 报告。
4. 键盘/鼠标继续使用 Interception，不受影响。
5. 通过电脑端网页的「输入控制器层」切换到虚拟硬件层验证。

GlassLink 当前内置的是 vmulti-win11 后端；MultiTouchVista 作为驱动级触摸备选路线保留。

### 2.5 构建 vmulticlient.dll

vmulti 上游把客户端编译为静态 `vmulticlient.lib`，GlassLink 需要 DLL：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File native\vmulticlient-shim\build.ps1
```

产物：

```text
native\vmulticlient-shim\bin\vmulticlient.dll
```

C# 构建时复制到：

```text
src\GlassLink\bin\<Configuration>\net10.0-windows\win-x64\native\vmulticlient.dll
```

也可以用 `install-vmulti.ps1` 自动构建并复制到 `native\vmulticlient.dll`。

---

## 3. 安装 Interception

### 3.1 下载与安装

官方仓库：<https://github.com/oblitum/Interception>

脚本自动安装（管理员 PowerShell）：

```powershell
scripts\install-interception.ps1
```

或手动：

1. 从 releases 下载 ZIP。
2. 管理员命令行进入解压目录。
3. 执行：

```text
install-interception.exe /install
```

4. 将 x64 的 `interception.dll` 复制到：
   - `native\interception\interception.dll` 或
   - 应用输出目录中的 `native\interception.dll`。

5. 重启。

### 3.2 验证

```powershell
sc.exe query interception
Test-Path native\interception\interception.dll
```

启动 GlassLink，电脑端网页输入层诊断应出现：

```text
Interception 设备 ID 学习完成：键盘=1，鼠标=11，枚举到 N 个硬件 ID。
Interception 使用内核 HID 过滤器注入扫描码，可穿过 UAC / Secure Desktop ...
```

### 3.3 设备 ID 学习与热身

Interception 最多暴露 10 个键盘槽位和 10 个鼠标槽位。GlassLink 启动时会：

1. 枚举所有有效槽位；
2. 调用 `interception_get_hardware_id` 查询硬件 ID；
3. 向候选鼠标槽位发送一次 **零位移相对鼠标热身报告**；
4. 选择第一个接受报告的槽位用于后续注入；
5. 键盘选择第一个可用/有硬件 ID 的槽位。

这就是驱动文档中所说的「设备 ID 学习 / 热身」。若热身失败，日志会提示驱动未就绪；
GlassLink 会回退到软件层。

### 3.4 UAC / Secure Desktop

Interception 能把输入送入 Secure Desktop，因此可以在 UAC 同意界面、登录界面等场景工作。
注意：

- 运行 GlassLink 的进程本身需要足够的权限才能注入到安全桌面；通常需要管理员身份。
- 部分受保护进程/反作弊仍可能阻止注入。
- 不要依赖此能力进行未授权的系统操作。

---

## 4. 验证 GlassLink 的虚拟硬件层

```powershell
# 管理员 PowerShell
scripts\verify-input-layers.ps1 -TestVMultiPath C:\path\to\driver_dist\x64\testvmulti.exe
```

检查：

1. `bcdedit` 显示 `testsigning Yes`。
2. `sc.exe query interception` 存在且 running。
3. vmulti 触摸设备出现在设备管理器。
4. `native\vmulticlient.dll` 和 `native\interception\interception.dll` 存在。
5. `testvmulti.exe /multitouch` 显示多点移动。
6. 启动 GlassLink：

```powershell
dotnet run --project src\GlassLink\GlassLink.csproj -c Release -- --controller=hardware
```

7. 电脑端网页输入层显示「虚拟硬件层」，诊断包含 vmulti/Interception 就绪信息。
8. 手机普通模式多指触摸能同时控制多个触点；
9. 游戏模式摇杆/按键/视角区可用。

---

## 5. 常见问题

### vmulti 驱动加载失败

- 确认已启用 testsigning 并重启。
- 确认证书在「受信任的根证书颁发机构」和「受信任的发布者」。
- Secure Boot 必须关闭或使用正式签名。
- `pnputil /enum-drivers` 查看 vmulti 是否安装成功。
- 设备管理器查看有无黄色感叹号。

### 多指触摸失败

- 用 `testvmulti.exe /multitouch` 先行验证。
- 若只有单指/触点抖动，使用 MultiTouchVista 兼容驱动后备。
- 确认 Windows 触摸键盘/触控设置未被组策略禁用。

### Interception 找不到

- `interception.dll` 架构必须是 x64。
- 必须重启后才能加载内核驱动。
- 确认安装脚本以管理员运行。
- 如启用了 Memory Integrity/HVCI，某些旧驱动会被阻止。

### 虚拟硬件层回退软件层

电脑端网页的「回退原因」会给出具体原因。可在命令行日志中查找：

```text
Virtual hardware layer unavailable: ...
```

GlassLink 不会崩溃，键鼠/触摸会改用软件层。

### 安全桌面/全屏游戏仍然无效

- 软件层本身不支持这些场景，这是预期行为。
- 切到虚拟硬件层并确认 Interception 驱动已加载。
- 反作弊可能仍然阻止任何注入。
