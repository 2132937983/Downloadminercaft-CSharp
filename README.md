# MC 原版下载器

一个 Minecraft 原版 `client.jar` 下载工具，同时支持 **Mojang 官方源** 与 **BMCLAPI 国内镜像**，提供命令行（shell）与图形界面（GUI）两种形态。

> **编写说明**
>
> - **代码（三个脚本）由 AI（DeepSeek）编写**。需求梳理、功能迭代与脚本实现均在与 AI 的对话中完成，仓库维护者负责整理与发布。
> - **本 README 由 AI（DeepSeek）编写**。

## 功能特性

- 🔀 **双源扫描**：启动时并发拉取 Mojang 与 BMCLAPI 的版本清单，合并展示
- 📦 **完整版本**：包含 `release`、`snapshot`、`old_alpha`、`old_beta`，以及全部愚人节版本
- 🚀 **智能回退**：下载优先走指定源，失败自动回退到另一源；可选“优先源”切换
- ⚡ **并发下载**：多版本同时下载，每个文件独立进度条 + 实时速度 + 总进度条
- ✅ **完整性校验**：下载完成后按版本 JSON 中的 SHA1 与文件大小校验
- 🗂 **文件树预览**：GUI 版内置下载目录文件树，实时查看落盘结果
- 🔍 **搜索过滤**：GUI 版顶部搜索框实时过滤版本列表
- ☑️ **全局勾选**：GUI 版左右两个源列表勾选状态同步

## 三个版本

仓库包含三个独立脚本，功能逐级增强，按需选择：

| 文件 | 形态 | 依赖 | 适用场景 |
| --- | --- | --- | --- |
| `MC原版下载-shell.py` | 命令行交互 | Python 标准库 | 服务器、脚本自动化、远程终端 |
| `MC原版下载-GUI.py` | 图形界面 | Python 标准库（tkinter） | 桌面使用，零额外依赖 |
| `MC原版下载-ttkbootstrap.py` | 图形界面（美化版） | Python 标准库 + `ttkbootstrap` | 桌面使用，追求观感 |

## 环境要求

- **Python**：3.8 及以上（推荐 3.10+）
- **操作系统**：Windows / macOS / Linux 均可运行
  - GUI 版依赖 `tkinter`，Windows/macOS 官方安装包自带；部分 Linux 发行版需额外安装：
    ```bash
    # Debian / Ubuntu
    sudo apt install python3-tk
    # Fedora
    sudo dnf install python3-tkinter
    # Arch
    sudo pacman -S tk
    ```

### 安装依赖

- **`MC原版下载-shell.py`**：无需安装任何依赖
- **`MC原版下载-GUI.py`**：无需安装任何依赖（tkinter 已随 Python 内置）
- **`MC原版下载-ttkbootstrap.py`**：
  ```bash
  pip install ttkbootstrap
  ```

## 使用方法

### 1. 命令行版（shell）

```bash
python MC原版下载-shell.py
```

启动后会打印双源扫描统计，进入交互提示符 `mc>`：

```text
mc> list 10 -T=R,S -S=1.21
mc> get 0001,0002 B out-"C:\Users\qpolb\Downloads" N
mc> quit
```

#### 命令列表

| 命令 | 说明 |
| --- | --- |
| `list [数量] [-D=M/B] [-S=搜索] [-T=类型] [-a]` | 列出合并后的版本，带 `0001` 起始序号 |
| `april` | 单独列出已知愚人节版本 |
| `info <版本ID或序号>` | 查看版本在 Mojang / BMCLAPI 的可用情况 |
| `get <版本[,版本...]> [M/B] [out-"路径"] [Y/N]` | 下载 client.jar |
| `outdir [目录]` | 查看或设置默认下载目录 |
| `quit` / `Exit` | 退出 |

**`list` 参数说明**

- `-D=M` / `-D=B`：按下载源过滤
- `-S=关键字`：按版本 ID 搜索
- `-T=`：类型过滤，支持 `R`（release）、`S`（snapshot）、`a`（april）、`oa`（old_alpha）、`ob`（old_beta），逗号分隔
- 数量省略默认 30，`all` 或 `-a` 显示全部

**`get` 参数说明**

- 版本可用 `list` 输出的序号（如 `0001`），多版本用半角 `,` 或全角 `，` 分隔
- `M/B`：指定下载源；省略时按优先级自动选择
- `out-"路径"`：指定输出目录；省略时使用系统下载文件夹（Windows 读取注册表，其他平台读 `~/Downloads`）
- `Y/N`：下载完成后是否退出，默认 `N`（不退出）

### 2. GUI 版

```bash
# 基础版（仅标准库）
python MC原版下载-GUI.py

# 美化版（需先 pip install ttkbootstrap）
python MC原版下载-ttkbootstrap.py
```

**界面说明（从上到下）**

1. **搜索框**：输入关键词实时过滤版本
2. **双源列表**：左 Mojang，右 BMCLAPI，按类型折叠显示，勾选状态同步，各带“全选 / 全不选”按钮
3. **下载路径 + 并发数 + 开始下载**：点击路径框或“浏览”按钮选择目录；并发数 1–8
4. **优先源切换按钮**：在“优先 Mojang → 回退 BMCLAPI”与“优先 BMCLAPI → 回退 Mojang”间切换
5. **下载目录文件树**：显示输出目录下最多 3 层文件与子目录
6. **单版本进度条**：每个选中版本独立显示实时速度与进度
7. **总进度条**：汇总全部任务的字节数与百分比
8. **退出按钮**：下载进行中退出会二次确认

### 3. 美化主题切换

`MC原版下载-ttkbootstrap.py` 通过 ttkbootstrap 提供多个主题，只需修改一行：

```python
root = tb.Window(themename="cosmo")  # 改这里
```

- **浅色系**：`cosmo`、`flatly`、`minty`、`pulse`、`lumen`、`yeti`、`morph`、`journal`、`litera`、`sandstone`、`united`
- **深色系**：`darkly`、`cyborg`、`superhero`、`solar`、`vapor`

## 关于下载源

本项目仅使用以下两个公开源：

- **Mojang 官方**：`piston-meta.mojang.com` 与 `launcher.mojang.com`
- **BMCLAPI 镜像**：`bmclapi2.bangbang93.com`（由 [BangBang93](https://github.com/bangbang93) 维护）

选择 BMCLAPI 时，脚本会自动将以下官方域名替换为 BMCLAPI 域名：

- `launchermeta.mojang.com`
- `launcher.mojang.com`
- `piston-meta.mojang.com`
- `piston-data.mojang.com`

> 镜像仅代理文件分发，不改变文件内容与哈希，下载后仍按官方 SHA1 校验。

## Mojang EULA（最终用户许可协议）

下载或使用 Minecraft `client.jar` 即表示你同意 Mojang 的最终用户许可协议。官方入口：

- **EULA 主页面（英文）**：https://www.minecraft.net/en-us/eula
- **EULA 主页面（简体中文）**：https://www.minecraft.net/zh-hans/eula
- **快捷短链**：https://aka.ms/MinecraftEULA
- **旧版 Mojang 账户条款存档**：https://account.mojang.com/terms

请在使用本工具前阅读并遵守 EULA 条款，尤其是关于**分发、商业用途和修改客户端**的部分。

## 目录结构

```text
.
├── MC原版下载-shell.py        # 命令行版
├── MC原版下载-GUI.py          # 基础 GUI 版（仅标准库）
├── MC原版下载-ttkbootstrap.py # 美化 GUI 版（需 ttkbootstrap）
└── README.md
```

## 常见问题

**Q：Windows 上运行 GUI 版会弹黑框？**  
可以保存为 `.pyw` 后缀，或使用 `pythonw MC原版下载-ttkbootstrap.py` 启动。

**Q：脚本会不会修改游戏安装或启动器配置？**  
不会。脚本只下载 `client.jar` 到指定目录，不读写 `.minecraft` 或任何启动器数据。

**Q：下载校验失败怎么办？**  
先确认磁盘空间充足；如持续失败，可切换优先源后重试。脚本会自动删除失败的 `.part` 临时文件。

**Q：能否只用离线环境？**  
不能。需要访问 Mojang 或 BMCLAPI 获取版本清单与 JAR 文件。

**Q：文件名带中文会影响使用吗？**  
不影响运行，但命令行引用时建议加引号，例如 `python "MC原版下载-shell.py"`。

## 致谢

- [BMCLAPI](https://bmclapidoc.bangbang93.com/) 提供国内镜像加速
- [ttkbootstrap](https://github.com/israel-dryer/ttkbootstrap) 提供现代化的 ttk 主题

## License

MIT