# MC 原版下载器 · MC Vanilla Downloader

[中文](#中文) · [English](#english)

---

## 中文

一个 Minecraft 原版 `client.jar` 下载工具，同时支持 **Mojang 官方源** 与 **BMCLAPI 国内镜像**，提供图形界面（WPF）与命令行两种形态。只依赖 Windows 10 / 11 自带的 .NET Framework，无需安装任何运行库，也无需 .NET SDK 或 Visual Studio 即可编译。

> **编写说明**：本项目最初是三个 Python 脚本（由 AI DeepSeek 编写），现已由 AI（Claude）移植为 C#，并参考 Minecraft 启动器 LauncherX 重新设计了图形界面。本 README 由 AI 编写。

### 功能特性

- 🔀 **双源扫描**：启动时并发拉取 Mojang 与 BMCLAPI 的版本清单并合并
- 📦 **完整版本**：`release`、`snapshot`、`old_alpha`、`old_beta` 以及愚人节版本
- 🚀 **智能回退**：优先使用指定源，解析或下载失败时自动回退到另一源
- ⚡ **并发下载**：1–8 个任务并行，每个任务显示来源、进度与实时速度
- ✅ **完整性校验**：按版本 JSON 中的 SHA1 与文件大小校验；已存在且一致的文件自动跳过

**图形界面（WPF）**

- 🧭 **侧边栏导航**：首页 / 版本列表 / 下载任务 / 下载目录 / 设置，切换时带过渡动画
- 🃏 **卡片式布局**：自绘标题栏、圆角内容层、扁平控件，图标使用系统自带的 Segoe Fluent / MDL2 图标字体
- 🏠 **首页**：最新正式版一键下载；两个版本源的在线状态、版本数与响应耗时；各类型版本数量（点击直达筛选结果）
- 🔍 **版本列表**：按版本号搜索，按类型与可用源筛选；点击整行勾选，支持单个下载与批量下载；近千个版本也能流畅滚动（虚拟化列表）
- 📊 **下载任务**：总进度、总速度与完成数量，进度同步显示在任务栏图标上
- 🗂 **下载目录**：以文件树查看下载目录（最多 4 层），可一键在资源管理器中打开
- 🎨 **主题**：浅色 / 深色 / 跟随系统，6 种主题色；设置自动保存
- 🔔 **应用内通知与对话框**：操作结果以通知提示，下载中关闭窗口会先确认

### 程序

| 文件 | 形态 | 适用场景 |
| --- | --- | --- |
| `bin\MCDownloader.exe` | 图形界面（WPF） | 桌面使用 |
| `bin\MCDownloader.Shell.exe` | 命令行交互 | 远程终端、脚本自动化 |

### 环境要求

- Windows 10 / 11（自带 .NET Framework 4.6 及以上，满足要求）
- 图形界面的图标来自 Windows 10 / 11 自带的图标字体，不支持更早的 Windows

### 编译

在项目根目录运行 `build.bat`（双击亦可）：

| 命令 | 作用 |
| --- | --- |
| `build.bat` | 编译全部（等同于 `build.bat all`） |
| `build.bat gui` | 只编译图形界面 `bin\MCDownloader.exe` |
| `build.bat shell` | 只编译命令行版 `bin\MCDownloader.Shell.exe` |
| `build.bat clean` | 删除整个 `bin\` 目录 |

脚本会：

1. 查找系统自带的编译器 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`（找不到时改用 32 位目录），并确认 WPF 程序集存在；
2. 如果要替换的程序正在运行，提示先关闭并停止编译（在删除任何文件之前检查）；
3. 删除即将重新生成的程序，以及旧版本遗留的 `MCDownloader.Gui.exe`、`MCDownloader.Styled.exe`；
4. 编译并列出生成的文件。成功时退出码为 `0`，失败为 `1`，参数错误为 `2`。

开发说明：

- 源码限定为系统自带编译器支持的 **C# 5** 语法（不能使用 `$""` 字符串插值、`?.` 等）
- `src\App\*.xaml` 会自动以 `McDownloader.<文件名>` 的名称内嵌进程序，运行时由 `XamlReader` 加载，因此**不需要 MSBuild**；XAML 中不能写 `x:Class` 与事件处理器，事件在 `MainController.cs` 中按 `x:Name` 绑定
- 新增的 `src\App\*.cs` 文件会被自动编译，无需修改脚本
- 程序图标为 `assets\app.ico`

### 使用方法

#### 图形界面

运行 `bin\MCDownloader.exe`：

1. 在 **版本列表** 中搜索或筛选，点击整行勾选版本；
2. 点击右上角 **下载所选**，自动跳转到 **下载任务** 查看进度；
3. 下载完成后可在 **下载目录** 中查看文件。

在 **设置** 中可修改下载目录、同时下载数量（1–8）、优先下载源、主题模式与主题色。设置保存在 `%APPDATA%\MCDownloader\settings.json`。

#### 命令行

```bat
bin\MCDownloader.Shell.exe
```

启动后会打印双源扫描统计，进入交互提示符 `mc>`：

```text
mc> list 10 -T=R,S -S=1.21
mc> get 0001,0002 B out-"C:\Users\me\Downloads" N
mc> quit
```

| 命令 | 说明 |
| --- | --- |
| `list [数量] [-D=M/B] [-S=搜索] [-T=类型] [-a]` | 列出合并后的版本，带 `0001` 起始序号 |
| `april` | 列出已知愚人节版本 |
| `info <版本ID或序号>` | 查看版本在 Mojang / BMCLAPI 的可用情况 |
| `get <版本[,版本...]> [M/B] [out-"路径"] [Y/N]` | 下载 client.jar |
| `outdir [目录]` | 查看或设置默认下载目录 |
| `help` / `quit` | 帮助 / 退出 |

- `list`：`-D=M` / `-D=B` 按源过滤；`-S=` 按版本 ID 搜索；`-T=` 按类型过滤，支持 `R`、`S`、`a`（愚人节）、`oa`、`ob`，逗号分隔；数量默认 30，`all` 或 `-a` 显示全部
- `get`：版本可用 `list` 的序号，多个版本用 `,` 或 `，` 分隔；`M/B` 指定源（省略时优先 BMCLAPI）；`out-"路径"` 指定目录（省略时使用系统下载文件夹）；`Y` 表示下载后退出
- 下载中按 `Ctrl+C` 取消当前操作；在提示符下按 `Ctrl+C` 退出

### 项目结构

```text
.
├── build.bat                 # 编译脚本（系统自带 csc.exe）
├── assets/
│   └── app.ico               # 程序图标
├── src/
│   ├── Common/               # 两个程序共用
│   │   ├── McCore.cs         #   清单拉取与合并、HTTP、带 SHA1 / 大小校验的下载
│   │   └── GuiCore.cs        #   版本目录、并发批量下载引擎（含回退）
│   ├── App/                  # 图形界面（WPF）
│   │   ├── AppMain.cs        #   入口：加载主题与界面 XAML
│   │   ├── MainWindow.xaml   #   窗口布局：标题栏、侧边栏、各页面、通知与对话框
│   │   ├── Theme.xaml        #   控件样式与模板（按钮、导航、标签、列表、滚动条等）
│   │   ├── MainController.cs #   界面逻辑：导航、筛选、下载调度、通知
│   │   ├── ViewModels.cs     #   数据绑定对象
│   │   ├── ThemeManager.cs   #   浅色 / 深色配色与主题色
│   │   ├── AppSettings.cs    #   设置读写
│   │   └── Native.cs         #   目录选择框、窗口外框等 Win32 互操作
│   └── Shell/
│       └── ShellProgram.cs   # 命令行版
├── bin/                      # 编译输出（已加入 .gitignore）
└── README.md
```

### 关于下载源

- **Mojang 官方**：`piston-meta.mojang.com` 与 `launcher.mojang.com`
- **BMCLAPI 镜像**：`bmclapi2.bangbang93.com`（由 [BangBang93](https://github.com/bangbang93) 维护）

使用 BMCLAPI 时，程序会把 `launchermeta.mojang.com`、`launcher.mojang.com`、`piston-meta.mojang.com`、`piston-data.mojang.com` 替换为 BMCLAPI 域名。镜像只代理文件分发，下载后仍按官方 SHA1 校验。

### Mojang EULA

下载或使用 Minecraft `client.jar` 即表示你同意 Mojang 的最终用户许可协议：<https://www.minecraft.net/zh-hans/eula>（快捷链接 <https://aka.ms/MinecraftEULA>）。请特别注意关于**分发、商业用途和修改客户端**的条款。

### 常见问题

**程序会修改游戏或启动器吗？** 不会。程序只把 `client.jar` 下载到指定目录，除自身设置文件外不读写其他数据。

**校验失败怎么办？** 先确认磁盘空间充足，再在设置中切换优先下载源后重试。校验失败的 `.part` 临时文件会被自动删除。

**某个版本源显示「不可用」？** 多为网络波动，在首页点击「重新获取清单」重试；只要有一个源在线即可下载。

**编译时提示程序正在运行？** 关闭 `MCDownloader.exe` / `MCDownloader.Shell.exe` 后重新运行 `build.bat`。

### 致谢

- [BMCLAPI](https://bmclapidoc.bangbang93.com/) 提供国内镜像加速
- 界面设计参考了 Minecraft 启动器 LauncherX

### 许可

MIT

---

## English

A downloader for the vanilla Minecraft `client.jar` that works with both the **official Mojang source** and the **BMCLAPI mirror** (fast in mainland China). It comes as a WPF desktop app and a command-line tool. It needs only the .NET Framework that ships with Windows 10 / 11: nothing to install to run it, and no .NET SDK or Visual Studio to build it.

> **About this project**: it started as three Python scripts written by the AI DeepSeek. They were ported to C# by the AI Claude, and the GUI was redesigned with the Minecraft launcher LauncherX as a reference. This README was written by AI.

### Features

- 🔀 **Two sources**: fetches the Mojang and BMCLAPI version manifests in parallel and merges them
- 📦 **Every version**: `release`, `snapshot`, `old_alpha`, `old_beta` and the April Fools' versions
- 🚀 **Automatic fallback**: uses your preferred source and switches to the other one if parsing or downloading fails
- ⚡ **Parallel downloads**: 1–8 at a time, each showing its source, progress and live speed
- ✅ **Verification**: checks the SHA1 and size from the version JSON; files that already exist and match are skipped

**Desktop app (WPF)**

- 🧭 **Sidebar navigation**: Home / Versions / Downloads / Folder / Settings, with page transitions
- 🃏 **Card-based layout**: custom title bar, rounded content area and flat controls, with icons from the Segoe Fluent / MDL2 icon fonts built into Windows
- 🏠 **Home**: one-click download of the latest release; online status, version count and response time for each source; per-type version counts that open the filtered list
- 🔍 **Versions**: search by version ID, filter by type and source, click a row to select it, download one version or a batch; the virtualized list stays smooth with 900+ versions
- 📊 **Downloads**: overall progress, total speed and completed count, mirrored on the taskbar icon
- 🗂 **Folder**: tree view of the download folder (up to 4 levels) with an "open in Explorer" button
- 🎨 **Themes**: light / dark / follow system, 6 accent colors; settings are saved automatically
- 🔔 **In-app notifications and dialogs**: results appear as toasts, and closing the window during a download asks for confirmation

### Programs

| File | Type | Use it for |
| --- | --- | --- |
| `bin\MCDownloader.exe` | Desktop app (WPF) | Everyday use |
| `bin\MCDownloader.Shell.exe` | Interactive command line | Remote terminals, scripting |

### Requirements

- Windows 10 / 11 (includes .NET Framework 4.6 or later, which is enough)
- The desktop app's icons come from icon fonts that only ship with Windows 10 / 11, so older Windows versions are not supported

### Building

Run `build.bat` from the project root (double-clicking works too):

| Command | What it does |
| --- | --- |
| `build.bat` | Build everything (same as `build.bat all`) |
| `build.bat gui` | Build only the desktop app `bin\MCDownloader.exe` |
| `build.bat shell` | Build only the command-line tool `bin\MCDownloader.Shell.exe` |
| `build.bat clean` | Delete the whole `bin\` folder |

The script:

1. Finds the compiler that ships with Windows, `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (falling back to the 32-bit folder), and checks that the WPF assemblies are present;
2. Stops with a message if a program it would replace is running (checked before anything is deleted);
3. Deletes the programs it is about to rebuild, plus `MCDownloader.Gui.exe` and `MCDownloader.Styled.exe` left over from older versions;
4. Compiles and lists the output files. Exit code `0` means success, `1` failure, `2` an unknown argument.

Notes for developers:

- The source code is limited to **C# 5**, the version the built-in compiler supports (no `$""` interpolation, no `?.`, and so on)
- Every `src\App\*.xaml` file is embedded automatically as `McDownloader.<file name>` and loaded at runtime with `XamlReader`, so **MSBuild is not needed**. XAML files therefore cannot use `x:Class` or event handler attributes; events are wired up by `x:Name` in `MainController.cs`
- New `src\App\*.cs` files are compiled automatically; the script does not need to change
- The program icon is `assets\app.ico`

### Usage

#### Desktop app

Run `bin\MCDownloader.exe`:

1. On **Versions** (版本列表), search or filter, then click rows to select versions;
2. Click **Download selected** (下载所选) in the top-right corner; the app switches to **Downloads** (下载任务) to show progress;
3. Finished files appear on **Folder** (下载目录).

**Settings** (设置) has the download folder, number of parallel downloads (1–8), preferred source, theme mode and accent color. Settings are stored in `%APPDATA%\MCDownloader\settings.json`.

#### Command line

```bat
bin\MCDownloader.Shell.exe
```

It prints the manifest statistics for both sources and then shows the `mc>` prompt:

```text
mc> list 10 -T=R,S -S=1.21
mc> get 0001,0002 B out-"C:\Users\me\Downloads" N
mc> quit
```

| Command | Description |
| --- | --- |
| `list [count] [-D=M/B] [-S=search] [-T=types] [-a]` | List merged versions, numbered from `0001` |
| `april` | List the known April Fools' versions |
| `info <version ID or number>` | Show whether a version is available on Mojang / BMCLAPI |
| `get <version[,version...]> [M/B] [out-"path"] [Y/N]` | Download client.jar |
| `outdir [folder]` | Show or set the default download folder |
| `help` / `quit` | Help / exit |

- `list`: `-D=M` / `-D=B` filters by source; `-S=` searches version IDs; `-T=` filters by type (`R`, `S`, `a` for April Fools', `oa`, `ob`, comma-separated); shows 30 by default, `all` or `-a` shows everything
- `get`: versions can be numbers from `list`, separated by `,` or `，`; `M/B` picks the source (BMCLAPI first when omitted); `out-"path"` sets the folder (the system Downloads folder when omitted); `Y` exits after downloading
- `Ctrl+C` during a download cancels it; `Ctrl+C` at the prompt exits

### Project structure

```text
.
├── build.bat                 # Build script (uses the built-in csc.exe)
├── assets/
│   └── app.ico               # Program icon
├── src/
│   ├── Common/               # Shared by both programs
│   │   ├── McCore.cs         #   Manifest fetching and merging, HTTP, downloads with SHA1 / size checks
│   │   └── GuiCore.cs        #   Version catalog, parallel batch download engine with fallback
│   ├── App/                  # Desktop app (WPF)
│   │   ├── AppMain.cs        #   Entry point: loads the theme and window XAML
│   │   ├── MainWindow.xaml   #   Window layout: title bar, sidebar, pages, toast and dialog
│   │   ├── Theme.xaml        #   Control styles and templates (buttons, navigation, chips, lists, scrollbars...)
│   │   ├── MainController.cs #   UI logic: navigation, filtering, download orchestration, notifications
│   │   ├── ViewModels.cs     #   Data-binding objects
│   │   ├── ThemeManager.cs   #   Light / dark palettes and accent colors
│   │   ├── AppSettings.cs    #   Settings load / save
│   │   └── Native.cs         #   Win32 interop: folder picker, window frame
│   └── Shell/
│       └── ShellProgram.cs   # Command-line tool
├── bin/                      # Build output (in .gitignore)
└── README.md
```

### Download sources

- **Mojang (official)**: `piston-meta.mojang.com` and `launcher.mojang.com`
- **BMCLAPI mirror**: `bmclapi2.bangbang93.com` (maintained by [BangBang93](https://github.com/bangbang93))

When BMCLAPI is used, the program replaces `launchermeta.mojang.com`, `launcher.mojang.com`, `piston-meta.mojang.com` and `piston-data.mojang.com` with the BMCLAPI host. The mirror only serves the same files, so downloads are still checked against the official SHA1.

### Mojang EULA

Downloading or using the Minecraft `client.jar` means you accept Mojang's End User License Agreement: <https://www.minecraft.net/en-us/eula> (short link <https://aka.ms/MinecraftEULA>). Pay particular attention to the terms on **distribution, commercial use and modifying the client**.

### FAQ

**Does it change my game or launcher?** No. It only downloads `client.jar` to the folder you choose, and writes nothing except its own settings file.

**What if verification fails?** Make sure there is enough disk space, then switch the preferred source in Settings and try again. A `.part` file that fails verification is deleted automatically.

**A source shows "unavailable" (不可用)?** Usually a network hiccup. Click "Reload manifests" (重新获取清单) on Home to retry; one working source is enough to download.

**The build says a program is running?** Close `MCDownloader.exe` / `MCDownloader.Shell.exe` and run `build.bat` again.

### Acknowledgements

- [BMCLAPI](https://bmclapidoc.bangbang93.com/) for the mirror
- The UI design takes LauncherX, a Minecraft launcher, as a reference

### License

MIT
