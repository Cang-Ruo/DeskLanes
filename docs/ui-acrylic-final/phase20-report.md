# 阶段 20：画布自由拖放、分组框、侧边栏重做

用户反馈五件事，另确认一个好消息：**全屏与悬浮窗的双击打开已实测通过**
（阶段 14 的 `ItemTile` 双击修复终于有了真实实例结论）。

视觉证据：`phase20-{floatingfolders,sidebar,panels}.png`。

## 一、全屏自由分区

| 反馈 | 处理 |
|---|---|
| 画布不能自由拖动 | 给画布 `CanvasBorder` 加 `AllowDrop`，把项目拖到空白处时在**落点新建分区**并收下该项目（`CreatePanelAt`）；落点从屏幕坐标逆变换回 1600×900 虚拟画布。已有分区的拖动把手加大、加 `⠿` 图标与提示，右下角仍可拉伸 |
| 拖软件进去不会放大文件夹边框 | `WorkspaceService.AutoGrowPanel()`：按成员数算每行磁贴数与所需行数，只增不减地放大 `Height`（必要时先增宽到 480），且不超过画布边界 |

`PanelView` 内层先处理 Drop 并 `Handled=true`，只有画布空白处才会落到 `OnCanvasDrop`，
不会和「拖进已有分区」冲突。阶段 19 修的空面板 AllowDrop 也仍然生效。

## 二、悬浮窗文件夹视图

用户明确不要「卡片 + 点进去」，要**和自由分区一样：一个框里直接显示软件**。

- `FloatingFolderCard` 增加 `Items` 集合，切换视图时按 A-Z 填入该分组的成员
- XAML 从卡片 ListBox 改成 `ItemsControl`：每个分组一个带边框的 `Border`，
  框内标题 + 项目数 + WrapPanel 直接铺 `ItemTile`
- 顺带修掉阶段 19 卡片点不开的根因：`Command` 绑到了
  `RelativeSource AncestorType=ListBox` 的 `Data`，但 `ListBox` 没有 `Data` 属性
  （那是 `BindingProxy` 的），绑定必然失败。现在不需要点击了
- 没有分组时显示「还没有分组…」提示，不再显示通用空状态

实测截图：`phase20-floatingfolders.png` —— 「常用工具」「工作」两个框，
框里分别直接列出 6 / 8 个软件图标。

## 三、侧边栏

| 反馈 | 处理 |
|---|---|
| 双击打不开 | 侧边栏项目用的是自定义 Border+Image+TextBlock 行模板，**不是 `ItemTile`**；阶段 14 删掉 `MouseBinding` 后这里就没绑定了。给 `ListBoxItem` 加 `EventSetter MouseDoubleClick`，直接执行 `SidebarViewModel.OpenItemCommand` |
| 只显示设置好的文件夹 | `Rebuild()` 固定先加「全部」节点，再追加用户分组；`ApplyItems()` 选「全部」时显示所有项目（A-Z）。没有分组时就只有「全部」 |
| 高度占满屏幕 | `PlaceOnDockedSide()` 不再用 `min(虚拟屏高*0.85, 760)`，改为取**窗口所在那块显示器的工作区高度**（`MonitorInterop.TryGetWorkArea` + 该屏 DPI 换算） |

**踩坑记录**：第一版直接把 `Height` 设成 `SystemParameters.VirtualScreenHeight`，
三屏时那是所有屏叠加的总高，窗口被推到屏幕外（`--screen` 截图只剩壁纸）。
改成按所在屏工作区后，视觉树快照为 `280×1018` 竖长条，分类「全部 15 / 工作 8 / 常用工具 6」、
项目列表 15 行都在（`phase20-sidebar.png`）。
> 贴边窗口的 `--screen` 屏幕合成截图抓不到内容，是截图工具限制；
> 视觉树快照（不加 `--screen`）可正常验证。

## 四、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **486 / 486**（把 2 条旧「侧边栏不伪造分类」断言反转成「全部」语义；把阶段 19 的 3 条卡片点开断言换成阶段 20 的分组框断言；新增画布落点建面板、边框自动放大） |
| 启动 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **36 / 36** |
| 发布 | 正式 selfcontained `d7e759124bea95680f36a7efaf617680`，`install.ps1 -Force` 覆盖安装，安装副本 MD5 一致 |

为替换 exe 结束了当时正在运行的实例（PID 27944）；用户需重新按 `Win` → `DeskLanes` 启动。

## 五、仍需用户实测 / 未闭环

1. 真实鼠标把「全部」里的软件拖到画布空白处（合成 OLE 拖放不稳定，
   `CreatePanelAt` 与 `AutoGrowPanel` 有冒烟覆盖，但没有端到端鼠标验证）。
2. 侧边栏双击打开、侧边栏高度观感（视觉树快照已确认数据正确）。
3. 物理热插拔修复复测（再拔一次左屏线 `--unplug`）、真实睡眠唤醒未实测；
   远程桌面用户暂缓；阶段 14 之后新构建 24h+ 驻留未重跑。
