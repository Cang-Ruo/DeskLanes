# 阶段 8 第三步：开机自启 / 桌面图标 · 系统级验收（脚本已交付，待虚拟机实测）

日期：2026-10-25（脚本交付）；虚拟机实测：待用户执行

---

## 1. 这一步验收什么

用户定义的第三步范围（P1）：

1. **开机自启**：启用 → 重启后真的启动 → 禁用 → 卸载清理，四步全部纳入脚本自动检测；
2. **桌面图标隐藏**与三种事件的交互：正常退出、异常强杀（`Stop-Process -Force`）、
   Explorer 重启，隐藏状态都不能丢，也不能产生新的错误日志。

对应脚本：`tools/stage8-step3-check.ps1`（两段式，见 §4）。

## 2. 为什么是「引导 + 自动检测」而不是全自动

`AutoStartEnabled` 和 `HideDesktopIcons` 都**只在用户点设置窗口的「保存」时才应用**
（`SettingsViewModel` 的保存命令写注册表 + 调 `StartupService` / 图标服务）。
直接改配置文件再重启并不会触发。所以脚本负责：

- 引导你点哪几个按钮（每步一个回车确认）；
- 然后**自动检测系统真实状态**（注册表 Run 项、`HideIcons`、进程、错误日志行数、
  桌面 shell 完整性、截图），写进报告。

人工点击清单（合计 7 次设置窗口操作）：

| 段 | 操作 | 次数 |
|---|---|---|
| A | 勾选「开机自启」→ 保存 | 1 |
| B | 勾选「隐藏桌面图标」→ 保存；再取消勾选 → 保存 | 2 |
| C | 再次勾选「隐藏桌面图标」→ 保存 | 1 |
| E | 取消勾选「隐藏桌面图标」→ 保存 | 1 |
| G（Phase 2） | 取消勾选「开机自启」→ 保存 | 1 |
| H（Phase 2） | 勾选「隐藏桌面图标」→ 保存 | 1 |

## 3. 事故记录与修复（本机自检时发生，已全部修复）

自检 D 段旧写法 `taskkill /f /im explorer.exe` + `Start-Process explorer.exe` 之后，
本机桌面 shell 未能自动重建（Progman/DefView 消失，只剩壁纸）。排查后确认三个根因，
现版脚本已全部规避：

| # | 根因 | 现象 | 规避方式 |
|---|---|---|---|
| 1 | 诊断脚本**只枚举顶层窗口**找 `SHELLDLL_DefView` | DefView 是 **Progman 的子窗口**，顶层枚举永远找不到，误判「桌面坏了」 | 改为 `EnumWindows` 找 Progman → `FindWindowEx` 找子 DefView（`N8.ShellComplete()`） |
| 2 | 在**沙箱化的工具进程树**里 `Start-Process explorer` | explorer 随工具调用结束被回收，桌面彻底没有 shell | 重启 explorer 改用 **WMI `Win32_Process.Create`**（创建者变成 WMI 服务宿主，不在进程树里） |
| 3 | `HideIcons` 被我自己的切换命令改乱 | 盲目发两次 `WM_COMMAND 0x7402` 前没读状态，把「显示」切成了「隐藏」，误以为「有东西在偷偷改注册表」 | 任何写操作前先读状态；脚本不再盲目切换 |

附带确认的系统行为：沙箱回收 shell 后，Windows 的 `AutoRestartShell`（默认开）会自动
拉回新 shell——桌面最终自己恢复了。这也说明**暴力杀 explorer 不是不可恢复**，但恢复
期间桌面不可用，且 `uninstall.ps1` 的自动恢复图标逻辑会失效（找不到 DefView）。

`tools/uninstall.ps1` 同步修复：

- 找 Progman 时增加 `IsWindowVisible` 判断（不可见的 Progman 没法用）；
- 找不到 DefView 时按场景给指引（Progman 都没有 → 提示任务管理器重启 explorer 或重启电脑；
  有 Progman 没 DefView → 提示重启资源管理器/注销重登），**不再建议不存在的桌面右键菜单**；
- 增加注册表兜底：shell 不在时直接把 `HideIcons` 写回 0，shell 一恢复图标就显示。

## 4. 脚本用法（两段式，中间必须重启一次）

```powershell
# 第一阶段（约 10 分钟，含 5 次人工点击，不用重启）
powershell -NoProfile -ExecutionPolicy Bypass -File stage8-step3-check.ps1 -Phase 1

# ……重启虚拟机……

# 第二阶段（约 5 分钟，含 2 次人工点击）
powershell -NoProfile -ExecutionPolicy Bypass -File stage8-step3-check.ps1 -Phase 2
```

产出（都在脚本同目录，直接把 `stage8-step3-report.txt` 和 4 张 png 发回来即可）：

- `stage8-step3-report.txt` —— 全部检查项的 PASS/FAIL 与实测值
- `step3-hide-on.png` / `step3-hide-off.png` —— 隐藏/恢复后的桌面
- `step3-after-explorer-restart.png` —— Explorer 重启后的桌面
- `step3-after-uninstall.png` —— 卸载后的桌面

截图前会自动把干扰窗口最小化、拍完立刻恢复，保证拍到的是桌面而不是 PowerShell 窗口。

**D 段安全设计**（重启资源管理器）：执行前会让你确认；用 WMI 启动新 explorer；
轮询最多 60 秒等 `Progman + DefView` 回来；回不来会提示
`Ctrl+Shift+Esc → 文件 → 运行新任务 → explorer.exe` 手动恢复，不会把桌面弄丢。
本段还会让你肉眼确认托盘图标是否自动回来（软件注册了 `TaskbarCreated`，
Explorer 重启后应自动重注册——`TrayIconService.HandleMessage` 已实现）。

## 5. 本机自检证据

`docs/stage8-step3-selfcheck.log`：9 PASS / 2 FAIL。两个 FAIL 是预期内的——
自检时只模拟了「隐藏」没有真的取消勾选，恰好证明「恢复后 HideIcons = 0」判据有效。
该次自检跑的是旧版脚本（D 段危险写法），现版脚本已按 §3 重写并通过语法解析 +
函数级自检（`ShellComplete=True`、截图最小化/恢复循环正常）。

## 6. 虚拟机实测（2026-10-27 完成，Phase 2 全过）

环境：干净 Win11 专业版 Build 26200 / x64 / 1920×1080 / **无 .NET 10 桌面运行时** /
**100% DPI / 单显示器**。安装的是阶段 8.0 重发布的**自包含版**
（65,064,093 B，MD5 `b8f3e6ef…`，即包含 UI 轻玻璃改版的最新构建）。

### Phase 2 结果：通过 10 项，失败 0 项

| 场景 | 结果 | 关键证据 |
|---|---|---|
| F 重启后自启生效 | PASS | Run 项仍在 + 重启后 DeskSpace 进程自动拉起（进程数=1） |
| G 禁用自启删 Run 项 | PASS | 设置里取消勾选 → Run\DeskSpace 不存在 |
| H 卸载清理含图标恢复 | PASS | 先制造"卸载时正隐藏图标"（HideIcons=1）→ uninstall.ps1 识别 selfcontained 版 → 恢复图标（HideIcons=0）、删 Run 项/安装目录/卸载项/开始菜单，**配置目录按预期保留** |

截图（`docs/stage8-step3-evidence/vm/`）：`step3-hide-on.png`（图标全隐藏）→
`step3-hide-off.png`（恢复显示）→ `step3-after-explorer-restart.png`（Explorer 重启后
shell 完整、图标可见）→ `step3-after-uninstall.png`（卸载后图标全可见、应用已移除）。

### 附加证据（vm-check-report.txt）

- 自包含版在**无运行时的干净机**上直接跑起来（解压出 `wpfgfx_cor3.dll`，自带运行时确认）
- 窗口为侧边栏**收起态**：44×760 @(1876,160)，中心点 (1898,540) 在屏内 ——
  顺带补上了 100% DPI 下侧边栏收起态的跨设备证据（此前缺虚拟机侧边栏证据）

### Phase 1 结果：通过 12 项，失败 1 项（1 项为**脚本判据 bug**，非软件缺陷）

| 场景 | 结果 |
|---|---|
| A 开机自启启用写 Run 项 | PASS（+1 项误报 FAIL，见下） |
| B 桌面图标隐藏 → 恢复 | PASS ×2（HideIcons 1 → 0） |
| C 异常强杀不改变状态 | PASS ×4（强杀后 HideIcons 仍为 1；errors.log 11→11 未增长；重启后状态一致） |
| D Explorer 重启后状态保持 | PASS ×3（shell 自动恢复、图标仍隐藏、**托盘图标自动重新注册**） |
| E 恢复正常显示 | PASS（HideIcons = 0） |

**唯一 FAIL 的判定：脚本问题，软件行为正确。**
`StartupService` 写 Run 项时是有意写成**带引号的命令行**（`SetValue(ValueName, $"\"{exePath}\""`，
`src/DeskSpace.App/Services/StartupService.cs` 第 96 行）——这是 Run 项的标准写法，路径含空格时才安全。
而脚本用带引号的字符串与不带引号的期望路径直接 `-eq` 比较，结果误报 FAIL。两条佐证：

1. 报告里实际值与期望值去掉引号后**完全一致**；
2. Phase 2 的 F 项用同一条 Run 值验证，**重启后 DeskSpace 确实自动启动了**（进程数=1）。

已修正脚本判据（先 `Trim('"')` 再去比较、忽略大小写），并用实测值复算验证：旧判据 `False`、新判据 `True`。

### 已知缺口与修复

- **两段式报告互相覆盖**（两阶段写同一文件）：已改为按阶段分别写
  `stage8-step3-report-phase1.txt` / `-phase2.txt`，`vm-package` 内脚本已同步。
  本次已用新脚本重跑 Phase 1，A–E 的文字记录已补齐（见上方表格）。
- **Run 项路径判据误报**：已修正（见上）。
- 至此第三阶段 A–H 全部场景都有虚拟机文字记录 + 截图证据，**第三步完整收口**。
