# 第三方代码与依赖

| 组件 | 固定版本 / 提交 | 用途 | 许可证 |
|---|---|---|---|
| Avalonia | 12.1.4 | 跨平台桌面界面 | MIT |
| Avalonia DataGrid | 12.1.2 | 表格控件；该控件独立发布版本 | MIT |
| .NET | SDK 10.0.402 / Runtime 10.0.13 | 运行时和标准库 | MIT 及其第三方说明 |
| System.Management / System.CodeDom | 10.0.0 | Windows 进程和脚本入口识别 | MIT |
| ProxyBridge | `d697164b836f0d3f897128774d3cb72e54770e74` | Windows 转发内核 | MIT |
| Microsoft Detours | 4.0.1 | 托管启动与 DNS API 拦截 | MIT |
| WinDivert | 2.2.2-A | WFP 网络驱动和接口 | LGPLv3 或其发行版提供的其他授权选项 |
| SkiaSharp | 3.119.4 | 界面绘制及原生图形依赖 | MIT 及包内第三方说明 |
| HarfBuzzSharp | 8.3.1.3 | 文字排版 | MIT 及包内第三方说明 |
| ANGLE | 2.1.27548.20260419 | Windows 图形后端 | 包内 BSD 及第三方说明 |
| Inter | Avalonia.Fonts.Inter 12.1.4 随附版本 | 字体 | SIL Open Font License 1.1 |
| MicroCom.Runtime | 0.11.6 | COM 互操作 | MIT |
| Tmds.DBus.Protocol | 0.94.1 | Linux 桌面集成依赖 | MIT |

来源：

- [Avalonia](https://github.com/AvaloniaUI/Avalonia)
- [ProxyBridge](https://github.com/InterceptSuite/ProxyBridge)
- [Microsoft Detours](https://github.com/microsoft/Detours)
- [WinDivert](https://github.com/basil00/WinDivert)

保留的原始许可证位于 `native/vendor` 和 `licenses`，发布目录包含对应副本及 .NET 第三方说明。Microsoft.Extensions 和 System.* 托管依赖使用 .NET 的许可证。WinDivert 使用独立动态库和未经修改的签名驱动；完整 Windows 包内置两者。WinDivert v2.2.2 的上游源码归档随发布包提供于 `licenses/WinDivert-source-v2.2.2.zip`，保留原始许可证。

对 ProxyBridge 的修改由 `scripts/patch-native.ps1` 记录：接入共享规则裁决、增加独立 DNS 调度、调整不支持 UDP 和缺失代理时的失败行为、保留旧代理代次以供既有连接使用。上游原有 GUI、安装器和规则管理 API 不作为本项目界面或策略来源。

可选的在线安装入口固定下载官方 `WinDivert-2.2.2-A.zip`，预期 SHA-256 为 `63cb41763bb4b20f600b6de04e991a9c2be73279e317d4d82f237b150c5f3f15`，同时执行 Windows 驱动签名校验。该摘要与 [Caspian 的原始发行依赖记录](https://github.com/Iman/caspian/blob/main/third_party/windivert/README.md) 一致；本机官方包曾被安全软件拦截，尚未成功读取并独立复核该 ZIP 的摘要。完整包直接使用下述本机已有组件，不依赖该下载入口。

内置组件在 2026-10-16 从本机已安装的 `D:\AppStore\ProxyBridge` 复制，未修改原版文件或安全设置。x64 `WinDivert.dll` 的 SHA-256 为 `c1e060ee19444a259b2162f8af0f3fe8c4428a1c6f694dce20de194ac8d7d9a2`；`WinDivert64.sys` 为 `8da085332782708d8767bcace5327a6ec7283c17cfb85e40b03cd2323a90ddc2`。DLL 本身未带 Authenticode 签名；驱动的 Windows 签名验证为 Valid，版本为 2.2，签名者为成都密思听科技有限公司。程序缺少内置文件时仍可从已注册驱动路径校验并复用同一兼容库。

WinDivert 源码归档来自 [官方 v2.2.2 标签](https://github.com/basil00/WinDivert/tree/v2.2.2)，动态库和驱动按上游 LGPLv3 选项随附许可证及源码。
