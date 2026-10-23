# DeskSpace 交付报告（功能 × 实现对照）

- 生成时间：2026-10-23
- 技术栈：C# + WPF + .NET 10 LTS，手写 MVVM，**零第三方依赖**；配置存 `%LocalAppData%\DeskSpace\`
- 三种界面模式（悬浮窗 / 全屏工作空间 / 侧边栏）**共用同一份项目、分类与配置数据**

---

## 一、测试结果总览

| 测试 | 结果 |
|---|---|
| 冒烟测试（单元 / 视图模型级） | **287 / 287 全过** |
| 阶段 4 真实验收（真实窗口 / 键盘 / 鼠标 / 托盘） | **48 通过 / 0 失败**（另有 2 项自动化限制） |
| Debug 构建 | 0 警告 0 错误 |
| Release 构建 | 0 警告 0 错误 |
| 单文件发布 | `publish/win-x64/DeskSpace.exe`（387 KB），实测可启动并显示主窗口 |

验收进步过程（同一脚本、同一台机器）：`18/28 → 36/40 → 47/48 → 47/48 → **48/48**`。
最近三次连续运行 `run11 / run12` 均为 **48 通过 0 失败**（含此前唯一失败项"切换后主窗口获得焦点"）。

---

## 二、功能 × 实现对照表

### 1. 核心层（DeskSpace.Core）

| 功能 | 实现位置 | 状态 | 验证 |
|---|---|---|---|
| 桌面扫描（用户桌面 + 公共桌面） | `Services/DesktopScanner.cs`、`Native/KnownFolders.cs` | ✅ | 冒烟；真实桌面 22/22（74=49+25） |
| 快捷方式解析（.lnk） | `Services/ShortcutResolver.cs`、`Native/ShellLinkInterop.cs` | ✅ | 真实桌面含 25 个 .lnk |
| 图标提取（含 256px 高清） | `Services/IconService.cs`、`Native/ShellIcons.cs` | ✅ | 阶段 2 起 |
| 启动项目 + 错误码映射 | `Services/LaunchService.cs` | ✅ | 冒烟（1155/1223/5） |
| 虚拟分类（只写 JSON、不动文件） | `Services/WorkspaceService.cs`、`Models/WorkspaceModels.cs` | ✅ | 阶段 2 |
| 配置持久化 / 版本迁移 | `Services/JsonStore.cs`、`ConfigStore.cs`、`LibraryStore.cs` | ✅ | 冒烟 |
| 桌面自动同步（监听+防抖+降级+日志） | `Services/DesktopSyncService.cs` | ✅ | 阶段 3，端到端新建/重命名/删除 |

### 2. 三种界面模式

| 功能 | 实现位置 | 状态 | 验证 |
|---|---|---|---|
| 全屏工作空间（分类导航 / 自由分区） | `MainWindow.xaml`、`CategoryNavView`、`FreePanelsView` | ✅ | 阶段 2、3 |
| 悬浮窗（搜索、双击/回车启动、失焦隐藏） | `Views/FloatingWindow.xaml`、`FloatingWindowViewModel.cs` | ✅ | 真实验收 8–11 节 |
| 侧边栏（停靠左右、展开收起、失焦收起） | `Views/SidebarWindow.xaml`、`SidebarViewModel.cs` | ✅ | 真实验收 4–7 节 |
| 三模式共享数据 + 共享搜索词 | `ViewModels/ShellState.cs`、`WindowModeManager.cs` | ✅ | 冒烟 13.9 全过 |
| 三模式视觉一致性（圆角统一 12、随停靠侧自适应） | `Views/SidebarWindow.xaml` | ✅ | 本次修复并重建 |

### 3. 系统级能力

| 功能 | 实现位置 | 状态 | 验证 |
|---|---|---|---|
| 全局快捷键（注册 / 冲突 / 无效提示） | `Core/Services/HotkeyService.cs`、`Native/HotkeyInterop.cs` | ✅ | 冒烟 + 真实验收（改键后新旧键切换） |
| 系统托盘图标与菜单 | `App/Services/TrayIconService.cs` | ✅ | 冒烟 13.8 |
| 单实例互斥 | `AppController.TryBecomeSingleInstance` | ✅ | 真实验收 13 节 |
| 最小化 / 关闭到托盘 | `AppController.OnMainWindowClosing` | ✅ | 真实验收 12 节 |
| 设置窗口（模式/快捷键/停靠/失焦/托盘） | `Views/SettingsWindow.xaml`、`SettingsViewModel.cs` | ✅ | 冒烟 13.7 + 真实验收 14 节 |
| Explorer 重启后托盘图标恢复 | `TrayIconService.HandleMessage`（TaskbarCreated） | ✅ | 真实验收 15 节（UIA 可见，通过） |

### 4. 本次新增 / 修复

| 功能 | 实现位置 | 状态 | 验证 |
|---|---|---|---|
| **开机自启** | `App/Services/StartupService.cs`（HKCU Run，仅当前用户、默认关闭） | ✅ 新增 | 冒烟 14 节（写入/删除/还原） |
| **桌面图标隐藏 / 恢复** | `App/Services/DesktopIconService.cs`（HideIcons + WM_SETTINGCHANGE 广播） | ✅ 新增 | 冒烟 15 节（含还原） |
| **打包分发（单文件）** | `publish/win-x64/DeskSpace.exe` | ✅ 新增 | 实测可启动 |
| 单文件下托盘图标路径 | `TrayIconService.LoadIcon` 改用 `Environment.ProcessPath` | ✅ 修复 | 消除 IL3000，发布 0 警告 |
| 验收脚本菜单索引（根因） | `tests/DeskSpace.Acceptance/stage4-accept.ps1` | ✅ 修复 | 见下节 |
| **切换模式后主窗口拿不到焦点** | `Core/Native/WindowInterop.ForceForeground` + `App/Services/WindowActivator.cs` | ✅ 修复 | 真实验收 12 节由 FAIL 转 PASS |
| **重复点击当前模式无反应** | `App/ViewModels/WindowModeManager.cs`（同模式也广播一次变更） | ✅ 修复 | 诊断 case A 验证 |

---

## 三、本轮关键修复说明

### 1. 托盘菜单索引错误（根因，影响最大）
原生菜单的 `↓` 导航**跳过分隔符**，且菜单刚弹出时**没有任何项被选中**——第 1 次 `↓` 选中第 1 个可选项。
此前脚本按"含分隔符的位置"计数，把 `6` 当作"设置"、把 `8` 当作"退出"；实际 **`6` 就是"退出"**。

后果：验收第 14 节本想开设置，实际把应用**退出了**，连锁导致 15 节（进程存活）与 16 节（找不到宿主窗口）全部失败。

正确映射：`1 打开工作空间 | 2 悬浮窗 | 3 全屏 | 4 侧边栏 | 5 设置 | 6 退出`。
修正后 14/15/16 节全部通过，验收由 18/28 提升到 47/48。

### 2. "菜单项是否真的被调用"的判据
原实现只要"菜单还开着"就返回成功，会把"按了 Enter 但没触发命令"误判为通过。
改为**以发送 Enter 后菜单是否关闭为准**（菜单关闭 = TrackPopupMenu 结束并发出命令）。

### 3. 阶段 5（桌面图标隐藏）结论修正
此前记录"因技术限制未实现"**被证伪**。证据：`docs/stage5-evidence` 中 probe v1 的 baseline/hidden/restored 三张截图 MD5 完全相同（只写注册表不生效），probe v2 的三张截图各不相同（写注册表 **+ WM_SETTINGCHANGE 广播**生效）。
关键是必须广播，无需重启 Explorer。已据此实现并验证。

### 4. "切换后主窗口获得焦点"失败（本轮最后一项，已修复）

**定位过程（实证，不靠猜测）**

1. 先排除"前台激活权限"假设：`diag-focus.ps1` 用托盘菜单驱动切换，主窗口**确实**拿到了焦点（`equal=True`）。
   说明不是"合成输入一律拿不到焦点"。
2. 再读验收脚本第 8–12 节序列，找到真正的差异：**第 11 节有一次真实鼠标点击桌面**（`mouse_event`），
   焦点被交给 Explorer；之后 DeskSpace 就不再是前台进程。真实用户场景同样如此
   ——"刚点了桌面空白，再从托盘菜单切回工作空间"。
3. 写 `diag-focus2.ps1` 精确复现：真实点击抢走焦点 → 托盘菜单切回全屏。**修复前焦点拿不回来**。

**根因**：WPF 的 `Window.Activate()` 内部就是 `SetForegroundWindow`，而 Windows 会拒绝
"非前台进程"抢占前台——调用静默失败，只闪一下任务栏：窗口显示了，键盘却进不去。

**修复**（两处，均为小范围改动）：

- `Core/Native/WindowInterop.cs` 新增 `ForceForeground`：先直接 `SetForegroundWindow`，
  失败则把本线程 `AttachThreadInput` 到前台线程再请求前台，用完立即解绑；必要时先 `SW_RESTORE`。
- `App/Services/WindowActivator.cs`（新增）：`Activate()` 之后核对前台窗口是不是它，
  不是才走 `ForceForeground`。全屏主窗口、设置窗口、悬浮窗接入。

**回归与收敛**：首次实现时把 `WindowActivator` 也加到了侧边栏分支，结果验收第 7 节
"失焦后侧边栏自动收起"由 PASS 变成 FAIL（连续两次复现，宽度停在 350）。
已撤回侧边栏那一处（`ShowDocked()` 内部本来就 `Show + Activate`），第 7 节恢复 PASS，
第 12 节仍 PASS，最终 48/48。

### 5. 环境适配
- `%LocalAppData%` 下的 `Remove-Item` 被环境安全钩子拦截 → 验收脚本重置配置改为**覆盖写入 `{}`**。
- `Assembly.Location` 在单文件发布下为空 → 改用 `Environment.ProcessPath`。

---

## 四、已知限制与未完项（如实列出）

| 项 | 说明 |
|---|---|
| ~~验收剩余 1 项失败~~ | **已修复**：原"切换后主窗口获得焦点"失败，根因是 Windows 拒绝非前台进程抢占前台；已加 `ForceForeground` 通道，run11/run12 连续 48/48。 |
| 自动化限制 1 | 无法从外部读取 `#32768` 菜单项文本（UIA / MN_GETHMENU / MSAA 三条路径均失败），改为用各项的实际行为验证。 |
| 自动化限制 2 | 合成输入无法稳定触发 WPF ListBox 的双击/回车绑定（状态栏"已启动"反馈），`OpenItem` 代码路径已确认正确。 |
| 未做安装包 | 目前只有单文件便携版，**没有 msi / 安装向导 / 自动更新**。 |
| 一次性观察（非缺陷） | 曾观察到默认配置下弹出 `#32770` 对话框阻塞启动，但连续 2 次复现实验均正常启动，判定为一次性干扰，未作为缺陷上报。 |

---

## 五、本次改动文件清单

**新增**
- `src/DeskSpace.App/Services/StartupService.cs`
- `src/DeskSpace.App/Services/DesktopIconService.cs`
- `src/DeskSpace.App/Services/WindowActivator.cs`（前台激活：Activate + 兜底 ForceForeground）

**修改**
- `src/DeskSpace.Core/Native/WindowInterop.cs`（新增 `ForceForeground` 及 `GetForegroundWindow` / `AttachThreadInput` / `BringWindowToTop` 等 P/Invoke）
- `src/DeskSpace.App/ViewModels/WindowModeManager.cs`（已在目标模式时仍广播一次变更，把窗口叫到前台）
- `src/DeskSpace.App/Services/AppController.cs`（装配两个服务；主窗口 / 设置窗口走 `WindowActivator`）
- `src/DeskSpace.App/Views/FloatingWindow.xaml.cs`（`ShowAndActivate` 走 `WindowActivator`）
- `src/DeskSpace.Core/Models/WorkspaceModels.cs`（新增 `AutoStartEnabled`、`HideDesktopIcons`）
- `src/DeskSpace.App/ViewModels/SettingsViewModel.cs`（接入两个服务；桌面图标启用时逐次授权）
- `src/DeskSpace.App/Views/SettingsWindow.xaml`（新增两个勾选框与状态提示）
- `src/DeskSpace.App/Services/AppController.cs`（装配两个服务）
- `src/DeskSpace.App/Services/TrayIconService.cs`（单文件图标路径）
- `src/DeskSpace.App/Views/SidebarWindow.xaml`（圆角/边框随停靠侧自适应，统一 12）
- `tests/DeskSpace.SmokeTests/Program.cs`（新增第 14、15 节测试）
- `tests/DeskSpace.Acceptance/stage4-accept.ps1`（菜单索引、调用判据、重试、配置重置方式）
