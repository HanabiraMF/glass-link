# GlassLink 安装包

使用 **Inno Setup 6/7** 生成 Windows 安装程序。

## 构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

脚本会：

1. 用 `dotnet publish -r win-x64 --self-contained true` 发布 WinExe；
2. 复制 `wwwroot`、托盘图标、native 客户端 DLL、驱动脚本到发布目录；
3. 查找本机 Inno Setup 的 `ISCC.exe`；
4. 若本机没有 Inno Setup，但存在 `installer\innosetup.exe`，则以当前用户静默安装到 `installer\.tools\inno`；
5. 编译输出 `installer\Output\GlassLink-Setup-1.0.0.exe`。

可自定义版本：

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.1.0
```

## 安装包提供

- 自包含 x64 WinExe 与所有依赖。
- 桌面快捷方式、开始菜单快捷方式。
- 可选开机自启（HKCU Run + 启动菜单快捷方式）。
- 可选安装 Interception / vmulti 输入驱动。
- 安装目录 `drivers\install-input-drivers.cmd`，可右键以管理员身份运行。
- 安装程序会保留日志到 `%TEMP%`（Inno Setup 的 Setup Logging）。

## 可选离线驱动 payload

Inno Setup 不支持安全地自动下载第三方内核驱动。若要构建完全离线安装包，在构建前准备：

```text
installer\drivers\payload\Interception\
  install-interception.exe
  ...
  library\x64\interception.dll

installer\drivers\payload\vmulti-win11\
  driver_dist\x64\vmulti.inf
  driver_dist\x64\vmulti.sys
  driver_dist\x64\testvmulti.exe
  driver_dist\cert\WDKTestCert.cer
```

构建脚本会把 `payload` 一起复制进安装包。驱动脚本会优先使用本地 payload；Interception 缺失时会尝试从官方 release 下载。vmulti-win11 没有官方预编译二进制，必须使用自行构建并签名的驱动。

> 安装内核驱动必须管理员权限；vmulti 需要启用 testsigning 并重启。GlassLink 在驱动不可用时自动回退软件层，不阻塞正常串流。

## 安装后

- 启动 `GlassLink.exe`，无控制台窗口，托盘图标出现。
- 右键托盘 →「打开设置面板」。
- 若需要硬件输入层：开始菜单 →「GlassLink 输入驱动安装（管理员）」→ 按提示重启。
- 详见 [../docs/DRIVERS.md](../docs/DRIVERS.md)。

## 签名建议

发布给最终用户前，请用代码签名证书给 `GlassLink.exe` 和安装包签名。驱动需要 WHQL/正式签名，测试签名版本仅适合开发机。
