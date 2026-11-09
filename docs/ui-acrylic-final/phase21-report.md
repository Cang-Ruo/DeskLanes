# 阶段 21：侧边栏两区、首次显示修复、面板边框可拖动

三件事，全部处理。视觉证据：`phase21-{sidebar,panels,floatingfolders}.png`。

## 一、侧边栏分「软件 / 文件夹」，文件夹在下

`SidebarViewModel` 新增 `SoftwareItems` / `FolderItems`（各自 A-Z），
`SidebarWindow.xaml` 项目区改成 ScrollViewer 里上下两个带标题的列表：

- **软件** 在上（带深色底衬的小标签）
- **文件夹** 在下

两个列表都保留 `EventSetter MouseDoubleClick` 双击打开。
选「全部」时两区显示所有项目，选「游戏」等分类时按该分类成员过滤。
实测截图：`phase21-sidebar.png` —— 分类「全部 15 / 工作 8 / 常用工具 6」，
软件区 12 条、文件夹区 3 条（归档 2026 / 临时下载 / 素材库）。

## 二、切到侧边栏首次显示空白灰块

**根因**：`SidebarViewModel.IsExpanded` 来自配置里的 `SidebarExpanded`。
用户上次收起过，切回侧边栏模式时 `IsExpanded=false`，内容 Grid 与顶部工具栏
都被隐藏、只剩一个 ◀ 展开按钮，但窗口宽度还停在展开宽度 —— 于是出现
「一大块灰色矩形 + 一个小箭头」（用户图 1）。点那个按钮展开后才正常。

**修法**：`ShowDocked()` 里若当前是收起态就强制 `IsExpanded = true`，
并在 `Show()/Activate()` 之后用 `Dispatcher.BeginInvoke(Loaded)` 再同步一次
`ApplyWindowWidth()` + `PlaceOnDockedSide()`，避开 WPF 布局时序。

**实测**（用新安装版、临时把配置改成 `WindowMode=2` + `SidebarExpanded=false` 后启动）：

```
after start: WindowMode 2 SidebarExpanded True
sidebar windows: [('DeskLanes 侧边栏', (-864,292,-584,1524), 280, 1232)]
VERDICT expanded-on-switch: True
```

配置随后字节级还原。窗口是 280 宽、1232 高的展开态，不再是空白灰块。

## 三、自由分区面板像窗口边框一样拖动

原来只有**标题栏能移动、右下角能缩放**，用户拖边框没反应。
`PanelView.xaml` 现在给根 Grid 加了四条边 + 四个角的透明 `Thumb`
（统一 `OnEdgeResizeDragDelta`，用 `Tag` 区分 `Left/Right/Top/Bottom` 与四角），
按 `DragDeltaEventArgs` 与画布缩放系数换算，分别调整 `X/Y/Width/Height`；
`PanelViewModel` 的 setter 会把几何钳到画布与 `MinWidth/MinHeight` 内。
标题栏移动保持原样，项目区的双击与拖放不受影响。

## 四、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **490 / 490**（新增第 30 节 4 条：两区分流、Kind 正确、各自 A-Z、点分类过滤） |
| 启动 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **36 / 36**（首跑有一条缩放钳制断言因合成鼠标拖拽偶发未生效，复跑全绿，非回归） |
| 侧边栏首次展开 | 真实安装版实测 `SidebarExpanded false → true`，窗口 280×1232 |
| 发布 | 正式 selfcontained `7cf87a58607eeed40158d6ca5a7d0844`，`install.ps1 -Force` 覆盖安装，安装副本 MD5 一致 |

为替换 exe 结束了当时正在运行的实例（PID 22372）；用户需重新按 `Win` → `DeskLanes` 启动。

## 五、仍需用户实测 / 未闭环

1. 真实鼠标拖面板的四条边 / 四个角（合成鼠标对本轮 e2e 有偶发波动，
   代码路径与几何钳制有冒烟覆盖，但没做端到端鼠标验证）。
2. 侧边栏两区顺序与标题观感（视觉树快照已确认）。
3. 物理热插拔修复复测（再拔一次左屏线 `--unplug`）、真实睡眠唤醒未实测；
   远程桌面用户暂缓；阶段 14 之后新构建 24h+ 驻留未重跑。
