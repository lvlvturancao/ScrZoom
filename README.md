# 水月清影 ScreenZoom

> 把安卓设备的小屏放大上桌：无线投屏 · 虚拟分辨率放大 · 一机多屏

面向安卓设备（手机、平板、掌机等）的 Windows 投屏工具。基于 [scrcpy](https://github.com/Genymobile/scrcpy) + ADB，把设备侧渲染放大、多虚拟屏实例、参数自动还原、局域网设备发现等能力封装成一套开箱即用的 GUI 工具。

## 功能特性

- **屏幕镜像**：一键镜像设备屏幕，兼容性最好，支持全屏显示
- **虚拟分辨率放大**：通过 `wm size / wm density` 让设备以更高分辨率重绘画面，投到大屏更清晰（而非简单拉伸）
- **多屏实例（虚拟屏电脑模式）**：为每个应用创建独立虚拟显示器与独立窗口，PC 上可并排多个应用；支持 720P / 1080P / 2K 模板与自定义模板
- **设备应用直达**：读取设备已安装应用列表（包名 + 启动入口，本地缓存增量解析），点一下就把应用启动到虚拟屏
- **无线连接**：mDNS 局域网自动发现 ADB over Wi-Fi 设备，首次配对后自动连接，无需记忆 IP/端口
- **参数安全还原**：改过设备分辨率/DPI 后，支持一键还原出厂值，异常断开有兜底
- **虚拟屏生命周期管理**：正常关窗自动释放；残留虚拟屏一键清理
- **中文界面**：开箱即用的中文操作路径与提示文案

## 使用截图

| 投屏设置 | 虚拟屏电脑模式 | 投屏效果 |
| :-: | :-: | :-: |
| ![投屏设置](docs/screenshots/main.png) | ![虚拟屏电脑模式](docs/screenshots/vd.png) | ![投屏效果](docs/screenshots/cast.png) |

## 快速开始

### 直接使用（推荐）

1. 下载发布页的 `shuiyue-x.y.z.zip`，解压到任意目录（路径里有中文也可以）
2. 双击 `水月清影.exe`

压缩包里已经带好投屏引擎（scrcpy 与 adb），不用另外下载或安装任何东西，也不用装驱动。

界面依赖微软的 WebView2 组件：Windows 11 自带；Windows 10 多数已随 Edge 装过。确实缺失时程序会直接说明并给出官方下载入口，装一次即可。

目录结构：

```
你的目录/
├── 水月清影.exe
├── index.html
├── engine/            # 投屏引擎，随包提供
└── *.dll              # WebView2 组件库，随包提供
```

### 自己构建

需要 .NET Framework 4.0+ 的 `csc.exe`（Windows 自带）与 WebView2 引用 DLL（`Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.WinForms.dll`，可从 NuGet 获取）。

```powershell
# 在项目根目录执行
.\build.ps1
```

编译产物为 `水月清影.exe`，前端为纯 HTML（`index.html`），无需打包资源。

## 工作原理

- **画面传输**：scrcpy 在设备端推送临时 server，捕获屏幕/虚拟显示器 → 硬编码 H.264/H.265 → 通过 ADB TCP 隧道回传 → 本机 FFmpeg 解码渲染
- **虚拟分辨率放大**：投屏前用 `wm size` / `wm density` 改设备渲染分辨率，让系统以更高分辨率重绘 UI，画面细节真正提升；投屏结束后自动还原
- **多屏实例**：`scrcpy --new-display` 创建独立虚拟显示器，配合 `--start-app` 把指定应用启动到该虚拟屏；支持多窗口并行
- **无线连接**：扫描局域网 ADB 无线调试服务（mDNS），自动配对、自动发现连接端口

## 已知限制

- 普通第三方 APP 无法静默打开设备的「无线调试」，仍需要用户按系统引导手动开启一次
- 部分应用强制绑定物理主屏，无法在虚拟屏中启动（会自动切回主屏或黑屏）
- 部分安卓系统不支持 `am start --display-id` 追加启动，此时每次「打开」会新开一块虚拟屏
- 个别系统不支持 `display remove` 命令，异常残留的虚拟屏需重启设备清除（正常关窗会自动释放）

## 支持与打赏

开发不易，如果这个软件对你有帮助，可以随手支持一下，codex的额度真的不够用啦，哭哭~

打赏完全自愿，不影响任何功能使用。

![支付宝打赏二维码](alipay_reward_qr.png)

## 许可证

本项目基于 **GPL-3.0** 协议开源，详见 [LICENSE](LICENSE)。

依赖组件与协议：

| 组件 | 协议 |
|---|---|
| [scrcpy](https://github.com/Genymobile/scrcpy) | Apache-2.0 |
| [WebView2](https://developer.microsoft.com/microsoft-edge/webview2/) | 微软专有，仅供运行期使用 |
