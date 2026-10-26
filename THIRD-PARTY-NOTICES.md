# 第三方组件许可声明

本项目以 GPL-3.0 发布（见 [LICENSE](LICENSE)）。仓库内随附的第三方二进制均取自上游官方发行版，**未做任何修改**；各组件仍适用其自身许可证。

| 组件 | 仓库内文件 | 版本 | 许可证 |
|---|---|---|---|
| [scrcpy](https://github.com/Genymobile/scrcpy) | `engine/scrcpy.exe`、`scrcpy-server`、`scrcpy-menu.ps1` 等 | 5.0.1 | Apache-2.0 |
| [Android Debug Bridge](https://android.googlesource.com/platform/packages/modules/adb/) | `engine/adb.exe`、`AdbWinApi.dll`、`AdbWinUsbApi.dll` | 随 scrcpy 5.0.1 发布 | Apache-2.0 |
| [FFmpeg](https://ffmpeg.org/)（libavcodec / libavformat / libavutil / libswresample） | `engine/avcodec-63.dll`、`avformat-63.dll`、`avutil-61.dll`、`swresample-7.dll` | libavcodec 63.1.102 | LGPL-2.1+（未启用 GPL 组件） |
| [SDL3](https://github.com/libsdl-org/SDL) | `engine/SDL3.dll` | 3.4.18 | Zlib |
| [libusb](https://github.com/libusb/libusb) | `engine/libusb-1.0.dll` | 1.0.30 | LGPL-2.1 |
| [WebView2 SDK](https://learn.microsoft.com/microsoft-edge/webview2/) | `Microsoft.Web.WebView2.Core.dll`、`Microsoft.Web.WebView2.WinForms.dll`、`WebView2Loader.dll` | 1.0.4258.31 | BSD 风格（微软） |

许可证全文见 [LICENSES](LICENSES/) 目录；scrcpy 的 Apache-2.0 原文同时保留在 `engine/LICENSE.txt`。

## LGPL 组件说明

FFmpeg 与 libusb 以独立 DLL 形式动态链接，未与主程序静态合并：

- 未修改上游源代码；
- 可自行替换 `engine/` 下的同名 DLL 并重新运行；
- 对应源码可从上游获取：[FFmpeg 源码](https://ffmpeg.org/download.html)、[libusb 源码](https://github.com/libusb/libusb/releases)。

## WebView2

仓库只包含编译用的 WebView2 SDK 程序集与加载器，不含 WebView2 运行时。运行时由用户系统提供（Windows 11 自带，Windows 10 通常随 Edge 安装），本项目不随包分发。

WebView2 SDK 许可要求再分发时保留版权声明与免责条款，全文见 [LICENSES/WebView2.txt](LICENSES/WebView2.txt)。