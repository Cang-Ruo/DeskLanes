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

对 `publish/win-x64/DeskSpace.exe` 全文件检索 `TTjustTT`、`.cs` 等特征串，均未命中
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

## 四、改动后仍需人工确认的项（未做，需你授权）

以下四项会真实改变桌面 / 系统状态或弹出窗口，本轮**未执行**：

1. **单文件产物实机启动**：直接运行 `publish/win-x64/DeskSpace.exe`，确认托盘图标（现在应显示新图标而非通用图标）、
   悬浮窗、热键在发布模式下与 Debug 表现一致。当前配置为 `WindowMode=Floating`，启动会弹出悬浮窗。
2. **真实桌面图标可见性复测**：截图对比 baseline / 隐藏 / 恢复，复核历史疑点
   （`gt4-02` 与 `gt4-03` 两张 MD5 相同 = 重复切换无视觉变化）。
3. **运行中改系统设置 → 重新打开设置是否显示新状态**（对应修复项 5）：
   软件运行时用 Windows 右键菜单手动切换"显示桌面图标"，再打开设置看是否同步。
4. **第二台机器 / 干净环境**：验证未装 .NET 10 Desktop Runtime 时的报错是否友好。

---

## 五、遗留风险（修完 12 项后仍然存在）

1. 依赖目标机预装 .NET 10 Desktop Runtime，缺依赖时的错误提示友好度未验证。
2. 仅在本机验证（Win11 Build 26300、多显示器 + 虚拟显示器、DPI 1.25），无第二台机器、无单显示器/标准 DPI 验证。
3. 无安装包、无自动更新、无卸载器；分发靠手工替换 exe，清理靠设置里的「卸载前清理」按钮。
4. 开机自启与桌面图标**仍未进入真实验收脚本**（`stage4-accept.ps1` 中相关关键字出现数为 0），
   只有冒烟层的注册表级断言与历史截图证据。
5. 托盘菜单项文本无法从外部读取（已知自动化限制）。
