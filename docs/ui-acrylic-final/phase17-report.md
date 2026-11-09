# 阶段 17：名字牌回到深色半透明、悬浮窗不再置顶、全屏无边框

用户一次提了三件事，全部处理并实证：

| 反馈 | 处理 |
|---|---|
| 浅灰不好看，要「之前一样的底色」：是黑色但**没有现在这么实** | 名字牌（以及共用 `BrushLabelScrim` 的状态栏 / 计数 / 分组标题）改回**45% 黑半透明 + 白字**，能透出壁纸，恒定不随壁纸切换 |
| 悬浮窗一直置顶，点其他软件切不到前面 | 去掉 `Topmost`，呼出时仍由 `WindowActivator` 强制前台 |
| 全屏要真全屏 / 无边框，不要看到窗口界面 | 主窗口 `WindowStyle="None"` + `ResizeMode="NoResize"` + 最大化；补 Esc 退出 |

证据：`phase17-floating.png`、`phase17-full.png`、
`phase13-probe-p17-floating-final.json`（实测 `topmost=False`）。

## 一、名字牌

用户附图是旧版那种**深色半透明、能透出一点壁纸的白字标签**，不是纯黑实心，
也不是阶段 16 的 90% 不透明浅灰。

- `BrushLabelScrim` → `#73000000`（45% 黑）
- 名字牌改回 `BrushLabelScrim` + `ItemNameText`（浅字 + 玻璃投影），
  仍不挂 `dt:DynamicText.Scrim`，恒定深色
- 删除阶段 16 的 `BrushNamePlate` / `BrushNamePlateText` / `ItemNameTextOnLight`

因为 `BrushLabelScrim` 被 9 处共用，这次顺带把状态栏、分类计数、分组标题
一起变回同样的深色半透明——上一轮「浅灰文件名 + 深黑小标签」的混搭也一并消失。

**为什么是 45% 而不是原来的 30%**：白字在 30% 黑底上叠亮壁纸只有约 3.0:1，
低于 4.5；45% 黑实测亮壁纸约 **4.66:1**、中灰 7.72:1、暗壁纸 14.71:1，
同时仍明显比 55% 黑 / 90% 浅灰更透。

## 二、悬浮窗置顶（有实证）

`FloatingWindow.xaml` 的 `Topmost="True"` → `"False"`；
呼出时 `ShowAndActivate()` 仍调 `WindowActivator.Activate(forceForeground: true)`，
所以快捷键/托盘呼出照样在最前，但不再长期压住其他软件。

`phase13-manual-probes.py` 增加读取 `WS_EX_TOPMOST` 位（`topmost` 字段）。
用**新安装版**、临时把 `WindowMode` 切到 Floating 启动后实测：

```
VISIBLE DeskLanes 快速启动   topmost False
```

配置随后字节级还原（`settings.json restored byte-exact: True`）。
> 中途有一次测到 `topmost=True`，那是误用了尚未重新发布的安装目录旧 exe；
> 发布并覆盖安装后复测才得到上面的结果，没有拿旧数据当结论。

侧边栏 `Topmost="True"` 本轮**保留**：它是贴边待命的窄条，设计上需要一直可见，
用户只反馈了悬浮窗。

## 三、全屏无边框

`MainWindow` 加 `WindowStyle="None"`、`ResizeMode="NoResize"`，
仍由 `BeginFullMode()` 最大化铺满屏幕。`phase17-full.png` 可见：
没有系统标题栏、边框、最小化/最大化/关闭按钮；软件自带的顶部工具栏
（添加 / 悬浮 / 侧栏 / 布局 / 刷新 / 托盘）仍在内容区。

因为系统关闭按钮没了，新增 `OnWindowKeyDown`：**Esc 隐藏到托盘**（应用继续运行），
与「⌄ 托盘」按钮、`Ctrl+Alt+Space` 切模式互为兜底。

这带来一个行为变化：主窗口不再是可还原、可拖到副屏的普通窗口。

## 四、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **472 / 472** |
| 启动 | **7 / 7**，`errors.log` 未增长 |
| 悬浮窗置顶 | 新安装版实测 `topmost=False` |
| 端到端 | **29 / 36**，7 条竖屏/跨屏断言不成立，见下 |
| 发布 | 正式 selfcontained `819a9df4299174c50f78b1421a47ece9`，`install.ps1 -Force` 覆盖安装 |

端到端 7 条失败全是竖屏/跨屏相关。根因仍是**左侧竖屏不在显示器拓扑里**
（`SM_CMONITORS=2`）；此外阶段 17 把主窗口改成无边框全屏后，
Round E/F 里「把主窗口拖到左竖屏再最大化/还原」的前提也不再成立。
按约定**不改判据、不删断言、不标通过**。接回左竖屏后需要重新评估这两条测试
是否还适用于无边框全屏设计。

历史工具前提变化：`phase10/12/13` 的像素审计需 `DESKLANES_DYNAMIC_TEXT=1`
（阶段 14 起默认关闭自适应），描述的是旧行为路径。

## 五、用户需确认

1. 重新按 `Win` → `DeskLanes` 启动（替换 exe 时结束了旧实例），
   看名字牌是否就是附图那种「黑但透」的感觉；45% 若还偏实可再降到 35–40%，
   但白字对比度会掉到 4.5 以下，需要权衡。
2. 悬浮窗点击其他软件是否能让开；**侧边栏仍贴边置顶**，要不要也取消。
3. 全屏是否要连任务栏一起盖住（当前无边框最大化，任务栏行为以系统为准）；
   Esc 退出是否顺手。
