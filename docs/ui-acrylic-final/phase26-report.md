# 阶段 26：留白、拖动流畅度、自适应框、侧栏、滚轮

六件反馈，均已处理。视觉证据：`phase26-{panels,sidebar}.png`。

## 一、画布两侧空隙太大

**根因**：`OnCanvasHostSizeChanged` 用 `min(scaleX, scaleY)` 等比缩放 1600×900。
内容区高度通常比 16:9 矮，`scaleY` 成为瓶颈，画布物理宽远小于窗口宽，
居中后两侧留白很大。

**做法**：`scale = min(scaleX, scaleY * 1.12)` 适度放大用满一点宽度；
同时把 `CanvasBorder.Height` 动态设为 `hostHeight / scale`，让画布边框的物理
高度正好铺满内容区、边框完整可见。超出部分是画布底部空白（面板都在上半部）。

## 二、文件夹框移动不流畅

**根因**：`OnMoveDragDelta` / `OnEdgeResizeDragDelta` 每帧都调
`UpdatePanelLayout` → `Save()`，等于拖动时每帧序列化整个 `library.json`。

**做法**：`UpdatePanelLayout` 的写盘改成 **200ms 节流**（几何仍在内存里，绑定实时更新）；
给移动 Thumb 和 8 个缩放 Thumb 统一加 `DragCompleted="OnGeometryDragCompleted"`，
松手时调 `PanelViewModel.FlushGeometry()` 补一次 `Save()`，保证最后一次几何一定落盘。

## 三、方格下拉没用 / 框要自适应

上一轮的 20/40/60/80 选项用户觉得没意义，已**从工具栏去掉**；内部仍保留
`PanelGridSize = 40` 只用于位置/尺寸吸附到整数格，维持间距整齐。

`AutoGrowPanel`（只增不减、按现有宽度算）换成 **`AutoFitPanel`**：按成员数
`columns = clamp(ceil(sqrt(n)), 2, 5)` 与行数精确计算宽高，向上取整到格，
保证**所有软件都显示得下**，移出软件后框也会缩小。

**重要保护**：只在 `EnsurePairedContainers` 里检测到**成员数变化**时才重算，
启动加载不重算 —— 否则用户手动拖过的框尺寸每次重启都会被覆盖（冒烟首跑
暴露了这一点，已修，并加了「重启后手动尺寸保留」断言）。

## 四、侧栏太宽 / 长文件名

- `AppSettings.SidebarWidth` 默认 **280 → 220**，构造时只迁移恰好等于旧默认
  280 的配置（用户自己调过的宽度保留）；设置窗口里本来就有 180–520 的滑块
- 项目行文字 `MaxWidth` 200 → 150，配合缩窄后的侧栏
- **悬停提示其实一直有**：`TooltipText` = `名称 + 完整路径`，Border 上已挂
  `ToolTip`；本轮再给文字 `TextBlock` 本身也挂一份，确保悬停文字任何位置都能触发

## 五、滚轮只有滚动条能滚

**根因**：外层 `ScrollViewer` 里嵌 `ListBox`，滚轮事件被内层路由吃掉，
外层只在指针落在滚动条上才响应。上一轮禁用内层滚动条后问题仍在。

**做法**：侧边栏的 1 个、悬浮窗的 2 个外层 `ScrollViewer` 都挂
`PreviewMouseWheel="OnOuterPreviewMouseWheel"`，在隧道阶段直接
`ScrollToVerticalOffset(VerticalOffset - Delta/3)` 并 `Handled = true`。
现在整个内容区任意位置滚轮都能滚。

## 六、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **514 / 514**（新增第 34 节 4 条：成员多时框不变小、几何有效、重启后手动尺寸保留、侧栏默认 220；另把 1 条旧宽度断言 280→220） |
| 启动 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **36 / 36** |
| 发布 | 正式 selfcontained `80c54e05b5e429c3f8e1d7661d669fe7`，`install.ps1 -Force` 覆盖安装，安装副本 MD5 一致 |

冒烟首跑 2 项失败：① `重启后分区几何保留` 暴露「加载时也自适应会覆盖手动尺寸」，
已按上面第三节修正；② 侧栏默认宽度断言过期，更新为 220。

为替换 exe 结束了当时正在运行的实例（PID 60308）；用户需重新按
`Win` → `DeskLanes` 启动。

## 七、仍需用户实测 / 未闭环

1. 滚轮是否真的能在整个侧栏 / 悬浮窗滚动（真实滚轮无法自动化）。
2. 拖动是否变流畅（写盘节流是明确的根因修复，但帧率没法用断言量）。
3. 两侧留白 1.12 倍是否合适、面板自适应后的观感。
4. 侧栏 220 是否够窄；不满意可在设置里用「展开宽度」滑块自己调。
5. 物理热插拔修复复测、真实睡眠唤醒未实测；远程桌面用户暂缓；
   阶段 14 之后新构建 24h+ 驻留未重跑。
