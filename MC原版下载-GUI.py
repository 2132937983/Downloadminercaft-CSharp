#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Minecraft 原版 client.jar 下载器（GUI 版）
- 顶部搜索框实时过滤版本
- 左右两个列表分别显示 Mojang / BMCLAPI 扫描结果，勾选状态全局同步
- 下载按钮下方可切换优先下载源（默认 Mojang 优先，失败回退另一源）
- 每个版本独立进度条（实时速度）+ 总进度条
仅使用 Python 标准库（tkinter）。
"""

import collections
import concurrent.futures as cf
import hashlib
import json
import os
import queue
import threading
import time
import urllib.parse
import urllib.request
import tkinter as tk
from tkinter import ttk, filedialog, messagebox

MOJANG_MANIFEST = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"
BMCLAPI_MANIFEST = "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json"
BMCLAPI_HOST = "bmclapi2.bangbang93.com"
MIRROR_HOSTS = (
    "launchermeta.mojang.com",
    "launcher.mojang.com",
    "piston-meta.mojang.com",
    "piston-data.mojang.com",
)
UA = "mc-jar-downloader/1.0 (+gui)"

APRIL_FOOLS = {
    "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
    "22w13oneBlockAtATime", "23w13a_or_b", "24w14potato", "25w14craftmine",
}

TYPE_ORDER = ["release", "snapshot", "april", "old_alpha", "old_beta"]
TYPE_NAMES = {
    "release": "release（正式版）",
    "snapshot": "snapshot（预览版）",
    "april": "april（愚人节）",
    "old_alpha": "old_alpha（早期 Alpha）",
    "old_beta": "old_beta（早期 Beta）",
}
SOURCE_NAMES = {"mojang": "Mojang", "bmclapi": "BMCLAPI"}


# ---------- 通用工具 ----------

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
    if n < 0:
        n = 0
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return f"{n:.1f}{unit}"
        n /= 1024
    return f"{n:.1f}GB"


def resolve_url(url, source):
    if source != "bmclapi" or not url:
        return url
    for host in MIRROR_HOSTS:
        url = url.replace(host, BMCLAPI_HOST)
    return url


def fetch_manifest(source, url):
    t0 = time.time()
    try:
        data = json.loads(http_get(url).decode("utf-8"))
        return source, data, time.time() - t0, None
    except Exception as e:  # noqa: BLE001
        return source, None, time.time() - t0, e


def get_version_json(vid, source, versions):
    info = versions[vid]
    url = info["sources"].get(source)
    if not url:
        raise RuntimeError(f"{SOURCE_NAMES[source]} 源没有该版本")
    return json.loads(http_get(resolve_url(url, source)).decode("utf-8")), source


def default_download_dir():
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


# ---------- 主应用 ----------

class MCDownloaderApp:
    def __init__(self, root):
        self.root = root
        self.root.title("Minecraft 原版 JAR 下载器（Mojang + BMCLAPI）")
        self.root.geometry("1180x960")
        self.root.minsize(960, 720)

        self.versions = {}
        self.versions_by_source = {"mojang": {}, "bmclapi": {}}
        self.checked = set()          # 全局勾选：版本 id 集合
        self.collapsed = {"mojang": set(), "bmclapi": set()}
        self.priority_source = "mojang"  # 优先下载源

        self.trees = {}
        self.count_labels = {}
        self.task_widgets = {}
        self.task_got = {}
        self.total_size = 0
        self.queue = queue.Queue()
        self.download_thread = None
        self._search_after = None

        self.out_dir_var = tk.StringVar(value=default_download_dir())
        self.concurrency_var = tk.StringVar(value="4")
        self.total_progress_var = tk.DoubleVar(value=0.0)

        self.build_ui()
        self.refresh_filetree()
        self.load_manifests()
        self.poll_queue()

    # ---------- 界面构建 ----------

    def build_ui(self):
        # 1. 顶部搜索框
        search_frame = ttk.Frame(self.root, padding=(8, 8, 8, 4))
        search_frame.pack(fill="x")
        ttk.Label(search_frame, text="搜索版本：").pack(side="left")
        self.search_var = tk.StringVar()
        self.search_var.trace_add("write", self.on_search_change)
        search_entry = ttk.Entry(search_frame, textvariable=self.search_var)
        search_entry.pack(side="left", fill="x", expand=True, padx=(4, 4))
        ttk.Button(search_frame, text="清空", command=lambda: self.search_var.set("")).pack(
            side="left"
        )

        # 2. 左右两个源列表
        lists_frame = ttk.Frame(self.root, padding=(8, 4, 8, 4))
        lists_frame.pack(fill="both", expand=True)

        left = ttk.LabelFrame(lists_frame, text="Mojang（M）", padding=4)
        left.pack(side="left", fill="both", expand=True, padx=(0, 4))
        self.build_source_panel(left, "mojang")

        right = ttk.LabelFrame(lists_frame, text="BMCLAPI（B）", padding=4)
        right.pack(side="right", fill="both", expand=True, padx=(4, 0))
        self.build_source_panel(right, "bmclapi")

        # 3. 下载路径 + 并发数 + 下载按钮
        path_frame = ttk.Frame(self.root, padding=(8, 4, 8, 2))
        path_frame.pack(fill="x")
        ttk.Label(path_frame, text="下载路径：").pack(side="left")
        self.path_entry = ttk.Entry(path_frame, textvariable=self.out_dir_var)
        self.path_entry.pack(side="left", fill="x", expand=True, padx=(4, 4))
        self.path_entry.bind("<Button-1>", lambda e: self.choose_dir())
        ttk.Button(path_frame, text="浏览...", command=self.choose_dir).pack(side="left")

        ttk.Label(path_frame, text="并发数：").pack(side="left", padx=(8, 0))
        self.concurrency_spin = ttk.Spinbox(
            path_frame, from_=1, to=8, width=3, textvariable=self.concurrency_var
        )
        self.concurrency_spin.pack(side="left", padx=(2, 6))

        self.download_btn = ttk.Button(
            path_frame, text="开始下载", command=self.start_download, state="disabled"
        )
        self.download_btn.pack(side="left")

        # 3.5 优先源切换
        priority_frame = ttk.Frame(self.root, padding=(8, 0, 8, 4))
        priority_frame.pack(fill="x")
        self.priority_btn = ttk.Button(
            priority_frame, text="", command=self.toggle_priority
        )
        self.priority_btn.pack(side="left")
        self.update_priority_btn()

        # 4. 下载目录文件树
        tree_frame = ttk.LabelFrame(self.root, text="下载目录文件树", padding=4)
        tree_frame.pack(fill="x", padx=8, pady=(4, 4))
        self.filetree = ttk.Treeview(
            tree_frame, columns=("size",), show="tree headings", height=6
        )
        self.filetree.heading("#0", text="文件 / 目录")
        self.filetree.heading("size", text="大小")
        self.filetree.column("#0", width=520)
        self.filetree.column("size", width=110, anchor="e")
        ft_scroll = ttk.Scrollbar(tree_frame, orient="vertical", command=self.filetree.yview)
        self.filetree.configure(yscrollcommand=ft_scroll.set)
        self.filetree.pack(side="left", fill="both", expand=True)
        ft_scroll.pack(side="right", fill="y")

        # 5. 每个版本的下载进度条
        progress_frame = ttk.LabelFrame(self.root, text="下载进度", padding=4)
        progress_frame.pack(fill="both", expand=True, padx=8, pady=(4, 4))
        self.progress_canvas = tk.Canvas(progress_frame, highlightthickness=0, height=160)
        progress_scroll = ttk.Scrollbar(
            progress_frame, orient="vertical", command=self.progress_canvas.yview
        )
        self.progress_inner = ttk.Frame(self.progress_canvas)
        self.progress_inner.bind(
            "<Configure>",
            lambda e: self.progress_canvas.configure(
                scrollregion=self.progress_canvas.bbox("all")
            ),
        )
        self.progress_window = self.progress_canvas.create_window(
            (0, 0), window=self.progress_inner, anchor="nw"
        )
        self.progress_canvas.configure(yscrollcommand=progress_scroll.set)
        self.progress_canvas.bind(
            "<Configure>",
            lambda e: self.progress_canvas.itemconfigure(
                self.progress_window, width=e.width
            ),
        )
        self.progress_canvas.pack(side="left", fill="both", expand=True)
        progress_scroll.pack(side="right", fill="y")

        # 6. 总进度条
        total_frame = ttk.Frame(self.root, padding=(8, 4, 8, 4))
        total_frame.pack(fill="x")
        ttk.Label(total_frame, text="总进度：").pack(side="left")
        self.total_progress = ttk.Progressbar(
            total_frame, variable=self.total_progress_var, maximum=100
        )
        self.total_progress.pack(side="left", fill="x", expand=True, padx=(4, 4))
        self.total_label = ttk.Label(total_frame, text="0%", width=32)
        self.total_label.pack(side="left")

        # 7. 退出按钮
        bottom_frame = ttk.Frame(self.root, padding=(8, 4, 8, 8))
        bottom_frame.pack(fill="x")
        ttk.Button(bottom_frame, text="退出", command=self.on_close).pack(side="right")

        self.root.protocol("WM_DELETE_WINDOW", self.on_close)

    def build_source_panel(self, parent, source):
        btns = ttk.Frame(parent)
        btns.pack(fill="x")
        ttk.Button(
            btns, text="全选", command=lambda s=source: self.select_all(s, True)
        ).pack(side="left")
        ttk.Button(
            btns, text="全不选", command=lambda s=source: self.select_all(s, False)
        ).pack(side="left", padx=(4, 0))
        label = ttk.Label(btns, text="已选：0")
        label.pack(side="right")
        self.count_labels[source] = label

        tree_frame = ttk.Frame(parent)
        tree_frame.pack(fill="both", expand=True, pady=(4, 0))
        tree = ttk.Treeview(tree_frame, show="tree", selectmode="none")
        scroll = ttk.Scrollbar(tree_frame, orient="vertical", command=tree.yview)
        tree.configure(yscrollcommand=scroll.set)
        tree.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")
        tree.bind("<Button-1>", lambda e, t=tree, s=source: self.on_tree_press(e, t, s))
        self.trees[source] = tree

    # ---------- 优先源切换 ----------

    def toggle_priority(self):
        self.priority_source = (
            "bmclapi" if self.priority_source == "mojang" else "mojang"
        )
        self.update_priority_btn()

    def update_priority_btn(self):
        name = SOURCE_NAMES[self.priority_source]
        other = SOURCE_NAMES[
            "bmclapi" if self.priority_source == "mojang" else "mojang"
        ]
        self.priority_btn.config(
            text=f"优先下载源：{name} → 失败回退 {other}（点击切换）"
        )

    # ---------- 清单加载 ----------

    def load_manifests(self):
        def worker():
            with cf.ThreadPoolExecutor(max_workers=2) as pool:
                futs = [
                    pool.submit(fetch_manifest, "mojang", MOJANG_MANIFEST),
                    pool.submit(fetch_manifest, "bmclapi", BMCLAPI_MANIFEST),
                ]
                results = [f.result() for f in futs]
            self.queue.put(("manifests_loaded", results))

        threading.Thread(target=worker, daemon=True).start()

    def on_manifests_loaded(self, results):
        failed = []
        for source, data, elapsed, err in results:
            if not data:
                failed.append((source, err))
                continue
            for item in data.get("versions", []):
                vid = item.get("id")
                if not vid:
                    continue
                entry = {
                    "id": vid,
                    "type": item.get("type", "unknown"),
                    "releaseTime": item.get("releaseTime", ""),
                    "url": item.get("url"),
                }
                self.versions_by_source[source][vid] = entry
                if vid not in self.versions:
                    self.versions[vid] = {
                        "id": vid,
                        "type": entry["type"],
                        "releaseTime": entry["releaseTime"],
                        "sources": {},
                    }
                self.versions[vid]["sources"][source] = entry["url"]

        self.rebuild_tree("mojang")
        self.rebuild_tree("bmclapi")
        self.download_btn.config(state="normal")

        if failed:
            msg = "\n".join(f"{SOURCE_NAMES[s]}：{e}" for s, e in failed)
            messagebox.showwarning("部分源加载失败", msg)

    # ---------- 树构建与交互 ----------

    def rebuild_tree(self, source):
        tree = self.trees[source]
        tree.delete(*tree.get_children())
        data = self.versions_by_source[source]
        groups = {}
        for vid, info in data.items():
            t = "april" if vid in APRIL_FOOLS else info["type"]
            groups.setdefault(t, []).append((vid, info))

        search = self.search_var.get().strip().lower()

        for t in TYPE_ORDER:
            if t not in groups:
                continue
            items = groups[t]
            if search:
                items = [i for i in items if search in i[0].lower()]
                if not items:
                    continue
            items.sort(key=lambda x: x[1]["releaseTime"], reverse=True)

            is_open = t not in self.collapsed[source]
            parent = tree.insert(
                "",
                "end",
                text=f"{TYPE_NAMES[t]} ({len(items)})",
                values=(t,),
                tags=("group",),
                open=is_open,
            )
            for vid, info in items:
                mark = "☑" if vid in self.checked else "☐"
                tree.insert(
                    parent,
                    "end",
                    text=f"{mark} {vid}    {info['releaseTime'][:10]}",
                    values=(vid,),
                    tags=("item",),
                )

        self.update_count()

    def on_tree_press(self, event, tree, source):
        item = tree.identify_row(event.y)
        if not item:
            return
        tags = tree.item(item, "tags")
        values = tree.item(item, "values")
        if not values:
            return
        if "group" in tags:
            is_open = bool(tree.item(item, "open"))
            tree.item(item, open=not is_open)
            t = values[0]
            if is_open:
                self.collapsed[source].add(t)
            else:
                self.collapsed[source].discard(t)
            return "break"
        if "item" in tags:
            vid = values[0]
            if vid in self.checked:
                self.checked.discard(vid)
            else:
                self.checked.add(vid)
            self.update_tree_marks("mojang")
            self.update_tree_marks("bmclapi")
            self.update_count()
            return "break"

    def update_tree_marks(self, source):
        """刷新指定树中所有条目的勾选显示（与全局 checked 同步）。"""
        tree = self.trees[source]
        for parent in tree.get_children():
            for item in tree.get_children(parent):
                tags = tree.item(item, "tags")
                if "item" not in tags:
                    continue
                values = tree.item(item, "values")
                if not values:
                    continue
                vid = values[0]
                mark = "☑" if vid in self.checked else "☐"
                text = tree.item(item, "text")
                if text and text[0] in "☐☑":
                    tree.item(item, text=mark + text[1:])

    def select_all(self, source, select):
        """对当前面板中可见的版本全选 / 全不选，并同步到全局状态。"""
        tree = self.trees[source]
        vids = []
        for parent in tree.get_children():
            for item in tree.get_children(parent):
                tags = tree.item(item, "tags")
                if "item" not in tags:
                    continue
                values = tree.item(item, "values")
                if values:
                    vids.append(values[0])
        for vid in vids:
            if select:
                self.checked.add(vid)
            else:
                self.checked.discard(vid)
        self.update_tree_marks("mojang")
        self.update_tree_marks("bmclapi")
        self.update_count()

    def update_count(self):
        n = len(self.checked)
        for source in self.count_labels:
            self.count_labels[source].config(text=f"已选：{n}")

    def on_search_change(self, *args):
        if self._search_after:
            try:
                self.root.after_cancel(self._search_after)
            except Exception:
                pass
        self._search_after = self.root.after(200, self.apply_search)

    def apply_search(self):
        self._search_after = None
        self.rebuild_tree("mojang")
        self.rebuild_tree("bmclapi")

    # ---------- 路径与文件树 ----------

    def choose_dir(self):
        d = filedialog.askdirectory(initialdir=self.out_dir_var.get() or os.getcwd())
        if d:
            self.out_dir_var.set(d)
            self.refresh_filetree()

    def refresh_filetree(self):
        self.filetree.delete(*self.filetree.get_children())
        path = self.out_dir_var.get().strip()
        if not path or not os.path.isdir(path):
            return
        self._add_dir_items(self.filetree, "", path, depth=0)

    def _add_dir_items(self, tree, parent, path, depth):
        if depth > 3:
            return
        try:
            entries = sorted(
                os.listdir(path),
                key=lambda n: (not os.path.isdir(os.path.join(path, n)), n.lower()),
            )
        except OSError:
            return
        for name in entries:
            full = os.path.join(path, name)
            is_dir = os.path.isdir(full)
            try:
                size = human(os.path.getsize(full)) if os.path.isfile(full) else ""
            except OSError:
                size = ""
            node = tree.insert(parent, "end", text=name, values=(size,))
            if is_dir:
                self._add_dir_items(tree, node, full, depth + 1)

    # ---------- 下载 ----------

    def start_download(self):
        if self.download_thread and self.download_thread.is_alive():
            messagebox.showinfo("提示", "当前已有下载任务进行中")
            return

        out = self.out_dir_var.get().strip()
        if not out:
            messagebox.showerror("错误", "请先选择下载路径")
            return
        try:
            os.makedirs(out, exist_ok=True)
        except OSError as e:
            messagebox.showerror("错误", f"无法创建下载目录：\n{e}")
            return

        try:
            max_workers = int(self.concurrency_var.get())
        except ValueError:
            max_workers = 4
        max_workers = max(1, min(8, max_workers))

        vids = [vid for vid in self.checked if vid in self.versions]
        if not vids:
            messagebox.showinfo("提示", "请先在左右列表中勾选要下载的版本")
            return

        tasks = [(vid, self.versions[vid]) for vid in sorted(vids)]
        priority = self.priority_source
        self.begin_download(tasks, max_workers, priority)

    def begin_download(self, tasks, max_workers, priority):
        for w in self.progress_inner.winfo_children():
            w.destroy()
        self.task_widgets = {}
        self.task_got = {vid: 0 for vid, _ in tasks}
        self.total_size = 0
        self.total_progress_var.set(0.0)
        self.total_label.config(text="0%")

        for vid, info in tasks:
            frame = ttk.Frame(self.progress_inner)
            frame.pack(fill="x", pady=2, padx=2)
            ttk.Label(frame, text=vid, width=28, anchor="w").pack(side="left")
            src_label = ttk.Label(frame, text="?", width=4)
            src_label.pack(side="left")
            pbar = ttk.Progressbar(frame, maximum=100)
            pbar.pack(side="left", fill="x", expand=True, padx=4)
            label = ttk.Label(frame, text="等待解析", width=36, anchor="w")
            label.pack(side="left")
            self.task_widgets[vid] = {
                "pbar": pbar,
                "label": label,
                "src_label": src_label,
            }

        self.download_btn.config(state="disabled")
        self.concurrency_spin.config(state="disabled")
        self.priority_btn.config(state="disabled")
        self.download_thread = threading.Thread(
            target=self.download_worker,
            args=(tasks, max_workers, priority),
            daemon=True,
        )
        self.download_thread.start()

    def download_worker(self, tasks, max_workers, priority_source):
        fallback_source = "bmclapi" if priority_source == "mojang" else "mojang"
        max_workers = max(1, min(max_workers, len(tasks)))

        def prepare(task):
            vid, info = task
            sources_to_try = []
            if priority_source in info["sources"]:
                sources_to_try.append(priority_source)
            if fallback_source in info["sources"]:
                sources_to_try.append(fallback_source)

            candidates = []
            last_err = None
            for src in sources_to_try:
                try:
                    vj, actual = get_version_json(vid, src, self.versions)
                    dl = (vj.get("downloads") or {}).get("client")
                    if not dl or not dl.get("url"):
                        last_err = f"{SOURCE_NAMES[src]} 无 client.jar"
                        continue
                    candidates.append(
                        {
                            "source": actual,
                            "url": resolve_url(dl["url"], actual),
                            "size": dl.get("size") or 0,
                            "sha1": dl.get("sha1"),
                        }
                    )
                except Exception as e:  # noqa: BLE001
                    last_err = f"{SOURCE_NAMES[src]}: {e}"

            if not candidates:
                return {"vid": vid, "error": last_err or "无可用源"}
            return {
                "vid": vid,
                "path": os.path.join(self.out_dir_var.get(), f"{vid}.jar"),
                "candidates": candidates,
            }

        parsed = []
        with cf.ThreadPoolExecutor(max_workers=max_workers) as pool:
            for result in pool.map(prepare, tasks):
                if "error" in result:
                    self.queue.put(
                        ("task_done", result["vid"], False, result["error"])
                    )
                else:
                    parsed.append(result)
                    self.queue.put(
                        (
                            "task_source",
                            result["vid"],
                            result["candidates"][0]["source"],
                        )
                    )

        total_size = sum(p["candidates"][0]["size"] for p in parsed)
        self.queue.put(("total_size", total_size))

        if parsed:
            with cf.ThreadPoolExecutor(max_workers=max_workers) as pool:
                list(pool.map(self.download_one_task, parsed))

        self.queue.put(("all_done",))

    def download_one_task(self, task):
        vid = task["vid"]
        path = task["path"]
        candidates = task["candidates"]

        first = candidates[0]
        if os.path.exists(path) and first["sha1"]:
            try:
                h = hashlib.sha1()
                with open(path, "rb") as f:
                    for chunk in iter(lambda: f.read(1 << 20), b""):
                        h.update(chunk)
                if h.hexdigest().lower() == first["sha1"].lower():
                    file_size = os.path.getsize(path)
                    self.queue.put(("progress", vid, file_size, file_size, 0.0))
                    self.queue.put(("task_done", vid, True, "已存在"))
                    return
            except OSError:
                pass

        last_err = None
        for i, cand in enumerate(candidates):
            try:
                if i > 0:
                    self.queue.put(("task_source", vid, cand["source"]))
                    self.queue.put(
                        ("progress_msg", vid, f"回退到 {SOURCE_NAMES[cand['source']]}...")
                    )
                else:
                    self.queue.put(("progress", vid, 0, cand["size"], 0.0))
                self._download_url(cand["url"], path, cand["size"], cand["sha1"], vid)
                self.queue.put(("task_done", vid, True, "完成"))
                return
            except Exception as e:  # noqa: BLE001
                last_err = e
        self.queue.put(("task_done", vid, False, str(last_err)))

    def _download_url(self, url, path, size, sha1, vid):
        os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
        tmp = path + ".part"
        req = urllib.request.Request(safe_url(url), headers={"User-Agent": UA})
        digest = hashlib.sha1()
        got = 0
        total = size or 0
        t_start = time.time()
        last_report = 0.0
        samples = collections.deque(maxlen=32)
        samples.append((t_start, 0))

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
                    samples.append((now, got))
                    while len(samples) > 2 and now - samples[0][0] > 1.0:
                        samples.popleft()
                    if now - last_report >= 0.15:
                        last_report = now
                        speed = 0.0
                        if len(samples) >= 2:
                            dt = samples[-1][0] - samples[0][0]
                            db = samples[-1][1] - samples[0][1]
                            if dt > 0.05 and db >= 0:
                                speed = db / dt
                        if speed <= 0:
                            speed = got / max(now - t_start, 0.001)
                        self.queue.put(("progress", vid, got, total, speed))

            if sha1 and digest.hexdigest().lower() != sha1.lower():
                raise RuntimeError("SHA1 校验失败")
            os.replace(tmp, path)
            final_size = got
            self.queue.put(("progress", vid, final_size, total or final_size, 0.0))
        except BaseException:
            try:
                if os.path.exists(tmp):
                    os.remove(tmp)
            except OSError:
                pass
            raise

    # ---------- 消息处理 ----------

    def poll_queue(self):
        try:
            while True:
                msg = self.queue.get_nowait()
                self.handle_message(msg)
        except queue.Empty:
            pass
        self.root.after(60, self.poll_queue)

    def update_total_progress(self):
        done = sum(self.task_got.values())
        total = self.total_size
        if total:
            pct = min(done * 100.0 / total, 100.0)
            self.total_progress_var.set(pct)
            self.total_label.config(text=f"{pct:5.1f}%  {human(done)}/{human(total)}")
        else:
            self.total_label.config(text=human(done))

    def handle_message(self, msg):
        kind = msg[0]
        if kind == "manifests_loaded":
            self.on_manifests_loaded(msg[1])
        elif kind == "task_source":
            _, vid, source = msg
            w = self.task_widgets.get(vid)
            if w:
                w["src_label"].config(
                    text="M" if source == "mojang" else "B"
                )
        elif kind == "progress":
            _, vid, got, total, speed = msg
            self.task_got[vid] = got
            w = self.task_widgets.get(vid)
            if w:
                if total:
                    pct = min(got * 100.0 / total, 100.0)
                    w["pbar"]["value"] = pct
                    w["label"].config(
                        text=f"{human(got)}/{human(total)}  {human(speed)}/s"
                    )
                else:
                    w["label"].config(text=f"{human(got)}  {human(speed)}/s")
            self.update_total_progress()
        elif kind == "progress_msg":
            _, vid, text = msg
            w = self.task_widgets.get(vid)
            if w:
                w["label"].config(text=text)
        elif kind == "task_done":
            _, vid, success, text = msg
            w = self.task_widgets.get(vid)
            if w:
                if success:
                    w["pbar"]["value"] = 100
                    w["label"].config(text="完成")
                else:
                    w["label"].config(text=f"失败：{text[:40]}")
        elif kind == "total_size":
            self.total_size = msg[1]
            self.update_total_progress()
        elif kind == "all_done":
            self.download_btn.config(state="normal")
            self.concurrency_spin.config(state="normal")
            self.priority_btn.config(state="normal")
            self.refresh_filetree()
            if self.total_size:
                done = sum(self.task_got.values())
                pct = min(done * 100.0 / self.total_size, 100.0)
                self.total_progress_var.set(pct)
            else:
                self.total_progress_var.set(100.0)
            messagebox.showinfo("完成", "所有下载任务已结束")

    # ---------- 退出 ----------

    def on_close(self):
        if self.download_thread and self.download_thread.is_alive():
            if not messagebox.askyesno("确认", "仍有下载任务进行中，确定退出吗？"):
                return
        self.root.quit()
        self.root.destroy()


def main():
    root = tk.Tk()
    try:
        style = ttk.Style()
        if "vista" in style.theme_names():
            style.theme_use("vista")
        elif "clam" in style.theme_names():
            style.theme_use("clam")
        style.configure("Treeview", rowheight=22)
    except Exception:
        pass
    MCDownloaderApp(root)
    root.mainloop()


if __name__ == "__main__":
    main()