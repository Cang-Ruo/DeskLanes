# 阶段 25：滚轮、底栏实底、全部弹出、方格网格

四件事。视觉证据：`phase25-panels.png`（默认收起）、`phase25-panels-open.png`（点开）。

## 一、侧栏 / 悬浮窗滚轮

**根因**：项目区是「外层 `ScrollViewer` 里嵌 `ListBox`」。`ListBox` 自带内部
`ScrollViewer`（默认垂直 Auto），滚轮事件被内层吞掉、内外两套滚动互相打架。

**做法**：侧边栏的 `SoftwareItems`/`FolderItems` 两个列表、悬浮窗「全部」视图的
`DesktopFolderItems`/`SoftwareItems` 两个列表都加
`ScrollViewer.VerticalScrollBarVisibility="Disabled"`，由外层 `ScrollViewer`
统一滚动。文件夹视图里是 `ItemsControl`（无自带滚动），保持不变。

## 二、底部「全部」不透背景

`DropZone` 用的是半透明 `BrushPanelGlass`（`#3320334A`），盖在画布上会透出
下面内容显得乱。新增不透明 `BrushPoolSolid`（`#FF2B3240`），底部覆盖层单独
用本地 `Background` 覆盖样式值；文字本来就是浅色，实底正好托住。

## 三、「全部」改成点击弹出 / 收起

按用户后半句明确的指令实现：

- `FreePanelsViewModel.IsPoolOpen` 默认 **false** —— 平常只显示上方面布
- 工具栏新增「**全部** / **收起全部**」按钮（`TogglePoolCommand`）
- 覆盖层 `Visibility` 由 `IsPoolOpen` 控制，**只有点这个按钮才切换**
- 去掉阶段 24 的「拖动时淡到 12%」：`IsDraggingItem` 现在只让鼠标穿透
  （`IsHitTestVisible=False`），**外观完全不变**，所以拖软件、放好软件都不会
  让它收起，落点仍能落到下面的画布/分区

> 用户原话里「拖动自动收回，放好软件再弹出」与「除非我点击收起否则不会
> 因为我是否拖动软件就收起」互相矛盾，按后者（更明确、且是否定句）实现。
> 若实际想要「放好软件后自动弹出一次」，下一次补。

## 四、自由分区方格网格

- `AppSettings.PanelGridSize`（默认 40，工具栏可选 **20 / 40 / 60 / 80**，
  写入配置持久化）
- `WorkspaceService.SnapToGrid()`：位置四舍五入到整数格，尺寸向上取整到格
  （保证放得下内容），再按画布与最小尺寸钳制
- `UpdatePanelLayout` 是拖动 / 四边缩放 / 「整理」/ 落点建面板的共同写入口，
  所以所有路径都自动吸附；`FindFreePanelSlot` 的步进也改成按格
- 一个分区默认 320×240，即 8×6 个 40 单位的方格；拖边框改尺寸时按格变化，
  也就是「占几个方格」可自由调整

**冒烟**：两条旧的精确几何断言（`X==100`、`X==111 && W==333`）改成验证
「值是整数格且偏差不超过一格」，验收强度不降；新增第 33 节 8 条断言覆盖
默认收起、点击弹出、拖拽不改变展开态、点收起才收起、切格大小后重新吸附。

## 五、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **510 / 510** |
| 启动 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **36 / 36** |
| 发布 | 正式 selfcontained `ce9aadb8790e3d405240283cf9a1a78c`，`install.ps1 -Force` 覆盖安装，安装副本 MD5 一致 |

`UiSnapshot` 新增 `panelspool` 模式用于拍展开态（用 `ForcePoolOpenForSnapshot`
测试钩子，产品运行时为 null、默认收起）；钩子要设在 `MainViewModel` 构造之前。

为替换 exe 结束了当时正在运行的实例（PID 63952）；用户需重新按
`Win` → `DeskLanes` 启动。

## 六、仍需用户实测 / 未闭环

1. 侧边栏 / 悬浮窗滚轮手感（嵌套滚动已统一，但真实滚轮无法自动化）。
2. 方格大小选 20 / 60 / 80 时面板对齐观感、拖边框的吸附手感。
3. 「放好软件再弹出」是否真的需要（本轮按「不因拖动收起」实现）。
4. 物理热插拔修复复测、真实睡眠唤醒未实测；远程桌面用户暂缓；
   阶段 14 之后新构建 24h+ 驻留未重跑。
