# 阶段 46：悬浮球单击分类选择窗 / 双击展开

用户要求：**其他全部不动**，只改悬浮球点击逻辑：
- 单击 → 弹出一行一行的分类选择窗（像侧栏菜单），点某分类直接展开悬浮窗并定位；
- 双击 → 才展开完整悬浮窗。

## 一、改动

**EdgeStripWindow**：新增 `SingleClickRequested` / `OrbPressed` 事件。
`OnMouseUp` 用 250ms `DispatcherTimer` 区分单击/双击：第一次 up 启动计时，
窗口内第二次 down 取消计时、第二次 up 走 `ClickRequested`（展开）；
超时才触发 `SingleClickRequested`。拖动中随时清掉 pending，长按拖动不受影响。

**OrbPickerWindow（新增）**：轻量一行式分类窗，条目来自
`FloatingWindowViewModel.Categories`（全部 + 各分组，带计数），
复用现有 GlassTheme 画刷，位置摆在小球右侧。`SetEntries` /
`ResetSelection` 期间抑制 `SelectionChanged`，避免填充列表误触发打开。

**AppController**：接 `SingleClickRequested` → 弹选择窗；
`OrbPressed` → 关闭已开的窗；分类选中 → `ExpandFromEdge` 后设置
`SelectedCategory`（`GroupId` 对应分组，null = 全部）。

## 二、未改

窗口尺寸 / 视觉 / 长按 350ms + 6 DIP / 位置保存 / 多屏钳制 / 快捷键 /
悬浮窗主体 / 侧边栏 / 全屏 / 桌面同步 / 安装卸载 —— 全部未动。

## 三、验收

`tools/phase46-picker-probe.py` **10 / 10**：收起、单击弹分类窗、
分类窗打开时悬浮窗仍收起、Esc 关窗、双击展开且不弹窗、
选分类后展开并关窗、长按拖动仍工作且不误开任何窗。

回归：构建 0/0、冒烟 593/593、启动 7/7、端到端两次 37/37、
阶段 31 8/8、阶段 34 19/19、阶段 35 9/9、阶段 38 收起球 13/13、
三模式焦点一致性全部「两态一致」。

**前提变化的等价替换**：P34/P35/P38 里原「单击小球展开悬浮窗」
断言改为「双击小球展开（单击现在弹分类窗）」，断言数量未减。

安装 `F1D39D16…`。配置恢复为真实值（模式 0、失焦自动收起 True、
尺寸 504.56×369.24、小球位置 2079/951）。

## 四、未验证

分类窗只验证了「全部」行的打开路径，未逐一点过每个分组
（数据来自同一 `Categories`，逻辑一致）；分类窗在极窄分辨率下的布局未穷举。
