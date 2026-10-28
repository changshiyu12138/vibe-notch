# Vibe Notch（Windows）

把 [vibe-notch](https://github.com/farouqaldori/vibe-notch) 的体验搬到 Windows：屏幕顶部居中的浮岛，实时显示 Qoder 会话状态，并可以直接在浮岛里允许 / 拒绝工具授权，不用切回 Qoder 窗口。

如果这个项目对你有用，欢迎在 GitHub 上点个 Star。

> 这是独立实现的 Windows 版本（.NET 8 + WPF），目前对接 Qoder；协议层按通用 Hook 设计，后续计划兼容 WorkBuddy、Trae。

## 功能

- 多会话实时状态：思考中、正在执行工具、等待授权、等待输入、完成、出错、压缩上下文、子智能体
- 工具执行摘要、会话列表、待授权队列、相对时间
- 会话信息行：git 分支 + 未提交标记、上下文占用、本轮任务进度、子智能体数、本轮耗时（模型名在行的悬停提示里）
- 折叠态胶囊上的 `ctx NN%` 徽标，占用 ≥60% 变黄、≥85% 变红
- 展开态页脚显示今日统计：完成几个任务、授权 / 拒绝 / 自动放行各几次；数据落在 `stats.json`，保留 14 天
- 浮岛内「允许 / 拒绝」，结果直接回传给 Qoder
- 授权面板带差异预览：文件路径、`- 旧内容` / `+ 新内容`、Bash 命令行，按类型着色
- 拒绝可附理由，理由会回传给 Qoder；「编辑后允许」可修改命令/内容后再放行（`updatedInput`）
- 「总是允许 <工具>」一键加入白名单，之后同类请求自动放行（见配置 `AutoApprove`）
- 浮岛内直接回答 `AskUserQuestion` 选择题（单选点选项即提交，多选勾选后按允许），答案通过 `updatedInput.answers` 回传
- `ExitPlanMode` 计划审批：浮岛显示计划全文，允许 / 拒绝直接回传
- 遥控会话：展开态页脚的「中断」向 Qoder 发送 Esc、「继续」输入「继续」并回车，全程 PostMessage 不抢焦点
- 点击会话行聚焦 Qoder 窗口（优先匹配项目名，跳过 Desktop Pet 窗口）
- 完成提示音、授权提示音：内置「清铃 / 水滴 / 琶音」三款合成音效，可在设置或托盘里随时切换并立即试听
- 完成 / 授权时弹 Windows 系统通知（右下角 Toast，与 Qoder 自带弹窗同款，可分别关闭，点击可聚焦 Qoder）
- 多屏：固定在某块屏幕，或跟随鼠标 / 跟随 Qoder 窗口自动换屏；设置里可直接选择固定到哪块显示器
- 浮岛内设置菜单：悬停后头部右上角出现 ☰，点开内容区换成设置列表（浮岛位置、显示器、提示音开关、提示音音效、系统通知、开机自启、Hooks、测试通知、退出）
- 托盘菜单：隐藏/显示浮岛、打开 Qoder、浮岛位置、提示音音效、开机自启、提示音与通知开关、重装/移除 Hooks、退出
- 安全回退：浮岛 90 秒未操作会自动交回 Qoder 原生授权弹窗

## 环境要求

- Windows 10 / 11 x64
- 运行需要 .NET 8 Desktop Runtime；从源码构建需要 .NET 8 SDK
- 已安装并登录过 Qoder（本工具会写入 `~/.qoder/settings.json`）

## 安装

```powershell
cd vibe-notch
powershell -ExecutionPolicy Bypass -File .\install.ps1

# 顺便开启开机自启
powershell -ExecutionPolicy Bypass -File .\install.ps1 -AutoStart
```

安装脚本会：

1. 发布程序到 `%LOCALAPPDATA%\VibeNotch\app`
2. 在开始菜单创建「Vibe Notch」快捷方式
3. 把 Hook 写入 `~/.qoder/settings.json`（写入前备份为 `settings.json.vibenech-时间戳.bak`）
4. 启动浮岛

> 如果 Qoder 正在运行，Hook 可能要等下一次会话才生效；重启 Qoder 最稳妥。安装脚本会先结束本应用，避免占用文件。

## 使用

- 浮岛默认折叠成顶部中央的细条；有任务活动时展开为胶囊，显示项目名和当前动作
- 折叠胶囊右侧的 `ctx NN%` 是当前会话的上下文占用；会话行第二行会追加 `分支* · ctx · 任务 d/t · 耗时`
- 鼠标悬停展开会话列表；点击胶囊可固定 / 取消固定
- 展开态头部右上角 ☰ 打开浮岛内设置；再点 ✕ 或「返回」回到会话列表。设置项与托盘菜单同源，改完立即生效并写入 `settings.json`
- 设置里的「显示器」点开后列出当前所有屏幕（分辨率、是否主屏），选中的那块会打勾，浮岛立即平移过去；「浮岛位置」里可切回跟随鼠标 / 跟随 Qoder
- 设置里的「提示音音效」点一下切到下一款（清铃 → 水滴 → 琶音），切换后立即试听；托盘菜单里也可以直接选
- 展开态页脚底部的「今天 · …」统计当天完成的任务数和授权次数
- 需要授权时浮岛自动变大：显示工具、命令或文件差异，点「允许 / 拒绝」即可回传 Qoder
- 授权面板里可以：填拒绝理由、「编辑后允许」改动命令或文件内容、「总是允许 <工具>」加白名单
- `AskUserQuestion` 弹出时浮岛显示选项列表：单选题直接点选项即作答；多选题勾选后点「允许」提交
- 悬停展开态右下角有「中断 / 继续」：向 Qoder 窗口后台投递按键（Esc / 「继续」+ 回车），不会抢走你当前窗口的焦点
- 点击会话行 → 聚焦 Qoder 窗口
- 点击右下角通知（完成 / 授权）→ 聚焦 Qoder 窗口
- 托盘图标右键 → 全部设置项（浮岛位置、通知开关、「测试通知」等）

## 配置

配置文件：`%LOCALAPPDATA%\VibeNotch\settings.json`（修改后重启生效）。从旧版本 `QoderVibeNotch` 升级时，首次启动会把 `settings.json` 与 `stats.json` 自动复制过来。

| 键 | 默认值 | 说明 |
| --- | --- | --- |
| `AutoStart` | `false` | 开机自启 |
| `SoundOnComplete` | `true` | 任务完成提示音 |
| `SoundOnPermission` | `true` | 授权请求提示音 |
| `SoundName` | `"crystal"` | 提示音音效：`crystal`（清铃）/ `drop`（水滴）/ `chord`（琶音） |
| `AutoHideSeconds` | `6` | 无活动多少秒后折叠 |
| `PermissionTimeoutSeconds` | `90` | 浮岛等待授权的超时；超时后交回 Qoder 原生弹窗 |
| `PermissionOutput` | `"qoder"` | `qoder` 输出 IDE 格式 `hookSpecificOutput.permissionDecision`；`cli` 或 `claude` 输出 CLI 格式 `hookSpecificOutput.decision.behavior` |
| `AutoApprove` | `[]` | 自动放行白名单。`"Bash"` 放行整个工具；`"Bash:git*"` 只放行以 `git` 开头的命令（`*`、`?` 通配）。文件类工具匹配文件路径，如 `"Edit:src/*"`。浮岛里点「总是允许」会自动写入 |
| `NotifyOnComplete` | `true` | 任务完成时弹 Windows 通知（右下角 Toast） |
| `NotifyOnPermission` | `true` | 有新的授权请求时弹 Windows 通知（自动放行不弹） |
| `MonitorMode` | `"fixed"` | `fixed` 固定屏幕（配合 `MonitorDeviceName`）；`mouse` 跟随鼠标所在屏幕；`qoder` 跟随 Qoder 窗口所在屏幕 |
| `MonitorDeviceName` | `null` | `fixed` 模式下的显示器，`null` 表示主屏 |

## 工作原理

```
Qoder hooks ──stdin JSON──▶ VibeNotch.Hook.exe ──命名管道 vibe-notch──▶ WPF 浮岛
```

- 普通事件（`PreToolUse`、`PostToolUse`、`Stop` 等）快速上报后立即退出，Hook 在 Qoder 里配置为 `async`
- `PermissionRequest` 会同步等待浮岛决定（Hook 最长 300 秒，浮岛默认 90 秒）；浮岛超时后 Hook 退出码 0 且不输出，Qoder 自动显示原生授权弹窗
- `AskUserQuestion` 不会触发 `PermissionRequest`，因此安装器额外写了一条**同步**的 `PreToolUse` + `matcher: AskUserQuestion` Hook（超时 300 秒）：浮岛靠它拦截选择题，并把答案塞进 `updatedInput.answers` 回传；普通 `PreToolUse` 仍是 async，只用于状态显示
- 允许 / 拒绝 / 理由 / 编辑后的内容都通过 hook 回包下发：IDE 格式为 `hookSpecificOutput.permissionDecision` + `permissionDecisionReason` + `updatedInput`，CLI 格式为 `hookSpecificOutput.decision.behavior` + `message` + `updatedInput`
- 白名单在浮岛进程内匹配（`AutoApprove`），命中后直接回 `allow`，不弹面板；选择题（`AskUserQuestion`）永远不自动放行
- 「中断 / 继续」用 `PostMessage` 把 `WM_KEYDOWN/WM_KEYUP/WM_CHAR` 投进 Qoder 窗口的消息队列，不激活窗口、不抢焦点；Qoder 窗口通过 EnumWindows 按进程名 + 可见 + 非 Desktop Pet 标题匹配
- 会话信息行每 3 秒刷新一次，全部在后台线程完成：
  - git 分支 / 未提交标记来自 `git status --porcelain=v1 --branch`（按 cwd 缓存 20 秒，单次超时 1.5 秒）
  - 上下文占用与模型来自 transcript JSONL 的尾部：`message.usage.context_usage_ratio`、`message.model`（按文件长度 + 修改时间缓存）
  - 任务进度来自 Qoder 的 `TaskCreate` / `TaskUpdate` / `TaskStop` 工具调用计数，`N/M` 中的 M 是创建数减删除数，是近似值
- 今日统计只在浮岛进程里累加（`Stop` 记完成任务，允许 / 拒绝 / 自动放行分别记数），写入 `%LOCALAPPDATA%\VibeNotch\stats.json` 并清理 14 天前的数据
- 系统通知是真正的 Windows Toast（WinRT `ToastNotificationManager`），不是托盘气泡——Win11 会静默丢弃 `NotifyIcon.ShowBalloonTip` 的传统气泡。未打包的 Win32 程序要有带 `System.AppUserModel.ID` 的开始菜单快捷方式才能发 Toast，所以启动时会自动创建 / 修复 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Vibe Notch.lnk`（AUMID 为 `VibeNotch.Desktop`）并调用 `SetCurrentProcessExplicitAppUserModelID`；Toast 初始化失败时退回托盘气泡。托盘菜单「测试通知」、以及启动 / Hooks 安装提示都走同一条 Toast 通道
- Toast 点击用协议激活回传（未打包应用要走 COM 激活器需要额外注册 CLSID，协议更省事）：Toast 带 `launch="vibe-notch:focus" activationType="protocol"`，启动时在 `HKCU\Software\Classes\vibe-notch` 注册协议，指向本程序；点击后系统按 `"%1"` 拉起本程序，新实例发现单实例互斥锁已存在，就把 `VibeNotch.ToastActivate` 注册窗口消息 `PostMessage` 给正在运行的浮岛窗口，然后退出；浮岛收到后调用 Qoder 窗口聚焦（复用点击会话行的那套逻辑）。卸载脚本会一并删除该协议注册
- 三款提示音是内置的 16-bit / 44.1kHz 单声道 WAV（`src/VibeNotch.App/Sounds`），`SoundPlayer` 按 `SoundName` 缓存播放；切换音效后立即试听一次
- 「浮岛位置」切换 `MonitorMode` 后每秒检查目标屏幕，变了就 `SetWindowPos` 平移；设置里选中某块显示器会把 `MonitorMode` 设为 `fixed` 并写入 `MonitorDeviceName`
- 中断、关闭 Qoder 窗口等情况不会发出 `Stop`/`SessionEnd`，所以运行中的状态会按时自动过期：思考中 2 分钟、压缩上下文 3 分钟、子智能体 5 分钟、执行工具与等待授权 10 分钟，过期后转成「空闲」并折叠，空闲会话 5 分钟后从列表移除
- 日志：
  - `%LOCALAPPDATA%\VibeNotch\logs\hook.log`（每个 Hook 事件）
  - `%LOCALAPPDATA%\VibeNotch\logs\app.log`（应用日志与异常）

注册的 Qoder 事件：`SessionStart`、`SessionEnd`、`UserPromptSubmit`、`PreToolUse`、`PostToolUse`、`PostToolUseFailure`、`PermissionRequest`、`PermissionDenied`、`Notification`、`Stop`、`StopFailure`、`SubagentStart`、`SubagentStop`、`PreCompact`、`PostCompact`。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1

# 同时删除配置、日志和程序目录
powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 -PurgeData
```

卸载会移除 `~/.qoder/settings.json` 中所有指向 `VibeNotch.Hook.exe` 的 Hook（只删自己的条目，其他 Hook 保持不变），并清理开机自启、开始菜单快捷方式和 Toast 协议注册（`HKCU\Software\Classes\vibe-notch`）。

## 排错

| 现象 | 处理 |
| --- | --- |
| 浮岛不出现 / 没有事件 | 看 `logs\hook.log` 是否有记录；托盘「重装 Hooks」；确认 `~/.qoder/settings.json` 的 hooks 命令指向 `VibeNotch.Hook.exe` |
| 授权仍弹在 Qoder 里 | 确认 Hook 已安装；Qoder 的权限模式为 `auto` 时，多数工具不会触发 `PermissionRequest`，浮岛只显示执行状态 |
| 浮岛点允许/拒绝后 Qoder 仍弹窗 | Qoder IDE 与 CLI 的 PermissionRequest 回包结构不同：IDE 需要 `hookSpecificOutput.permissionDecision`（即 `PermissionOutput` 为 `qoder`），CLI 需要 `hookSpecificOutput.decision.behavior`（`cli`/`claude`）。用 `logs\hook.log` 里的 `(qoder)/(cli)` 标记确认当前生效的格式 |
| 任务早就结束了，浮岛还显示「思考中」 | 中断或关闭会话时 Qoder 不会发 `Stop`，浮岛靠超时兜底：思考中 2 分钟无事件即转「空闲」并折叠。`logs\app.log` 里的 `stale:` 行记录了每次过期 |
| 浮岛位置不对 | 托盘菜单「浮岛位置」选固定屏幕或跟随模式；设置 → 显示器里直接点选目标屏幕 |
| 换了提示音没声音 | 托盘里确认「提示音」为开；系统音量 / 输出设备是否正常；`logs\app.log` 出现 `sound failed` 说明 WAV 缺失，重跑 `install.ps1` |
| 会话行没有 git 分支 / ctx / 任务进度 | 分支需要 cwd 是 git 仓库且 `git` 在 PATH 里；ctx 与模型需要 transcript JSONL 里已写入 `context_usage_ratio`；任务进度只统计 Qoder 的 Task 工具调用，普通工具不计数 |
| 通知不出现 | 检查托盘菜单里「完成通知 / 授权通知」是否被关掉；系统「设置 → 系统 → 通知」里确认「Vibe Notch」未被关闭（首次发 Toast 后会出现在列表里）；专注助手也可能拦截。托盘「测试通知」可以快速验证 |
| 通知出现了但图标/名称不对 | 开始菜单快捷方式 `Vibe Notch.lnk` 被删或被改，重启应用会自动补回；`logs\app.log` 的 `toast` 行会显示是否走了气泡回退 |
| 点通知不聚焦 Qoder | 协议注册被清理或被安全软件拦截：`reg query HKCU\Software\Classes\vibe-notch\shell\open\command` 应指向 `VibeNotch.exe`，不对就重启应用自动补回；`logs\app.log` 的 `toast activated:` 行说明收到点击后有没有找到 Qoder 窗口 |
| 应用异常退出 | 查看 `logs\app.log`；重新运行 `install.ps1` 覆盖安装 |

## 从源码构建

```powershell
dotnet publish src\VibeNotch.Hook\VibeNotch.Hook.csproj -c Release -o "$env:LOCALAPPDATA\VibeNotch\app"
dotnet publish src\VibeNotch.App\VibeNotch.App.csproj -c Release -o "$env:LOCALAPPDATA\VibeNotch\app"
```

- `src/VibeNotch.Hook`：Hook 命令行客户端（读取 stdin JSON、写命名管道、输出授权决定）
- `src/VibeNotch.App`：WPF 浮岛 + 托盘 + Hook 安装器 + Qoder 窗口聚焦

## 与 vibe-notch 的差异

| vibe-notch (macOS) | Vibe Notch (Windows) |
| --- | --- |
| Unix domain socket | Windows 命名管道 |
| 刘海区域 | 顶部居中可折叠浮岛 |
| tmux / tty 聚焦 | EnumWindows 聚焦 Qoder 窗口（优先项目名，跳过 Desktop Pet） |
| 打开态头部 ☰ → NotchMenuView（含屏幕 / 声音选择） | ☰ → 浮岛内设置（同样有显示器选择与三款提示音，另有 Windows 的位置模式与 Toast 开关） |
| 菜单里的 Sparkle 自动更新 | 未内置更新器，版本号显示在设置面板底部，更新走 `install.ps1` |
| 菜单里的 Star on GitHub | 仓库地址见本页顶部，欢迎 Star |
| SwiftUI 单文件 | .NET 8 WPF，无 Node / Electron 依赖 |
| Claude Code | Qoder（兼容 Claude Code Hook 协议），后续计划支持 WorkBuddy / Trae |

## 许可

本项目基于 vibe-notch（Copyright 2025 Farouq Aldori）的体验与部分设计实现，遵循 Apache License 2.0，详见 [LICENSE.md](LICENSE.md) 与 [NOTICE](NOTICE)。
