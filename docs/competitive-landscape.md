# DeskSpace 竞品调研（2026-10-28）

> 调研目的：确认本项目是否已有同类实现，判断差异化空间，并为阶段 9（收口与发布）提供依据。
> 调研范围：GitHub、Gitee、微软商店、V2EX / 少数派 / 知乎 / 今日头条等国内外论坛与媒体。
> 结论一句话：**同类项目很多，赛道已相当拥挤；头部项目 DeskBox 完成度显著领先。本项目的差异化不在"有没有人做"，而在"三形态共用一套数据"。**

## 1. 结论速览

| 判断 | 说明 |
|---|---|
| 是否首创 | **否**。桌面整理 / 图标分区类开源工具至少十余个，2026 年仍在新增 |
| 最强势竞品 | **DeskBox**（Tianyu199509），2026-06 建仓，4 个月 7.6k ★ |
| 国内同类 | GeekDesk、Zbox、Layouter、Desk Tidy、QuickLink、Nimble、DeskPanel、desktop_box |
| 国外同类 | DeskFrame、Paddock、SentientDesk、PecoFence、Portals、Palisades、SideSlide |
| 商业竞品 | Stardock Fences（转订阅）、Nimi Places、TAGO Fences、iTop Easy Desktop、KoBar（免费开源侧边栏） |
| 本项目真实差异 | 悬浮窗 / 全屏工作空间 / 侧边栏**三种形态共用同一套项目、分类、配置数据** |
| 本项目明显差距 | 无自动更新、无 ARM64、多显示器未验证、Win10 未验证、单语言、无附加小组件 |

## 2. 竞品清单

### 2.1 GitHub · 开源

| 项目 | 技术栈 | 热度 | 许可 | 形态 | 与本项目重合点 |
|---|---|---|---|---|---|
| **DeskBox**（Tianyu199509） | C# / WinUI 3 / .NET 10 Native AOT / Rust native shell | **7.6k ★**（2026-06 建仓） | GPL-3.0 | 桌面 widget 分区 | 真实文件夹映射、**不移动文件**、本地优先、Mica/Acrylic、多显示器独立布局、13 语言、x64+ARM64 |
| **DeskFrame**（PinchToDebug） | C# / WPF | 532 ★ | MIT | 桌面 frames | 即时搜索、正则过滤、虚拟桌面感知、多语言（含简体中文） |
| **Paddock**（HappyDane） | C# / .NET 8 / WPF | 活跃 | MIT | 桌面 zones | 排序、布局快照（corrals）、智能间距、原生右键菜单、Roll-Up |
| **SentientDesk**（Ryan Malacina） | WPF / .NET 8 | 小 | 开源 | fences | 隐藏原生桌面图标、Velopack 自动更新、多显示器虚拟坐标 |
| **PecoFence** | Rust | 已上架 Microsoft Store | 开源 | 玻璃栅栏 | 自动分类规则、标签页、快捷键、栅栏可隐藏 |
| **Layouter**（VrezenStrijder） | WPF | V2EX 有帖 | Apache-2.0 | 桌面分区 | 始终在桌面层、自动对齐吸附、托盘 |
| **Zbox**（evachxji） | — | V2EX 有帖 | MIT | 桌面格子 + 悬浮面板 | **图标自动隐藏/恢复、原位置记忆还原**——与本项目高度重合；作者明说想法来自腾讯桌面助手 |
| **DeskPanel**（QinJiaEn） | **.NET 10 WPF + Mica** | 小 | — | Alt+\` 呼出收纳面板 | 与本项目悬浮窗模式最接近；分类管理、一键收集桌面、归还桌面、Mica 毛玻璃 |
| **desktop_box**（kof2000git） | .NET 8 WPF | 小 | — | 一键整理 + 标签盒子 | **中英双语 README**、不移动文件、隐藏桌面图标、原生右键菜单 |
| **KoBar** | 免费开源 | — | MIT | **侧边栏 dock** | 边缘停靠、工作区、插件、多语言含中文；但内容是笔记/插件，**不管理桌面项目** |
| **SideSlide** | 免费 | — | — | 隐藏式工作空间面板 | 与本项目悬浮窗模式概念相近 |
| **Portals / Palisades** | .NET 8 | 小 | 开源 | 文件夹面板 / Fences 克隆 | 规则排序、每栏透明度 |

### 2.2 Gitee · 开源

| 项目 | 技术栈 | 形态 | 备注 |
|---|---|---|---|
| **Desk Tidy**（ksun22515） | Flutter | 收纳盒 + 主面板 | 磨砂玻璃、热区唤醒、磁吸自动隐藏、桌面图标隐藏/显示；有独立子项目 `desk_tidy_box` |
| **QuickLink**（ggmenu） | — | 图标整理 + 软件启动器 | 3 ★，区域标记分组 |
| **GeekDesk** 镜像（dotnetchina） | WPF / .NET 4.7.2 | 快速启动 + 桌面管理 | 国内知名，深度集成 Everything 全盘搜索 |
| **Nimble**（WPFDevelopersOrg） | WPF | 快捷管理 | 悬浮/嵌入、边缘隐藏、托盘换肤 |

### 2.3 商业 / 免费闭源

Stardock Fences（已转年费订阅，用户流失主因）、Nimi Places（donationware，文件夹缩略图）、
TAGO Fences（轻量克隆）、iTop Easy Desktop（一键自动整理，含推广）、
Winstep Nexus / RocketDock / Seelen UI / MyDockFinder（dock 与 shell 类）、Flare（Launchpad 类，Flutter）。

## 3. 头部项目 DeskBox 详解（最值得研究的对手）

| 维度 | DeskBox |
|---|---|
| 星标 / 建仓 | 7.6k ★，2026-06-11 建仓，GitTrend 全站 #7,907、C# 类 #170 |
| 技术栈 | C# + WinUI 3 + Windows App SDK 2.5.1 + .NET 10 Native AOT + **Rust 原生 Shell 层** |
| 许可 | GPL-3.0（早期版本 MIT，不溯及既往） |
| 平台 | Windows 10/11，最低 build 19041；**x64 与 ARM64 双架构全 AOT** |
| 核心机制 | 文件格子背后是**真实文件夹**（映射不移动）；拖入"收纳格子"则是真实移动（README 明确区分） |
| 多显示器 | **主打能力**：格子绑定到显示器硬件标识，每种连接组合独立布局；拔屏后宽限期重定位，插回精确还原；换屏/改分辨率/改缩放按比例重排；设置页有 Displays 页可识别屏幕 |
| 附加组件 | 待办（截止日期/提醒/重复/Markdown/附件）、速记、搜索、天气、音乐控制、胶囊模式 |
| 国际化 | 13 种语言，资源键与占位符覆盖一致 |
| 安全 | 1.5.6 做过一轮"文件安全加固"审计：恢复不可未经确认应用、删除走回收站、网络盘不绕过回收站、附件删除只碰托管副本、移动失败保留格子并提示 |
| 性能 | 作者公开称全部格子打开时内存稳定在约 50 MB，并专门做了 AOT 内存曲线测量 |
| 治理 | 单人维护，**暂不合并外部 PR**，但欢迎 Issue / Discussion |
| 短板 | 安装包未签名（建议校验 sha256）、依赖 Windows App Runtime、仅 Windows、无云同步/协作 |

**可借鉴的点**（不必抄功能，但发布前应参考其公开承诺的严谨度）：
文件安全审计清单、明确区分"映射 vs 移动"、多显示器绑定硬件标识、公开的已知限制、性能基线。

## 4. 命名冲突（**已于 2026-10-29 更名为 DeskLanes**）

> 决策：仓库名定为 **DeskLanes**（GitHub 与软件圈均干净，"泳道/分区"语义准，与虚拟桌面完全区隔）。
> 中文显示名定为 **一格桌面**（写进 README 标题与窗口副标题）。
> 已改动：程序集名、RootNamespace、Company/Product、目录与 csproj、解决方案文件、
> 配置目录 `%LocalAppData%\DeskLanes\`（带一次性旧配置迁移）。详见提交 `def38b6`。

原名字 **DeskSpace** 已被多方占用，且语义全在"虚拟桌面"方向，与本项目"桌面整理"错位：

| 占用方 | 性质 |
|---|---|
| 百度百科 `deskspace` 词条 | 老牌**商业虚拟桌面**软件，3D 立方体切换，最新 v1.5.8.14（2024-12） |
| `deskspaceapp.com` | macOS 虚拟桌面管理器（商业） |
| GitHub `brodeeC/DeskSpace` | Tauri/Rust 任务式文件管理，跨平台 |
| GitHub `djekanovic/Deskspace` | Windows 空间窗口管理器，无限 2D 画布（daily.dev 报道过） |
| GitHub `angelparakon931/Deskspace` | 0 ★ 的 SEO 占位仓库 |

搜索会被"虚拟桌面"语义淹没，SEO 与口碑积累成本极高。

### 候选名查重结果（2026-10-28）

| 候选 | GitHub | 软件圈 | 结论 |
|---|---|---|---|
| DeskZones | 未占用 | **微软商店有同名商业软件**（$4.99，桌面图标分区工具） | ❌ 排除 |
| DeskNest | 未占用 | **已被占用**：微软商店「DeskNest 桌面巢」（Rust+WebView2 顶部坞条）+ 小众软件论坛推荐 + Gumroad 上的 DeskNest Pro 付费版 | ❌ 排除 |
| ClearDesk | 被印度用户 `cleardesk` 占用 | — | ❌ 排除 |
| DeskTidy | 未占用 | Gitee 有 `desk_tidy`（Flutter 桌面整理） | ⚠️ 撞车 |
| GridDesk | 未占用 | `griddesk.com` 域名挂牌出售（$189） | ⚠️ 若在意官网则不合适 |
| **DeskLanes** | **未占用** | 未见同名软件 | ✅ **可用**（推荐） |
| **NookDesk** | **未占用** | 未见同名软件 | ✅ 可用 |

命名规律：含 `Desk` + 常见整理词（Zones / Nest / Tidy / Clear / Grid）的组合大多已被占。
建议往"Desk + 较冷门的空间词"方向找，或直接起中文名配一个英文仓库名。

## 5. 本项目定位

### 5.1 仍然成立的差异点

1. **三形态共用一套数据**：悬浮窗 / 全屏工作空间（分类导航 + 自由分区画布）/ 侧边栏，共用同一份项目、分类、配置。
   绝大多数竞品是单一形态；侧边栏这一格玩家最少，且现存的 KoBar 是笔记/插件向，**不管理桌面项目**。
2. **零第三方依赖、纯 P/Invoke**：对比 DeskFrame（WPF UI + WindowsCommunityToolkit）、DeskPanel（System.Drawing.Common）等，体积小、行为可控。
3. **全屏下的自由分区画布**：把 Fences 那套"分区"放进全屏工作区，而不是叠在桌面上。

### 5.2 明确落后项（写入已知限制，不宣称具备）

无自动更新 · 无 ARM64 · 多显示器/混合 DPI 未验证 · Win10 未验证 · 仅中文 · 无待办/搜索等附加组件 ·
长时驻留资源占用无数据 · 大量项目（200+）性能无数据。

### 5.3 建议的传播口径

不要说"Fences 替代品"（这个标签已被 DeskBox、PecoFence、Paddock、Layouter 占满），
改说**"一个窗口，三种形态，同一份桌面数据"**，主打"侧边栏形态管理桌面项目"这个几乎没人做的组合。

## 6. 对阶段 9 的影响

- **9.1 命名**：必须先定，改名牵涉程序集名、窗口标题、HKCU 卸载项、开始菜单快捷方式、配置目录 `%LocalAppData%\DeskSpace\`（需决定迁移还是保持）。
- **9.4 补测**：按"公开发布"标准，需补多显示器/混合 DPI、Win10 兼容、长时驻留资源、200+ 项目性能。
- **9.5 物料**：README 需明确"映射 vs 移动"语义、列出已知限制、给出性能基线——参照 DeskBox 的公开严谨度。
