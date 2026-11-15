## 下载

| 文件 | 大小 | 说明 |
| --- | --- | --- |
| `DSH-Orbit-Setup.exe` | ~94 MB | **推荐**。安装器：可选安装位置 / 便携模式 / 快捷方式，写入「应用和功能」；**不需要预装 .NET** |
| `DSH-Orbit.exe` | ~47 MB | 绿色单文件，自带 .NET 运行时，拷走就能跑 |
| `DSH-Orbit-lite.exe` | ~1.5 MB | 最小体积，需要 [.NET 10 桌面运行时](https://dotnet.microsoft.com/download/dotnet/10.0) |

三者都还需要系统装有 **WebView2 运行时**（Win10/11 一般自带）。

安装器静默部署：`DSH-Orbit-Setup.exe /S /MODE=user /D=D:\Apps\DSH-Orbit`
卸载：`DSH-Orbit.exe --uninstall` 或「设置 → 应用和功能」。

> 本项目为 AIGC（AI 生成内容），使用前请自行评估风险。
