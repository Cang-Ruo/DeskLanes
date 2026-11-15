# 阶段 32：全屏 2cm 边距、搜索框对齐、更清淡的玻璃、焦点无关透明度

视觉证据：`phase32-fullscreen-layout.png`、`phase32-sidebar-glass.png`。
取证工具：`tools/phase32-focus-glass.py`（三种窗口模式的真实焦点切换）。

## 一、问题1：左右边距 3cm → 2cm

旧逻辑 `scale = min(scaleX, scaleY*1.12)` 由高度决定缩放，宽屏上会留出约 3cm
空白。改成「按物理 2cm 目标边距反推缩放」，再与高度约束取较大、`scaleX` 封顶：

- 计算抽到 `WorkspaceLayoutMath.ComputeCanvasScale(hostW, hostH, dpiScale)` 纯函数；
- 用 `VisualTreeHelper.GetDpi` 把 2cm（75.6 物理像素）换算成 DIP，
  100% / 125% / 150% 缩放下物理尺寸都一致，换分辨率不会跑偏；
- `scaleX` 封顶保证画布永远不被横向裁掉；面板自身的虚拟 X 偏移与窗口
  14 DIP 内边距保持不变。
- 冒烟新增 9 条断言，含 150% DPI 验证与「确实比阶段 31 更窄」。

## 二、问题2：搜索框太宽、太短

**理解说明**（若理解反了请直接告诉我）：「太宽」= 横条太厚，「太短」= 横向不够长。
于是做两件事：

- **变薄**：`FontSize 13→12`、`Padding 10,7→10,2`，高度约减 0.5cm；
- **加长并对齐**：给自由分区工具栏的「当前分区：…」文字加 `x:Name`，
  `MainWindow` 在布局稳定后用 `TransformToVisual` 算出它右边缘相对窗口的 X，
  把 `SearchBox.Width` 设成「右边缘 − 根 Grid 左边距」，
  即右边缘精确对齐「档」字右侧；分类导航视图没有该提示文字时回退 540 DIP。
- 对齐计算同样抽成 `WorkspaceLayoutMath.AlignSearchWidth`（纯函数，冒烟覆盖
  下限钳制与窗口宽度上限）。只有目标值真的变化才赋值，避免 `LayoutUpdated` 自激。

## 三、问题3：焦点不在软件上就变丑 + 侧边栏太明显

### 焦点无关透明度（三个窗口全部）

阶段 30 只把侧边栏改成分层窗口；全屏与悬浮窗仍用 DWM 系统 Acrylic，
失焦时被 DWM 换成不透明纯色（截图里那块深灰）。本轮把
`MainWindow` 与 `FloatingWindow` 也改成 `AllowsTransparency=True`：

- 透明像素由 WPF 自己合成，`WindowBackdropService` 对分层窗口
  把系统 backdrop 关成 `NONE`，改走窗口级 `ACCENT_ENABLE_ACRYLICBLURBEHIND`；
  DWM 再没有机会按焦点状态换底色。
- 窗口底色用低 alpha 深色玻璃（全屏 15%、悬浮窗 12.5%），
  **alpha≠0 同时保证分层窗口下鼠标不穿透**；拖动条与八方向缩放热区
  全部改成 `#01000000`，避免阶段 30 那次「合成鼠标点空」的回归。
- 去掉悬浮窗的 `WindowChrome`（分层窗口不兼容），拖动/缩放仍由自己的热区负责。
- 实测：全屏 / 侧边栏 / 悬浮窗在**真实焦点切换**（`focused_ok=true`、
  `focus_lost_ok=true`）下，同一矩形平均像素差均为 **0.0**。
  `tools/phase32-focus-glass.py` 会校验前台必须真是被测窗口，
  否则判定无效并删除截图，杜绝把别的窗口画面当成证据。

### 侧边栏更清淡

红壁纸上的粉白罩来自「根 Border 20% 白 + 窗口级 tint 9% 白」叠加（约 29%）。
降到 **6% 白 + 2% tint（合计约 8%）**，壁纸颜色与花枝直接透出来，
只保留一点点玻璃感与模糊；名字牌仍有 38% 黑 scrim 保证白字可读。

## 四、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **555 / 555**（新增第 39 节 9 条：2cm 换算、DPI 无关、缩放不裁画布、对齐计算） |
| 启动探针 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **连跑两次 37 / 37**（分层窗口后拖拽与八方向缩放无退化） |
| 收束功能探针 | **连跑两次 8 / 8** |
| 三模式焦点一致性 | 全屏 / 侧边栏 / 悬浮窗 **平均像素差 0.0**（真实焦点切换 `focused_ok` / `focus_lost_ok` 均为 true） |
| 发布 | selfcontained `B35392598ED2F6C6AA37229B3A55069D`、portable `27103987AAE9BDBE8354A0F0544641D3` |
| 安装 | `install.ps1 -Edition selfcontained -Force` 覆盖安装，安装副本 MD5 一致 |

证据图：`phase32-{full,sidebar,floating}-{focused,unfocused}.png`，
每对文件字节完全一致；另有 `phase32-fullscreen-layout.png`（布局）、
`phase32-sidebar-glass.png`（最终玻璃）。

## 五、第一次复跑的干扰与最终复跑（22:03）

**第一次（18:41 之后）**：前台是《三角洲行动》全屏窗口（0,0,2048,1152，
独占合成输入并把主屏分辨率改成 2048×1152），另有 NVIDIA Overlay / GamePP。
它导致 e2e 拖拽、收束探针 Esc、焦点探针前台切换全部失败，截图混入游戏地图。
处理：没有擅自结束或最小化用户游戏；临时开启的 `FloatingTopmost` 已还原，
6 张污染截图已删，取证工具加了「前台必须真是被测窗口，否则判无效并删图」。

**最终复跑（游戏退出后）**：前台恢复为普通窗口，全套结果见上表，
两次 e2e 37/37、两次收束 8/8、三模式焦点差 0.0。

复跑中额外定位并修好一个**工具问题**（不是产品缺陷）：
`phase31-collapse-probe.py` 原来只用 `SetForegroundWindow` 抢前台，
被 Windows 前台锁拒绝（返回 False），合成 Esc 发给了别的窗口，
于是「全屏 Esc 收束」两项误报失败。诊断确认产品功能正常
（强制前台后细线正常出现、主窗口隐藏），已给探针加
`AttachThreadInput` 的强制前台切换，随后连跑两次 8/8。
`phase32-focus-glass.py` 也加了同样的前台校验，杜绝把别的窗口画面当证据。

为替换 exe 结束过运行中的实例，请重新按 `Win` → `DeskLanes` 启动。

## 六、仍需用户实测 / 未闭环

1. 搜索框「变薄 + 对齐档字右侧」是否符合你的原意（我按横条太厚、长度不够理解）。
2. 2cm 边距在你的分辨率下的观感；面板自身虚拟 X 偏移未改，若整体仍觉宽可调。
3. 侧边栏降到 8% 白后是否够「清新简约」；亮壁纸下白字可读性。
4. 全屏 / 悬浮窗改成分层玻璃后，毛玻璃质感是否满意（两态已恒定一致）。
5. 长期：物理热插拔复测、真实睡眠唤醒、阶段 14 之后新构建 24h+ 驻留、
   远程桌面会话；Windows 更新后的 explorer.exe DLL 报错是否消失。
