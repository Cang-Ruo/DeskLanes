# 阶段 8 第一步：补齐发布版本（自包含版与便携版并存）

日期：2026-10-25
范围：只做"发布版本"这一件事，不新增软件功能。

---

## 0. 目标与验收标准

按阶段 8 路线定义的第一步：

> 新增 Self-contained 自包含发布配置，与现有约 506 KB 的 framework-dependent 版本并存。
> 安装脚本提供明确的版本选择，并验证两种版本的发布路径不会混淆。
> **验收标准：两种版本都能独立发布、正确安装和卸载，原有便携版不受影响。**

工作方式：先检查现状 → 再实施 → 再运行验收 → 给出证据，而不是只报告"功能已完成"。

---

## 1. 动手前的现状

| 项目 | 改之前 |
|---|---|
| 发布配置 | 只有 `win-x64-singlefile.pubxml`（`SelfContained=false`）→ `publish\win-x64\` |
| 产物 | 只有一种：约 494 KB，目标机**必须**已装 .NET 10 桌面运行时 |
| install.ps1 | 没有版本概念，只认"脚本同目录 exe → 回退 `publish\win-x64\DeskSpace.exe`" |
| 风险 | 发给别人时，对方没装运行时就跑不起来，且没有替代方案 |

---

## 2. 做了什么

| 文件 | 改动 |
|---|---|
| `src/DeskSpace.App/Properties/PublishProfiles/win-x64-selfcontained.pubxml` | 新增。自包含单文件发布配置，输出到独立目录 `publish\win-x64-selfcontained\` |
| `tools/install.ps1` | 新增 `-Edition portable\|selfcontained` 版本选择；两个产物都在时列出候选让用户选；`-SourceExe` 优先级最高并按体积自动识别版本；运行时检查只对便携版生效；版本写入卸载项 `Edition` |
| `tools/uninstall.ps1` | 卸载时回显"已安装版本"，便于确认卸的是哪一版 |
| `tools/verify-edition-install.ps1` | 新增。本阶段的可重复验收脚本（40 项检查） |
| `tools/probe-launch.ps1` | 新增。单个 exe 的启动探针：进程、可见窗口、解压目录内容 |
| `docs/install-and-uninstall.md` | 补两个版本的取舍、参数表、新限制 |

自包含版的关键设置（每一条都有原因，写在 pubxml 注释里）：

```
SelfContained=true                     自带运行时
PublishSingleFile=true                 仍然是单文件 exe
IncludeNativeLibrariesForSelfExtract=true   WPF 有原生组件，不允许解压会启动失败
PublishTrimmed=false                   不裁剪：WPF 反射与 COM 互操作多，裁剪风险大于收益
DebugType=embedded                     符号嵌入程序集，发布目录不出现 .pdb
```

---

## 3. 验收结果：40 项检查，0 失败

验收脚本：`tools/verify-edition-install.ps1`（可重复执行，自带清理）
完整输出：`docs/stage8-step1-verify.log`

### A. 两条发布路径互不干扰（11 项）

| 检查 | 结果 |
|---|---|
| 两个版本都发布成功 | ✅ |
| **自包含发布后，便携版产物 MD5 不变** | ✅ `ab8ea1c9…` → `ab8ea1c9…` |
| 便携版体积未变 | ✅ 505,949 字节 |
| 便携版仍输出到 `publish\win-x64`（目录里只有 1 个 exe） | ✅ |
| 便携版重复发布 MD5 稳定 | ✅ |
| 自包含版输出到独立目录 `publish\win-x64-selfcontained`（只有 1 个 exe） | ✅ |
| 自包含版体积远大于便携版 | ✅ 65,056,570 vs 505,949 |

### B. 自包含版：安装 → 启动 → 卸载（13 项）

| 检查 | 结果 |
|---|---|
| 安装落盘，大小 65,056,570 / MD5 `2b70c0317d6ce8b144883c0cb737cda7` 与源一致 | ✅ |
| 输出标注 `selfcontained`，卸载项 `Edition=selfcontained` | ✅ |
| 运行时检查被跳过（自带运行时） | ✅ |
| 开始菜单快捷方式生成 | ✅ |
| **启动 15 秒后进程仍在、有可见窗口「DeskSpace 快速启动」** | ✅ |
| **启动后解压出 `wpfgfx_cor3.dll` 等 WPF 原生组件**（自带运行时的直接证据） | ✅ `%TEMP%\.net\DeskSpace\A0xPkY6hFtLR\` |
| 卸载后：安装目录 / 卸载项 / 开始菜单目录全部删除，配置目录保留 | ✅ |

### C. 便携版：安装 → 启动 → 卸载（8 项）

| 检查 | 结果 |
|---|---|
| 安装落盘，大小 505,949 / MD5 `ab8ea1c96d902fafd99033a052eb54c1` 与源一致 | ✅ |
| 卸载项 `Edition=portable`，运行时检查命中 `Microsoft.WindowsDesktop.App 10.0.13` | ✅ |
| 启动后进程仍在、有可见窗口 | ✅ |
| **解压目录为空（用系统已装的 WPF，不自带原生组件）** | ✅ 与自包含版形成对照 |
| 卸载后安装目录已删除 | ✅ |

### D. 版本选择确实生效（5 项）

不带 `-Edition` 时脚本列出两个候选：

```
检测到两个发布版本：
  [1] portable       约 0.5 MB  需要目标机已装 .NET 10 桌面运行时  (…\publish\win-x64\DeskSpace.exe)
  [2] selfcontained  约 62 MB   自带运行时，目标机无需安装任何东西 (…\publish\win-x64-selfcontained\DeskSpace.exe)
```

- 输入 `1` → 落盘 505,949 字节（便携版）✅
- 输入 `2` → 落盘 65,056,570 字节（自包含版）✅

### E. `-SourceExe` 优先级最高（3 项）

`-SourceExe` 指向自包含 exe、同时 `-Edition portable` 时：装的是自包含版，
版本按**文件实际体积**识别为 `selfcontained`，运行时检查相应跳过 ✅

---

## 4. 过程中发现并修掉的问题

这七项都是验收跑出来才暴露的，不是"写完就过"：

1. **pubxml 注释里的 `--` 让 MSBuild 加载失败**（MSB4024）。XML 注释不能含连续 `--`，
   表格分隔线会踩雷 → 注释改写。
2. **`install.ps1` 格式化串 `{1}` 越界**：候选列表第 2 行抛 FormatError，
   "两个版本都在"这条路径根本走不通 → 改 `{0}`。
3. **窗口判据选错**：`Process.MainWindowHandle` 对 DeskSpace 恒返回 0（WPF 主窗口隐藏），
   一度误报"没窗口"。实际用 `EnumWindows` 按 PID 找可见窗口，能拿到标题「DeskSpace 快速启动」。
4. **"自带运行时"的判据选错**：原以为会解压 `coreclr.dll`，实测自包含版**只解压**
   `wpfgfx_cor3.dll`、`PresentationNative_cor3.dll`、`D3DCompiler_47_cor3.dll`、
   `PenImc_cor3.dll`、`vcruntime140_cor3.dll`（coreclr 由宿主从 bundle 内加载，不落盘）。
   → 改用 `wpfgfx_cor3.dll`，且便携版扫描结果为空，对照成立。
5. **验收脚本被 `Read-Host` 卡死**：`powershell -File` 起的子进程 stdin 可交互，会一直等输入。
   → 用管道把答案喂给子进程 stdin（实测 `GOT=2`），并给 install.ps1 的提问加 try/catch 回落，
   非交互环境不会挂住。
6. **`-SourceExe` 与 `-Edition` 冲突时显示与实装不一致**：曾出现"安装版本: portable"却装了
   65 MB 的自包含 exe。→ 显式给 exe 时一律按文件体积识别，并提示覆盖了 `-Edition`。
7. **对 MD5 的认知纠正**：单文件 exe 的 MD5 会随**重新编译**而变化（嵌入的调试符号每次重新生成），
   阶段 7 记录的 `59fe52…` 与本次 `ab8ea1c9…` 不同，但体积都是 505,949。
   正确判据是"同一份编译产物重复发布 MD5 稳定"+"交叉发布前后不变"，而不是和历史 MD5 比对。
   已写进 `docs/install-and-uninstall.md` 的已知限制第 7 条。

---

## 5. 现在的用法

```powershell
# 发布
dotnet publish src/DeskSpace.App/DeskSpace.App.csproj -c Release -p:PublishProfile=win-x64-singlefile
dotnet publish src/DeskSpace.App/DeskSpace.App.csproj -c Release -p:PublishProfile=win-x64-selfcontained

# 安装（两个产物都在时会让你选 1 或 2）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1

# 明确指定
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1 -Edition selfcontained
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1 -Edition portable -DesktopShortcut
```

Windows 需要 `DOTNET_ROOT` 指向用户级 SDK（`C:\Users\<用户名>\.dotnet`）才能找到 SDK。

---

## 6. 结论与下一步

**第一步验收标准已满足**：两种版本可独立发布、独立安装、独立卸载，原有便携版产物未受影响
（大小与发布路径都不变，MD5 变化仅来自重新编译，已用交叉发布对照证明不是路径混淆）。

**留给第二步的（必须在别的机器上做）**：

1. 干净 Win11（无 .NET 10 桌面运行时）上跑自包含版 —— 预期直接可用；
   同时确认便携版在缺运行时时的报错是否足够清楚（这是便携版唯一的硬依赖）。
2. 单显示器与不同 DPI（100% / 125% / 150% / 200%）下的侧边栏停靠与悬浮窗定位。
3. 自包含版 62 MB，实际分发（网盘/邮件附件）是否可接受，必要时应以自包含版为主推。
