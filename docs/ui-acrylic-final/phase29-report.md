# 阶段 29：失焦保持玻璃、悬浮窗留底层、设置窗口崩溃、系统更新报错归因

视觉证据：`phase29-sidebar.png`、`phase29-settings.png`（「失去焦点时自动隐藏」默认未勾选）。

## 一、失焦时变灰不透明，希望始终维持焦点态透明

**现象**：图 1（非焦点）侧栏是灰白实底、名字牌发闷；图 2（焦点）是透出壁纸的蓝色玻璃。

**根因**：不是我们的画刷或配色。`backdrop-probe.txt` 显示
`applied=3`（Desktop Acrylic 已被 DWM 接受），是 **Win11 DWM 在窗口非活动时
把 Acrylic 降级成不透明纯色**的系统行为；XAML 里没有任何 `IsActive` 触发器，
阶段 27 配的 `BrushSidebarBaseGlass` 在两态都是同一个值。

**做法**（`WindowBackdropService` / `DwmInterop`）：

- `Attach` 在 `SourceInitialized` 后订阅 `Activated` / `Deactivated`，
  焦点变化用 `DispatcherPriority.Background` 重新下发材质
  （社区里 MicaForEveryone 等工具维持失焦透明也是这个思路）。
- 重下发时**先 `NONE` 再 `ACRYLIC`**：直接写同一个值可能被 DWM 合并掉、
  不重建材质；同时把 `CompositionTarget.BackgroundColor` 复位为透明。
  不动 `Window.Background` / 子 Border，避免焦点切换闪一下。
- `Apply` 成功时额外打开 `DWMWA_USE_HOSTBACKDROPBRUSH`（官方常量 17），
  让非活动状态也尽量继续采样桌面。
- 新增排障开关 `DESKLANES_PERSIST_ACRYLIC=0`：关掉重下发，便于 A/B 对比。

**代码路径已验证**：新构建启动后 `backdrop-probe.txt` 出现
`reapply on focus change hr=0x00000000 (none -> acrylic)`，说明重下发确实执行且被 DWM 接受。

> **限度（如实记录）**：DWM 最终是否愿意在非活动窗口上继续画 Acrylic，
> 由系统版本、透明效果开关、电源策略共同决定，应用侧只能"持续重新申请"。
> 本轮没有完成「失焦/焦点两张真实屏幕截图的像素比对」（取证脚本执行时被打断），
> 因此需要你实测确认。若系统仍强制降级，下一步可改回分层窗口 + WPF 自绘
> 半透明底：两态能绝对一致，代价是失去 DWM 的毛玻璃模糊。

## 二、悬浮窗一点别的软件就消失，希望留在最底层

**根因**：`AppSettings.FloatingHideOnDeactivate` 默认 `true`，你的配置里也显式写着
`true`；`FloatingWindow.OnDeactivated` 直接 `HideFloating()`。

**做法**：

- 默认改为 **false**。旧配置里显式存过的 `true` 由 `WorkspaceService` 构造时
  **一次性迁移**为 false（新标记 `FloatingHideOnDeactivateMigrated` 落盘后，
  你之后再主动勾回 true 不会被覆盖）。
- 失焦不再隐藏，改为 `SetWindowPos(HWND_BOTTOM, SWP_NOACTIVATE|SWP_NOMOVE|SWP_NOSIZE)`
  把窗口送到 z 序最底层：继续显示、不抢焦点、不盖住正在用的软件。
- 设置文案改为「失去焦点时自动隐藏（默认关闭：失焦后留在最底层，Esc 仍可隐藏）」；
  Esc 隐藏与主动勾选自动隐藏都保留。

## 三、点设置报「无法将 Owner 属性设置为之前未显示的 Window」

`errors.log` 2026-11-12 15:57:09 抓到完整堆栈：托盘菜单 → `ShowSettings()` →
`Owner = _mainWindow` 抛 `InvalidOperationException`。悬浮窗 / 侧边栏模式下主窗口
可能一次都没 `Show()` 过，WPF 不允许拿它当 Owner。

**做法**：`SelectSettingsOwnerIndex(...)` 按「已加载且当前可见」挑 Owner，
顺序主窗口 → 悬浮窗 → 侧边栏；都不满足就**不设 Owner**（`ShowDialog` 仍能正常模态显示），
再包一层 `InvalidOperationException` 兜底。该选择逻辑抽成纯函数，冒烟覆盖 4 种组合。

> 这条与第二条是连带的：旧行为下悬浮窗一失焦就消失，用户只能从托盘点设置，
> 正好撞上 Owner 崩溃。现在两条都修了。

## 四、24h 任务后 Windows 更新时的 explorer.exe 报错（归因，非代码改动）

截图里的报错是 **`explorer.exe - 无法找到入口`**：
`无法定位程序输入点 NtdxgkGetContextQos 于动态链接库 C:\Windows\SYSTEM32\dcore.dll 上`，
出现在「更新已完成 30%」阶段。

- 这是 **Windows 系统组件 DLL 新旧版本错配**：explorer.exe 期望新版的
  `dcore.dll` 导出，却加载到了更新中途的旧版（或相反）。更新过程中系统文件
  处于 staging 状态时最容易出现，属于 Windows 更新链路的问题。
- **不是 DeskLanes 崩溃**：报错进程是 explorer.exe，不是 DeskLanes.exe；
  应用是 .NET 托管进程，不依赖这个导出函数。本轮检查 `errors.log` 也没有
  同一时刻的 DeskLanes 异常（唯一新异常是上面那条 Owner）。
- 与 24h 驻留任务的关联：`tools/soak-8h.py` 会调用
  `SetThreadExecutionState` 阻止机器睡眠，因此可能让 Windows 更新在后台挂起、
  重启被推迟；但**没有证据表明它导致 DLL 错配**，两者只是时间相邻。

**建议**（不改系统文件、不下载第三方 DLL）：

1. 让 Windows 更新完整跑完并**重启一次**，多数情况下报错随之消失；
2. 若重启后仍复现，用管理员终端依次执行
   `DISM /Online /Cleanup-Image /RestoreHealth` 与 `sfc /scannow`；
3. 检查「Windows 更新历史」是否有失败/回滚的补丁，必要时重装该累积更新；
4. 绝对不要从第三方 DLL 站点下载单个文件覆盖 `System32`。

## 五、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **537 / 537**（第 37 节新增 11 条；1 条旧断言「悬浮窗默认失焦隐藏」按前提变化等价替换为「默认失焦不隐藏」） |
| 启动探针 | **7 / 7**，`errors.log` 未增长 |
| 三屏端到端 | **36 / 36** |
| 发布 | selfcontained `C286CBF28078EABA66D904ECB63999A6`、portable `991363D8A2B187123787CA8D83FF43BF` |
| 安装 | `install.ps1 -Edition selfcontained -Force` 覆盖安装，安装副本 MD5 与正式构建一致 |

两版均按仓库固化 `.pubxml` 发布（65,119,801 字节）。你当前配置里的
`FloatingHideOnDeactivate: true` 会在新版首次启动时自动迁移为 false。
为替换 exe 结束过运行中的实例，请重新按 `Win` → `DeskLanes` 启动，
悬浮窗用 `Ctrl+Alt+Space` 呼出。

## 六、仍需用户实测 / 未闭环

1. **失焦时侧栏/悬浮窗是否还变灰白**（重下发日志已确认触发，视觉由 DWM 最终决定；
   若仍变灰，告诉我，我改走分层窗口自绘方案）。
2. 悬浮窗失焦后是否稳定留在最底层、从托盘点设置是否不再报错。
3. Windows 更新跑完并重启后，explorer.exe 的 DLL 报错是否消失。
4. 长期未闭环：物理热插拔复测（`--unplug`）、真实睡眠唤醒、
   阶段 14 之后新构建 24h+ 驻留、远程桌面会话（用户暂缓）。
