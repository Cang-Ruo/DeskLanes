# DeskLanes 当前状态

> 首个公开版本 v0.10 的技术参数与验证状态。
> 数据来自源码、配置与发布产物的实测核对。

---

## 1. 身份

| 项 | 值 |
|---|---|
| 项目 | DeskLanes · 一格桌面 |
| 仓库 | https://github.com/Cang-Ruo/DeskLanes |
| 版本 | **v0.10**（首个公开版本，2026-11-21） |
| License | MIT，Copyright (c) 2026 Ruo Chang |
| 技术栈 | C# + WPF + .NET 10（`net10.0-windows`），零第三方依赖 |

## 2. 代码与测试规模

| 项 | 值 |
|---|---|
| 源码文件 | 649 个（`.cs` + `.xaml`） |
| 源码行数 | 29,585 行 |
| 冒烟断言 | 593 项 |
| 工具脚本 | 34 个 Python 探针 / 工具 |

## 3. 构建与安装

| 项 | 值 |
|---|---|
| 发布配置 | .NET 10 + WPF，自包含（win-x64）与便携版两套 |
| 发布产物 | `publish/release/DeskLanes-v0.10-*.zip` + `SHA256SUMS-v0.10.txt` |
| 安装目录 | `%LocalAppData%\Programs\DeskLanes\` |
| 开机自启 | 默认关闭 |
| 桌面图标隐藏 | 默认关闭 |

## 4. 本机配置（settings.json）

| 字段 | 当前值 |
|---|---|
| `Version` | 1 |
| `WindowMode` | 0（悬浮窗） |
| `LayoutMode` | 1 |
| `WindowWidth × Height` | 1180 × 760 |
| `FloatingWindowWidth × Height` | 504.56 × 369.24 |
| `FloatingWindowLeft × Top` | 738.4 / 360.8 |
| `CollapseBallLeft × Top` | 1981 / 976 |
| `CollapseOnLaunch` | true |
| `FloatingHideOnDeactivate` | true |
| `FloatingTopmost` | false |
| `SidebarDock` | 0（左） |
| `SidebarWidth` | 256.61 DIP |
| `SidebarAutoCollapse` | true |
| `SidebarCategoryHeight` | 420 DIP |
| `PanelGridSize` | 20 DIP |
| `MinimizeToTray` | true |
| `Hotkey` | Ctrl+Alt+Space |

## 5. 项目库数据（library.json）

| 项 | 值 |
|---|---|
| 桌面项目 | 71 个 |
| 分组 | 8 个（游戏 6 / 工具 14 / 编码 6 / 音乐 2 / 文档 2 / 网盘 2 / 办公 4 / 浏览器 3） |
| 自由分区 | 8 个 |
| 已忽略路径 | 6 个 |

## 6. 视觉与交互参数

**三种窗口尺寸**：

| 窗口 | 尺寸 (DIP) | 最小 | 底色 |
|---|---|---|---|
| 全屏 | 1180 × 760 | 620 × 420 | `#2620334A` |
| 悬浮窗 | 620 × 430 | 420 × 360 | `#2020334A` |
| 侧栏 | 280 × 760 | 44 × 240 | `#2020334A` |

**悬浮球**：

| 参数 | 当前值 |
|---|---|
| 可视 / 拖动可视 / 命中区 / 渲染窗口 | 44 / 46 / 56 / 76 DIP |
| 长按 / 位移 / 过渡 / Tooltip 延迟 | 350 ms / 6 DIP / 140 ms / 550 ms |
| 主体渐变（Idle） | `#4DF5FBFF` → `#24D9EEFF` → `#36A8D2F0` → `#52B7AFE9` |
| 主体渐变（Hover / Drag） | `#57F5FBFF…#5AB7AFE9` / `#5CF5FBFF…#5EB7AFE9` |
| 边缘 | 珍珠白主轮廓 `#DCEEFF` 0.9 DIP + 左上局部高光 |
| 双轨波浪 | `#F7FCFF` 2.1 DIP / `#BDE8FF` 2 DIP，圆头、平行、三态全亮 |
| 光晕 | 单层 50 DIP 方向渐变，Idle 1.5% / Hover 12% / Drag 18% |
| Tooltip | 浅冰晶底 `#F0E6F2FF`，球下方 `VerticalOffset=26` |
| 单击 / 双击 | 单击弹分类选择窗（250 ms 判定）；双击展开完整悬浮窗 |
| 分类窗定位 | 贴球右缘 + 6 DIP，垂直中心对齐球中心 |

**其它关键常量**：同步防抖 400 ms、兜底重扫 5 min、回前台静置 60 s 重扫、
图标缓存 512、日志 512 KB×3 归档、自由画布 1600×900 DIP、分区 320×240。

## 7. 验证结果

| 项目 | 结果 |
|---|---|
| Release 构建 | 0 警告 / 0 错误 |
| 冒烟 | 593 / 593 |
| 启动探针 | 7 / 7 |
| 端到端交互 | 两次 37 / 37 |
| 收束 / 形态 / 失焦 | 8/8、19/19、9/9 |
| 悬浮球验收 | 13 / 13 |
| 分类窗验收 | 10 / 10 |
| 三模式焦点一致性 | 0.0 / 0.0 / 0.0（逐像素一致） |

内存：启动 337 MB → 切换 339.9 MB → 静置 339.8 MB，稳定无泄漏。

## 8. 文档索引

| 文档 | 内容 |
|---|---|
| `README.md` | 产品首屏：定位、三形态、特性、安装、截图 |
| `docs/RELEASE-AUDIT.md` | 项目详细说明 |
| `docs/PARAMETERS.md` | 完整参数手册 |
| `docs/release-notes-v0.10.md` | Release 说明 |
| `docs/release-verification-v0.10.md` | 发布验收记录 |
| `docs/known-limitations.md` | 已知限制清单 |
| `docs/install-and-uninstall.md` | 安装 / 卸载说明 |
| `docs/post-release-30day-plan.md` | 发布后 30 天行动计划 |
