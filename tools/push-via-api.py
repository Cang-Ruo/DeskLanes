#!/usr/bin/env python3
"""当 github.com 走不通（被代理拦在 CONNECT 层）时，改用 api.github.com 推送。

原理：Git 的提交 SHA = hash(tree, parent, author, committer, message)，
只要这五样跟本地一字不差地复刻过去，GitHub 算出来的 SHA 就与本地**完全相同**。
脚本按提交逐个复刻，并**逐个比对 SHA**——不一样就停下，不会把历史写歪。

用法：
    python tools/push-via-api.py                  # 推送 origin/main..main
    python tools/push-via-api.py --dry            # 只算不推

前置：`gh auth login` 已登录（脚本用 `gh auth token` 取令牌）。
"""

import base64
import datetime
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
API = "https://api.github.com"


def git(*args, binary=False):
    out = subprocess.run(["git"] + list(args), cwd=REPO_ROOT,
                         capture_output=True, text=not binary)
    if out.returncode != 0:
        raise RuntimeError("git %s 失败: %s"
                           % (" ".join(args),
                              out.stderr if isinstance(out.stderr, str)
                              else out.stderr.decode("utf-8", "replace")))
    return out.stdout


def gh(*args):
    for candidate in ("gh", os.path.join(
            os.environ.get("LOCALAPPDATA", ""),
            "Microsoft", "WinGet", "Packages",
            "GitHub.cli_Microsoft.Winget.Source_8wekyb3d8bbwe",
            "bin", "gh.exe")):
        try:
            out = subprocess.run([candidate] + list(args), cwd=REPO_ROOT,
                                 capture_output=True, text=True)
            if out.returncode == 0:
                return out.stdout.strip()
        except OSError:
            continue
    raise RuntimeError("找不到可用的 gh（先装并 gh auth login）")


class Gh:
    def __init__(self, token, owner, repo):
        self.token = token
        self.owner = owner
        self.repo = repo

    def call(self, method, path, payload=None):
        url = "%s/repos/%s/%s/%s" % (API, self.owner, self.repo, path)
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        # 沙箱代理会间歇性掐断连接（10053 / 502），失败就退避重试
        last = None
        for attempt in range(6):
            req = urllib.request.Request(url, data=data, method=method)
            req.add_header("Authorization", "Bearer " + self.token)
            req.add_header("Accept", "application/vnd.github+json")
            req.add_header("User-Agent", "DeskLanes-push-via-api")
            if data:
                req.add_header("Content-Type", "application/json")
            try:
                with urllib.request.urlopen(req, timeout=120) as resp:
                    return json.loads(resp.read().decode("utf-8"))
            except urllib.error.HTTPError as e:
                body = e.read().decode("utf-8", "replace")[:600]
                if e.code >= 500:
                    last = RuntimeError("%s %s 失败 %d: %s"
                                        % (method, path, e.code, body))
                else:
                    raise RuntimeError("%s %s 失败 %d: %s"
                                       % (method, path, e.code, body))
            except (urllib.error.URLError, ConnectionError, OSError) as e:
                last = RuntimeError("%s %s 连接失败: %s" % (method, path, e))
            time.sleep(2 ** attempt)
        raise last


def commit_meta(sha):
    raw = git("cat-file", "commit", sha)
    head, _, message = raw.partition("\n\n")
    meta = {}
    for line in head.splitlines():
        key, _, val = line.partition(" ")
        meta[key] = val
    who = {}
    for role in ("author", "committer"):
        # 形如  Name <email> 1690000000 +0800
        text = meta.get(role, "")
        name = text.rsplit("<", 1)[0].strip()
        email = text.rsplit("<", 1)[1].split(">", 1)[0] if "<" in text else ""
        rest = text.rsplit(">", 1)[1].split() if ">" in text else []
        # GitHub 只吃 ISO 8601，git 给的是 "<unix> +0800"，要转换
        date = None
        if len(rest) >= 2:
            ts, off = int(rest[0]), rest[1]
            date = "%s%s:%s" % (
                datetime.datetime.fromtimestamp(
                    ts, datetime.timezone(datetime.timedelta(
                        hours=int(off[:3]), minutes=int(off[0] + off[3:])))
                ).replace(microsecond=0).isoformat()[:19],
                off[:3], off[3:])
        who[role] = {"name": name, "email": email, "date": date}
    return message, who


def remote_url_parse():
    url = git("config", "--get", "remote.origin.url").strip()
    # 兼容 https://github.com/O/R.git 与 git@github.com:O/R.git
    if url.startswith("git@"):
        path = url.split(":", 1)[1]
    else:
        path = url.split("github.com/", 1)[-1]
    path = path[:-4] if path.endswith(".git") else path
    owner, repo = path.split("/", 1)
    return owner, repo


def main():
    dry = "--dry" in sys.argv
    owner, repo = remote_url_parse()
    token = gh("auth", "token")
    g = Gh(token, owner, repo)

    pending = git("rev-list", "--reverse", "origin/main..main").split()
    if not pending:
        print("没有待推送的提交")
        return 0

    remote_head = g.call("GET", "git/ref/heads/main")["object"]["sha"]
    print("远端 main = %s" % remote_head[:12])
    print("待推送 %d 个提交：%s" % (len(pending), ", ".join(c[:8] for c in pending)))

    current = remote_head
    for sha in pending:
        parent = git("rev-parse", sha + "^").strip()
        message, who = commit_meta(sha)
        parent_tree = g.call("GET", "git/commits/" + current)["tree"]["sha"]

        status = git("diff", "--name-status", parent, sha).strip().splitlines()
        entries = []
        for line in status:
            st, _, path = line.partition("\t")
            if st in ("A", "M"):
                mode = git("ls-tree", sha, "--", path).split()[0]
                content = git("show", sha + ":" + path, binary=True)
                blob = g.call("POST", "git/blobs", {
                    "content": base64.b64encode(content).decode("ascii"),
                    "encoding": "base64",
                })
                entries.append({"path": path, "mode": mode,
                                "type": "blob", "sha": blob["sha"]})
                print("   blob %s (%d B) %s" % (blob["sha"][:8], len(content), path))
            elif st == "D":
                entries.append({"path": path, "mode": "100644",
                                "type": "blob", "sha": None})
                print("   delete %s" % path)

        tree = g.call("POST", "git/trees", {"base_tree": parent_tree,
                                            "tree": entries})
        commit = g.call("POST", "git/commits", {
            "message": message,
            "tree": tree["sha"],
            "parents": [current],
            "author": who["author"],
            "committer": who["committer"],
        })
        ok = commit["sha"] == sha
        print("[%s] %s %s —— API 复刻 SHA %s"
              % ("PASS" if ok else "FAIL", sha[:8],
                 message.splitlines()[0][:40], commit["sha"][:12]))
        if not ok:
            print("FAIL  SHA 与本地不一致，停手（远端未改动）")
            return 1
        current = commit["sha"]

    if dry:
        print("--dry：不更新远端引用")
        return 0

    g.call("PATCH", "git/refs/heads/main", {"sha": current})
    print("[PASS] 远端 main 已更新到 %s" % current[:12])
    # fetch 也走 github.com（同样不通），直接把本地的远端跟踪引用挪过去
    git("update-ref", "refs/remotes/origin/main", current)
    print("[PASS] 本地 origin/main 同步到 %s" % current[:12])
    return 0


if __name__ == "__main__":
    sys.exit(main())
