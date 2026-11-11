# 阶段 27：全部拉满、图标缩小、侧栏标识与配色、宽度实时预览

视觉证据：`phase27-{panels-open,sidebar}.png`。

## 一、「全部」拉得不够完全 / 内容被遮挡

三个原因一起处理：

- `PoolPanelHeight` 上限写死 **600**，往上拖到一半就停。改成 **2000**，
  可以一直拉到接近铺满内容区。
- 加 **双击分隔条**：一次拉满（2000）/ 还原（250），不必一直按住拖。
  提示文字同步改成「按住拖动调整高度；双击直接拉满 / 还原」。
- 画布用了 `RenderTransform` 放大（阶段 26），而 WPF 的 `ClipToBounds`
  **不裁剪 RenderTransform 后的内容**，最下面一行图标会画到窗口底部状态栏
  区域、看起来像被一条横条盖住。`OnCanvasHostSizeChanged` 里显式给
  `CanvasHost` 设 `RectangleGeometry(0,0,hostW,hostH)` 裁剪，内容不再溢出。

> 展开时上方面板被底栏盖住半截，是阶段 24 起「底部是覆盖层」的设计
> （用户当时要求允许覆盖）。现在可以双击拉满，就不会留半截了。
> 若希望展开时干脆隐藏上方面布，可下一次调整。

## 二、图标太大

`ItemTile` 112×110 → **88×88**，图标 48×48 → **36×36**，名字
`ItemNameText` 字号 13 → 11.5、最大宽度 100 → 78。
`WorkspaceService.AutoFitPanel` 里的磁贴常量同步 120×116 → 96×92、
标题 46 → 40、内边距 20 → 16，框仍然刚好包住所有软件。

## 三、侧栏标识被遮挡

分类节点原来是「一个 Grid 里 Left / Right 两个 Border」，长分类名会直接
压到右侧计数上。改成 **两列 Grid**：名称列 `*` 且 `MaxWidth=150` +
`TextTrimming`，计数列 `Auto MinWidth=24` 固定靠右，两者不再重叠。

## 四、颜色再淡一点

- `BrushLabelScrim` 45% 黑 → **38% 黑**（`#61000000`），全窗口名字牌一起变淡
- `BrushPoolSolid` `#FF2B3240` → `#FF3B4452`（底部「全部」实底变浅）
- 侧边栏单独新增 `BrushSidebarBaseGlass`（`#32E8EEF5` 浅玻璃），
  替代原来 29% 深紫的 `BrushWindowBaseGlass`，红壁纸上不再压出暗红底

> 代价：深色底变淡后，亮壁纸下白字对比度会从约 4.6 降到约 4.2，
> 仍可读但不像之前那么"实"。若觉得字发飘可以再回一点点。

## 五、侧栏宽度不能边调边看

**根因**：设置窗口的 `SidebarWidth` 滑块只改 `SettingsViewModel` 自己的
字段，要等点「应用」的 `ApplySettings()` 才写回共享配置；侧栏窗口也没有
订阅宽度变化。

**做法**：

- `IWorkspaceService` 新增 `NotifySettingsChanged()`（只触发 `Changed`、不写盘，
  落盘仍由 `ApplySettings` 统一处理）
- `SettingsViewModel.SidebarWidth` setter 改值后立刻写入
  `_workspace.Settings` 并调用通知
- `SidebarViewModel` 暴露 `SidebarWidth`，在 `Rebuild()` 里发属性变更通知
- `SidebarWindow` 的 `PropertyChanged` 处理加上 `SidebarWidth`，
  立即 `ApplyWindowWidth()` + `PlaceOnDockedSide()`

## 六、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **516 / 516**（新增第 35 节 2 条：宽度通知实时、全部可拉满；1 条旧上限断言 600→2000） |
| 启动 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **36 / 36** |
| 发布 | 正式 selfcontained `cf37d850402e641b2181481a63d797e9`，`install.ps1 -Force` 覆盖安装，安装副本 MD5 一致 |

为替换 exe 结束了当时正在运行的实例（PID 50820）；用户需重新按
`Win` → `DeskLanes` 启动。

## 七、仍需用户实测 / 未闭环

1. 图标 36px 是否合适（还嫌大可以继续缩）。
2. 淡化后的名字牌在你那张红壁纸上是否清楚。
3. 「全部」双击拉满的手感、展开时上方面板是否还嫌被盖。
4. 设置里拖滑块时侧栏是否实时跟着变宽（通知链路有冒烟覆盖，未做 UI 自动化）。
5. 物理热插拔修复复测、真实睡眠唤醒未实测；远程桌面用户暂缓；
   阶段 14 之后新构建 24h+ 驻留未重跑。
