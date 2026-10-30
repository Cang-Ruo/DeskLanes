# DeskLanes v0.1.1 · 一格桌面

**Windows 11 本地桌面工作空间管理器**——把桌面项目组织成分类和自由分区，
用悬浮窗 / 全屏工作空间 / 侧边栏三种形态随叫随到地取用，
而**桌面上的文件一个字节都不会被移动**。

桌面乱，往往不是因为文件放错了地方，而是因为"找东西"只能靠肉眼扫。
DeskLanes 的做法是：只在自己的库里记录"哪个项目属于哪个分类/分区"，
然后给你一个全屏工作空间、一个快捷键呼出的悬浮搜索窗、一条贴在屏幕边缘的侧边栏。

---

## 下载哪个？

| 文件 | 体积 | 谁该选它 |
|---|---|---|
| `DeskLanes-v0.1.1-selfcontained.zip` | 约 57 MB | **绝大多数人**。自带 .NET 10 运行时，机器上什么都不用装，解压双击即用 |
| `DeskLanes-v0.1.1-portable.zip` | 约 318 KB | 机器上已经装了 .NET 10 **桌面**运行时（`Microsoft.WindowsDesktop.App` 10.x） |

两个包解压后结构一致：`DeskLanes.exe` + `install.ps1` / `uninstall.ps1` + `vm-check.ps1` + 说明.txt。

**校验和**（下载后建议核一下）：

```
DeskLanes-v0.1.1-selfcontained.zip
  size   : 59,796,077 B
  md5    : 595b328063c20d5e035c407ada9191b9
  sha256 : 50c4b85741ceafaa7e266ede1cc18b13e7cb7e1451e2c49c27674eacaba17ae8

DeskLanes-v0.1.1-portable.zip
  size   : 325,682 B
  md5    : 8b2e8a5c3dc1c0236b76c54e83a72523
  sha256 : b4783cdfd40f6ad98eede00c9b38a6deb9aadcf8e26fb8823cd1cf40927dd427
```

包内裸 exe 的校验和：自包含 `md5:253333151e1ed1da476739992fd7aec5`、
便携 `md5:bc27d365e038cb14e008179a715eae87`。

---

## 怎么用

**最省事**：解压后双击 `DeskLanes.exe`。不写注册表、不需要管理员权限，删掉就算卸载。

**正式安装**（开始菜单入口 + 可在「设置 → 应用 → 已安装的应用」里卸载）：
在解压目录里按住 Shift + 右键 → 「在此处打开命令行窗口」，粘贴：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1
```

想顺便加桌面快捷方式，末尾加 `-DesktopShortcut`。

**默认快捷键**：`Ctrl`+`Alt`+`Space` 呼出悬浮窗。设置里可改，也能改停靠位置、
开机自启、桌面图标隐藏等。

> 完整说明见
> [`docs/install-and-uninstall.md`](https://github.com/Cang-Ruo/DeskLanes/blob/main/docs/install-and-uninstall.md)。

---

## 这一版修了什么

v0.1.1 是**公开发布前的收口版本**，不新增功能，专注修掉真实缺陷、补上自动化验证。
对用户最有感知的几条：

- **悬浮窗可以拖动、可以八方向改大小了**（之前完全不能动）。四个边、四个角都能拉，
  光标有方向提示，拖到哪、拉多大都会记住
- **悬浮窗拖到窄屏（竖屏 / 低分辨率）会自动收缩**，不会再被切掉一半
- **进「全屏工作空间」直接就是最大化**，不用再手点一次
- **主窗口挪到副屏后重启，位置还在副屏**（之前会跑回主屏）
- **配置文件损坏时会给中文提示**，并把损坏前的原件备份到 `backups\`，
  不再静默变回默认设置（这一条之前是真会让人懵的）
- **长时间挂着不再慢慢变胖**：图标缓存加了上限（512 条）与 LRU 淘汰，
  日志改成轮转而不是只增不减。6 小时驻留实测：内存首小时预热后走平，句柄 / GDI / 线程不涨
- **修掉一个会崩的路径**：分区宽高取到 `NaN` 时保存会抛异常并带崩整个应用
  （WPF 里 `Width` 的默认值就是 `NaN`，这条是自动测出来的）
- 另有 4 处健壮性修复：越界枚举值、快捷键乱码、文件/文件夹互换后误报"已不存在"等

---

## 系统要求

- **Windows 11**（Windows 10 未充分测试，Mica 回退分支还没在真机跑过）
- **x64**
- 自包含版不需要 .NET；便携版需要 .NET 10 桌面运行时

---

## 已知限制

发布前建议先扫一眼
[`docs/known-limitations.md`](https://github.com/Cang-Ruo/DeskLanes/blob/main/docs/known-limitations.md)，
里面列了明确未覆盖的场景（Windows 10、200%+ DPI、1366×768 及以下分辨率、
运行中热插拔显示器、锁屏 / 睡眠唤醒等）与已知的行为取舍。

---

## 验证情况（可复现）

| 项目 | 结果 |
|---|---|
| Release 构建 | 0 警告 0 错误 |
| 冒烟测试 | 460 / 460（`dotnet run --project tests/DeskLanes.SmokeTests -c Release`） |
| 端到端 UI | 36 / 36（`python tools/verify-floating-interaction.py`，真实 exe + 合成鼠标） |
| 启动性能 | 7 / 7（`python tools/measure-startup.py`）：冷启动约 1.35 s、热启动中位约 1.25 s |
| 长时驻留 | 6 小时 / 72 采样点，无泄漏（报告见 `docs/stage9-8-soak/report.md`） |

---

**License**：MIT · **源码**：<https://github.com/Cang-Ruo/DeskLanes>
