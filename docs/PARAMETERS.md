# DeskLanes 完整参数手册

> 全部数值取自当前源码（阶段 38）与你本机 `settings.json` 的实际值。
> 单位约定：**DIP** = 设备无关像素（WPF 布局单位，96 DPI 下 1 DIP = 1 px）；
> **px** = 物理像素。颜色为 ARGB，括号内是 alpha 百分比。

---

## 1. 三种窗口模式的尺寸

| 窗口 | 默认宽 × 高 (DIP) | 最小 (DIP) | 底色 | 圆角 | 内边距 | 顶最 | 进任务栏 | 透明方式 |
|---|---|---|---|---|---|---|---|---|
| 全屏工作空间 `MainWindow` | 1180 × 760 | 620 × 420 | `#2620334A`（15% 深蓝） | — | 外 Grid `Margin=14` | 否 | 否 | 分层 `AllowsTransparency=True`，系统 backdrop=NONE + 窗口级模糊 tint `0x06FFFFFF`（2.4% 白） |
| 悬浮窗 `FloatingWindow` | **620 × 430** | **420 × 360** | `#2020334A`（12.5% 深蓝） | 12 | 14 | 否 | 否 | 分层，根 Border 用 `BrushShellGlass` |
| 侧边栏 `SidebarWindow` | 280 × 760 | 44 × 240 | `#2020334A`（12.5%） | 左侧 `12,0,0,12`；右侧 `0,12,12,0` | 10 | **是** | 否 | 分层，根 Border 用 `BrushShellGlass` |

- 全屏磁贴统一缩到 **80%**（`TileScale=0.8`，仅全屏；悬浮窗/侧栏保持 1.0）。
- 悬浮窗八方向缩放热区：上 5 / 下 6 / 左 6 / 右 6 DIP，四角 16×16，
  右下角 17×17 带可见双斜线标记；拖动条高 17 DIP；热区颜色 `#01000000`（1/255 黑，肉眼透明但保证分层窗口鼠标命中）。
- 侧栏收起宽度固定 **44 DIP**；分类区高度可调 **60–420 DIP**（默认 150）。

### 全屏布局数学

- 画布：`WorkspacePanel` 1600 × 900 DIP，分区默认 320 × 240，最小 120 × 80，默认坐标 (48,48)。
- 左右留白目标 **2 cm = 75.59 px**（按 96 DPI 换算），再除以实际 DPI 缩放得到 DIP。
- 画布缩放 = `min(scaleX, max(scaleY × 1.12, 由 2cm 边距反推的缩放))`。
- 搜索框宽度 = 右边缘对齐工具栏提示文字右边缘，钳制在 `[300, 窗口宽-28]` DIP；
  全屏搜索框字号 12、内边距 `10,2`；悬浮窗搜索框字号 15、内边距 `12,10`。

---

## 2. 收束指示器

### 2.1 悬浮球 Glass Orb（阶段 38）

| 参数 | 值 |
|---|---|
| 可视直径 | **44 DIP** 静止/悬停；**46 DIP** 拖动（缩放 1.05） |
| 透明热区 | **56 DIP**（≥ 规格要求的 48） |
| 窗口 `Width/Height` | 56 DIP（物理像素由 `SetWindowPos` 按 DPI 换算，125% 屏 = 70 px） |
| 悬停缩放 / 拖动缩放 | 1.04 / 1.05 |
| 过渡动画 | 140 ms，`CubicEase EaseOut`，透明度 + ScaleTransform |
| Tooltip 延迟 | 550 ms（Idle 无提示 / Hover「打开 DeskLanes」/ 拖动「拖动以移动」） |
| 长按门槛 | **350 ms** + 位移 **6 DIP** 才进入拖动（两个条件同时满足） |
| 拖动光标 | `SizeAll`；静止/悬停 `Hand` |
| 拖动范围 | 钳制在小球所在显示器工作区内 |
| 位置字段 | `CollapseBallLeft` / `CollapseBallTop`（物理像素，沿用，不新增字段） |
| 捕获丢失 | `LostMouseCapture`：拖过则保存位置，绝不误触发点击 |

**三态渐变玻璃（RadialGradientBrush，GradientOrigin 0.30,0.24 / Center 0.42,0.38 / Radius 0.82）**

| 层 | 起点 Offset 0 | 中段 | 底 Offset 1 |
|---|---|---|---|
| 静止 | `#8FC4E8` | `#527FA9` @0.42 | `#294E72` |
| 悬停 | `#C6E9FF` | `#6FA3C9` @0.42 | `#315D85` |
| 拖动 | `#BDE6FF` | `#3489C2` @0.45 | `#24567A` |

**其它视觉件**

| 元素 | 参数 |
|---|---|
| 外光晕 | `#BDE6FF`，`BlurEffect Radius=5`，Margin -6，描边 1.2，悬停 opacity 0.55 / 拖动 0.85 |
| 阴影 | `#0A1E30` opacity 0.42，`BlurEffect Radius=5`，Margin 3 |
| 内高光 | `#BDE6FF`，描边 0.6，Margin 2.5，opacity 静止 0.35 / 悬停 0.6 / 拖动 0.75 |
| 描边 | 静止 `#9EC4DE` 1 DIP；激活 `#BDE6FF` 1.2 / 拖动 1.5 |
| 双轨图标 | Viewbox 22×22，画布 24×24；主线 `M3,15 C7,15 8,8 12,8 C15,8 16,6 19,3` 冷白 `#F1FAFF`；副线 `M3,20 C8,20 9,13 14,13 C17,13 18,11 21,8` 浅冰蓝 `#BDE6FF`；线宽 2.1；圆头 |

### 2.2 侧栏收起 / 全屏收起

| 项 | 值 |
|---|---|
| 侧栏边栏可见宽度 | **0.5 cm = 18.90 px**（取整 19 px），热区 38 px（2 倍） |
| 侧栏交互 | 鼠标悬停即展开、移开自动收束（350 ms `DispatcherTimer` 监视） |
| 全屏窄条 | 可见宽 19 px，热区 38 px，铺满工作区高度 |
| 全屏交互 | 默认隐藏，鼠标靠近左边界才弹出，点击展开 |
| 指示器材质 | 边栏/窄条与三窗口共用 `BrushShellGlass`；hover 玻璃 `#6620334A`（40% 深蓝） |

---

## 3. 颜色与画刷（GlassTheme.xaml）

### 基础色

| 资源 | 色值 | 用途 |
|---|---|---|
| `ColorTextPrimary` / Brush | `#F1F5FA` | 主文字（全不透明） |
| `ColorTextSecondary` | `#B7C6D8` | 次级文字 |
| `ColorTextTertiary` | `#8CA0B8` | 提示文字 |
| `ColorAccent` | `#70B5F5` | 强调蓝（选中/焦点，不做大面积着色） |
| `ColorPanel` / `ColorNav` | `#20334A` / `#17283B` | 面板 / 导航基色 |
| `ColorWarning` / `ColorDanger` | `#FFCE8A` / `#FF9C8A` | 警告 / 危险 |

### 玻璃材质（alpha 越高越实）

| 画刷 | 色值 | alpha | 用途 |
|---|---|---|---|
| `BrushShellGlass` / `BrushWindowBaseGlass` | `#4A20334A` | 29% | 三窗口共享根玻璃（阶段 33 统一） |
| `BrushSidebarGlass` | `#E61E2F46` | 90% | 遗留未用 |
| `BrushFloatingGlass` | `#E620334A` | 90% | 遗留未用 |
| `BrushMenuGlass` | `#F21B2B3F` | 95% | Tooltip / 右键菜单 |
| `BrushNavGlass` | `#4D17283B` | 30% | 导航区 |
| `BrushPanelGlass` | `#3320334A` | 20% | 内容面板 / 投放区 |
| `BrushCardGlass` | `#4020334A` | 25% | 卡片 |
| `BrushPoolSolid` | `#FF3B4452` | 100% | 「全部」覆盖层实底 |
| `BrushInputGlass` | `#730A1220` | 45% | 输入框 / 按钮底 |
| `BrushLabelScrim` | `#61000000` | 38% 黑 | 文字局部底（白字恒定） |
| `BrushWindowFallback` | `#FF1E3046` | 100% | 原生背景不可用时的回退 |

### 交互反馈色

| 画刷 | 色值 | 场景 |
|---|---|---|
| `BrushHoverGlass` | `#0FFFFFFF`（6% 白） | 列表悬停 |
| `BrushHoverStrongGlass` | `#1FFFFFFF`（12% 白） | 按钮悬停 |
| `BrushPressedGlass` | `#2EFFFFFF`（18% 白） | 按下 |
| `BrushSelectedGlass` | `#2E70B5F5`（18% 蓝） | 选中 |
| `BrushSelectedStrongGlass` | `#4770B5F5`（28% 蓝） | 选中+悬停 |
| `BrushSelection` | `#5570B5F5`（33% 蓝） | 文本选中 |
| `BrushBorderGlass` | `#24FFFFFF`（14% 白） | 常规边框 |
| `BrushBorderStrongGlass` | `#40FFFFFF`（25% 白） | 强边框 |
| `BrushFocusBorder` | `#8070B5F5`（50% 蓝） | 键盘焦点 |
| `BrushSeparator` | `#1AFFFFFF`（10% 白） | 分隔线 |
| `BrushWarningGlass` / Border | `#2EF5C97B` / `#59F5C97B` | 警告条 |
| `BrushDangerGlass` / Border | `#2EF97066` / `#59F97066` | 移除投放区 |
| 滚动条 thumb | `#2EFFFFFF` / hover `#47FFFFFF` / drag `#59FFFFFF` | 宽 8 DIP，圆角 4 |
| 文字投影 | Blur 3 / Depth 1 / 270° / 55% 黑 | 玻璃上托字用 |

### 动态文字（按壁纸亮度自动切换）

- 亮度网格 **32 px** 采样；每 1.5 s 检查一次。
- 切换滞回 `SwitchMargin = 0.8`（新方案须明显更优才翻）。
- 基底 tint alpha 0.29、亮度基准 47.9；浅字 `#F1F5FA/#B7C6D8/#8CA0B8`，
  深字 `#08111C/#1B2B3D/#2E4258`；深字侧的局部底 `#73FFFFFF`（45% 白）。

---

## 4. 控件通用尺寸

| 控件 | 参数 |
|---|---|
| Button | 圆角 6，边框 1，内边距 `12,6`，禁用 opacity 0.45 |
| TextBox | 圆角 6，边框 1，内边距 `10,7` |
| ListBoxItem | 圆角 6，内边距 `6,4` |
| CheckBox / RadioButton | 方框 16×16 圆角 4 / 圆环 16 内径 8，勾 stroke 2 |
| ProgressBar | 高 4，圆角 2 |
| ScrollBar | 宽/高 8，thumb 圆角 4 margin 1 |
| ToolTip | 圆角 6，边框 1，内边距 `10,6`，无阴影 |
| ContextMenu | 圆角 8，内边距 4；MenuItem 圆角 5，内边距 `10,6` |
| GlassNavPanel / GlassPanel / GlassCard | 圆角 10，边框 1，padding 10（Card 无 padding） |
| WarningBar / DangerZone / DropZone | 圆角 8，padding `12,8` / `10,6` / `10,8` |
| StatusBarScrim | 圆角 6，padding `10,4` |
| 文字层级 | SectionTitle 13 SemiBold；ItemName 11.5；ItemNameTight 12.5；Hint 11 |

### 项目磁贴

- 88 × 88 DIP，圆角 8，padding `6,6`；图标 36 × 36，HighQuality 缩放；
- 名称牌 `#61000000` 圆角 4、padding `4,1`、最大宽 78、字号 11.5；
- 状态文字字号 10、危险色。

---

## 5. 时间与阈值总表

| 参数 | 值 | 位置 |
|---|---|---|
| 桌面同步防抖 | **400 ms** | `DesktopSyncOptions.DebounceInterval` |
| 兜底重扫间隔 | **5 分钟** | `PeriodicRescanInterval` |
| 回前台自动重扫 | 离开 > **60 s**，且距上次扫描 > 30 s | `AppController` |
| 侧栏悬停预览收束判定 | **350 ms** | `_peekTimer` |
| 窗口尺寸钳制延迟 | **300 ms + 1200 ms** 两次 | `ScheduleDeferredClamp` |
| 动态文字亮度刷新 | **1.5 s** | `DynamicText` |
| 长按进入拖动 | **350 ms + 6 DIP** | `CollapseVisualPolicy` |
| Glass Orb 过渡 | **140 ms** | `CollapseVisualPolicy` |
| Tooltip 延迟 | **550 ms** | `CollapseVisualPolicy` |
| 全局快捷键 | `Ctrl + Alt + Space`（Modifiers=3，Key=0x20） | `HotkeyDefinition.Default` |
| 图标缓存上限 | **512** | `IconService.DefaultMaxCachedIcons` |
| 日志上限 | **512 KB × 3 归档** | `LogFileWriter` |

---

## 6. 配置项与当前实际值

默认值取自代码，实际值取自你本机 `settings.json`。

| 配置项 | 代码默认 | 你当前值 | 功能 |
|---|---|---|---|
| `WindowMode` | 0 悬浮 | **0 悬浮** | 悬浮 / 全屏 / 侧栏 |
| `LayoutMode` | — | 1 | 分类导航 / 自由分区 |
| `WindowWidth × Height` | 1100 × 720 | **2048 × 1104** | 全屏窗口偏好；最大化=True |
| `FloatingWindowWidth × Height` | 620 × 430 | **504.56 × 369.24** | 悬浮窗尺寸（阶段 36 从 593.6×434.4 缩 15%） |
| `FloatingWindowLeft/Top` | null | 738.4 / 360.8 | 悬浮窗位置 |
| `CollapseBallLeft/Top` | null | **2079 / 951** | 小球位置（测试拖动残留，见文末） |
| `FloatingCompactSizeMigrated` | false | true | 尺寸一次性迁移标记 |
| `FloatingHideOnDeactivate` | false | **true** | 勾了→失焦收束成小球（阶段 35 统一） |
| `CollapseOnLaunch` | true | **true** | 打开软件后自动收起 |
| `FloatingTopmost` | false | false | 悬浮窗常驻置顶；小球始终置顶 |
| `SidebarDock` | 0 左 | 0 左 | 停靠侧 |
| `SidebarExpanded` | true | true | 展开/收起 |
| `SidebarWidth` | 220 | **256.61** | 展开宽度（设置里 100–520 可调） |
| `SidebarAutoCollapse` | true | **true** | 失焦自动收起 |
| `SidebarCategoryHeight` | 150 | **420** | 分类区高度（60–420） |
| `PanelGridSize` | 40 | **20** | 自由分区网格吸附 |
| `MinimizeToTray` | true | true | 关窗最小化到托盘 |
| `AutoStartEnabled` | false | false | 开机自启 |
| `HideDesktopIcons` | false | false | 隐藏桌面图标 |
| `HotkeyModifiers / Key` | 3 / 32 | 3 / 32 | `Ctrl+Alt+Space` |

> 配置目录：`C:\Users\<用户名>\AppData\Local\DeskLanes\`
> 项目库当前 **70 个项目 / 8 个分组 / 8 个分区**。

---

## 7. 功能行为一览

**三种模式**：共用同一份项目/分类/分区数据，切换不丢内容。

**悬浮窗**
- 搜索（不区分大小写，切换布局不丢失）、分类标签过滤、全部/文件夹两种视图；
- 顶部按钮：视图切换、返回、全屏、侧边栏、✕ 隐藏（Esc 同效）；
- 拖动条 + 八方向缩放；失焦默认沉到最底层（勾选则收束成小球）。

**全屏**
- 添加自定义项、⇱悬浮、▐侧栏、布局切换、⇳刷新、⌄托盘；
- 分类导航 与 自由分区（画布 1600×900，可拖分区、吸附网格）；
- 磁贴 80% 缩放；Esc / 托盘 退出全屏。

**侧边栏**
- 顶部：展开/收起、切换停靠、全屏、悬浮、✕；
- 软件在上、文件夹在下各自 A-Z；双击启动；分类区高度可拖动；
- 收起态 44 DIP 窄条带「◀」按钮。

**收束与呼出**
- 打开软件后自动收起（可关）：侧栏→0.5cm 边栏（悬停展开），悬浮窗→Glass Orb（点击展开），全屏→靠近左边界弹窄条（点击展开）；
- Esc：三模式统一收束（关掉自动收起时沿用隐藏）；
- `Ctrl+Alt+Space`：收束态展开；全屏/侧栏态带到前台；悬浮态呼出/隐藏切换。

**托盘菜单**：打开工作空间 / 悬浮窗模式 / 全屏模式 / 侧边栏模式 / 设置… / 退出 DeskLanes。

**系统联动**：桌面文件变化 400 ms 防抖同步；显示器变更/休眠唤醒/会话解锁自动重排；
回前台静置 60 s 后重扫桌面；图标缓存 512。

---

## 8. 构建与分发

| 项 | 值 |
|---|---|
| 版本 | `0.1.0`（Assembly/File `0.1.0.0`） |
| 目标框架 | .NET 10 Windows (`net10.0-windows`) |
| 当前正式构建 | selfcontained `46D90BD6871BFD85D5558F3334F3644D`，portable `D9238B461703AC7A3373DB73774852A5` |
| 安装目录 | `C:\Users\<用户名>\AppData\Local\Programs\DeskLanes` |
| 安装大小 | 约 65 MB（自包含版，无需装 .NET） |

---

## 附注：小球位置残留

当前 `CollapseBallLeft=2079 / Top=951` 是回归测试拖动留下的坐标（在屏幕右侧），
不影响功能，只是小球默认会出现在那里。要恢复「左边缘中部」默认值，删掉这两个字段即可；
需要的话我可以帮你清掉并重跑一次确认。
