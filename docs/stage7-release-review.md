# DeskSpace 阶段 7：发布前可靠性审查报告

- 审查时间：2026-10-24
- 审查性质：**只读审查**。未修改任何代码、未删除文件、未重置用户配置、未执行任何写注册表 /
  改桌面图标 / 改开机启动项 / 重启 Explorer 的操作。
- 标注约定：**【已实测】** 本次审查中实际运行并得到结果 ｜ **【代码证据，未实测】** 有明确代码或历史日志，
  但本次未在真实环境复验 ｜ **【未测试】** 本次及历史都未覆盖 ｜ **【发现问题】** 存在缺陷或风险
- 复核过的历史记录：冒烟 287/287、阶段 4 验收 48/48、Debug/Release 0 警告 0 错误——本轮重新构建验证通过
  （Debug 解决方案级构建 **0 警告 0 错误**【已实测】）；冒烟测试因会写注册表**本轮未运行**（见 §三.3）。

---

## 一、项目状态与版本控制

| 项 | 结果 | 结论 |
|---|---|---|
| 解决方案 | `DeskSpace.sln`，含 `DeskSpace.Core` / `DeskSpace.App` 与 4 个测试/工具项目 | 正常 |
| **版本控制** | `git rev-parse --is-inside-work-tree` → **`fatal: not a git repository`** | **【发现问题】没有 Git** |
| 忽略文件 | `.gitignore` 仅 5 行（`bin/ obj/ *.user *.suo .vs/`），但没有仓库承载它 | 同上 |
| 构建（Debug，全解决方案） | **0 警告 0 错误** | 【已实测】 |
| 源码结构 | App 层 Services / ViewModels / Views 分离清晰，Core 层纯逻辑 + Native 互操作 | 正常 |

**【发现问题 P1】项目完全没有版本控制。**
影响：无法回溯任一发布版本对应的源码；阶段 7 一旦发布，出问题无法定位"当时发的是哪份代码"；
也无法做 tag / 分支 / 回滚。仓库里只有一份 `.gitignore` 空转。
建议：发布前至少 `git init` 并做一次带完整信息的初始提交 + 打 `v0.1.0` tag（我会等你确认再执行）。

---

## 二、四项专项审查

### 1. 桌面图标隐藏 / 恢复

实现：`src/DeskSpace.App/Services/DesktopIconService.cs`
调用路径：`AppController.Startup()` 构造一次 → `SettingsViewModel`（每次打开设置新建）读取其 `IsHidden` →
`Save()` 里经用户 Confirm 授权后 `SetHidden(...)`。

| 审查点 | 结论 | 证据 |
|---|---|---|
| 是否真的改变原生桌面图标显示 | **有效，但只有历史截图证据，本轮未复验** | `docs/stage5-evidence`：probe v1 五张截图 MD5 **全部相同** `42915aef…`（只写注册表不广播 → 无效）；probe v2 四张 `p2-*` MD5 **全部不同** → 写注册表 **+ WM_SETTINGCHANGE 广播**生效【已实测 MD5 复核 / 原始可见性属历史证据】 |
| 是否只凭注册表值判断成功 | **【发现问题】是，且更宽松——写完之后不回读校验** | `DesktopIconService.cs:69` 写值 → `:79` 直接 `IsHidden = hidden`（没有重新读注册表确认），`:81` 无条件 `Ok()` |
| 状态检测是否可靠 | 读的是系统同一键值（`HKCU\...\Explorer\Advanced\HideIcons`），**是系统的真实设置值**；但**不等于"Explorer 已重绘"** | `:52-54` |
| 广播失败是否反馈 | **【发现问题】否** | `:98` `SendMessageTimeout(...)` 返回值被丢弃，注释写"失败也允许继续" → 广播失败仍返回 Ok，用户看到"已隐藏"但桌面没变 |
| 正常退出的行为 | **不恢复**，保持用户选择的持久状态 | `AppController.Dispose()`（`:259-320`）没有 `SetHidden(false)`；设计上符合"随时可在设置里取消勾选" |
| 异常终止后图标持续隐藏 | **【发现问题】会持续隐藏，且无兜底** | 同上：无 `AppDomain.UnhandledException` 级别的图标恢复、无退出钩子、无卸载清理 |
| Explorer 重启后能否重新判断 | **值本身不受 Explorer 重启影响**，且 Explorer 启动时会读该值，所以隐藏状态能被保留；但 DeskSpace **没有任何主动刷新机制** | `Refresh()` 只在构造函数 `:43-46` 调用 |
| 用户运行期间手动改系统设置 | **【发现问题】UI 会显示陈旧状态** | 服务在 `AppController.cs:111` 只构造一次；`SettingsViewModel.cs:59` 的注释写"以系统实际状态为准（用户可能在桌面右键菜单里改过）"，但读的是**启动时快照**，意图未真正实现。`Refresh()` 甚至不在 `IDesktopIconService` 接口上（`:22-32`） |
| 是否会覆盖用户新设置 | **不会**（这点设计是对的） | `SettingsViewModel.cs:360` 的写入条件是 `HideDesktopIcons != IsHidden \|\| HideDesktopIcons != settings.HideDesktopIcons`，两边一致时**不写系统设置**，因此不存在"无条件恢复旧状态把用户新设置冲掉"的情况 |
| 重复切换 | 幂等（重复写同值 + 再广播一次），无副作用 | `:69` `SetValue` 覆盖写 |
| 操作失败 / 状态未知反馈 | 部分：注册表写失败有明确错误弹窗（`:361-368`）；读取失败有 `StatusText`（`:60`）；**广播失败无反馈** | 同上 |
| 读取失败时仍允许操作 | **【发现问题】小**：`CanEditDesktopIcons` 恒为 true（`:243`），读取失败、状态未知时勾选框仍可操作 | 建议状态未知时禁用或二次确认 |

**未测试项**：桌面图标真实可见性（本轮按约定未做任何隐藏/恢复操作）；单文件产物下的图标隐藏行为。

### 2. 开机自启

实现：`src/DeskSpace.App/Services/StartupService.cs`

| 审查点 | 结论 | 证据 |
|---|---|---|
| 是否限制在 HKCU | **是**。`Software\Microsoft\Windows\CurrentVersion\Run`，`Registry.CurrentUser`，无 HKLM 访问 | `:36`、`:59`、`:89` |
| 是否只动自己那一条 | **是**，只读写名为 `DeskSpace` 的值 | `:37` |
| 开启/关闭/重复的幂等性 | **正确**：开启用 `SetValue` 覆盖；关闭先 `GetValue` 判断再 `DeleteValue(throwOnMissingValue:false)`；重复调用结果一致 | `:91-102` |
| 路径空格 / 特殊字符 | **正确处理**：`$"\"{exePath}\""` 加引号 | `:43`、`:93` |
| 单文件发布路径 | **正确**：用 `Environment.ProcessPath`（单文件下有效），回退 `MainModule.FileName` | `:46-47`；**【代码证据，未实测】** 未在单文件产物上实测过真实写入 |
| 路径无效时的反馈 | **有**：路径为空或文件不存在 → 直接失败，不写入 | `:81-85` |
| 写入失败反馈 | **有**：`IOException / SecurityException / UnauthorizedAccessException / ObjectDisposedException` → `OperationResult.Fail`，设置界面弹窗且不把配置记成"已开启" | `:104-107`；`SettingsViewModel.cs:322-336` |
| 配置与系统不一致 | **有处理**：写失败时 `settings.AutoStartEnabled = _startupService.IsEnabled`（以系统实际值为准） | `SettingsViewModel.cs:333` |
| 状态刷新 | **【发现问题】同桌面图标**：只在启动时读一次，`Refresh()` 不在 `IStartupService` 接口上；用户外部改启动项后 UI 显示陈旧 | `AppController.cs:110`、`StartupService.cs:55` |
| **卸载清理** | **【发现问题】没有任何清理路径**。没有卸载器（也无 msi）；用户删掉 exe 后 HKCU Run 里的 `DeskSpace` 会残留 | 全仓无卸载相关代码 |
| 多实例风险 | **低**：自启项无参数启动单个 exe；单实例由 `Local\DeskSpace.SingleInstance.v1` 互斥体 + 唤起已有实例保护，验收第 13 节已验证 | `AppController.TryBecomeSingleInstance`；验收 13 节 3/3 PASS【已实测（历史）】 |
| 默认是否开启 | 默认关闭，首次运行不替用户决定 | `WorkspaceModels.AutoStartEnabled` 默认 false |

**未测试项**：本轮未运行冒烟第 14 节（会写 HKCU Run），未实测单文件产物下的自启路径。

### 3. 窗口前台激活与快捷键

实现：`src/DeskSpace.App/Services/WindowActivator.cs`、`src/DeskSpace.Core/Native/WindowInterop.cs:64-110`、
`src/DeskSpace.Core/Services/HotkeyService.cs`

| 审查点 | 结论 | 证据 |
|---|---|---|
| AttachThreadInput 连接/解除配对 | **严格配对**：`attached` 只在 attach 返回 true 时置位，`finally` 里无条件解除；未成功 attach 时不会误解除 | `WindowInterop.cs:93` / `:104-108` |
| 句柄失效 | **安全**：`hWnd == Zero` 直接返回 false；句柄失效时 `SetForegroundWindow` 失败不会抛异常，attach/detach 仍配对 | `:66-69`；`WindowActivator.cs:34-37` |
| 异常路径是否遗留连接 | **不会**，`finally` 保证解除 | `:104-108` |
| 最小化窗口与恢复 | **已处理**：`IsIconic` → `SW_RESTORE`；回退路径再 `SW_SHOW` | `:71-73`、`:98` |
| 窗口关闭后访问无效句柄 | **无此路径**：`ShowSettings` 在 `Closed` 事件里把 `_settingsWindow` 置 null；`ApplyMode` 只操作仍持有的窗口 | `AppController.cs:595-598` |
| 焦点抢占 / 输入错发风险 | **【发现问题】中等**：attach 的目标是**任意前台线程**（可能是正在忙的 Explorer / 登录时的 Shell）。`AttachThreadInput` 若对方线程无响应，理论上可能连带阻塞本方 UI 线程；代码里没有"对方窗口是否挂起"的判断（如 `IsHungAppWindow`） | `WindowInterop.cs:81-93` |
| **启动路径也走强制前台** | **【发现问题】中等**：`Startup()` 里 `ApplyMode(mode, activate: true)` → `WindowActivator.Activate`。开机自启场景下，应用可能在登录阶段对 Shell 线程做 attach。建议：启动/首次显示走普通 `Activate()`，**只有用户显式操作**（托盘菜单、热键、设置保存）才允许强制前台 | `AppController.cs:165`、`WindowActivator.cs:31-41` |
| 快捷键注册失败反馈 | **有**：日志 + 气泡 + 模态弹窗，且不覆盖原快捷键 | `AppController.cs:654-676` |
| 注册失败时气泡无效 | **【发现问题】轻微**：`RegisterHotkeyWithFeedback()` 在创建托盘之前调用，此时 `_tray` 为 null，`_tray?.ShowBalloon(...)` 是空操作，只有模态弹窗生效 | `AppController.cs:141`（调用）vs `:144`（创建 tray）、`:669` |
| 修改快捷键时释放 | **正确**：`Register` 先 `UnregisterCore()` 再注册，避免同进程重复占用 | `HotkeyService.cs:96`；验收 14 节已验证新旧键切换【已实测（历史）】 |
| 退出时释放 | **正确**：`Dispose()` → `UnregisterCore()`；`AppController.Dispose()` 先释放热键再关消息宿主（顺序正确） | `HotkeyService.cs:139-153`；`AppController.cs:266-270`；验收 16 节"退出后全局快捷键已释放"PASS【已实测（历史）】 |
| 注销失败时状态不一致 | **【发现问题】轻微**：`UnregisterCore` 无论 `UnregisterHotKey` 是否成功都把 `_current = null`，失败时"自认为已注销"而系统仍占用 | `HotkeyService.cs:131-133` |
| **侧边栏失焦收起是否被激活逻辑破坏** | **当前不会**：侧边栏分支只调 `ShowDocked()`（内部 `Show + Activate`），**没有**叠加 `WindowActivator` | `AppController.cs:487-492`（含注释说明）；验收 7 节 PASS【已实测】 |
| 是否使用键盘钩子 | **否**，只用 `RegisterHotKey` | `HotkeyInterop.cs` |

### 4. 单文件发布与运行环境

| 审查点 | 结论 | 证据 |
|---|---|---|
| 目标框架 | `net10.0-windows` | `DeskSpace.App.csproj:4`；产物内嵌 runtimeconfig `"tfm": "net10.0"`【已实测】 |
| 目标平台 | **win-x64**（PE machine = `0x8664`，PE32+） | 【已实测，Python 解析 PE 头】 |
| 单文件发布 | **是**（`DeskSpace.exe` 内嵌 `DeskSpace.dll` / `DeskSpace.Core.dll` / `DeskSpace.runtimeconfig.json` / `DeskSpace.deps.json`，387 KB） | 【已实测，读到内嵌 bundle 清单】 |
| self-contained | **否**（387 KB，非 80 MB+ 量级）→ **framework-dependent** | 【已实测】 |
| ReadyToRun | **未启用** | csproj 与历史命令行均未设置【代码证据】 |
| 裁剪（Trimming） | **未启用** | 同上；符合"不盲目裁剪"的要求 |
| **运行时依赖结论** | **目标机器必须安装 .NET 10 Desktop Runtime**（`Microsoft.WindowsDesktop.App 10.0.0`），而不只是基础 `Microsoft.NETCore.App` | 【已实测】产物内嵌 runtimeconfig 的 frameworks 段同时列出 `Microsoft.NETCore.App 10.0.0` 与 `Microsoft.WindowsDesktop.App 10.0.0` |
| **发布配置是否入库** | **【发现问题】完全没有**：csproj 里没有 `RuntimeIdentifier` / `PublishSingleFile` / `SelfContained` / `PublishReadyToRun` / `PublishTrimmed`，也没有 `.pubxml` 发布配置文件——全部靠命令行临时传参 | `DeskSpace.App.csproj` 全文仅 15 行；`find` 无 `.pubxml`【已实测】 |
| **图标资源** | **【发现问题】产物没有任何 PE 资源**（资源目录 RVA = 0）：**无应用图标、无版本信息** | 【已实测】Debug / Release / win-x64 / 单文件四种 exe 的资源数据目录 RVA 与 Size **均为 0** |
| 托盘图标在发布模式下的行为 | **会退化为系统默认图标**：`LoadIcon` 用 `ExtractIconEx(Environment.ProcessPath, 0, …)`，exe 无图标资源 → 返回 0 → 落到 `IDI_APPLICATION` | `TrayIconService.cs:277-314` + 上面 PE 结论【代码证据，未目视实测】 |
| 配置路径 | **正确**：用 `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)\DeskSpace`，不受单文件解压目录影响 | `App.xaml.cs:30-33`【代码证据】 |
| 日志路径 | `%LocalAppData%\DeskSpace\logs\`（sync.log / errors.log） | `AppController.cs:112`、`App.xaml.cs:96-98`；当前 **无 errors.log**（无未处理异常记录）【已实测】 |
| 符号文件 | publish 目录带 `DeskSpace.pdb` / `DeskSpace.Core.pdb`，且 exe 内嵌了开发机绝对路径 `C:\Users\<用户名>\...\obj\Release\...` | 【已实测】低危，属发布卫生问题 |
| 仅在开发机验证的限制 | **明确说明**：以上结论全部基于**本机静态检查 + PE/产物解析**。单文件 exe 的**实机行为**（能否启动、托盘图标、自启写入、图标隐藏）在本机只做过"能启动并显示主窗口"的验证，**没有**在第二台机器 / 干净环境验证过 | — |

---

## 三、问题清单（按严重程度）

### P1 — 发布前应解决

| # | 问题 | 位置 | 证据 | 影响 |
|---|---|---|---|---|
| 1 | **项目无版本控制** | 仓库根目录 | `git rev-parse` → `fatal: not a git repository` | 无法回溯发布版本、无法回滚、无发布基线 |
| 2 | **发布产物无图标、无版本资源** | `DeskSpace.App.csproj`（缺 `<ApplicationIcon>` / `<Version>` / `<FileVersion>`） | 四种 exe 的 PE 资源目录 RVA=0 | Explorer/Alt-Tab/任务栏显示通用图标；托盘图标必然走 `IDI_APPLICATION` 兜底；文件属性无版本号 |
| 3 | **发布配置不入库** | `DeskSpace.App.csproj`、无 `.pubxml` | csproj 仅 15 行，无任何发布属性 | 重建发布产物依赖人肉记命令行，参数漂移无法察觉 |

### P2 — 建议发布前解决（功能正确性 / 用户可感知）

| # | 问题 | 位置 | 证据 | 影响 |
|---|---|---|---|---|
| 4 | **桌面图标"成功"判据偏乐观**：不回读校验 + 广播结果被丢弃 | `DesktopIconService.cs:79`（直接赋值）、`:98`（丢弃 `SendMessageTimeout` 返回值） | 代码 | Explorer 未生效时也报成功，用户以为生效了 |
| 5 | **状态只在启动时读一次，打开设置不刷新** | `AppController.cs:110-111`；`Refresh()` 不在接口上（`DesktopIconService.cs:22-32`、`StartupService.cs:19-32`） | 代码 + `SettingsViewModel.cs:57-59` 注释与实际行为不符 | 用户在系统里手动改过设置后，设置界面显示陈旧；注释宣称的"以系统实际状态为准"未成立 |
| 6 | **无卸载/清理路径** | 全局无卸载器 | 全仓检索 | 删掉 exe 后：HKCU Run 残留、桌面图标可能永久隐藏、`%LocalAppData%\DeskSpace` 残留 |
| 7 | **启动路径也会走强制前台** | `AppController.cs:165` → `WindowActivator.cs:31-41` → `WindowInterop.cs:93` | 代码 | 开机自启 + 登录阶段可能对 Shell 线程做 `AttachThreadInput`，存在罕见的卡顿/异常风险 |

### P3 — 建议修复（健壮性 / 体验）

| # | 问题 | 位置 | 证据 | 影响 |
|---|---|---|---|---|
| 8 | `AttachThreadInput` 目标线程未做"是否挂起"判断 | `WindowInterop.cs:81-93` | 代码 | 极端情况下可能连带阻塞本方 UI 线程 |
| 9 | `UnregisterCore` 失败仍清空状态 | `HotkeyService.cs:131-133` | 代码 | 注销失败时状态与系统不一致，后续重注册可能自冲突 |
| 10 | 注册失败时气泡是空操作（tray 尚未创建） | `AppController.cs:141` vs `:144`、`:669` | 代码 | 少一条通知通道（仍有模态弹窗，影响小） |
| 11 | 状态未知时勾选框仍可操作 | `SettingsViewModel.cs:243` `CanEditDesktopIcons` 恒 true | 代码 | 读取失败时用户可能在未知状态下写入 |
| 12 | publish 目录带 .pdb，exe 内嵌开发机绝对路径 | `publish/win-x64/*.pdb`；exe 内字符串 | 【已实测】 | 发布卫生 / 轻微信息泄露 |

---

## 四、建议修复方案与复测清单

> 以下均为"建议"，**我未实施任何修改**，等你逐条确认后再动手。

| # | 建议修复 | 涉及文件 | 修复后需执行的测试 |
|---|---|---|---|
| 1 | `git init` + 初始提交 + `v0.1.0` tag；`.gitignore` 补 `publish/` | 仓库根 | 无（纯仓库操作） |
| 2 | csproj 增加 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>`（需先准备图标文件）、`<Version>0.1.0</Version>`、`<Company>`/`<Product>` | `DeskSpace.App.csproj` | 重新发布后：**解析 PE 确认资源目录非 0**；目视确认 exe 图标与托盘图标 |
| 3 | 把发布参数写进 csproj 或新增 `Properties/PublishProfiles/win-x64.pubxml`（`RuntimeIdentifier=win-x64`、`SelfContained=false`、`PublishSingleFile=true`、显式 `PublishTrimmed=false`、`PublishReadyToRun=false`） | `DeskSpace.App.csproj` 或新增 pubxml | 用同一配置重新发布，对比产物字节级一致 |
| 4 | `SetHidden` 写完后**回读注册表校验**；`BroadcastSettingChange` 返回 `bool` 并检查 `SendMessageTimeout` 结果，失败时返回带说明的 `OperationResult.Fail` 或 `OkWithWarning` | `DesktopIconService.cs:64-99` | 冒烟第 15 节扩展断言 + 一次真实桌面截图对比（需你授权） |
| 5 | 给 `IDesktopIconService` / `IStartupService` 增加 `Refresh()`，在 `ShowSettings()` 构造 `SettingsViewModel` 前调用 | 两个服务 + 接口 + `AppController.cs:591` | 手工验证：运行中在系统里改"显示桌面图标" / 删除 Run 项 → 重新打开设置应显示新状态 |
| 6 | 至少提供"清理"入口：设置里增加"移除开机启动项并恢复桌面图标"按钮；或在发布说明中写明手动清理三步；长期方案是做安装包 | `SettingsViewModel` / `SettingsWindow.xaml` / 发布说明 | 冒烟新增一条"清理后注册表与图标状态复位" |
| 7 | 启动时用 `activate: false`（普通 `Show`/`Activate`），只在用户显式操作路径启用强制前台 | `AppController.cs:165` 及 `WindowActivator` 调用点 | 重跑阶段 4 验收全量（重点 12 节焦点 + 7 节侧边栏收起） |
| 8 | `ForceForeground` 里先判断前台窗口所属线程是否响应（如 `IsHungAppWindow`），挂起则跳过 attach | `WindowInterop.cs` | 构造"前台窗口无响应"场景验证不卡 UI（需你授权，属高风险实验） |
| 9 | `UnregisterCore` 仅在 `UnregisterHotKey` 成功时清空 `_current`，失败时返回 false 并保留状态 | `HotkeyService.cs:124-134` | 冒烟补充：注销失败路径 |
| 10 | 把热键注册移到 `_tray` 创建之后（`Startup()` 中调整顺序） | `AppController.cs:141/144` | 冒烟 + 构造"快捷键被占用"场景确认气泡出现 |
| 11 | `CanEditDesktopIcons` / `CanEditAutoStart` 在 `StatusText != null`（读取失败）时返回 false | `SettingsViewModel.cs:240-243` | 冒烟补充 |
| 12 | 发布时加 `-p:DebugType=none` 或单独归档 pdb；把 pdb 移出发布目录 | 发布命令 / `publish/` | 发布后确认目录无 pdb |

### 需要你授权才能执行的测试（我会先说明再动）

1. **冒烟测试第 14/15 节**：会临时写 `HKCU\...\Run\DeskSpace` 与
   `HKCU\...\Explorer\Advanced\HideIcons`，并**短暂隐藏桌面图标后还原**。
2. **真实桌面图标可见性复测**：截图对比（baseline / hidden / restored），验证 `gt4-02` 与 `gt4-03`
   MD5 相同的"重复切换无视觉变化"疑点（历史证据：`866caef1683e8005fcd6b658fa78eb8d` 两张相同）。
3. **单文件产物实机验证**：运行 `publish/win-x64/DeskSpace.exe`，确认托盘图标、自启路径 `CommandLine`、
   图标隐藏在发布模式下与 Debug 一致。
4. **第二台机器 / 干净环境验证**：确认"未装 .NET 10 Desktop Runtime"时的报错是否友好。

---

## 五、发布模式与运行时依赖（明确结论）

| 项目 | 结论 |
|---|---|
| 目标框架 | `net10.0-windows`（.NET 10） |
| 目标平台 | `win-x64`（仅 64 位 Windows；不支持 x86/ARM64） |
| 发布模式 | **单文件（PublishSingleFile = true）** |
| 是否自包含 | **否（SelfContained = false，framework-dependent）** |
| ReadyToRun | **未启用** |
| 代码裁剪 | **未启用**（符合"不盲目裁剪"要求） |
| **目标电脑必须安装** | **.NET 10 Desktop Runtime（`Microsoft.WindowsDesktop.App` 10.0.0）** —— 因为用了 WPF，**仅装 .NET Runtime 不够** |
| 产物 | `publish/win-x64/DeskSpace.exe`，387 KB（另含两个 .pdb） |
| 配置 / 日志位置 | `%LocalAppData%\DeskSpace\`（settings.json、library.json、logs\），与单文件解压目录无关 |
| 验证范围限制 | 全部结论基于本机静态检查与产物解析；**未在第二台机器或干净环境做过实机验证** |

---

## 六、是否建议进入正式发布

**建议：暂不发布（Conditional No），先解决 P1 三项 + P2 第 4、5、7 项，再发。**

理由：
- 功能层面（三种模式、快捷键、托盘、同步）确实已通过 48/48 真实验收与 287/287 冒烟，主流程是稳的；
- 但**发布工程层面**还差三件事：没有版本控制、产物没有图标和版本号、发布参数不可复现；
  这三项不影响"能不能跑"，但影响"能不能作为一次可回溯、可维护的正式发布"。
- 另外**桌面图标成功的判据偏乐观**是唯一可能让用户误判的功能性问题，且没有卸载清理路径，
  一旦用户隐藏图标后卸载，桌面会莫名少图标且找不到原因。

**发布后仍需承担的风险（即使修完上述项）**：
1. 依赖目标机器预装 **.NET 10 Desktop Runtime**；未安装时启动会失败/提示依赖框架缺失（未验证提示是否友好）。
2. 仅在本机（Win11 26H2 / Build 26300、多显示器 + 虚拟显示器、DPI 1.25）验证过；
   无第二台机器、无干净用户环境、无标准 DPI/单显示器验证。
3. 无安装包、无自动更新、无卸载器 → 分发与后续升级靠手工替换 exe。
4. 开机自启与桌面图标两项**从未进入真实验收脚本**（`stage4-accept.ps1` 中相关关键字出现次数为 **0**），
   只有冒烟层的注册表级断言 + 一次历史截图证据。
5. 托盘菜单项文本无法从外部读取（已知自动化限制），菜单行为靠各条目实际效果间接验证。

---

**本次审查到此结束。所有发现均为只读取证，未修改任何代码与系统设置。**
请告诉我下一步：确认开启哪些修复项，以及是否授权执行 §四 中需要授权的四类测试。
