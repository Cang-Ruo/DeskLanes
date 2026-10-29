# 阶段 9.9：日志轮转（补完 P3-13）

日期：2026-10-30（夜间自主执行）
改动：`src/DeskLanes.Core/Services/LogFileWriter.cs`（新增）、`FileDiagnosticLogger.cs`、`DiagnosticExporter.cs`、`src/DeskLanes.App/App.xaml.cs`、`tests/DeskLanes.SmokeTests/Program.cs`
验收：构建 0 警告 0 错误；冒烟 **415/415**（新增第 22 节 21 条全过）。

---

## 1. 先核实：到底哪个日志只增不减

昨天在 `known-limitations.md` 里写的是「`logs\*.log` 只增不减」。这句**不准确**，本轮核实后订正：

| 文件 | 修前 | 实况（2026-10-30） |
|---|---|---|
| `sync.log` | **已**有 `TrimIfNeeded()`：超 512 KB 就原地保留后半段 | 100,035 B（有上界） |
| `errors.log` | `App.xaml.cs` 直接 `File.AppendAllText`，**无上限** | 3,634 B（目前小，但崩溃循环下会一直长） |

所以真正缺的是 `errors.log`；`sync.log` 有上界但用的是"原地砍掉前半段"，
**排障时最有价值的"出事之前那一段"会被砍掉**——这个实现方式本身也值得换掉。

## 2. 方案：轮转，而不是原地截断

新增 `LogFileWriter`：单份超上限 → 当前文件改名为 `xxx.log.1`，旧档依次后挪
（`.1 → .2 → .3`），超过保留份数的最老一份删掉。

- 默认：单份 512 KB + 保留 3 份归档 → 日志总量上界约 2 MB
- `errors.log` 用更保守的 256 KB + 2 份（崩溃堆栈很长，单条就可能几 KB）
- `FileDiagnosticLogger` 改为走 `LogFileWriter`（替掉原来的 `TrimIfNeeded`）
- `DiagnosticExporter` 的日志枚举从 `*.log` 放宽到 `*.log*`，
  **归档也要打进诊断包**——否则用户报障时拿不到出事之前那一段
- 所有 IO 失败一律吞掉：日志写不进去绝不能影响应用

## 3. 过程中修掉的一个真 bug（冒烟抓出来的）

`maxArchives = 0` 时仍然留下了 `xxx.log.1`。根因在 `ArchivePath`：

```csharp
public string ArchivePath(int n)
{
    if (n <= 1) return _filePath + ".1";   // ArchivePath(0) 也回落成 ".1"
    return _filePath + "." + n;
}
```

轮转时先算 `oldest = ArchivePath(_maxArchives)`，`_maxArchives == 0` 时拿到 `.1`，
接着 `File.Move(_filePath, ArchivePath(1))` 照样生成了 `.1` —— "保留 0 份"变成留了 1 份。
修法：`RotateIfNeeded` 开头对 `_maxArchives == 0` 单独处理（直接删当前文件），不再往下走。

## 4. 冒烟第 22 节（21 条，全过）

| 分组 | 断言 |
|---|---|
| 基本 | 追加三行都落盘；默认 512 KB / 3 份归档；上限低于 16 KB 时兜到 16 KB |
| 轮转 | `.1`、`.2` 存在；**没有 `.3`**（超份数的最老一份被删）；轮转后当前份回到上限内 |
| 归档顺序 | 两份归档都非空；`.1` 的末尾行号 > `.2` 的末尾行号（`.1` 更新） |
| 保留 0 份 | 不留档；当前份仍有上界 |
| 并发 | 8 线程各写 50 行 → 400 行**一条不丢** |
| 同步日志 | 走同一套轮转；行带中文级别「[信息]」；有体积上界 |
| 容错 | 父路径是文件（目录建不出来）时 `Append` **不抛异常** |
| 诊断包 | zip 里同时有 `logs/sync.log` 与 `logs/sync.log.1` |

```
===== 冒烟测试结果：通过 415 项，失败 0 项 =====
```

## 5. 残留

- **没有真实的长时增长验证**：轮转逻辑有单元级断言，但"跑一个月日志会不会撑爆磁盘"
  要靠时间。上界是数学确定的（当前份 512 KB + 3 × 512 KB ≈ 2 MB），风险很低。
- 老的 `TrimIfNeeded` 已删除，`FileDiagnosticLogger` 的构造签名**兼容**（多了可选参数
  `maxArchives`），调用方 `AppController` 未改。
