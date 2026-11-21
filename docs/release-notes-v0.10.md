# DeskLanes v0.10

**发布日期**：2026-11-21
**标签**：`v0.10`
**定位**：A lightweight, glass-inspired desktop workspace for Windows — organize your desktop without moving your files.

相对 v0.2.0 的变化集中在**悬浮入口**与**发布准备**，三种窗口模式的数据共用机制、不移动桌面文件的红线均未改变。

## 悬浮球：珍珠冰晶玻璃球

- 清透冰晶主体（中心约 30% alpha，桌面可透出），一条珍珠白主轮廓，取消多重同心圆；
- 双轨波浪改为纯色清晰描边（珍珠白 2.1 DIP / 冰蓝 2 DIP），三态始终清晰；
- 克制的冰晶柔光：Idle 1.5% / Hover 12% / Drag 18%，贴在球缘、不做第二道圆环；
- Tooltip 改浅冰晶样式并移到球下方，不遮挡球体。

## 悬浮球点击逻辑

- **单击**：弹出一行式分类选择窗，**贴着小球右侧展开并跟随小球移动**；点分类直接展开悬浮窗并定位到该分组。
- **双击**：展开完整悬浮窗（原行为）。
- **长按拖动**：350 ms + 6 DIP，位置保存与多显示器边界钳制不变。

## 发布准备

- 清理仓库与历史中的个人信息（Windows 登录名、私人路径与壁纸名）；
- LICENSE 署名确定为 Ruo Chang（MIT）；
- 配置写入加服务层与存储层两把锁，消除并发保存竞争；
- 端到端测试的配置恢复改为原子写入，并在测试后清理自己制造的损坏备份；
- README 与截图按当前视觉更新，统一版本号 v0.10。

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
