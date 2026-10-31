#!/usr/bin/env python3
"""把 publish 出来的两个单文件 exe 打成对外发布的 zip。

做三件事：
  1. 新鲜度自检 —— exe 的修改时间必须晚于最后一次改 src/ 的提交时间。
     只 build 不 publish 会让装的是旧界面，这个坑踩过一次，所以放进脚本里。
  2. 组装包内容：exe + install.ps1 + uninstall.ps1 + 使用说明.txt。
  3. 打 zip 到 publish/release/，回显体积与 MD5。

用法：
    python tools/package-release.py v0.2.0
"""

import hashlib
import os
import shutil
import subprocess
import sys
import time
import zipfile

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PUBLISH = os.path.join(REPO_ROOT, "publish")
RELEASE = os.path.join(PUBLISH, "release")
TOOLS = os.path.join(REPO_ROOT, "tools")

# 自包含版自带运行时，便携版要目标机先装 .NET 10 桌面运行时
VARIANTS = [
    ("portable", os.path.join(PUBLISH, "win-x64"), False),
    ("selfcontained", os.path.join(PUBLISH, "win-x64-selfcontained"), True),
]


def md5_of(path):
    h = hashlib.md5()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def git_src_commit_time():
    """最后一次改动 src/ 的提交时间（Unix 秒）。"""
    try:
        out = subprocess.run(
            ["git", "log", "-1", "--format=%ct", "--", "src/"],
            cwd=REPO_ROOT, capture_output=True, text=True,
        )
        if out.returncode == 0 and out.stdout.strip():
            return int(out.stdout.strip())
    except Exception:
        pass
    return None


def human_size(n):
    mb = n / 1024 / 1024
    if mb >= 1:
        return "%.1f MB" % mb
    return "%.0f KB" % (n / 1024)


NOTICE = """DeskLanes {version} — {variant_name}

==========================================
一、最快的上手方式
==========================================
{variant_hint}

第一次双击会弹 SmartScreen「Windows 已保护你的电脑」：
点「更多信息」→「仍要运行」。原因是没有数字签名（个人项目，签名要花钱）。

不想要了直接删掉这个文件夹就算卸载。

==========================================
二、想正式安装（开始菜单入口 + 可在系统设置里卸载）
==========================================
在这个文件夹里：按住 Shift + 鼠标右键 →「在此处打开命令行窗口」，粘贴：

    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1

想要桌面快捷方式就加一个参数：

    powershell -NoProfile -ExecutionPolicy Bypass -File install.ps1 -DesktopShortcut

装完之后：开始菜单 → DeskLanes 里有「启动」和「卸载」两个入口。

完全卸载（连配置一起删，分类与布局也会没）：

    powershell -NoProfile -ExecutionPolicy Bypass -File uninstall.ps1 -RemoveConfig

==========================================
三、这个软件会碰你电脑上的什么
==========================================
  - 桌面上的文件：一个都不动。分类与分区只是记在 JSON 里的归属关系，
    不会移动、重命名、删除任何文件
  - 配置目录：C:\\Users\\<用户名>\\AppData\\Local\\DeskLanes
    （settings.json 设置、library.json 分类与分区、logs\\ 日志）
  - 注册表：只写 HKCU 下三个点 —— 开机自启项、卸载项、桌面图标显示状态。
    不写 HKLM，不需要管理员权限
  - 开机自启与「隐藏桌面图标」默认都是关的，要你自己去设置里勾

==========================================
四、快捷键与三种形态
==========================================
  默认呼出快捷键：Ctrl + Alt + Space（设置里可改）
  三种形态：悬浮窗 / 全屏工作空间 / 侧边栏，共用同一份项目与分类，
  托盘图标右键可切换。

==========================================
五、出问题了怎么办
==========================================
设置窗口里有「导出诊断包」，会把配置与日志打包成一个 zip，
贴到 issue 里就能定位问题。

已知限制（发布前建议先看一眼仓库里的 docs/known-limitations.md，
完整清单在那里）：
  - 没有数字签名；无自动更新（升级 = 覆盖安装）
  - 只支持 Windows 11（玻璃材质走 DWM 系统背景，需要 22621+），Win10 未验证
  - DPI 已验证 100% / 125% / 150% 与混合 DPI；200%+ 与 1366x768 及以下未覆盖

==========================================
包内文件
==========================================
  DeskLanes.exe   {exe_size} / MD5 {exe_md5}
                  构建于 {exe_time}
  install.ps1     安装脚本
  uninstall.ps1   卸载脚本
  使用说明.txt     本文件
"""


def build_notice(version, exe_path, selfcontained):
    if selfcontained:
        variant_name = "自包含版（自带 .NET 10 运行时）"
        variant_hint = ("双击 DeskLanes.exe 就能跑。自带 .NET 10 运行时，\n"
                        "干净的系统上也不用先装任何东西，不需要管理员权限。")
    else:
        variant_name = "便携版（需要本机已装 .NET 10 桌面运行时）"
        variant_hint = ("双击 DeskLanes.exe 就能跑 —— 但前提是这台机器已经装了\n"
                        ".NET 10 **桌面**运行时（Microsoft.WindowsDesktop.App 10.x）。\n"
                        "只装 .NET Runtime 不够，WPF 必须是桌面运行时。\n"
                        "缺运行时时系统会弹一个带下载按钮的提示框，不会静默失败。\n"
                        "不确定就改用自包含版（约 62 MB，什么都不用装）。")
    st = os.stat(exe_path)
    return NOTICE.format(
        version=version,
        variant_name=variant_name,
        variant_hint=variant_hint,
        exe_size=human_size(st.st_size),
        exe_md5=md5_of(exe_path),
        exe_time=time.strftime("%Y-%m-%d %H:%M", time.localtime(st.st_mtime)),
    )


def main():
    if len(sys.argv) < 2:
        print("用法: python tools/package-release.py <版本号，如 v0.2.0>")
        return 2
    version = sys.argv[1].lstrip("v")
    tag = "v" + version

    os.makedirs(RELEASE, exist_ok=True)

    src_commit = git_src_commit_time()
    print("最后一次改 src/ 的提交时间：%s"
          % (time.strftime("%Y-%m-%d %H:%M", time.localtime(src_commit))
             if src_commit else "（查不到，跳过新鲜度检查）"))

    results = []
    for name, exe_dir, selfcontained in VARIANTS:
        exe = os.path.join(exe_dir, "DeskLanes.exe")
        if not os.path.exists(exe):
            print("[FAIL] 找不到 %s —— 先跑 publish" % exe)
            return 1

        mtime = os.stat(exe).st_mtime
        print("\n[%s] exe 构建于 %s"
              % (name, time.strftime("%Y-%m-%d %H:%M", time.localtime(mtime))))
        if src_commit and mtime < src_commit:
            print("[FAIL] exe 比源码旧 —— 源码在 exe 之后还改过，"
                  "必须先重新 publish 再打包")
            return 1
        print("[PASS]  exe 不比源码旧")

        stage = os.path.join(RELEASE, "stage-%s-%s" % (tag, name))
        if os.path.isdir(stage):
            shutil.rmtree(stage)
        os.makedirs(stage)

        shutil.copy2(exe, os.path.join(stage, "DeskLanes.exe"))
        for script in ("install.ps1", "uninstall.ps1"):
            shutil.copy2(os.path.join(TOOLS, script), os.path.join(stage, script))
        with open(os.path.join(stage, "使用说明.txt"), "w",
                  encoding="utf-8-sig", newline="\r\n") as f:
            f.write(build_notice(tag, exe, selfcontained))

        zip_path = os.path.join(RELEASE, "DeskLanes-%s-%s.zip" % (tag, name))
        if os.path.exists(zip_path):
            os.remove(zip_path)
        with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as z:
            for fn in sorted(os.listdir(stage)):
                z.write(os.path.join(stage, fn), fn)

        size = os.path.getsize(zip_path)
        print("[PASS]  %s —— %d B（%s）/ MD5 %s"
              % (os.path.basename(zip_path), size, human_size(size),
                 md5_of(zip_path)[:16]))
        results.append((os.path.basename(zip_path), size, md5_of(zip_path)))
        shutil.rmtree(stage)

    print("\n===== 打包完成 %d 个，产物在 publish/release/ =====" % len(results))
    for fn, size, digest in results:
        print("  %s  %d B  %s" % (fn, size, digest))
    return 0


if __name__ == "__main__":
    sys.exit(main())
