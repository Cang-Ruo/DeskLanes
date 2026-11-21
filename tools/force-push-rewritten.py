#!/usr/bin/env python3
"""历史重写后的强推：把整个本地历史逐提交复刻到 GitHub，再把 main 指到新 HEAD。

与 push-via-api.py 的区别：那个找 origin/main..main（增量），
这个从根提交开始全量复刻（因为 filter-repo 改写了所有 SHA，没有共同祖先）。

每个提交都比对 API 复刻出的 SHA 与本地 SHA，不一致立即停手且不动远端 ref。
"""
import base64
import os
import subprocess
import sys

import datetime
import json
import urllib.error
import urllib.request

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
API = "https://api.github.com"


def git(*args, binary=False):
    out = subprocess.run(["git"] + list(args), cwd=REPO_ROOT,
                         capture_output=True, text=not binary)
    if out.returncode != 0:
        raise RuntimeError("git %s 失败: %s" % (" ".join(args), out.stderr))
    return out.stdout


def gh(*args):
    out = subprocess.run(["gh"] + list(args), cwd=REPO_ROOT,
                         capture_output=True, text=True)
    if out.returncode != 0:
        raise RuntimeError("gh %s 失败: %s" % (" ".join(args), out.stderr))
    return out.stdout.strip()


class Gh:
    def __init__(self, token, owner, repo):
        self.token, self.owner, self.repo = token, owner, repo

    def call(self, method, path, payload=None):
        url = "%s/repos/%s/%s/%s" % (API, self.owner, self.repo, path)
        data = json.dumps(payload).encode("utf-8") if payload is not None else None
        last = None
        for attempt in range(6):
            req = urllib.request.Request(url, data=data, method=method)
            req.add_header("Authorization", "Bearer " + self.token)
            req.add_header("Accept", "application/vnd.github+json")
            req.add_header("User-Agent", "DeskLanes-force-push")
            if data:
                req.add_header("Content-Type", "application/json")
            try:
                with urllib.request.urlopen(req, timeout=120) as resp:
                    return json.loads(resp.read().decode("utf-8"))
            except urllib.error.HTTPError as e:
                try:
                    body = e.read().decode("utf-8", "replace")[:400]
                except Exception:
                    body = "(无法读取错误响应)"
                last = RuntimeError("%s %s HTTP %d: %s" % (method, path, e.code, body))
                if e.code < 500:
                    raise last
            except (urllib.error.URLError, ConnectionError, OSError) as e:
                last = RuntimeError("%s %s 连接失败: %s" % (method, path, e))
            import time
            time.sleep(2 ** attempt)
        raise last


def _iso(text):
    rest = text.rsplit(">", 1)[1].split() if ">" in text else []
    if len(rest) < 2:
        return None
    ts, off = int(rest[0]), rest[1]
    return "%s%s:%s" % (
        datetime.datetime.fromtimestamp(
            ts, datetime.timezone(datetime.timedelta(
                hours=int(off[:3]), minutes=int(off[0] + off[3:])))
        ).replace(microsecond=0).isoformat()[:19], off[:3], off[3:])


def _who(text):
    return {"name": text.rsplit("<", 1)[0].strip(),
            "email": text.rsplit("<", 1)[1].split(">", 1)[0] if "<" in text else "",
            "date": _iso(text)}


def commit_meta(sha):
    raw = git("cat-file", "commit", sha)
    head, _, message = raw.partition("\n\n")
    meta = {}
    for line in head.splitlines():
        key, _, val = line.partition(" ")
        meta[key] = val
    return message, {r: _who(meta.get(r, "")) for r in ("author", "committer")}


def remote_url_parse():
    url = git("config", "--get", "remote.origin.url").strip()
    path = url.split("github.com/", 1)[-1]
    path = path[:-4] if path.endswith(".git") else path
    return path.split("/", 1)


def main():
    owner, repo = remote_url_parse()
    g = Gh(gh("auth", "token"), owner, repo)
    shas = git("rev-list", "--reverse", "HEAD").split()
    print("本地提交 %d 个，根 %s，HEAD %s" %
          (len(shas), shas[0][:12], shas[-1][:12]))

    # 远端已有对象：旧历史的所有 commit/blob/tree 仍在 GitHub 上（未删除），
    # 但我们要从空 parent 开始复刻，首个提交无 parent。
    current = None
    for i, sha in enumerate(shas, 1):
        message, who = commit_meta(sha)
        if current is None:
            # 根提交：直接构造完整 tree（不用 base_tree）
            entries = []
            for line in git("ls-tree", "-r", sha).strip().splitlines():
                meta, _, path = line.partition("\t")
                mode, typ, obj = meta.split()
                if typ != "blob":
                    continue
                content = git("cat-file", "blob", obj, binary=True)
                blob = g.call("POST", "git/blobs", {
                    "content": base64.b64encode(content).decode("ascii"),
                    "encoding": "base64",
                })
                entries.append({"path": path, "mode": mode,
                                "type": "blob", "sha": blob["sha"]})
            tree = g.call("POST", "git/trees", {"tree": entries})
            payload = {"message": message, "tree": tree["sha"],
                       "parents": [], "author": who["author"],
                       "committer": who["committer"]}
        else:
            parent = git("rev-parse", sha + "^").strip()
            parent_tree = g.call("GET", "git/commits/" + current)["tree"]["sha"]
            status = git("diff", "--name-status", parent, sha).strip().splitlines()
            entries = []
            for line in status:
                st, _, path = line.partition("\t")
                # R100/C 重命名：形如 "R100\told\tnew"，旧路径删、新路径加
                if st.startswith("R") or st.startswith("C"):
                    old_path, new_path = path.split("\t", 1)
                    entries.append({"path": old_path, "mode": "100644",
                                    "type": "blob", "sha": None})
                    path = new_path
                if st.startswith("R") or st.startswith("C") or st in ("A", "M", "T"):
                    mode = git("ls-tree", sha, "--", path).split()[0]
                    content = git("show", sha + ":" + path, binary=True)
                    blob = g.call("POST", "git/blobs", {
                        "content": base64.b64encode(content).decode("ascii"),
                        "encoding": "base64",
                    })
                    entries.append({"path": path, "mode": mode,
                                    "type": "blob", "sha": blob["sha"]})
                elif st == "D":
                    entries.append({"path": path, "mode": "100644",
                                    "type": "blob", "sha": None})
            tree = g.call("POST", "git/trees", {"base_tree": parent_tree,
                                                "tree": entries})
            payload = {"message": message, "tree": tree["sha"],
                       "parents": [current], "author": who["author"],
                       "committer": who["committer"]}

        commit = g.call("POST", "git/commits", payload)
        if commit["sha"] != sha:
            print("[FAIL] 第 %d 个提交 %s 复刻 SHA 不一致：本地 %s vs API %s"
                  % (i, sha[:8], sha[:12], commit["sha"][:12]))
            print("远端 main 未改动，停手。")
            return 1
        current = commit["sha"]
        if i % 10 == 0 or i == len(shas):
            print("[%d/%d] %s 已复刻" % (i, len(shas), sha[:8]))

    if "--dry" in sys.argv:
        print("--dry：未更新远端 main；应为 %s" % current[:12])
        return 0

    g.call("PATCH", "git/refs/heads/main", {"sha": current})
    print("[PASS] 远端 main 已强制更新到 %s" % current[:12])
    git("update-ref", "refs/remotes/origin/main", current)
    print("[PASS] 本地 origin/main 同步到 %s" % current[:12])
    return 0


if __name__ == "__main__":
    sys.exit(main())
