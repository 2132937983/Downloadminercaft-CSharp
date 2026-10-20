#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Minecraft 原版 client.jar 下载器（命令行 shell 版）
- 启动时并发拉取 Mojang 与 BMCLAPI 的版本清单
- 支持 release / snapshot / old_beta / old_alpha（含愚人节版本）
- list 结果带 0001 起始序号，get/info 可直接引用序号
- 多版本下载时显示每个文件的进度条与一个总进度
仅使用 Python 标准库。
"""

import concurrent.futures as cf
import hashlib
import json
import os
import re
import shlex
import sys
import time
import urllib.parse
import urllib.request

MOJANG_MANIFEST = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
BMCLAPI_MANIFEST = "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json"
BMCLAPI_HOST = "bmclapi2.bangbang93.com"
MIRROR_HOSTS = (
    "launchermeta.mojang.com",
    "launcher.mojang.com",
    "piston-meta.mojang.com",
    "piston-data.mojang.com",
)
UA = "mc-jar-downloader/1.0 (+cli)"

SOURCE_NAMES = {"mojang": "Mojang", "bmclapi": "BMCLAPI"}

# 已知愚人节版本（仅用于列表标记与统计，可自行补充）
APRIL_FOOLS = {
    "15w14a",
    "1.RV-Pre1",
    "3D Shareware v1.34",
    "20w14infinite",
    "22w13oneBlockAtATime",
    "23w13a_or_b",
    "24w14potato",
    "25w14craftmine",
}


# ---------- 基础工具 ----------

def safe_url(url: str) -> str:
    return urllib.parse.quote(url, safe=":/?&=%#[]@!$'()*+,;~")


def http_get(url, timeout=20):
    req = urllib.request.Request(safe_url(url), headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return resp.read()


def human(n) -> str:
    try:
        n = float(n)
    except (TypeError, ValueError):
        return "未知"
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.1f}{unit}"
        n /= 1024
    return f"{n:.1f}GB"


def show_progress(got, total, t0):
    speed = got / max(time.time() - t0, 1e-6)
    if total:
        pct = min(got * 100.0 / total, 100.0)
        bar_len = 20
        filled = min(bar_len, int(bar_len * got / total))
        bar = "#" * filled + "-" * (bar_len - filled)
        msg = f"\r  [{bar}] {pct:5.1f}% {human(got)}/{human(total)} {human(speed)}/s"
    else:
        msg = f"\r  {human(got)} {human(speed)}/s"
    sys.stdout.write(msg)
    sys.stdout.flush()


def render_bar(got, total, width=20):
    if total:
        pct = min(got * 100.0 / total, 100.0)
        filled = min(width, int(width * got / total))
        bar = "#" * filled + "-" * (width - filled)
        return f"[{bar}] {pct:5.1f}% {human(got)}/{human(total)}"
    return human(got)


def default_download_dir():
    """优先使用系统下载文件夹。"""
    if os.name == "nt":
        try:
            import winreg

            key = winreg.OpenKey(
                winreg.HKEY_CURRENT_USER,
                r"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
            )
            try:
                val, _ = winreg.QueryValueEx(
                    key, "{374DE290-123F-4565-9164-39C4925E467B}"
                )
            finally:
                winreg.CloseKey(key)
            val = os.path.expandvars(val)
            if val:
                return val
        except Exception:
            pass

    xdg = os.environ.get("XDG_DOWNLOAD_DIR")
    if xdg:
        return os.path.expandvars(xdg)

    home = os.path.expanduser("~")
    for name in ("Downloads", "下载"):
        p = os.path.join(home, name)
        if os.path.isdir(p):
            return p
    return os.path.join(home, "Downloads")


# ---------- 清单拉取与合并 ----------

def fetch_manifest(source, url):
    t0 = time.time()
    try:
        data = json.loads(http_get(url).decode("utf-8"))
        return source, data, time.time() - t0, None
    except Exception as e:  # noqa: BLE001
        return source, None, time.time() - t0, e


def merge_manifests(results):
    versions = {}
    order = []
    for source, data, _, err in results:
        if not data:
            continue
        for item in data.get("versions", []):
            vid = item.get("id")
            if not vid:
                continue
            if vid not in versions:
                versions[vid] = {
                    "id": vid,
                    "type": item.get("type", "unknown"),
                    "releaseTime": item.get("releaseTime", ""),
                    "sources": {},
                }
                order.append(vid)
            versions[vid]["sources"][source] = item.get("url")
    return versions, order


def source_type_stats(data):
    stats = {
        "release": 0,
        "snapshot": 0,
        "april": 0,
        "old_alpha": 0,
        "old_beta": 0,
    }
    other = 0
    for item in data.get("versions", []):
        vid = item.get("id", "")
        t = item.get("type", "")
        if vid in APRIL_FOOLS:
            stats["april"] += 1
        elif t in stats:
            stats[t] += 1
        else:
            other += 1
    return stats, other


def resolve_url(url, source):
    if source != "bmclapi" or not url:
        return url
    for host in MIRROR_HOSTS:
        url = url.replace(host, BMCLAPI_HOST)
    return url


def get_version_json(vid, source, versions):
    info = versions[vid]
    url = info["sources"].get(source)
    if not url:
        raise RuntimeError(f"{SOURCE_NAMES[source]} 源没有该版本")
    try:
        return json.loads(http_get(resolve_url(url, source)).decode("utf-8")), source
    except Exception as e:  # noqa: BLE001
        fallback = "mojang" if source != "mojang" else "bmclapi"
        if fallback in info["sources"]:
            print(f"  ! {SOURCE_NAMES[source]} 解析失败（{e}），回退 {SOURCE_NAMES[fallback]}")
            return json.loads(http_get(info["sources"][fallback]).decode("utf-8")), fallback
        raise


# ---------- 下载 ----------

def download_file(url, path, expected_size=None, expected_sha1=None):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    tmp = path + ".part"
    req = urllib.request.Request(safe_url(url), headers={"User-Agent": UA})
    digest = hashlib.sha1()
    got = 0
    total = expected_size or 0
    t0 = time.time()
    last = 0.0
    try:
        with urllib.request.urlopen(req, timeout=30) as resp, open(tmp, "wb") as f:
            if not total:
                total = int(resp.headers.get("Content-Length") or 0)
            while True:
                chunk = resp.read(64 * 1024)
                if not chunk:
                    break
                f.write(chunk)
                digest.update(chunk)
                got += len(chunk)
                now = time.time()
                if now - last >= 0.1:
                    last = now
                    show_progress(got, total, t0)
        show_progress(got, total, t0)
        sys.stdout.write("\n")
        if expected_sha1 and digest.hexdigest().lower() != expected_sha1.lower():
            raise RuntimeError(
                f"SHA1 校验失败：期望 {expected_sha1}，实际 {digest.hexdigest()}"
            )
        if expected_size and got != expected_size:
            raise RuntimeError(f"文件大小不符：期望 {expected_size}，实际 {got}")
        os.replace(tmp, path)
    except BaseException:
        try:
            if os.path.exists(tmp):
                os.remove(tmp)
        except OSError:
            pass
        raise
    return path


def download_one(task):
    """下载单个任务，返回 (path, got, elapsed, skipped)。"""
    path = task["path"]
    size = task["size"]
    sha1 = task["sha1"]
    url = task["url"]

    if os.path.exists(path) and sha1:
        h = hashlib.sha1()
        with open(path, "rb") as f:
            for chunk in iter(lambda: f.read(1 << 20), b""):
                h.update(chunk)
        if h.hexdigest().lower() == sha1.lower():
            print("  文件已存在且 SHA1 一致，跳过下载")
            return path, size or os.path.getsize(path), 0.0, True

    try:
        download_file(url, path, size, sha1)
        return path, size or os.path.getsize(path), 0.0, False
    except Exception as e:  # noqa: BLE001
        official = task.get("official_url")
        if official and official != url:
            print(f"  下载失败: {e}")
            print(f"  回退官方链接: {official}")
            download_file(official, path, size, sha1)
            return path, size or os.path.getsize(path), 0.0, False
        raise


# ---------- 版本解析 ----------

def normalize_type(t):
    t = t.strip()
    low = t.lower()
    if low in ("r", "release", "正式版"):
        return "release"
    if low in ("s", "snapshot", "预览版"):
        return "snapshot"
    if low in ("a", "april", "愚人节"):
        return "april"
    if low in ("oa", "old_alpha"):
        return "old_alpha"
    if low in ("ob", "old_beta"):
        return "old_beta"
    return low


def resolve_version_token(token, versions, last_list):
    token = token.strip()
    if not token:
        raise ValueError("版本为空")

    if token.isdigit():
        if not last_list:
            raise ValueError("尚未使用过 list 命令，无法使用序号。请先运行 list。")
        key = token.zfill(4)
        if key in last_list:
            return last_list[key]
        raise ValueError(f"序号 {token} 不在上次 list 结果中")

    if token in versions:
        return token

    matches = [k for k in versions if token.lower() in k.lower()]
    if len(matches) == 1:
        return matches[0]
    if len(matches) > 1:
        raise ValueError(
            f"“{token}” 匹配到多个版本，请写完整：{', '.join(matches[:10])}"
        )
    raise ValueError(f"没有找到版本 “{token}”")


# ---------- 输入解析 ----------

def parse_input_line(line):
    """提取 out-"..." 路径，避免 shlex 吃掉 Windows 反斜杠与末尾引号。"""
    paths = []
    pattern = re.compile(r'(out-)(["\'])(.*?)\2')

    def _sub(m):
        paths.append(m.group(3))
        return f"out-__PATH_{len(paths) - 1}__"

    new_line = pattern.sub(_sub, line)
    try:
        parts = shlex.split(new_line, posix=True)
    except ValueError as e:
        raise ValueError(f"命令解析失败: {e}")

    restored = []
    for p in parts:
        if p.startswith("out-__PATH_") and p.endswith("__"):
            idx = int(p[len("out-__PATH_") : -2])
            restored.append("out-" + paths[idx])
        else:
            restored.append(p)
    return restored


# ---------- shell 命令 ----------

def print_help():
    print("命令:")
    print("  list [数量] [-D=M/B] [-S=搜索] [-T=类型] [-a]")
    print("        列出合并后的版本，带 0001 起始序号")
    print("        -D=M/B       按下载源过滤（M=Mojang, B=BMCLAPI）")
    print("        -S=关键字    按版本 ID 搜索")
    print("        -T=类型      类型过滤：R=release, S=snapshot, a=april,")
    print("                     oa=old_alpha, ob=old_beta（可逗号组合）")
    print("        数量省略默认 30，all 或 -a 显示全部")
    print("  april                       列出已知愚人节版本")
    print("  info <版本ID或序号>         查看版本在 Mojang / BMCLAPI 的可用情况")
    print('  get <版本[,版本...]> [M/B] [out-"路径"] [Y/N]')
    print("        下载 client.jar；版本可用 list 序号，逗号支持中英文")
    print("        未指定源时默认 BMCLAPI；未指定路径时用系统下载文件夹")
    print("        Y 表示下载后退出，N 或不写表示不退出")
    print("  outdir [目录]               查看或设置默认下载目录")
    print("  quit / Exit                 退出")
    print('提示: 版本 ID 含空格时用引号，如 get "3D Shareware v1.34" B')


def cmd_list(versions, order, args, state):
    limit = 30
    show_all = False
    source_filter = None
    search = None
    type_filter = set()

    for a in args:
        if a in ("-a", "--all", "all"):
            show_all = True
        elif a.isdigit():
            limit = int(a)
        elif a.startswith("-D="):
            val = a[3:].strip()
            if val.upper() in ("M", "MOJANG"):
                source_filter = "mojang"
            elif val.upper() in ("B", "BMCLAPI"):
                source_filter = "bmclapi"
            else:
                print(f"忽略无效 -D 值: {val}")
        elif a.startswith("-S="):
            search = a[3:].strip()
        elif a.startswith("-T="):
            for t in re.split(r"[,，]", a[3:]):
                if t.strip():
                    type_filter.add(normalize_type(t))
        else:
            print(f"忽略未知参数: {a}")

    items = []
    for vid in order:
        v = versions[vid]
        if search and search.lower() not in vid.lower():
            continue
        if source_filter and source_filter not in v["sources"]:
            continue
        if type_filter:
            is_april = vid in APRIL_FOOLS
            t = "april" if is_april else v["type"]
            if t not in type_filter:
                continue
        items.append(v)

    total = len(items)
    shown = items if show_all or limit == 0 else items[:limit]

    if not show_all and limit and total > len(shown):
        print(f"共 {total} 个版本，显示最近 {len(shown)} 个（list all 查看全部）")
    else:
        print(f"共 {total} 个版本")

    for i, v in enumerate(shown, 1):
        idx = f"{i:04d}"
        flags = ("M" if "mojang" in v["sources"] else "-") + (
            "B" if "bmclapi" in v["sources"] else "-"
        )
        april = " [愚人节]" if v["id"] in APRIL_FOOLS else ""
        print(
            f"  {idx}  {v['id']:<30} {v['type']:<10} "
            f"{v['releaseTime'][:10]}  {flags}{april}"
        )

    state["last_list"] = {f"{i:04d}": v["id"] for i, v in enumerate(shown, 1)}
    print("源标记: M=Mojang  B=BMCLAPI")


def cmd_april(versions):
    found = [vid for vid in APRIL_FOOLS if vid in versions]
    if not found:
        print("未在版本列表中匹配到已知愚人节版本。")
        return
    print(f"已知愚人节版本 {len(found)} 个：")
    for vid in sorted(found, key=lambda x: versions[x]["releaseTime"]):
        v = versions[vid]
        src = " + ".join(
            SOURCE_NAMES[s] for s in ("mojang", "bmclapi") if s in v["sources"]
        )
        print(f"  {vid:<24} {v['type']:<10} {v['releaseTime'][:10]}  可用源: {src}")


def cmd_info(versions, args, state):
    if not args:
        print("用法: info <版本ID或序号>")
        return
    key = args[0]
    try:
        vid = resolve_version_token(key, versions, state["last_list"])
    except ValueError as e:
        print(f"错误: {e}")
        return

    v = versions[vid]
    print(f"版本: {v['id']}")
    print(f"  类型: {v['type']}")
    print(f"  发布时间: {v['releaseTime']}")
    for src in ("mojang", "bmclapi"):
        if src in v["sources"]:
            print(f"  {SOURCE_NAMES[src]} 版本 JSON: {v['sources'][src]}")
        else:
            print(f"  {SOURCE_NAMES[src]}: 不可用")
    if v["id"] in APRIL_FOOLS:
        print("  标记: 愚人节版本")
    print()


def cmd_get(versions, args, state):
    vid_expr = None
    source = None
    out = state["out_dir"]
    exit_after = False

    for token in args:
        if token in ("M", "m", "mojang", "Mojang", "MOJANG"):
            source = "mojang"
        elif token in ("B", "b", "bmclapi", "BMCLAPI", "Bmclapi"):
            source = "bmclapi"
        elif token.lower() in ("y", "yes"):
            exit_after = True
        elif token.lower() in ("n", "no"):
            exit_after = False
        elif token.startswith("out-"):
            out = token[4:]
        elif vid_expr is None:
            vid_expr = token
        else:
            print(f"忽略未知参数: {token}")

    if not vid_expr:
        print('用法: get <版本[,版本...]> [M/B] [out-"路径"] [Y/N]')
        return False
    if not out:
        print("输出路径为空")
        return False

    tokens = [t.strip() for t in re.split(r"[,，]", vid_expr) if t.strip()]
    if not tokens:
        print("版本列表为空")
        return False

    targets = []
    for tok in tokens:
        try:
            targets.append(resolve_version_token(tok, versions, state["last_list"]))
        except ValueError as e:
            print(f"错误: {e}")
            return False

    seen = set()
    uniq = []
    for t in targets:
        if t not in seen:
            seen.add(t)
            uniq.append(t)
    targets = uniq

    tasks = []
    for target in targets:
        info = versions[target]
        src = source or ("bmclapi" if "bmclapi" in info["sources"] else "mojang")
        if src not in info["sources"]:
            print(f"跳过 {target}: {SOURCE_NAMES[src]} 源不可用")
            continue
        try:
            vj, actual = get_version_json(target, src, versions)
        except Exception as e:  # noqa: BLE001
            print(f"跳过 {target}: 解析版本 JSON 失败: {e}")
            continue

        dl = (vj.get("downloads") or {}).get("client")
        if not dl or not dl.get("url"):
            print(f"跳过 {target}: 版本 JSON 中没有 client.jar")
            continue

        official_url = dl["url"]
        url = resolve_url(official_url, actual)
        path = os.path.join(out, f"{target}.jar")
        tasks.append(
            {
                "target": target,
                "actual": actual,
                "url": url,
                "official_url": official_url,
                "size": dl.get("size"),
                "sha1": dl.get("sha1"),
                "path": path,
            }
        )

    if not tasks:
        print("没有可下载的版本。")
        return exit_after

    print(f"共 {len(tasks)} 个版本待下载，输出目录: {os.path.abspath(out)}")
    overall_total = sum(t["size"] or 0 for t in tasks)
    overall_got = 0
    overall_t0 = time.time()
    failed = []

    for idx, task in enumerate(tasks, 1):
        print(
            f"\n[{idx}/{len(tasks)}] {task['target']}  "
            f"源: {SOURCE_NAMES[task['actual']]}"
        )
        print(f"  实际链接: {task['url']}")
        try:
            path, got, _, _ = download_one(task)
            overall_got += got
        except Exception as e:  # noqa: BLE001
            print(f"  下载失败: {e}")
            failed.append(task["target"])
            continue

        if len(tasks) > 1:
            print(f"  总进度: {render_bar(overall_got, overall_total)}")

    if len(tasks) > 1:
        print(f"\n全部完成，总用时 {time.time() - overall_t0:.1f}s")
        print(f"总进度: {render_bar(overall_got, overall_total)}")
    if failed:
        print(f"失败 {len(failed)} 个: {', '.join(failed)}")

    return exit_after


def repl(versions, order, state):
    while True:
        try:
            line = input("mc> ").strip()
        except (EOFError, KeyboardInterrupt):
            print("\n已退出")
            return
        if not line:
            continue

        try:
            parts = parse_input_line(line)
        except ValueError as e:
            print(f"命令解析失败: {e}")
            continue

        cmd = parts[0].lower()
        args = parts[1:]

        try:
            if cmd in ("quit", "exit"):
                print("已退出")
                return
            elif cmd in ("help", "?"):
                print_help()
            elif cmd == "list":
                cmd_list(versions, order, args, state)
            elif cmd == "april":
                cmd_april(versions)
            elif cmd == "info":
                cmd_info(versions, args, state)
            elif cmd == "get":
                should_exit = cmd_get(versions, args, state)
                if should_exit:
                    print("已退出")
                    return
            elif cmd == "outdir":
                if args:
                    state["out_dir"] = args[0]
                print(f"下载目录: {os.path.abspath(state['out_dir'])}")
            else:
                print(f"未知命令: {cmd}（help 查看帮助）")
        except KeyboardInterrupt:
            print("\n已取消当前操作")
        except Exception as e:  # noqa: BLE001
            print(f"出错: {e}")


def print_stats(results):
    print("扫描结果统计：")
    for source, label in (("mojang", "M"), ("bmclapi", "B")):
        print(f"{label}：")
        data = next((d for s, d, _, _ in results if s == source), None)
        if not data:
            print("  （拉取失败，无数据）")
            continue
        st, other = source_type_stats(data)
        print(f"  release（正式版）：{st['release']}")
        print(f"  snapshot（预览版）：{st['snapshot']}")
        print(f"  april（愚人节）：{st['april']}")
        print(f"  old_alpha：{st['old_alpha']}")
        print(f"  old_beta：{st['old_beta']}")
        if other:
            print(f"  其他类型：{other}")


def main():
    print("Minecraft client.jar 下载器（Mojang + BMCLAPI 双源）")
    print("正在并发拉取版本清单...")
    t_start = time.time()
    with cf.ThreadPoolExecutor(max_workers=2) as pool:
        futs = [
            pool.submit(fetch_manifest, "mojang", MOJANG_MANIFEST),
            pool.submit(fetch_manifest, "bmclapi", BMCLAPI_MANIFEST),
        ]
        results = [f.result() for f in futs]
    total_elapsed = time.time() - t_start

    for source, data, elapsed, err in results:
        if data:
            n = len(data.get("versions", []))
            print(f"  [{SOURCE_NAMES[source]}] 成功：{n} 个版本，耗时 {elapsed:.2f}s")
        else:
            print(f"  [{SOURCE_NAMES[source]}] 失败：{err}，耗时 {elapsed:.2f}s")

    versions, order = merge_manifests(results)
    if not versions:
        print("两个源都拉取失败，退出。")
        return

    print(f"扫描完成，总用时 {total_elapsed:.2f}s")
    print()
    print_stats(results)
    print()
    print_help()
    print()

    state = {"last_list": {}, "out_dir": default_download_dir()}
    repl(versions, order, state)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\n已退出")