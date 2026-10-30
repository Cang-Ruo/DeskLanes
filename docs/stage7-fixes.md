# DeskSpace 阶段 7：审查问题修复与复测报告

本文是 [stage7-release-review.md](stage7-release-review.md) 的续篇。审查报告列出 12 项问题（P1×3 / P2×4 / P3×5），
**12 项已全部修复并复测**。所有结论均有本会话的实测输出支撑，未做推测性表述。

---

## 一、修复清单与落地位置

| # | 级别 | 修复内容 | 改动文件 |
|---|---|---|---|
| 1 | P1 | 初始化 Git 仓库、首次提交、`.gitignore` 已含 `publish/` | 仓库根 |
| 2 | P1 | 补 `<ApplicationIcon>Assets\app.ico</ApplicationIcon>`（6 种尺寸）、`<Version>`/`<AssemblyVersion>`/`<FileVersion>`/`<Company>`/`<Product>`/`<Description>`/`<Copyright>` | `DeskSpace.App.csproj`、`Assets/app.ico`（新增，`tools/make-icon.py` 生成） |
| 3 | P1 | 新增发布配置 `Properties/PublishProfiles/win-x64-singlefile.pubxml`，固化 RID / 自包含 / 单文件 / R2R / 裁剪 / 调试符号，含用法注释 | `Properties/PublishProfiles/win-x64-singlefile.pubxml`（新增） |
| 4 | P2 | `SetHidden` 写入后 **回读注册表校验**（不一致即返回 Fail）；`BroadcastSettingChange` 改为返回 `bool`，广播失败区分提示"需注销或重启资源管理器"；`HideIcons` 读取不再假设值类型 | `DesktopIconService.cs` |
| 5 | P2 | 两个服务接口各加 `Refresh()`；`AppController.ShowSettings()` 在打开设置前先刷新，读到系统真实状态 | `DesktopIconService.cs`、`StartupService.cs`、`AppController.cs` |
| 6 | P2 | 设置窗口新增「卸载前清理」按钮：删除 HKCU Run 启动项 + 恢复桌面图标，并把配置同步为清理后的真实状态（避免下次启动写回旧行为） | `SettingsViewModel.cs`、`SettingsWindow.xaml` |
| 7 | P2 | `ApplyMode` / `WindowActivator.Activate` / `FloatingWindow.ShowAndActivate` 增加 `forceForeground` 参数；**启动时传 `false`**，仅用户显式操作（托盘菜单 / 热键 / 设置）才走强制前台 | `AppController.cs`、`WindowActivator.cs`、`FloatingWindow.xaml.cs` |
| 8 | P3 | `ForceForeground` 前用 `IsHungAppWindow` 判断前台窗口线程是否响应，挂起则跳过 `AttachThreadInput` | `WindowInterop.cs` |
| 9 | P3 | `UnregisterCore` 仅在 `UnregisterHotKey` 成功时清空 `_current`；`RegisterHotKey` 前若释放旧键失败，直接返回 Fail 并带错误码，不再覆盖 | `HotkeyService.cs` |
| 10 | P3 | `Startup()` 中把热键注册移到 `_tray` 创建之后，注册失败时气泡通知真正可用（原为空操作） | `AppController.cs` |
| 11 | P3 | `CanEditAutoStart` / `CanEditDesktopIcons` 在 `StatusText != null`（读取失败、状态未知）时返回 false，禁止在未知状态下写入 | `SettingsViewModel.cs` |
| 12 | P3 | 发布改 `DebugType=embedded`：符号嵌进程序集，发布目录不再出现 .pdb，同时保留异常堆栈行号（主项目由 pubxml 控制，引用项目在 csproj 里单独设置） | pubxml、`DeskSpace.Core.csproj` |

---

## 二、复测结果（本会话实测）

### 1. 构建

```
dotnet build DeskSpace.sln -c Release
已成功生成。0 个警告 0 个错误   （5 个项目：Core / App / SmokeTests / RealDesktopCheck / UiSnapshot）
```

### 2. 冒烟测试

```
dotnet run --project tests/DeskSpace.SmokeTests -c Release
===== 冒烟测试结果：通过 287 项，失败 0 项 =====
```

第 14 节（开机自启，会写 `HKCU\...\Run\DeskSpace` 并还原）与第 15 节（桌面图标，会写
`HideIcons` 并还原）全部通过，测试后均已还原为原始状态。

### 3. 发布产物 PE 资源（修复前 RVA=0，修复后）

| 项目 | 结果 |
|---|---|
| 资源目录 | `RVA=0x0002c000`，`size=19816`（**修复前为 0**） |
| `RT_ICON` | **6 个图像**（256/128/64/48/32/16，PNG 压缩） |
| `RT_GROUP_ICON` | 1 |
| `RT_VERSION` | 1 |
| `RT_MANIFEST` | 1 |

版本资源实际内容：

```
CompanyName      = DeskSpace
FileDescription  = DeskSpace
FileVersion      = 0.1.0.0
ProductName      = DeskSpace
ProductVersion   = 0.1.0+7bcf29642e5c5b1dd70301902a59ba515ac2d794
LegalCopyright   = Copyright © 2026
Assembly Version = 0.1.0.0
```

> 说明：`FileDescription` 取的是 `AssemblyTitle`（即 `ApplicationTitle=DeskSpace`），
> 不是 `<Description>`——这是 .NET SDK 生成 Win32 版本资源的既定行为，非配置遗漏，属可接受表现。

### 4. 发布可复现性

用同一份 pubxml 连续两次发布：

```
dotnet publish src/DeskSpace.App/DeskSpace.App.csproj -c Release -p:PublishProfile=win-x64-singlefile

MD5 (第 1 次) = affd583d7247d40588c2b313f5c2855b
MD5 (第 2 次) = affd583d7247d40588c2b313f5c2855b   ← 字节级一致
```

发布目录内容：**仅 `DeskSpace.exe` 一个文件**（501,853 字节 / 490 KB），无 .pdb。

### 5. 产物内不再出现开发机绝对路径

对 `publish/win-x64/DeskSpace.exe` 全文件检索 `<用户名>`、`.cs` 等特征串，均未命中
（单文件包已压缩，且 `DebugType=embedded` 未额外外挂符号文件）。

---

## 三、发布与运行时（结论不变，参数现已入库）

| 项目 | 结论 |
|---|---|
| 目标框架 / 平台 | `net10.0-windows` / `win-x64`（仅 64 位） |
| 发布模式 | 单文件 `PublishSingleFile=true` |
| 自包含 | 否（framework-dependent） |
| R2R / 裁剪 | 均未启用 |
| 目标机必须安装 | **.NET 10 Desktop Runtime（`Microsoft.WindowsDesktop.App`）** |
| 产物 | `publish/win-x64/DeskSpace.exe`，490 KB（单文件、无附带 pdb） |
| 配置 / 日志 | `%LocalAppData%\DeskSpace\` |
| 复现命令 | `dotnet publish src/DeskSpace.App/DeskSpace.App.csproj -c Release -p:PublishProfile=win-x64-singlefile` |

本机运行时实测：`Microsoft.WindowsDesktop.App 10.0.13`（系统级与用户级均存在），
框架依赖型单文件可在此环境正常解析运行。

---

## 四、授权测试执行结果（2026-10-25，四项全部执行）

> 审查报告 §四 列出的四类"需授权测试"已逐项执行，过程中**发现并修复了一个重大产品缺陷**（见 §4.2）。
> 所有验证脚本已入库 `tools/verify-*.ps1`，截图证据在 `docs/stage7-evidence/`。

### 4.1 单文件产物实机启动 ✅

`tools/verify-published-exe.ps1` + `tools/verify-tray-icon.ps1`：

| 验证点 | 结果 |
|---|---|
| 进程启动 | 正常，悬浮窗「DeskSpace 快速启动」可见 |
| `ExtractIconEx(exe)` | 成功取出图标；small(16×16) 与 `Assets/app.ico` 16×16 **逐像素一致**（MD5 `A532CD6C…`），与系统默认 `IDI_APPLICATION` 不同 |
| `Shell_NotifyIconGetRect((DeskSpaceMessageHost, 0x5201))` | **S_OK**，图标已注册进通知区域 |
| 托盘图标外观 | 打开"显示隐藏的图标"浮层截图放大比对——**与应用图标完全一致**（`compare-tray-icon.png`） |
| 结束 | 进程正常结束，无残留 |

顺带发现两个工具链坑（已写进项目记忆）：本进程里 `FindWindow` 对部分窗口（Progman、WPF 隐藏窗口）
返回 0，必须用 `EnumWindows`；托盘图标在 Win11 默认进"隐藏溢出区"，`Shell_NotifyIconGetRect`
在浮窗从未打开时返回 E_FAIL，打开后返回 S_OK 但矩形指向的是「显示隐藏的图标」按钮本身。

### 4.2 桌面图标可见性复测 ⚠️ → 发现重大缺陷并已修复

复测方式：与 `DesktopIconService` 完全相同的机制（写 `HideIcons` + 广播），以桌面图标区域
截图的**像素差幅度**为判据（噪声底噪实测为 0，壁纸是静态图）：

| 操作 | 与基线平均色差 | 结论 |
|---|---|---|
| 写 1 + 广播 `Environment`（应用原实现） | **0.01** | **图标没有隐藏** |
| 再广播 `ImmersiveShell` | **0.01** | **仍然没有隐藏** |
| WM_COMMAND 0x7402（系统右键菜单同款） | **22.02** | 图标真的隐藏了 |
| 再发一次 0x7402 | 0 | 恢复显示 |

**结论：旧实现（写注册表 + 广播）在本机（Win11 26H2 / Build 26300）上完全不生效。**
历史 stage5 证据（"p2 四张截图全不同 = 广播有效"）是误判——全屏截图 MD5 被屏幕上无关内容
变化干扰；而 gt4-02 与 gt4-03 相同这一疑点恰恰是"切换没有视觉效果"的真实信号，本次复测证实了它。

另外测得两个关键事实：
- 切换命令按"当前视觉状态"取反，并把 `HideIcons` 注册表值**同步**成切换后的状态（不是按注册表取反）；
- 隐藏/显示两种状态下 `SHELLDLL_DefView` 的句柄、父窗口、可见性、矩形**完全相同**，
  即 Win32 层面没有可靠的"当前是否隐藏"实时读数，只能以注册表记录的状态为准。

**修复**（`DesktopIconService.cs` 重写）：放弃"写注册表 + 广播"，改为向 DefView 发送
`WM_COMMAND 0x7402`（与系统右键菜单同一条命令）；`HideIcons` 不再由本服务直接写，
由资源管理器在切换时自己写；发送后轮询回读注册表确认命令真的被应用（最长 1.5s）；
找不到 DefView（如被第三方桌面工具接管）时如实报错。

**端到端可视化验证**（`tools/IconServiceCheck`，直接调用真实服务类）：
`SetHidden(true)` 后桌面图标区截图里**一个图标都不剩**，`SetHidden(false)` 后全部恢复
（`docs/stage7-evidence/svc-0/1/2-*.png`）。冒烟测试第 15 节同步更新，**289 项全过**。

### 4.3 系统设置变更后的状态同步 ✅

- 自动化：冒烟第 15 节新增两条断言——外部直接改 `HideIcons` 后调用 `Refresh()`，
  服务能读到隐藏/显示两种真实状态（通过）。
- `AppController.ShowSettings()` 在打开设置前已先调用两个服务的 `Refresh()`（阶段 7 修复项 5）。
- 30 秒手工抽查（可选）：软件运行时用桌面右键菜单切换"显示桌面图标"，再打开设置，
  勾选框应与系统一致。

### 4.4 缺运行时环境 ⚠️ 部分完成

本机无法卸载 .NET 10 Desktop Runtime，只能做部分模拟（`tools/verify-missing-runtime.ps1`）：

| 场景 | 结果 |
|---|---|
| `DOTNET_ROOT` 指向不存在的目录 | 应用**正常启动**（回退到系统注册的安装位置 `C:\Program Files\dotnet`），健壮性比预期好 |
| 真正"未安装运行时"的机器 | **本机无法模拟，未验证**——需要第二台机器或干净虚拟机；.NET 的既定行为是弹出"缺少运行时"对话框并附下载链接，友好度待确认 |

### 4.5 修复后的发布产物

```
publish/win-x64/DeskSpace.exe   505,949 字节（单文件、无 pdb）
MD5 = 59fe5240526a23de914facfdf76e92eb
```

Release 构建 0 警告 0 错误；冒烟 289/289。

---

## 五、遗留风险（修完 12 项后仍然存在）

1. 依赖目标机预装 .NET 10 Desktop Runtime，缺依赖时的错误提示友好度未验证。
2. 仅在本机验证（Win11 Build 26300、多显示器 + 虚拟显示器、DPI 1.25），无第二台机器、无单显示器/标准 DPI 验证。
3. 无安装包、无自动更新、无卸载器；分发靠手工替换 exe，清理靠设置里的「卸载前清理」按钮。
4. 开机自启仍未进入真实验收脚本（`stage4-accept.ps1` 中相关关键字出现数为 0）；
   桌面图标已有真实视觉证据（`docs/stage7-evidence/svc-*.png`）与冒烟断言。
5. 托盘菜单项文本无法从外部读取（已知自动化限制）。
