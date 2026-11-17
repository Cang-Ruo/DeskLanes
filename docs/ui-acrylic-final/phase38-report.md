# 阶段 38：DeskLanes Glass Orb（冰川蓝 · 双轨流线 · 清透冰晶）

按用户最终规格实现，**只改悬浮窗收起小球**，不改造侧边栏、全屏收起把手，
也不改悬浮窗口主体布局。完成后暂停，未自动继续推广。

## 一、开始前检查（先查真实实现，不假设文件名）

| 检查项 | 实际位置 / 结论 |
|---|---|
| 悬浮窗隐藏 / 收束逻辑 | `AppController.OnItemLaunched`、`CollapseToEdge`、`ExpandFromEdge`；悬浮窗分支复用 `ShowAndActivate()`，没有第二个窗口 |
| 收起小球实现 | `Views/EdgeStripWindow.xaml(.cs)`，`CollapseVisual.FloatingBall` 形态 |
| 位置保存 | `AppSettings.CollapseBallLeft/Top`（物理像素），`OnCollapseBallMoved` 落盘 —— **已存在，直接沿用，未新增字段** |
| 鼠标事件 | 原 `MouseEnter/Leave/Down/Move/Up`，原「位移 4px 立即拖」，无长按时长判定 |
| DWM 服务 | `WindowBackdropService` 只挂接到主窗口 / 悬浮窗 / 侧栏 / 设置窗；`EdgeStripWindow` 不挂接，分层窗口自绘，无冲突 |
| 原交互 | 3cm 球、可拖动、位置持久化、<4px 算点击、点击展开 |

与规格的主要差异：原球约 90 DIP（3cm）而非 44 DIP；原拖动无 350ms 门槛；
原是纯色 + 菱形图标而非渐变冰晶 + 双轨流线。

## 二、实现

### 视觉（EdgeStripWindow.xaml）

- **44 DIP 可视 / 56 DIP 透明热区**（规格要求 ≥48 DIP，多留 8 DIP 给 1.05 缩放与光晕），
  仍只是一颗小球，不用大透明矩形盖桌面。
- 多层 `RadialGradientBrush`：左上白反射 `#8FC4E8/C6E9FF/BDE6FF` →
  主冰川蓝 `#527FA9/#6FA3C9/#3489C2` → 底部深蓝 `#294E72/#315D85/#24567A`。
- 外圈银蓝细描边 `#9EC4DE`（1 DIP）、内侧 0.6 DIP 高光线、内缩柔和阴影，
  悬停 / 拖动才淡入范围较小的冰蓝 `BlurEffect` 光晕（不做霓虹）。
- 双轨流线用**自绘 WPF Path**（24×24 画布、两条独立平行 S 曲线、左下→右上、
  不相交、Round LineCap、主线冷白 `#F1FAFF` / 副线浅冰蓝 `#BDE6FF`、线宽 2.1），
  没有用九宫格 / Grip / Feather / Waves / 点阵替代。
- **不用 Window.Opacity**；只淡入淡出材质层与描边，双轨图标始终清晰。
  三态颜色 / 缩放用 `DoubleAnimation` + `CubicEase(EaseOut)` 140ms 过渡。

### 交互（EdgeStripWindow.xaml.cs）

状态机 `Idle → Hover → PressPending → Dragging`：

- `MouseDown` 记录物理光标位置与 `Stopwatch`，进 PressPending 并 `CaptureMouse`；
- `MouseMove` 仅在 **≥350ms 且位移 ≥6 DIP** 时才进 Dragging，
  之后持续 `SetWindowPos(SWP_NOSIZE|NOZORDER|NOACTIVATE)`，鼠标移出球也不中断；
- `MouseUp`：拖过则保存位置，没拖过一律作为点击（长时间按住但没移动仍可打开）；
- `LostMouseCapture` 安全收尾：拖过则保存位置、释放状态、**绝不误触发点击**；
- `MouseLeave` 仅在非按下态回 Idle；拖动中 `SetHover` 有保护，不会被打断。

Tooltip：`ToolTipService.InitialShowDelay=550`；Idle 无 Tooltip，
Hover 显示「打开 DeskLanes」，拖动显示「拖动以移动」；切换时主动关闭上一个。

### DPI / 位置

WPF 侧 44 / 46 / 56 DIP 全部以 DIP 表达；只有传给 `SetWindowPos` 和落盘的
位置是物理像素，**没有混用 `Window.Left/Top`**。拖动范围 clamp 在小球所在
显示器的工作区内；换分辨率 / 换主屏时读回位置再 clamp。

## 三、视觉证据（真实 exe）

| 文件 | 内容 |
|---|---|
| `phase38-orb-idle.png` | 静止：清透冰川蓝圆、银蓝细边、双轨冷白标志，红壁纸与桌面图标透出 |
| `phase38-orb-hover.png` | 悬停：玻璃提亮、外圈柔光、边缘高光增强 |
| `phase38-orb-hover-tooltip.png` | 悬停 900ms：实际显示「打开 DeskLanes」提示 |
| `phase38-orb-dragging.png` | 拖动：更饱和冰川蓝、亮冰蓝描边、双轨标志不变 |
| `phase38-orb-positions.json` | 拖动前后坐标对比 |

像素差：静止 vs 悬停 **17.07**，静止 vs 拖动 **48.21**。

## 四、修改文件清单

产品代码：

- `src/DeskLanes.App/Views/EdgeStripWindow.xaml`
- `src/DeskLanes.App/Views/EdgeStripWindow.xaml.cs`
- `src/DeskLanes.App/Services/CollapseVisualPolicy.cs`

测试与文档：

- `tests/DeskLanes.SmokeTests/Program.cs`（第 41 / 44 节按新规格等价替换）
- `tools/phase38-orb-probe.py`（新增实机验收）
- `tools/phase34/35/36/37-*.py`（旧探针适配 44/56 DIP 与 350ms 长按）
- `docs/ui-acrylic-final/phase38-*`、`docs/known-limitations.md`、
  `docs/ui-acrylic-final/acceptance-report.md`、`.workbuddy/memory/2026-11-16.md`

## 五、测试结果

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **586 / 586**（长按门槛、6 DIP、350/550ms、140ms 过渡、44/46/56 DIP） |
| 启动探针 | **7 / 7**，`errors.log` 未增长 |
| Glass Orb 实机验收 | **12 / 12**（见下） |
| 三屏端到端 | 最终 **37 / 37**；期间出现过两次偶发失败（缩断续言 / 跨屏位置持久化），重跑通过，未删任何断言 |
| 阶段 31 收束 | **8 / 8** |
| 阶段 34 形态 | **19 / 19** |
| 阶段 35 真实配置失焦 | **9 / 9** |
| 阶段 36 小球拖动 | **9 / 9** |
| 阶段 37 三态 | **8 / 8** |
| 三模式焦点一致性 | 0.0 / 0.0 / 0.0 |
| 发布 | selfcontained `46D90BD6871BFD85D5558F3334F3644D`、portable `D9238B461703AC7A3373DB73774852A5` |
| 安装 | 覆盖安装，安装副本 MD5 与正式构建一致 |

`phase38-orb-probe.py` 12 项逐条结果：

```
floating window appears                                   PASS
Esc collapses to Glass Orb                                PASS
hit area = 56 DIP (DPI-scaled), orb is round 70x70px      PASS
hover visual differs from idle (17.07)                    PASS
plain click opens floating window                         PASS
dragging visual differs from idle (48.21)                 PASS
long-press drag does NOT open floating window             PASS
mouse leaving orb still completes drag (0,655)->(210,785) PASS
position saved on mouse up                                PASS
position restored after restart                           PASS
clamped inside work area at monitor edge                   PASS
Ctrl+Alt+Space still restores workspace                    PASS
```

回归结束后已把真实 `settings.json` 恢复并逐项核对：
`WindowMode=0`、`FloatingWindowWidth/Height=504.56/369.24`、
`FloatingHideOnDeactivate=True`、`CollapseOnLaunch=True`、
测试拖动产生的位置已清掉。

## 六、未验证事项

1. Tooltip「拖动以移动」的文字没有单独截图（Hover 的已截图确认）；
   代码路径与 Hover 同一套 ToolTip 机制。
2. 140ms 缓动的**中间帧平滑度**无法用静态截图验证，只能靠肉眼实机观察。
3. 多显示器混合 DPI 的全部组合未穷举；只实测了当前主屏 125% 及向左边界钳制。
4. 长时间驻留、真实睡眠唤醒、远程桌面会话、物理热插拔未覆盖（属长期项）。
5. 端到端偶发失败的具体触发条件未完全定位；同代码重跑通过，已记录未掩盖。

为替换 exe 结束过运行中的实例，请重新按 `Win` → `DeskLanes` 启动。

## 七、暂停说明

按你的要求，**本次没有修改侧边栏与全屏收起把手**，也没有改悬浮窗主体布局。
要不要把这套冰川蓝视觉推广到那两个入口，等你实机看过再说。
