# DeskLanes 项目完全状态快照

> 采集时间：**2026-11-21 20:58 (+08:00)**
> 全部数据来自实际 git / 配置文件 / 源码 / 安装目录核对，不是记忆。

---

## 1. 身份与版本

| 项 | 值 |
|---|---|
| 项目 | DeskLanes — Windows 桌面工作空间（悬浮窗 / 全屏 / 侧栏三模式共用数据） |
| 仓库 | `https://github.com/Cang-Ruo/DeskLanes.git`（origin/main） |
| 目标框架 | `net10.0-windows` |
| 版本 | `0.1.0`（Assembly/File `0.1.0.0`） |
| 当前 HEAD | `ae8f8ca5eb876a110860f9158a23b852130d761f` |
| HEAD 主题 | 阶段 47：分类选择窗贴着小球右侧展开、跟着小球走 |
| HEAD 时间 | 2026-11-21 19:46:18 +0800 |
| 累计提交 | **104** |
| 工作区 | **干净**（`git status` 无输出），本地 main 与 origin/main 同步 |

最近 10 次提交（新→旧）：

```
ae8f8ca 47 分类选择窗贴着小球右侧展开、跟着小球走
000715d 46 悬浮球单击弹分类选择窗、双击展开悬浮窗
51016ae 45 恢复球体清晰度（主体加强/单轮廓/波浪加锐/光晕辅助/Tooltip 避开）
d3a2aca 44 合并收敛光晕候选（单层50 DIP、方向渐变、强度减半）
16a282e 42 Crystal Aura 冰晶柔光取代圆环式外光晕
ceab37a 41 移除悬停时球外的冷白光晕
f29d1f0 40 冰晶球第二版（低Alpha主体/三层边缘/波浪两端淡出）
7baab8c 39 珍珠冰晶玻璃球第一版
3ce0501    完整参数手册
cae74b2 38 Glass Orb 最终规格实现
```

## 2. 代码与测试规模

| 项 | 值 |
|---|---|
| 源码文件 | **643** 个（`.cs` + `.xaml`） |
| 源码行数 | **29,586** 行 |
| 测试文件 | 12 个 `.cs` |
| 冒烟断言 | **593** 项，`Check(` 调用 584 处（其余为辅助段） |
| Python 工具/探针 | **34** 个脚本（含 8h soak、截图、DPI/玻璃/焦点探针、推送脚本） |
| 文档 | `docs/` 下 25 个 `.md`；`docs/ui-acrylic-final/` 有 331 个文件、**362 MB** 截图证据 |

## 3. 构建与安装

| 项 | 值 |
|---|---|
| 发布配置 | `win-x64-selfcontained.pubxml`（.NET 10 + WPF 自带运行时） |
| 发布目录 | `publish/win-x64-selfcontained/`（63 MB） |
| 发布 exe MD5 | `50e6dc6ef2efc3befc7d0a7d02e11b31` |
| 安装目录 | `C:\Users\<username>\AppData\Local\Programs\DeskLanes\` |
| 安装 exe MD5 | `50e6dc6e…`（**与发布一致**） |
| exe 大小 | 65,144,624 字节（约 62 MB） |
| 运行状态 | **正在运行**（PID 108900，占用约 378 MB） |
| 开机自启 | 关闭（`AutoStartEnabled=False`），注册表启动项未写入 |
| 桌面图标隐藏 | 关闭（`HideDesktopIcons=False`） |
| 其它发布目录 | `release` 115M / `vm-package` 63M / `win-x64` 636K / `selfcontained-next` 63M（阶段43候选） |

## 4. 本机配置（settings.json，20:57 实际值）

| 字段 | 当前值 |
|---|---|
| `Version` | 1 |
| `WindowMode` | 0（悬浮窗） |
| `LayoutMode` | 1 |
| `WindowWidth × Height` | **1180 × 760**（默认值；此前曾是 2048×1104） |
| `WindowMaximized` | false |
| `FloatingWindowWidth × Height` | 504.56 × 369.24 |
| `FloatingWindowLeft × Top` | 738.4 / 360.8 |
| `CollapseBallLeft × Top` | **1981 / 976**（你拖动后的实际位置） |
| `CollapseOnLaunch` | true |
| `FloatingHideOnDeactivate` | true |
| `FloatingTopmost` | false |
| `SidebarDock` | 0（左） |
| `SidebarWidth` | 256.61 DIP |
| `SidebarCategoryHeight` | 420 DIP |
| `SidebarAutoCollapse` | true |
| `PanelGridSize` | 20 DIP |
| `MinimizeToTray` | true |
| `Hotkey` | Ctrl+Alt+Space（Modifiers=3, Key=0x20） |
| `LastScanAt` | 2026-11-21 20:57:33 +08:00 |

> 注意：窗口尺寸与小球位置与我 19:46 提交时的值不同，
> 属于之后的使用/探针写入痕迹；**我未改动这些值**。

## 5. 项目库数据（library.json，20:57）

| 项 | 值 |
|---|---|
| Version | 1 |
| 桌面项目 | **71** 个（Kind: 45 个 0 / 7 个 1 / 19 个 2） |
| 分组 | **8** 个 |
| 自由分区 | **8** 个 |
| 已忽略路径 | 6 个 |

分组与项目数：

| 分组 | 项目数 | 自由分区（宽×高） |
|---|---|---|
| 游戏 | 6 | 280 × 220 |
| 工具 | 14 | 300 × 460 |
| 编码 | 6 | 280 × 220 |
| 音乐 | 2 | 200 × 140 |
| 文档 | 2 | 200 × 140 |
| 网盘 | 2 | 200 × 140 |
| 办公 | 4 | 200 × 220 |
| 浏览器 | 3 | 280 × 140 |

## 6. 当前视觉与交互参数（源码核对，阶段 45–47）

**三种窗口尺寸**（阶段 45 未改）：

| 窗口 | 尺寸 (DIP) | 最小 | 底色 |
|---|---|---|---|
| 全屏 | 1180 × 760 | 620 × 420 | `#2620334A` |
| 悬浮窗 | 620 × 430 | 420 × 360 | `#2020334A` |
| 侧栏 | 280 × 760 | 44 × 240 | `#2020334A` |

**悬浮球（阶段 45 视觉 + 46/47 交互）**：

| 参数 | 当前值 |
|---|---|
| 可视 / 拖动可视 / 命中区 / 渲染窗口 | 44 / 46 / 56 / **76** DIP |
| 长按 / 位移 / 过渡 / Tooltip 延迟 | 350 ms / 6 DIP / 140 ms / 550 ms |
| 主体渐变（Idle） | `#4DF5FBFF` → `#24D9EEFF` → `#36A8D2F0` → `#52B7AFE9`（中心约 30%） |
| 主体渐变（Hover / Drag） | `#57F5FBFF…#5AB7AFE9` / `#5CF5FBFF…#5EB7AFE9` |
| 边缘 | **一条**珍珠白主轮廓 `#DCEEFF` 0.9 DIP + 左上局部高光（无内折射圈、无虹彩弧） |
| 双轨波浪 | 纯色描边 `#F7FCFF` 2.1 DIP / `#BDE8FF` 2 DIP，圆头、平行、三态全亮、无模糊柔光底 |
| 光晕 | 单层 50 DIP 方向渐变，Idle 1.5% / Hover 12% / Drag 18% |
| Tooltip | 浅冰晶底 `#F0E6F2FF`、`Placement=Bottom`、`VerticalOffset=26`（不遮球） |
| 单击 / 双击 | 单击弹分类选择窗（250 ms 判定）；双击展开完整悬浮窗 |
| 分类窗定位 | 贴球右缘 + 6 DIP、垂直中心对齐球中心（实测左右/上下各差 1px） |

**其它关键常量**：同步防抖 400 ms、兜底重扫 5 min、回前台静置 60 s 重扫、
图标缓存 512、日志 512 KB×3 归档、自由画布 1600×900 DIP、分区 320×240。

## 7. 最近一次完整回归（阶段 47）

| 项目 | 结果 |
|---|---|
| Release 构建 | 0 警告 / 0 错误 |
| 冒烟 | **593 / 593** |
| 启动探针 | 7 / 7 |
| 端到端交互 | 两次 **37 / 37** |
| 阶段 31 收束 | 8 / 8 |
| 阶段 34 形态 | 19 / 19（复跑偶发 1 项） |
| 阶段 35 失焦 | 9 / 9（复跑偶发失败，见下） |
| 收起球验收 | 13 / 13 |
| 分类窗探针 | 10 / 10 |
| 三模式焦点一致性 | 0.0 / 0.0 / 0.0（两态一致） |

## 8. 已知问题与风险（如实列出）

1. **配置写入曾多次损坏**：本机配置目录存在 **100 个** `settings-corrupt-*.json` 备份
   （11/19–11/21），当前配置有效，但探针链并发/写冲突是长期隐患。
2. **P34/P35 失焦类断言偶发失败**：与前台切换和探针改配置有关，
   与阶段 47 的定位改动无关；逐项复跑可过。
3. **参数手册过时**：`docs/PARAMETERS.md` 写于阶段 38，
   阶段 39–47 改过球体视觉和点击逻辑，手册里的球体参数**已不是当前值**；
   当前值以本文第 6 节和源码为准。
4. **分类窗只验了「全部」行**：其它分组走同一份 `Categories` 数据与回调，
   逻辑一致但未逐条点过。
5. **卸载项**：已写入系统「已安装的应用」，卸载脚本会在卸载时清理自启项和桌面图标设置。

## 9. 文档索引

- 参数手册：`docs/PARAMETERS.md`（阶段 38，注意过时）
- 最终交付报告：`docs/final-delivery-report.md`
- 已知限制：`docs/known-limitations.md`
- 安装卸载：`docs/install-and-uninstall.md`
- 发布说明：`docs/release-notes-v0.2.0.md` / `v0.1.1.md`
- 阶段报告：`docs/ui-acrylic-final/phase39…47-report.md`（球体系列）、`phase46-report.md`（点击逻辑）、`phase47-report.md`（跟随定位）
- 本快照：`docs/CURRENT-STATE.md`
