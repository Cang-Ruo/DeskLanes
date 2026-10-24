// 端到端验证：直接调用真实的 DesktopIconService 隐藏/恢复桌面图标，
// 每一步截取桌面图标区域，用截图差异证明"服务调用 → 桌面真的变了"。
// 用法：dotnet run --project tools/IconServiceCheck -c Release
// 安全：隐藏后会立即恢复；不动桌面文件；不写除切换命令外的任何系统状态。
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using DeskSpace.App.Services;

const int cropW = 900, cropH = 1300;   // 桌面图标区域（物理像素）
const string outDir = @"C:\Users\TTjustTT\Documents\Github\DeskSpace\docs\stage7-evidence";

static Bitmap Capture()
{
    var bmp = new Bitmap(cropW, cropH);
    using var g = Graphics.FromImage(bmp);
    g.CopyFromScreen(0, 0, 0, 0, new Size(cropW, cropH));
    return bmp;
}

static string Save(Bitmap bmp, string name)
{
    string path = System.IO.Path.Combine(outDir, name);
    bmp.Save(path, ImageFormat.Png);
    using var sha = MD5.Create();
    using var fs = System.IO.File.OpenRead(path);
    return Convert.ToHexString(sha.ComputeHash(fs));
}

// 等桌面重绘
static void Settle() => Thread.Sleep(1500);

Console.WriteLine("===== DesktopIconService 端到端可视化验证 =====");

var service = new DesktopIconService();
Console.WriteLine($"初始 IsHidden={service.IsHidden} Status={service.StatusText ?? "-"}");

// 单步模式：只隐藏 / 只恢复，便于把某个状态留给别的脚本（如卸载脚本）去验证
string mode = args.Length > 0 ? args[0] : "";
if (mode == "hide" || mode == "show")
{
    bool want = mode == "hide";
    var step = service.SetHidden(want);
    Settle();
    Console.WriteLine($"[{mode}] result={step.Success} IsHidden={service.IsHidden} Status={service.StatusText ?? "-"}");
    return;
}

var b0 = Capture();
Console.WriteLine($"[0 基线(显示)] md5={Save(b0, "svc-0-baseline.png")}");

var r1 = service.SetHidden(true);
Settle();
var b1 = Capture();
Console.WriteLine($"[1 SetHidden(true)] result={r1.Success} IsHidden={service.IsHidden} md5={Save(b1, "svc-1-hidden.png")}");

var r2 = service.SetHidden(false);
Settle();
var b2 = Capture();
Console.WriteLine($"[2 SetHidden(false)] result={r2.Success} IsHidden={service.IsHidden} md5={Save(b2, "svc-2-restored.png")}");

Console.WriteLine("判读：hidden 与 baseline/restored 的 md5 应明显不同；baseline 与 restored 应一致" +
                  "（壁纸为静态图，理论上一致）");
Console.WriteLine("===== 验证结束 =====");
