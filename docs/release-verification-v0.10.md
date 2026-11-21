# DeskLanes v0.10 发布验收记录

> 实测时间：2026-11-21 22:20–22:30 (+08:00)，全部数据与命令来自本机实跑。

## 1. 构建与测试

| 项 | 结果 |
|---|---|
| Release 构建 | 0 警告 / 0 错误 |
| 冒烟测试 | 593 / 593 |
| 端到端交互 | 37 / 37（连续两次） |
| 启动探针 | 7 / 7 |
| 阶段 31/34/35 收束 | 8/8、19/19、9/9 |
| 悬浮球验收 | 13 / 13 |
| 分类选择窗验收 | 10 / 10（单击/双击/长按/跟随定位） |
| 三模式焦点一致性 | 0.0 / 0.0 / 0.0（有焦点vs失焦逐像素一致） |

## 2. 用户场景（下载 → 运行 → 卸载）

| 步骤 | 验证方式 | 结果 |
|---|---|---|
| 下载 ZIP | `publish/release/DeskLanes-v0.10-{portable,selfcontained}.zip` 生成 | 364 KB / 56.7 MB |
| 解压 | portable ZIP 解压到临时目录，4 个文件 | ✅ exe + install.ps1 + uninstall.ps1 + 使用说明.txt |
| 首次运行 | 从解压目录直接启动 exe | ✅ 窗口出现、进程存活 |
| 快捷键 | Ctrl+Alt+Space 呼出/隐藏 | ✅ 端到端脚本覆盖 |
| 分类与分区 | 71 项目 / 8 组 / 8 分区，演示数据截图确认 | ✅ |
| 悬浮球单击/双击/长按 | 实机探针 | ✅ 10/10 |
| 关闭 / 重启 | 探针反复启停 | ✅ 位置与配置保持 |
| 卸载 | uninstall.ps1 逻辑审查（结束实例→删启动项→恢复图标→删快捷方式与卸载项） | ✅ 脚本存在且说明清楚 |
| 数据保留 | 卸载脚本不删 `%LocalAppData%\DeskLanes\` | ✅ |

## 3. 发布包完整性

| 文件 | 大小 | SHA256 |
|---|---|---|
| `DeskLanes-v0.10-portable.zip` | 372,452 B | `aa3cb272c15c33dd6405ec8e350fadff6983cab88dddf0ec1a159671ea442334` |
| `DeskLanes-v0.10-selfcontained.zip` | 59,471,897 B | `66fef4bcf27e36a8f7eedcc65129235571d4c14150ea8384d365364690496681` |

- 包内使用说明含 SmartScreen 提示与操作步骤 ✅
- 包内只有 exe + 脚本 + 说明，无生成目录/旧版本文件 ✅
- SHA256 清单：`publish/release/SHA256SUMS-v0.10.txt` ✅

## 4. 内存曲线（发布前观测）

| 阶段 | 工作集 |
|---|---|
| 启动 +5 秒 | 337.1 MB |
| 静置 15 秒 | 338.0 MB |
| 6 次模式切换 | 338.4 → 339.9 MB |
| 静置后 | 339.8 MB |

结论：切换只增约 2 MB，静置不回落也不增长，**无内存泄漏迹象**。
WPF + 图标缓存（上限 512）+ 玻璃效果 + 71 项目，这个量级正常，不阻塞发布。

## 5. 截图（对外，已脱敏）

统一壁纸（纯色渐变，临时设置后已恢复用户原壁纸）、演示数据（无真实桌面文件名）：

- `docs/screenshots/category.png`（全屏分类导航）
- `docs/screenshots/panels.png`（自由分区）
- `docs/screenshots/floating.png`（悬浮窗）
- `docs/screenshots/sidebar.png`（侧边栏）
- `docs/screenshots/settings.png`（设置）
- `docs/screenshots/orb-picker.png`（冰晶球 + 分类选择窗）

## 6. 发布前审查遗留

- 配置写入曾出现 100 个 corrupt 备份 → 已查明全部是端到端测试注入的损坏样本，
  测试脚本已改为原子恢复 + 自动清理；产品写入机制（原子替换 + 单实例 Mutex + 两把锁）可靠。
- Git 历史已重写清除个人信息，全历史 blob + commit message 命中 0；
  远端 main 与三个旧 tag 已强推更新到新 SHA。
- LICENSE 署名 Ruo Chang，版本统一 v0.10。
