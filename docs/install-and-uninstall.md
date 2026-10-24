# DeskSpace 安装与卸载说明

## 现状

| 项目 | 结论 |
|---|---|
| 分发形态 | 单文件 exe，两个版本二选一（见下表） |
| 安装包 | **没有 MSI / 安装向导**，提供的是 `tools/install.ps1`（每用户安装脚本） |
| 卸载 | `tools/uninstall.ps1`，并已注册到「设置 → 应用 → 已安装的应用」 |
| 是否需要管理员 | **不需要**。全部写 HKCU 和当前用户目录 |
| 自动更新 | 无。升级 = 重新发布 exe 后覆盖安装（install.ps1 支持重复执行） |

### 两个版本怎么选

| 版本 | 产物 | 体积 | 目标机要求 | 适合 |
|---|---|---|---|---|
| `portable` 便携版 | `publish/win-x64/DeskSpace.exe` | 约 0.5 MB | **必须**已装 .NET 10 桌面运行时（`Microsoft.WindowsDesktop.App` 10.x） | 自己用、或目标机确定有运行时 |
| `selfcontained` 自包含版 | `publish/win-x64-selfcontained/DeskSpace.exe` | 约 62 MB | **无**，自带运行时，开箱即用 | 发给别人、干净机器、不想让用户装任何东西 |

> 用 WPF，只装 .NET Runtime（不含 WindowsDesktop）是不够的，必须是**桌面运行时**。
> 两个版本功能完全一致，只是"运行时打包方式"不同；两条发布路径目录独立，互不干扰。

> 为什么不做 MSI：项目约定是"零第三方依赖、不引入重量级工具链"。MSI/WiX/Inno Setup 都要额外装工具，
> 而每用户安装脚本能覆盖安装、快捷方式、卸载登记三件事，且可审计（就是个可读的 .ps1）。

---

## 安装

把 `install.ps1`、`uninstall.ps1` 和 `DeskSpace.exe` 放在同一目录分发，然后：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
```

在仓库里发布后安装（两个版本都在时，脚本会列出候选让你选 1 或 2）：

```powershell
dotnet publish src/DeskSpace.App/DeskSpace.App.csproj -c Release -p:PublishProfile=win-x64-singlefile
dotnet publish src/DeskSpace.App/DeskSpace.App.csproj -c Release -p:PublishProfile=win-x64-selfcontained
powershell -NoProfile -ExecutionPolicy Bypass -File tools\install.ps1
```

常用参数：

| 参数 | 作用 |
|---|---|
| `-Edition portable\|selfcontained` | 明确指定装哪个版本；省略时若两个产物都在会让你选，只有一个就装它 |
| `-SourceExe <路径>` | 直接指定 exe（优先级最高，`-Edition` 会被忽略，版本按体积自动识别） |
| `-DesktopShortcut` | 额外创建桌面快捷方式（默认只建开始菜单） |
| `-NoStartMenu` | 不建开始菜单快捷方式 |
| `-InstallDir <路径>` | 自定义安装目录，默认 `%LOCALAPPDATA%\Programs\DeskSpace` |
| `-Launch` | 装完立刻启动 |
| `-Force` | 不询问（结束运行中的 DeskSpace、缺运行时也继续、自动选便携版） |

安装会做：

1. 选定版本；**只有便携版**才检查 .NET 10 桌面运行时，缺了提示下载页
   （https://dotnet.microsoft.com/download/dotnet/10.0 → 桌面运行时），并提示可改选自包含版
2. 结束正在运行的 DeskSpace（否则 exe 被占用）
3. 复制 exe 到安装目录，并把 `uninstall.ps1` 一起放过去
4. 写卸载项：`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\DeskSpace`
   （额外记录 `Edition`，便于确认装的是哪一版）
5. 建开始菜单「DeskSpace / 卸载 DeskSpace」两个快捷方式

安装**不会**做：不开机自启、不隐藏桌面图标（这两项都是 opt-in，由用户在软件「设置」里自己开）、
不动桌面文件、不写 HKLM、不重启资源管理器。

---

## 卸载

任选一种：

1. **Windows 设置 → 应用 → 已安装的应用 → DeskSpace → 卸载**（走的就是 uninstall.ps1）
2. 开始菜单 → DeskSpace → 卸载 DeskSpace
3. 命令行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1          # 交互式确认
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -Silent  # 静默
powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -RemoveConfig  # 连配置一起删
```

卸载会做：

1. 结束正在运行的 DeskSpace（含托盘图标）
2. 删除开机自启项 `HKCU\...\Run\DeskSpace` —— 否则删了 exe 会留下指向空路径的启动项
3. **恢复桌面图标显示**（仅在当前是隐藏状态时；用系统右键菜单同款的 WM_COMMAND 0x7402，
   与软件内的实现一致；恢复失败会明确提示手动检查）
4. 删除开始菜单 / 桌面快捷方式
5. 删除「已安装的应用」里的卸载项
6. 删除安装目录（程序文件）

卸载**默认保留**配置目录 `%LOCALAPPDATA%\DeskSpace`（分类、布局、设置都在这里）。
想彻底清除就用 `-RemoveConfig`。

另外软件内还有一个「设置 → 卸载前清理」按钮，只做系统清理（删自启 + 恢复图标）而不删程序文件，
适合"想先清理痕迹、程序以后还要用"的场景；完整卸载请用上面的脚本。

---

## 本机实测记录（2026-10-25）

| 步骤 | 结果 |
|---|---|
| install.ps1 | ✅ 运行时检测命中 10.0.13；exe(505,949 B) + uninstall.ps1 就位；卸载项字段正确；两个开始菜单快捷方式生成 |
| 卸载前先隐藏桌面图标 → uninstall.ps1 -Silent | ✅ 输出"已恢复桌面图标显示"，注册表 `HideIcons` 回到 0 |
| 卸载后残留检查 | ✅ 安装目录 / 卸载项 / 开始菜单目录全部不存在；`HideIcons=0`；配置目录按预期保留 |
| 再次 install.ps1 | ✅ 可重复安装（幂等） |

---

## 已知限制

1. **便携版依赖目标机预装 .NET 10 桌面运行时**；未安装时的报错友好度尚未在干净环境验证过（阶段 8 第二步）。
   用自包含版可完全绕开这条限制。
2. 自包含版约 62 MB，比便携版大两个数量级；启动时会把原生运行时解压到
   `%TEMP%\.net\DeskSpace\<随机目录>`，首次启动略慢（这是 .NET 单文件的正常行为）。
3. 没有数字签名，双击 exe 可能遇到 SmartScreen 提示（"仍要运行"即可）。
4. 无自动更新：新版本需要重新发布后覆盖安装。
5. 便携版启动也会在 `%TEMP%\.net\DeskSpace\<版本>` 下解压少量内容，但**不含** coreclr.dll（用系统运行时）；
   自包含版才会解压出 coreclr.dll —— 这是区分两版是否"自带运行时"的可靠判据。
6. 若第三方桌面工具接管了桌面，卸载脚本可能找不到桌面图标窗口，此时会明确提示手动恢复图标。
7. 单文件 exe 的 MD5 会因重新编译而变化（嵌入的调试符号每次编译重新生成），
   同一份编译产物重复发布则 MD5 稳定。判断"产物有没有被改动"请以体积和实际运行验证为准，不要只看 MD5。
