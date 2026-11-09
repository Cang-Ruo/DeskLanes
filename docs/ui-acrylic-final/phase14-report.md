# 阶段 14：按用户反馈改交互、配色与扫描语义

> 一句话结论：用户提的 5 类问题里，「刷新没更新 / 还得手动删」其实不是没扫描，
> 而是旧设计**故意把消失的项目留在库里只标记失效**；现在改成确实不存在就自动清除。
> 另外按用户要求**全局关掉随壁纸变色的动态文字**，名字牌统一固定深色底 + 浅字，
> 分类导航拆成「文件夹 / 软件」两个区域，双击也不再依赖列表选中态。

证据：`phase14-evidence.json`、`phase14-floating.png`、`phase14-category.png`（UiSnapshot `--demo` 截图）。

---

## 一、用户反馈 → 定位 → 处理

| 反馈 | 定位 | 处理 |
|---|---|---|
| 双击打不开、得右键 | 5 个窗口的双击命令参数都绑 `ListBox.SelectedItem`；磁贴拖拽预览会吃掉按下事件，选中态常没同步，`CanExecute` 拿不到 `DesktopItem`。右键菜单用 `PlacementTarget.DataContext`（项本身）所以能用 | 双击统一由 `ItemTile` 自己处理，直接以自身 `DataContext` 为参数；删掉 5 处重复 `MouseBinding`，键盘 Enter 保留 |
| 名字有时变白、刷新回不到黑底 | 阶段 10–13 的局部亮度自适应会把名字牌/文字随壁纸切成浅底深字 | **默认关闭**自适应，只有显式 `DESKLANES_DYNAMIC_TEXT=1` 才恢复；`BrushLabelScrim` 从 30% 黑加深到 **55% 黑** |
| 刷新不能更新桌面图标、还得手动删 | `RefreshAsync()` 本来就 `ScanAsync` + `MergeScanAsync`；但 `MergeScanAsync` 注释明确写「保留在库中并标记失效，不清除」 | 改为 `ReconcileMissing`：文件确实不存在且所在目录仍可访问 → 从库移除并清掉分类/分区归属；目录整体不可访问只标记失效，避免误删 |
| 打开软件就扫描 / 点刷新就扫描 | 启动时 `InitializeAsync → RefreshAsync`、刷新按钮 `RefreshAsync` 都已在扫 | 无需改代码，报告里说明；真正没覆盖的是下一条 |
| 一段时间没打开、从后台切回来自动扫描 | 原先只有电源/会话恢复会重扫 | `AppController` 订阅 `Application.Deactivated/Activated`，离开超过 **60 秒**再切回触发一次全量重扫（30 秒内不重复） |
| 分类导航把文件夹和软件分成两个区域 | 原来一个 WrapPanel 全堆一起 | `CategoryNavViewModel` 拆出 `FolderItems` / `AppItems`，XAML 两个带标题的 WrapPanel 列表 |

用户确认过的三个选择：**问题发生在软件窗口内**、**全部窗口固定黑底浅字**、**确实不存在就自动删除**。

## 二、几个容易踩坑的地方

- **自动清除不写 `DismissedPaths`**。复用 `RemoveItem()` 会把扫描项记进忽略列表，以后软件放回桌面就永远加不回来了；所以内部单独走 `DetachFromAllContainers + Items.Remove`，冒烟专门锁了「放回桌面能自动重新出现」。
- **目录不可访问时不能删**。桌面/公共桌面临时掉线时 `ScanResult.ScopeDirectories` 不会包含该目录，此时保留并标记失效；只有父目录确实可访问、文件确实不存在才删。
- **分组一开始用 `CollectionViewSource + GroupStyle`，组内 WrapPanel 拿不到可用宽度，退化成竖排单列**（截图 `p14-category2.png` 可复现）。改成视图模型给两个集合、XAML 两个 ListBox 后正常横排。
- 关闭自适应后，`dynamic-text.json` 不再写出，阶段 10–13 的像素审计工具需要 `DESKLANES_DYNAMIC_TEXT=1` 才能复现旧行为。**那些历史证据仍有效，但描述的是已不再是默认的外观**。

## 三、视觉核对

用 `DeskLanes.UiSnapshot --demo --screen --keep-size` 拍了两张（不启动真实实例、不打断 24h soak）：

- `phase14-floating.png`：所有名字牌与底部状态栏都是**一致的深黑底 + 白字**，不再随壁纸变浅。
- `phase14-category.png`：出现「软件」分区标题，图标横向铺开 6 列。当前选中的「工作」分类里没有文件夹，所以「文件夹」区按设计隐藏；冒烟断言锁住两组分流正确。

> 按用户措辞，「悬浮窗也是」理解为配色问题（同样改回固定黑底）；**分区只做在分类导航**，
> 悬浮窗 / 侧边栏 / 自由分区布局未拆区。若用户想要悬浮窗也分区，下一次补。

## 四、回归与交付

| 项目 | 结果 |
|---|---|
| Release 构建 | **0 警告 0 错误** |
| 冒烟 | **472 / 472**（新增分组、自动清除、放回桌面、不误删等断言；同时把 15 条按旧「保留失效项」语义写的断言改成新语义） |
| 界面快照 | floating / category 两张，固定黑底与横排分区已目视确认 |
| 发布 | portable `6e0eda1f…`（587,869 B）、selfcontained 新版 `c05f0188…`（65,100,745 B）**在 `publish/win-x64-selfcontained-next/`** |
| 安装 | **本轮未覆盖安装**：正式 exe 仍被 24h soak 进程（PID 286804）占用，`install.ps1` 会结束它 |
| 未跑 | 三屏端到端 / 启动探针 / 真实实例上的双击点击验证——都需要启动新实例，与单实例互斥及 soak 冲突 |

**双击修复已完成代码与编译，但还没在真实运行的实例上点过**。这是本轮唯一没有端到端证据的改动，需要重启软件后确认。

## 五、下一步

1. 等 24h soak 跑完（约 2026-11-10 01:50）→ 把 `next` 覆盖到正式目录 → `install.ps1 -Force` → 重启软件。
2. 重启后确认：双击能打开、名字牌恒为黑底、分类导航两个区域、桌面删掉的软件会自动消失。
3. 用户方便时再拔一次左侧竖屏线，复测阶段 13 的热插拔修复（当前跑的还是旧实例）。
4. 分析 `soak-24h.csv` 出长时驻留结论。

如果用户不想等，也可随时提前结束 soak，立即做 1–3 步。
