# DeskSpace 发布准备检查（阶段 8 快照）

> **这是 2026-10-26 的历史快照，不是当前状态。** 当时的结论（含"导出诊断包未开始"、
> "冒烟 292 项"等）都已被后续阶段改写。**要看当前状态请读**
> `docs/project-overview-and-roadmap.md` §五（唯一基线）与 `docs/known-limitations.md`。

> 检查时间：2026-10-26 09:0x（自动化每周巡检，第 2 次）
> 范围：**只检查与汇报，未修改任何代码**（仅覆盖本报告文件）
> 基线：`docs/project-overview-and-roadmap.md` §七 P1

## 0. 回归复核（本轮实测）

| 项目 | 命令 | 结果 |
|---|---|---|
| Release 构建 | `DOTNET_ROOT=C:/Users/<用户名>/.dotnet dotnet build DeskSpace.sln -c Release` | **0 警告 / 0 错误**，5 个项目，1.86s（增量） |
| 冒烟测试 | `dotnet run --project tests/DeskSpace.SmokeTests -c Release --no-build` | **292 / 292 全过，失败 0** |

无回归。相比上周（289/289）多出的 3 条是阶段 8 第二步修 NaN 保存缺陷时补的回归断言
（窗口偏好序列化不再因 `Left/Top` 为 NaN 抛异常）。

> 环境注意：PATH 里的 `dotnet` 无 SDK，必须用 `C:\Users\<用户名>\.dotnet\dotnet.exe` + `DOTNET_ROOT`，
> 否则报 "No .NET SDKs were found"（本轮第一次构建即因此失败）。

## 1. 四项 P1 状态总览

| # | P1 项 | 上周 | **本轮** | 关键证据 |
|---|---|---|---|---|
| 1 | 自包含发布与便携版并存 | 进行中 | **已完成**（有 1 个新风险） | `win-x64-selfcontained.pubxml` 已提交；两个产物都在；`install.ps1 -Edition` 可选 |
| 2 | 干净环境 / 跨设备 + DPI 矩阵 | 未开始 | **进行中** | 干净虚拟机已跑通（无运行时场景）；DPI 100%/125% 已过，150%/200% 未测 |
| 3 | 开机自启进真实验收脚本 | 未开始 | **进行中** | `tools/stage8-step3-check.ps1` 两段式已交付，本机自检 9 PASS/2 FAIL；**虚拟机实测未做** |
| 4 | 可交付版本三项 + 三份文档 | 进行中 | **进行中** | 异常兜底✅、迁移测试✅、**导出诊断包未做**；文档齐备（含两处待订正） |

**整体判断：四项 P1 全部脱离"未开始"，但没有任何一项可以宣布完成。**
唯一的硬阻塞已经从"缺第二台机器"变成"**虚拟机第二轮实测需要用户执行**"（P1-3）。

## 2. 逐项证据

### P1-1 自包含发布（已完成，但产物已过期）

已完成：

- `src/DeskSpace.App/Properties/PublishProfiles/win-x64-selfcontained.pubxml` **已提交**
  （`git ls-files` 命中，上周是 `??`；提交 `10c303f`「阶段 8 第一步」）
- 两个产物并存且路径分离：
  - `publish/win-x64/DeskSpace.exe` —— 505,949 B，MD5 `ef6f5091…`（便携版，依赖系统运行时）
  - `publish/win-x64-selfcontained/DeskSpace.exe` —— 65,056,904 B，MD5 `a69ffc00…`（自包含）
- `tools/install.ps1` 已支持版本选择：`-Edition auto|portable|selfcontained`、
  `-SourceExe`（优先级高于 `-Edition`）、按体积识别（`>20MB` → selfcontained）、
  两者不一致时打印提示并以实际文件为准
- 验收：`tools/verify-edition-install.ps1` **40 项检查 0 失败**，日志
  `docs/stage8-step1-verify.log`，报告 `docs/stage8-step1.md`；已证明
  "发布自包含版不会动便携版产物"

**本轮新发现（风险，非阻塞但影响"可交付"）**：

- **两个 exe 都是 2026-10-25 06:01 的产物，已过期。** 10-26 的 UI 轻玻璃改版
  （`src/DeskSpace.App/Themes/GlassTheme.xaml`、`WindowBackdropService.cs`、
  `DwmInterop.cs` 等 16 个文件修改 + 4 个新增）**全部未提交、未发布**：
  `git status` 显示 16 个 `M` + 5 个 `??`，`DeskSpace.dll` 时间戳 10-26 04:36 > exe 的 10-25 06:01。
  **现状：任何人现在安装，拿到的都是改版前的界面。**
- MD5 与 `docs/stage8-step1.md` 记录的 `ab8ea1c9…` / `2b70c031…` 不同，且自包含体积
  65,056,570 → 65,056,904（+334 B）。原因已核实：10-25 修完侧边栏两个缺陷后重新发布过
  （提交 `b66612a`），MD5 随重新编译变化属已知行为，不是路径混淆。

### P1-2 跨设备与 DPI 矩阵（进行中）

已完成（有证据）：

| 场景 | 证据 | 结论 |
|---|---|---|
| **干净机 + 未装 .NET 10 桌面运行时** | 干净 Win11 虚拟机（专业版 Build 26200、x64、1920×1080、无运行时）：自包含版双击直接跑起来；便携版弹 .NET 宿主标准报错框 + Download it now 按钮（不静默消失） | ✅ 两种都符合预期 |
| **已装运行时（本机对照）** | Win11 家庭版 Build 26300，2560×1440 | ✅ |
| **DPI 100%** | 虚拟机：120→96，窗口 1100×720 @(370,136) 中心点在屏内 | ✅ |
| **DPI 125%** | 虚拟机 1375×900 @(854,144) / 本机 775×650 @(64,64)，均按物理像素缩放不变形 | ✅ |
| **单显示器 · 三种模式** | 全屏 ✅、悬浮窗 ✅、侧边栏 ✅（`tools/probe-sidebar.ps1`：贴右 55×950 通高，收起态只剩 1 个「◀」按钮） | ✅（修了两个缺陷后） |

未完成：

- **DPI 150% / 200% 未测**（`docs/stage8-step2.md` §106 记录为已知限制）
- **虚拟机的侧边栏截图未回传** —— 侧边栏结论目前靠本机 `probe-sidebar.ps1` 的几何数据，
  缺干净环境的视觉证据（文档 §3.4 已按"第二步完成"收口，MEMORY.md 记为"截图待补"，口径需统一）
- 报错框语言未在**中文**系统上验证（虚拟机是英文系统，框是英文）

证据文件：`docs/stage8-step2.md`、`tools/vm-check.ps1`、`tools/probe-sidebar.ps1`、
验收包 `publish/vm-package/`（自包含 + 脚本 + 说明）、`publish/vm-package-portable/`（便携版对照）。

### P1-3 开机自启与图标隐藏交互（进行中）

已完成：

- `tools/stage8-step3-check.ps1`（18,661 B，两段式）**已交付**并覆盖用户要求的全部四类场景：
  A 自启启用写 Run 项 / B 图标隐藏→恢复 / C 异常强杀后状态保持 + `errors.log` 不增长 /
  D Explorer 重启后状态保持（WMI 拉起 + 轮询 60s + 失败给手动恢复指引）/
  F 重启后自启生效 / G 禁用删 Run 项 / H 卸载清理含图标恢复
- 本机自检：**9 PASS / 2 FAIL**，日志 `docs/stage8-step3-verify.log`。
  两个 FAIL 是预期内的（自检只模拟"隐藏"没真的取消勾选），恰好证明
  "恢复后 HideIcons = 0" 这条判据有效 —— **判据可信**
- C 段实测有效：强杀后 `HideIcons` 仍为 1，`errors.log` 行数 26 → 26 未增长
- 顺带修掉两个真缺陷：`uninstall.ps1` 找 DefView 的方式（Progman 不可见时误判）+
  找不到 shell 时的注册表兜底

未完成 / 阻塞：

- **虚拟机实测未执行**（`docs/stage8-step3.md` §6 待办）。需要用户：把
  `publish/vm-package/` 的新 exe 重拷进虚拟机 → 跑 Phase 1 → 重启 → 跑 Phase 2 →
  回传 `stage8-step3-report.txt` + 4 张截图。脚本会写 HKCU Run、动桌面图标、
  重启 Explorer，**必须在用户自己的虚拟机上跑，不能在本机代跑**

### P1-4 可交付版本三项与三份文档（进行中）

| 子项 | 状态 | 证据 |
|---|---|---|
| 全局未捕获异常兜底 | **已完成** | `src/DeskSpace.App/App.xaml.cs` 20-95 行：Dispatcher + AppDomain + TaskScheduler 三处兜底 → 写 `%LocalAppData%\DeskSpace\logs\errors.log` + 弹友好提示。实测日志存在（3,634 B，10-25 05:54）。**注：roadmap §七 P3-12 仍把它列为"建议做"，基线文档该项已过时** |
| 关键数据迁移回归测试 | **已完成（当前版本口径）** | `DataVersions.Current = 1`（`WorkspaceModels.cs` 286-291）。冒烟 §11 覆盖 5 条路径：损坏文件 / 空文件 / Version 999 过新回退 / Version 0 旧版迁移 / 缺 Version 字段；`settings.json` 损坏也有用例。因为目前只有 v1 一个版本，**不存在"未覆盖的升级路径"**；将来升到 v2 时必须同步补用例 |
| 诊断日志 | 已完成 | `sync.log`（51,354 B，有 512 KB 上限）+ `errors.log`，都在 `%LocalAppData%\DeskSpace\logs\` |
| **导出诊断包** | **未开始** | `src/` 下检索"诊断 / 导出 / zip / Export"零命中（`MainViewModel.cs` 里那 1 处"诊断"是同步日志提示，不是导出入口）。**这是 P1-4 唯一缺口** |
| 安装说明 | 齐备 | `docs/install-and-uninstall.md` §安装 / §两个版本怎么选 / §在另一台电脑上安装 |
| 卸载说明 | 齐备 | 同文档 §卸载 + §本机实测记录（10-25 装→卸→装全流程） |
| 已知限制说明 | 齐备（未独立成文） | 同文档 §已知限制（7 条）+ roadmap §六（6 条） |

**文档待订正（本轮发现，不是代码问题，但会误导下一个读者）**：

1. `docs/install-and-uninstall.md` §已知限制 第 1 条仍写"未安装时的报错友好度**尚未**在干净环境验证过"
   —— 阶段 8 第二步已在干净虚拟机验证过（报错框 + 下载按钮），该表述已过时。
2. 同文档 第 5 条写"自包含版才会解压出 coreclr.dll，这是区分两版是否自带运行时的可靠判据"
   —— **结论错误**。`docs/stage8-step1.md` §123-126 与 `tools/verify-edition-install.ps1` §110-113
   已实测订正：coreclr.dll **不落盘**（宿主从 bundle 内加载），可靠判据是 `wpfgfx_cor3.dll`。

## 3. 当前阻塞点

| # | 阻塞点 | 影响 | 谁能解 |
|---|---|---|---|
| 1 | **虚拟机第二轮实测未执行**（P1-3） | 阶段 8 第三步无法收口 | 用户（需在其虚拟机上跑，脚本会改 Run 项 / 桌面图标 / 重启 Explorer） |
| 2 | **DPI 150% / 200% 未测**（P1-2） | 高分屏用户可能遇到定位问题 | 用户改虚拟机缩放跑一次；或接受为已知限制 |
| 3 | **10-26 UI 改版未提交、发布产物已过期** | 现在装到任何机器都是旧界面 | 我（需授权：重新发布 + 提交） |
| 4 | **导出诊断包未做**（P1-4） | 跨设备排障只能靠口头描述 | 我（需授权） |
| 5 | 基线文档 roadmap §七 P3-12 过时 | 会把已完成项当待办 | 我（需授权） |

## 4. 下一步最值得做的两个动作

1. **重发布 + 提交，让产物对得上代码**（阻塞点 3，最优先）。
   现在的状态是"代码比产物新一天"，任何跨设备验证（包括用户接下来要跑的虚拟机第三步）
   如果基于旧 exe，结论都不算数。动作：提交 10-26 UI 改版 → 重跑两条发布路径 →
   刷新 `publish/vm-package/` 里的 exe → 再让用户跑第三步。工作量小，且是后面所有验证的前置。
2. **补「导出诊断包」**（阻塞点 4，P1-4 最后一块拼图）。
   一键把 `settings.json` + `library.json` + `logs\` 打包成 zip 放到桌面，
   纯标准库 `System.IO.Compression`，零第三方依赖，符合项目红线。
   在跨设备排障场景下它是唯一的信息来源 —— 用户报"我这边打不开"，没有诊断包就只能靠猜。

（若用户愿意，可并行：把 roadmap §七 P3-12 与 install-and-uninstall.md 两处过时表述一并订正。）
