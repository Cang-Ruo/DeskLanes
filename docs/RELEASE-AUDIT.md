# DeskLanes 公开发布前只读审查报告

> 审查时间：2026-11-21 21:05 (+08:00)
> 性质：**只读审查**——未改代码、未改配置、未提交任何修复、未打 tag / Release。
> 依据：`git` 全历史、工作区源码、本机配置目录、`LICENSE`/`README`、构建产物实测。

---

## 0. 一个必须先澄清的事实：版本号

快照和提示词里说「准备发布 v0.1.0」，但仓库**已经发布过三个版本**：

```
git tag: v0.1.0 / v0.1.1 / v0.2.0
README:  下载 DeskLanes-v0.2.0-selfcontained.zip
CHANGELOG: [0.2.0] 2026-10-31 / [0.1.0] 2026-10-25（首个公开发布）
```

- `csproj` 里 `<Version>0.1.0</Version>` 是**程序集版本**，从首个版本起未改；
  它不决定 GitHub Release 的版本。
- 所以下一步不是「首次发布 v0.1.0」，而是**准备 v0.3.0（或你指定的新版本号）**。
- 这一步必须先定，否则 README / Release Notes / tag / 安装包命名都会对不上。

## 1. P0-1 配置安全：最大疑点已澄清，**不是产品 bug**

### 100 个 `settings-corrupt-*.json` 的来源

抽查全部 100 个备份文件内容，**100/100 都是**：

```json
{ "WindowMode": "this is not a valid mode", "Version": "oops"
```

来源定位到 `tools/verify-floating-interaction.py:579`——端到端测试里
**故意写入损坏配置**验证「损坏时应用仍能启动」，应用按设计把它备份到
`backups/` 后恢复默认配置。这是**测试设计缺陷**（每次跑测试累积一个备份并
写真实配置目录），**不是并发写入或产品配置损坏**。

### 产品配置写入机制的实测结论

| 检查项 | 结果 |
|---|---|
| 写入入口 | 全部汇聚到 `WorkspaceService.Save()` → `SaveCore()`（单一入口） |
| 原子写入 | `JsonStore.Save()` 用 `.tmp` → `File.Replace(tmp, file, .bak)`；文件不存在时 `File.Move` |
| 失败处理 | `SaveCore()` 捕获 `IOException` / `UnauthorizedAccessException` / 其它异常，触发 `SaveFailed` 事件 |
| 损坏恢复 | `Load()` 遇 `JsonException` → 备份原文件 → 恢复默认值 + 警告 |
| 版本迁移 | 版本过新/过旧均有备份与处理 |
| 多进程竞争 | 有单实例 Mutex：`Local\DeskLanes.SingleInstance.v1` |
| 节流 | 分区几何保存 200 ms 节流；DragCompleted 收尾落盘 |
| **无锁** | `JsonStore.Save()` 本身没有 `lock`。UI 线程与后台同步若同时 `Save()`，理论上 `File.Replace` 可能竞争抛异常（被 SaveFailed 捕获，最坏是那次保存失败，不损坏文件） |

**结论**：P0-1 的「配置损坏风险」**不成立**；但仍有两项值得修（非阻塞）：
1. 测试脚本不应写真实配置目录——改用临时目录或测试专用配置路径，跑完恢复；
2. `JsonStore.Save()` 加实例级 `lock`，消除并发保存竞争的理论风险。

## 2. P0-2 Git 仓库公开审查：发现**明确的公开风险**

### 仓库体积（实际很健康）

| 项 | 值 |
|---|---|
| 工作区 | 1.1 G（含未跟踪的 `publish/`、大截图证据） |
| `.git` | 64.6 MB（size-pack），606 个跟踪文件共 55.3 MB |
| `docs/ui-acrylic-final/` | 工作区 362 MB，但 git 只跟踪 241 个文件 / 15.7 MB（大头被 gitignore 排除） |
| 历史中的 exe/zip/dll | **无**（已扫描全历史文件名） |

### 必须清理：Windows 登录名与私人路径

被 git 跟踪的文件里含 `<username>`（Windows 登录名）与私人壁纸路径：

| 文件 | 内容 |
|---|---|
| `docs/CURRENT-STATE.md` | `C:\Users\<username>\AppData\Local\...`（1 处） |
| `docs/PARAMETERS.md` | 同上（2 处） |
| `docs/ui-acrylic-final/env.json` | `C:\Users\<username>\Pictures\Camera Roll\wallpaper.jpg` |
| `docs/ui-acrylic-final/phase{9,10,11,12}-evidence.json` | 同上私人壁纸路径 |
| `tools/phase13-manual-probes.py` | 私人 logs 路径示例 |

git 历史里还有更多（早期 PS 脚本写死 `C:\Users\<username>\Documents\Github\...`、
`C:\Users\<username>\.dotnet`）。历史中还有一条说明写着要把这些换成占位符——
说明之前有人意识到但未彻底完成。

**这是公开前必须处理的**：登录名属于个人信息，私人壁纸文件名也可能暴露生活信息。
历史清理需要用 `git filter-repo` 之类工具重写历史（会改所有 commit SHA），
或评估「旧历史是否也要公开」——请你在动手前决定。

### 其它扫描结果

- token / API Key / 私钥：**未发现真实凭据**。`tools/push-via-api.py` 只是从
  `gh auth token` 读取本机令牌，不含硬编码密钥（安全）。
- `KnownFolders.cs` 等里的 `hToken` 是 Win32 API 参数名，不是凭据。
- LICENSE 署名：`Copyright (c) 2026 Ruo Chang`——请确认是否要以真名公开署名，
  还是改成 GitHub 用户名 / "DeskLanes contributors"。

## 3. P0-3 License：已存在，无需新增

根目录已有 **MIT License**（`LICENSE`，1 KB）。README 和发布说明中也有许可说明。
你提示词里说「快照没明确说明是否有 LICENSE」——实际有，这项**不必做**。
只需确认署名是否合适，以及截图素材（第三方壁纸 `wallpaper.jpg`）是否允许公开分发。

## 4. P1-4 README：完成度高，但有两处过时

README 已有 8 KB，结构包含：项目定位、三形态表格、核心特性、实测数据、
安装、源码构建、安全约定、已知限制、文档索引；5 张截图链接全部有效；
有 GitHub Actions build badge。

**待更新**：
1. 截图是 2026-10 的旧视觉（v0.2.0 磨砂玻璃），球体已经过阶段 39–47 大改，
   README 里的悬浮球/悬浮窗截图与当前产品不符；
2. README 没有介绍**单击小球弹分类选择窗 / 双击展开**这个新交互（阶段 46/47）；
3. 版本号需随新 Release 统一。

## 5. P1-5 发布包与安装体验

已有：自包含版 + 便携版两套、`install.ps1` / `uninstall.ps1`、卸载项、
诊断包导出、SHA256 相关说明（需复核是否实际提供校验值）。

审查中未做陌生用户流程（需要干净 Windows 环境，超出只读范围）。
**建议发布前补**：ZIP 内容清单核对、SmartScreen 说明、SHA256 值、卸载后数据保留说明。

## 6. P1-6 截图视觉统一

`docs/screenshots/` 下 5 张（category/floating/panels/settings/sidebar）是旧版本视觉。
阶段 39–47 积累了 `docs/ui-acrylic-final/phase4x-*.png` 等新截图，
但格式/背景不统一。发布前建议用统一壁纸+演示数据重拍 6–7 张（含分类选择窗）。

## 7. 其它两项关注

**378 MB 内存占用**：当前运行进程约 378 MB。只读审查未做内存剖析；
WPF + 图标缓存（上限 512）+ 多窗口 + 玻璃效果，这个量级不算异常。
建议发布前做一次「启动→切换三模式→静置 30 分钟」的内存曲线观测，
若稳定则不阻塞发布。

**593 项测试**：层次覆盖不错（单元/系统级/真实交互/发布包），
但 GitHub 用户关心的是「下载后能跑、数据不丢」。建议补一条
「从 ZIP 安装→使用→卸载→数据保留」的用户场景验收，并写成发布检查清单。

## 8. 修复顺序建议（待你确认后执行）

| 顺序 | 事项 | 性质 |
|---|---|---|
| 1 | 定新版本号（v0.3.0？），统一 README/CHANGELOG/csproj/Release Notes | 必做 |
| 2 | **清理 git 工作区与历史里的 `<username>`、私人路径、壁纸名** | **阻塞公开** |
| 3 | 确认 LICENSE 署名、壁纸素材分发许可 | 必做 |
| 4 | 测试脚本改用临时配置目录；`JsonStore.Save()` 加 `lock` | 建议 |
| 5 | 重拍统一视觉截图（含分类窗），更新 README 首屏与新交互 | 建议 |
| 6 | 发布包 ZIP 内容 / SHA256 / SmartScreen 说明 / 陌生用户流程 | 建议 |
| 7 | 内存曲线观测；用户场景验收清单 | 建议 |
| 8 | Tag + GitHub Release + 正式公开 | 最后 |

## 9. 本次审查未做的事

- 未修改任何代码 / 配置 / 文档内容（本报告除外，且未提交）
- 未重写 git 历史、未清理个人信息
- 未打 tag、未创建 Release、未改版本号
- 未做陌生环境安装测试、未做内存剖析
