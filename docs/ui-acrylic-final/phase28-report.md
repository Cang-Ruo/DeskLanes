# 阶段 28：侧栏宽度下限 100、设置优先级、实时预览去卡顿、顶栏自适应、分类区可拖

视觉证据：`phase28-sidebar-180.png`（用户当前 180 宽的实际状态）、
`phase28-sidebar-100.png`（放开后的 100 宽下限）。

## 一、宽度下限 180 → 100

- `SettingsViewModel.SidebarWidth` 的 `Math.Clamp(value, 180, 520)` → `(100, 520)`；
  `SettingsWindow.xaml` 的 Slider `Minimum` 同步 180 → 100。
- `WorkspaceService` 构造里那条"恰好等于旧默认 280 才迁移到 220"的逻辑不动，
  用户已经调过的自定义宽度不受影响。
- 冒烟里「侧边栏宽度下限 180」按约定做**等价替换**为「下限 100」，没有删除断言。

## 二、打开设置时侧栏就被收起 → 给设置加优先级

**根因**：`ShowDialog()` 一打开设置窗口，侧栏立刻 `Deactivated` →
`OnWindowDeactivated()` → `SidebarAutoCollapse` 生效 → `IsExpanded=false`。
第一次点开设置，侧栏就缩成 44 DIP 的窄条，拖宽度滑块完全看不到效果。

**做法**：

- `SidebarViewModel` 新增 `SuppressAutoCollapse`（设置窗口打开期间为 true），
  `OnWindowDeactivated()` 开头优先判断它，直接返回。
- `AppController.ShowSettings()` 在 **ShowDialog 之前**置 true 并调
  `_sidebarWindow.ShowDocked()`（它本来就会强制展开）；
  `Closed` 事件里恢复 false，侧栏回到常规失焦收起行为。
- 顺序很关键：模态窗口一创建侧栏就失焦，晚一步就来不及。
- 冒烟新增 3 项：对照行为（未抑制时失焦确实收起）、抑制期间不收起、关闭后恢复。

## 三、实时调整宽度的卡顿与残影

**根因**：阶段 27 的 `NotifySettingsChanged()` 直接调 `OnChanged()`，
等于把"设置变化"当成"数据变化"。拖滑块的每一格都会让侧栏 / 分类导航 /
悬浮窗 / 自由分区四个 VM 全量 `Rebuild()` —— 清空并重建 `ObservableCollection`、
重建全部 ListBox 容器与图标、重设选中分类，所以又卡又闪残影。

**做法**：

- `IWorkspaceService` 新增专用 `SettingsChanged` 事件；
  `WorkspaceService.NotifySettingsChanged()` 只触发它，**不再触发 `Changed`**。
- `SidebarViewModel` 订阅 `SettingsChanged`，只发属性变更通知
  （`SidebarWidth` / `ExpandedWidth` / `IsDockedRight` / `DockToggleText` /
  `CategoryHeight`），一个集合都不动。
- `SidebarWindow.PlaceOnDockedSide()` 改成"先算目标值，值没变不赋值"：
  WPF 每次 `Left/Top/Height` 赋值都是一次 `SetWindowPos`，高频重排是残影的另一半来源。
- 冒烟新增 3 项：设置变化后宽度立刻到值、`Changed` 触发次数为 0、
  `Categories` 集合数量不变（没被清空重建）。

> 设置变化仍不写盘，落盘统一由「保存」和分类区分隔条 `DragCompleted` 负责。

## 四、顶栏不跟着宽度调整 / 按钮被遮挡

- 顶栏 `StackPanel(Orientation=Horizontal)` → **`WrapPanel`**，窄了自动换行，
  「全屏」「悬浮」「✕」不再被右边缘吃掉。
- 按钮 `Padding 10,5 → 8,4`、`Margin 0,0,6,0 → 0,0,4,4`；
  `DockToggleText` 「停靠到右侧/左侧」→「停靠右侧/左侧」（少一个字，100 宽也放得下）。
- 100 宽截图实测：收起 / 停靠右侧 / 全屏 / 悬浮 / ✕ 全部完整显示。
- 冒烟里 2 条按钮文案断言、验收脚本 `stage4-accept.ps1` 的按钮名同步为等价新值。

## 五、分类区"这边会遮挡"，希望可拖动调节

图 3 里「文档」被裁半行，是分类 ListBox 写死 `MaxHeight="150"` 的结果。

- `AppSettings` 新增 `SidebarCategoryHeight`（默认 150，范围 60–420）。
- `SidebarViewModel.CategoryHeight` 读写它（钳制到范围），另加
  `CommitPreferences()` 供拖动结束统一落盘。
- `SidebarWindow.xaml`：分类 ListBox 的 `MaxHeight` 改为绑定；分类与项目区之间
  插入一条 `Thumb` 分隔条（`SizeNS` 光标、悬停/拖动时高亮），
  `DragDelta` 只改内存高度、`DragCompleted` 才 `Save()`；展开区 Grid 由 4 行变 5 行。
- 拉到 420 就能一次看完全部分类，拉回 60 则把空间全留给项目列表。
- 冒烟新增 3 项：默认 150、上限 420、下限 60。

## 六、发布踩坑（透明记录）

1. **`install.ps1` 只复制 `publish\` 里现成的 exe，不会自己 publish**。
   第一次装完核对 MD5 发现与阶段 27 报告里的 `cf37d850…` 完全一致 ——
   装的是旧构建，阶段 28 改动根本没进 exe。随后重新 publish、重新安装，
   **并把启动探针与端到端回归在新构建上全部重跑**。
   （阶段 21 已踩过同类坑，这次靠 MD5 核对才在交付前拦住。）
2. 第一次补 publish 用了裸命令行参数，缺了仓库固化 `.pubxml` 里的
   `EnableCompressionInSingleFile` 与 `IncludeNativeLibrariesForSelfExtract`，
   自包含 exe 从 65 MB 涨到 132 MB。改用正式 profile：
   `-p:PublishProfile=win-x64-selfcontained` 与
   `-p:PublishProfile=win-x64-singlefile`，体积恢复 65,118,725 字节。
3. **流程固化**：发布后必须核对"安装副本 MD5 ≠ 上一阶段记录的 MD5"。

## 七、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **525 / 525**（第 36 节新增 9 条；3 条旧断言按前提变化做等价替换：宽度下限、2 条按钮文案） |
| 启动探针 | **7 / 7**，`errors.log` 未增长（3634 → 3634 字节） |
| 三屏端到端 | **36 / 36** |
| 发布 | selfcontained `7E4C8A34D7A5FF9A15C7C709F0A857FA`、portable `AC6795028E4D0F7F3E7028614C9B440E` |
| 安装 | `install.ps1 -Edition selfcontained -Force` 覆盖安装，安装副本 MD5 与正式构建一致 |

两版均为单文件、按仓库固化的 `.pubxml` 发布；为替换 exe 结束过正在运行的实例，
用户需重新按 `Win` → `DeskLanes` 启动。

## 八、仍需用户实测 / 未闭环

1. 100 宽在真机上的观感（截图已验证按钮不截断，但项目名会大量省略，靠 ToolTip 补）。
2. 分类区分隔条的拖动手感、拉到 420 是否够看全你的全部分类。
3. 打开设置时侧栏是否自动展开、拖「展开宽度」滑块是否真的不卡不闪（自动化只覆盖数据链路）。
4. 关闭设置后「失焦自动收起」是否恢复正常。
5. 长期未闭环：物理热插拔修复复测（`--unplug`）、真实睡眠唤醒、
   阶段 14 之后新构建 24h+ 驻留、远程桌面会话（用户暂缓）。
