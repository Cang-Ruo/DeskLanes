# DeskLanes v0.10

**发布日期**：2026-11-21
**标签**：`v0.10`（首个公开版本）
**定位**：A lightweight, glass-inspired desktop workspace for Windows — organize your desktop without moving your files.

DeskLanes 的首个公开版本。三种窗口模式共用同一份数据，始终遵守「不移动桌面文件」的红线。

## 珍珠冰晶悬浮球

- 清透冰晶主体（桌面可透出），一条珍珠白主轮廓；
- 双轨波浪为纯色清晰描边（珍珠白 2.1 DIP / 冰蓝 2 DIP），三态始终清晰；
- 克制的冰晶柔光：贴在球缘、不做第二道圆环；
- Tooltip 为浅冰晶样式，位于球下方不遮挡球体。

## 悬浮球点击逻辑

- **单击**：弹出一行式分类选择窗，**贴着小球右侧展开并跟随小球移动**；点分类直接展开悬浮窗并定位到该分组。
- **双击**：展开完整悬浮窗。
- **长按拖动**：350 ms + 6 DIP 防误触，位置自动保存并适配多显示器边界。

## 三种窗口形态

| 形态 | 用途 | 入口 |
|---|---|---|
| 全屏工作空间 | 分类导航与自由分区 | 托盘 / 模式切换 |
| 悬浮窗 | 快速查找与打开 | `Ctrl+Alt+Space` |
| 侧边栏 | 贴边常驻 | 模式切换 |

## 下载

| 文件 | 说明 |
|---|---|
| `DeskLanes-v0.10-selfcontained.zip` | **推荐**。自带 .NET 10 运行时，解压即用 |
| `DeskLanes-v0.10-portable.zip` | 机器上已装 .NET 10 桌面运行时时使用 |

安装：`install.ps1`；卸载：`uninstall.ps1`。程序未做数字签名，Windows SmartScreen 可能提示，选择「更多信息 → 仍要运行」。安装包 SHA256 见 Release 附件。

## 系统要求

Windows 11（推荐）/ Windows 10 x64，.NET 10（自包含版无需预装）。

## 已知限制

无自动更新、未做 ARM64 原生构建、无国际化。详见 `docs/known-limitations.md`。
