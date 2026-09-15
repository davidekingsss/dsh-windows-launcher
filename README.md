# DSH Windows Launcher

一个把 **DeepSeek Harness** 在 Windows 上变成"桌面应用"的静默启动器：
没有终端窗口、可固定到任务栏、开机自启、托盘托管服务。

代码量很小（两个 C# 文件，零第三方依赖），构建只用 Windows 自带的 `csc.exe`。

```
DSH.exe         静默启动器（WinExe，无控制台子系统）
make-icon.exe   从 DSH 自带的 favicon.svg 生成多分辨率 .ico
```

## 它解决什么问题

`npx @deepseek-ai/dsh web` 在 Windows 上直接跑有两个难受的地方：

1. **黑框**：`.bat` / `.cmd` 由 conhost 承载，双击必然闪一个命令提示符窗口，而且任务栏上
   只显示成"命令提示符"，没法作为一个独立应用固定。
2. **每次都要拿 token**：`dsh web` 的 index 路由有 browser-trust 栅栏，不带 token 访问
   `http://127.0.0.1:3080/` 会返回 **401**。你要么每次从终端里翻那一行带 token 的地址，
   要么手动拼。

本启动器把这两件事都吃掉：它是 **PE Subsystem = 2 的 GUI 程序**，系统层面就不存在可以
显示的控制台；同时它自己读服务器输出、提取带 token 的地址、按需为你打开浏览器。

## 快速开始

```powershell
git clone https://github.com/<you>/dsh-windows-launcher.git
cd dsh-windows-launcher
.\build.ps1 -Install
```

`-Install` 会装到 `%LOCALAPPDATA%\DSH\`，并在开始菜单和桌面各建一个快捷方式。
**固定到任务栏**：开始菜单 → `DSH` → 右键 → 更多 → 固定到任务栏。

## 用法

```
DSH.exe              只托管服务（默认）：静默拉起服务器 + 托盘图标，不碰浏览器
DSH.exe --open       额外用默认浏览器打开界面
DSH.exe --app        额外用 Edge/Chrome 的无边框应用窗口打开
DSH.exe --stop       让常驻实例停止服务器后退出
DSH.exe --restart    重启服务器
DSH.exe --kill-orphan  清理占着 3080 的孤儿服务器
DSH.exe --selftest   测 UI 线程健康度（最坏停顿毫秒数）
```

托盘菜单：Open DSH / Open as app window / Copy open link / Restart / Stop /
Clear orphaned server / **Start with Windows** / 三个日志入口 / Quit。

## 关于 token —— 这是设计使然，不是 bug

官方设计说明
[Browser launch-token authentication](https://github.com/deepseek-ai/deepseek-harness/blob/master/.agents/notes/implemented/architecture/2026-08-24-browser-token-authentication.md)
写得很明确：

> Each Host process generates a random launch token... only `GET /?token=...` exchanges
> the process token for a cookie, then redirects to clean `/`... **Missing and invalid
> credentials receive one minimal 401 response.**

> The launch token itself is never persisted and changes on every process start, while
> **an unexpired cookie remains valid across restarts** on the same authority.

并且"备选方案"章节**明确否决**了免鉴权路线（"Keep a method-specific privileged list" 与
"Persist or accept the launch token as an API bearer" 都被拒）。代码层面也可以穷尽验证：

- 整个安装里**只有一处产生 401**：`dsh-client-connection` 的 `BrowserAuth.writeUnauthorized`。
- connection 插件的 Config schema 只有 `recovery` / `trustedHosts` / `cookieMaxAgeDays`(默认 30) /
  `maxRequestBodyBytes` 四项，**没有关闭鉴权的开关**。
- 全部 `DSH_*` 环境变量里**没有一个与鉴权相关**。
- `--trusted-host` **不能**绕开它：那个只喂给 `isTrustedApiRequest`（失败返回 **403**），
  与产生 401 的 `authorizeIndex` 是两套。

所以 macOS 上"一个脚本就无 token 启动"的真相是：**脚本替你完成了 token 交换**，例如：

```bash
npx @deepseek-ai/dsh web --no-open > "$LOG_FILE" 2>&1 &
URL=$(grep -oE "http://127\.0\.0\.1:3080/\?token=[a-zA-Z0-9_-]+" "$LOG_FILE" | head -n 1)
open -a "Google Chrome" "$URL"     # 用带 token 的地址打开
```

本启动器做的就是同一件事（`grep` 换成 `PumpLog` + 正则，`open -a` 换成 ShellExecute）：

- 服务器的输出由 `start-dsh.cmd` 重定向到 `dsh-web.log`，启动器从中读回带 token 的地址。
- 打开前会**校验 token 是否仍有效**（有效 → 303；过期 → 401），因为 launch token
  **每个进程都会变**。缓存失效时会从日志里取当前服务器的那一个。
- 用带 token 的地址打开一次后，浏览器就拿到了**签名 cookie**（默认 30 天），此后你直接访问
  干净的 `http://127.0.0.1:3080/` 或收藏它，都不再需要 token。

想减少重复引导，可以在 profile 里把 cookie 寿命拉长（`~/.dsh/profiles/web/cordis.patch.yml`）：

```yaml
- id: connection
  config:
    cookieMaxAgeDays: 3650
```

## 两条硬约束（改代码前务必读）

**1. 必须用 `Application.Run` 泵消息，不能自己写消息循环。**
托盘右键菜单由 Win32 `TrackPopupMenu` 跟踪，它有自己的模态消息循环。手写的
`while (...) { Application.DoEvents(); }` 满足不了它：菜单能画出来，但鼠标跟踪永远走不完，
于是**菜单 hover 时一直转圈、关不掉也点不了**。

**2. 菜单回调里不能有任何阻塞调用。**
所有 I/O（探测、等 npx、开浏览器、杀进程）都在单独的工作线程上，菜单项只把任务塞进队列
就返回。曾经的写法把 HTTP 探测直接挂在菜单项上，服务器不在时阻塞约 **1.5 秒**，同样卡死菜单。

`DSH.exe --selftest` 就是用来验证第 1 条的：它跑 5 秒心跳并报告最坏停顿，健康值在 100 ms
以内（实测 77 ms）。消息泵一旦被阻塞，这个数字会变成数千毫秒。

## 排掉的历史问题（供参考，勿回退）

| 现象 | 根因 | 修法 |
|---|---|---|
| 右键菜单 hover 转圈、点不动 | 自写消息循环无法满足 `TrackPopupMenu` | `Application.Run` + 隐藏窗体 + 定时器 |
| 菜单卡死 | 菜单回调里同步 HTTP 探测阻塞 UI 线程 | 全部 I/O 移到工作线程 |
| 打开的地址报 401 | 用了不带 token 的地址 | 优先使用从服务器日志读回的带 token 地址 |
| 抓不到 URL | 正则 `\S+` 被 npx 的长 `npm warn exec` 横幅误捕 | 行内匹配且不锚定行首 |
| token 失效 | launch token 每进程轮换，缓存值会过期 | 打开前校验，失效则从日志取当前值 |
| `EADDRINUSE` | 启动器被强杀后 `cmd → npx → node` 成为孤儿仍占端口 | `taskkill /T` 杀整棵树 + `--kill-orphan` |
| 进程被安全中心截停 | `UseShellExecute=false` + `CreateNoWindow` + `WindowStyle.Hidden` + 重定向 stdio 是隐蔽启动子进程的特征 | 改为由 `.cmd` 文件自己承载重定向 |

## 工作原理

```
DSH.exe (WinExe, 无控制台)
├─ UI 线程      Application.Run 消息泵 + NotifyIcon 托盘 + 150ms 定时器
├─ 工作线程     探测服务器 / 启动 npx / 等待就绪 / 开浏览器 / 杀进程
└─ 单实例       Local\DSH.Launcher.Instance 互斥体 + 命名事件唤醒 + 文件传参
                  ↓
             start-dsh.cmd
                cd /d %USERPROFILE%
                npx --yes @deepseek-ai/dsh web --no-open > dsh-web.log 2>&1
                  ↓
             node (dsh web) 监听 127.0.0.1:3080
```

- **每次启动都拉最新版**：命令就是 `npx @deepseek-ai/dsh web`，不做版本固定。
- **就绪判定**：带 token 的 index 返回 303、不带 token 返回 401——**任何 HTTP 应答都算"活着"**，
  只有连接被拒才算"还没起来"。
- **端口持有者检测**：`GetExtendedTcpTable` 查出 3080 归哪个 PID，用来识别孤儿服务器
  （`--kill-orphan` 只在持有者确实是 `node.exe`/`cmd.exe` 时才动手）。
- **退出即停止**：启动器与服务器生命周期绑定；关掉托盘图标会 `taskkill /T /F` 杀整棵树，
  不会留下占端口的孤儿。

## 文件位置

| 项 | 路径 |
|---|---|
| 程序 / 图标 | `%LOCALAPPDATA%\DSH\DSH.exe`、`DSH.ico` |
| 服务器日志 | `%LOCALAPPDATA%\DSH\dsh-web.log` |
| 启动器日志 | `%LOCALAPPDATA%\DSH\launcher.log` |
| 状态 | `%LOCALAPPDATA%\DSH\state.txt` |
| 开机自启 | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 的 `DSH` 值 |

## 构建细节

图标由 `assets/favicon.svg`（取自 `@deepseek-ai/dsh-web-frontend`）**直接解析 SVG 路径矢量**
生成，不依赖浏览器或图像库：那条鲸鱼路径的眼睛和嘴巴是同一路径按 nonzero 规则挖空的，
所以不能简单放在透明底上——成品是 DeepSeek 蓝 `#4D6BFE` 圆角底 + 白色鲸鱼。

## 许可证

MIT
