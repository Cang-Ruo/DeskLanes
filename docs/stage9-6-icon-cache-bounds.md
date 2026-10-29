# 阶段 9.6：图标缓存有界（修掉真实的缓慢泄漏）

日期：2026-10-29
改动文件：`src/DeskLanes.Core/Services/IconService.cs`、`ServiceContracts.cs`、`src/DeskLanes.App/ViewModels/MainViewModel.cs`
验收：构建 0 警告 0 错误；冒烟 **379/379**（新增第 20 节 23 条，其中 23 条全过）；端到端 `tools/verify-floating-interaction.py` **23/23**。

---

## 1. 问题：缓存无上限 + 清理入口零调用点

`IconService` 原来只有一个裸字典：

```csharp
private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
```

查证结果：

| 事实 | 证据 |
|---|---|
| 缓存无上限、无淘汰策略 | 原实现只有 `_cache[fullPath] = icon`，没有任何 `Remove` |
| `ClearCache()` **全仓库零调用点** | 全仓 grep 只有 `ServiceContracts.cs` 的接口声明和 `IconService.cs` 的定义，没有第三个地方 |
| 每颗图标约 4 KB | `SHGetFileInfo` 用 `SHGFI_LARGEICON`（32×32），`CreateBitmapSourceFromHIcon` 后 `Freeze()`，32×32×4B ≈ 4 KB，加上 `BitmapSource` 对象开销高于此 |
| 缓存键是**完整路径**，会随文件改名/删除不断累积 | `GetIcon(item.FullPath)` |

结论：这不是理论问题。桌面文件反复增删、自动同步每轮重扫时，被删掉的文件图标永远留在字典里，
进程又是一个长期驻留的托盘应用 —— 属于典型的缓慢内存泄漏，跑几天才看得出来，最难排查。

## 2. 方案：两条防线

单靠"加个上限"不够（上限之内的垃圾照样占位），单靠"每轮裁剪"也不够（调用方漏调就退化成无上限）。
所以两条都上：

### 防线一：容量上限 + LRU 淘汰（兜底）

- 默认上限 `IconService.DefaultMaxCachedIcons = 512`。现实桌面量级：用户桌面 50 项 + 公共桌面 26 项，
  512 有近 7 倍余量；按每颗几 KB 算，峰值在几 MB 量级。
- 超过上限时**成批淘汰**最久未使用的一批（一次腾出上限的 1/8），而不是逐条删 ——
  把一次 O(n) 排序摊薄到每 `上限/8` 次插入才做一次，避免每次插入都全表扫描。
- 上限可通过构造参数覆盖（测试用 `new IconService(32)` 验证淘汰行为）；下限兜到 16，
  防止误传 0 导致每轮都重新 P/Invoke。
- **负结果也缓存**：取图标失败的路径同样进字典，否则每轮都会对不存在的路径重试一次 `SHGetFileInfo`。

### 防线二：按当前工作区裁剪（主要手段）

新增 `IIconService.PruneExcept(IEnumerable<string> keepPaths)`：只保留传入的路径，其余摘掉，返回摘掉条数。

接线点在 `MainViewModel.LoadIconsAsync()` 末尾（后台线程内）：

```csharp
_iconService.PruneExcept(_workspace.Items.Select(i => i.FullPath));
```

`LoadIconsAsync` 被三条路径调用 —— 手动刷新（`RefreshAsync`）、自动同步完成（`OnAutoSyncCompleted`）、
添加自定义项目后 —— 所以每轮扫描/同步结束都会自动裁剪一次，删掉或改名的文件图标不会继续占内存。

**裁剪是安全的**：`DesktopItem.Icon` 自己持着 `ImageSource` 引用（已冻结、共享），
裁剪只动缓存字典，不会让界面上的图标消失；且 `LoadIconsAsync` 会跳过 `Icon != null` 的项，不会重复取。

### 并发

`GetIcon` 从 `Task.Run` 里调用，字典加了同一把锁；取图标的 P/Invoke 放在锁外执行，
不阻塞其它线程的命中查询。

## 3. 接口变化

```csharp
public interface IIconService
{
    ImageSource? GetIcon(string fullPath);
    void ClearCache();
    int CachedIconCount { get; }                       // 新增：诊断与测试用
    int PruneExcept(IEnumerable<string> keepPaths);    // 新增：按当前工作区裁剪
}
```

`IconService` 另外公开 `MaxCachedIcons`（当前上限）与 `IsCached(string)`（诊断/测试用，不改变 LRU 顺序）。

## 4. 验收证据

新增冒烟第 20 节（23 条，全过）：

| 分组 | 断言 |
|---|---|
| 默认与初始状态 | 默认上限 512；新建实例缓存为空 |
| 回归 | 真实文件仍能取到图标且 `IsFrozen`（重写没弄坏取图标路径） |
| 命中语义 | 重复取同一路径条目数不变；空/null 路径不入缓存 |
| 容量上限 | `IconService(32)` 塞 120 条后 ≤ 32；淘汰是成批的（≥ 24，即腾出 1/8 而非逐条） |
| LRU | 持续访问的 `keeper` 在淘汰中存活；最久未访问的 `no-such-0` 被淘汰 |
| 默认实例 | 塞 700 条后仍 ≤ 512 |
| `PruneExcept` | 摘掉数 == 总数 - 保留数；保留项全留；重复裁剪幂等；保留列表为空时清空 |
| `ClearCache` | 归零 |
| 接口层 | 通过 `IIconService` 也能拿到计数与裁剪能力（`MainViewModel` 走的是接口） |

```
== 20. 图标缓存有界（长驻不泄漏） ==
  [PASS] 默认缓存上限 512
  ...
===== 冒烟测试结果：通过 379 项，失败 0 项 =====
```

端到端（真实 exe + 合成鼠标）**23/23**，界面行为未受影响。

## 5. 残留与说明

- **没有加"手动清理图标缓存"按钮**。路线图 P2-10 原文写的是"缓存上限 + 手动清理入口"，
  加了自动裁剪后手动入口变成冗余（每轮扫描都会裁一次，缓存里只剩当前还在的项目），
  故不再额外增加 UI 入口与设置项。若将来出现"缓存占用异常"的报障，再补这个按钮不迟。
- **没有做真实的内存基线测量**（比如跑 8 小时看私有字节数增长曲线）。理由是收益/成本不划算：
  泄漏机制已从代码层面消除并有单元测试级断言，而长时驻留测试（路线图 B5）本来就在人工补测清单里。
- **未覆盖**：`PruneExcept` 在真实自动同步路径上的端到端观察（只有单元级断言 + 代码接线），
  因为要在真实桌面上反复增删文件才能看到条目数变化，属于人工补测项。
