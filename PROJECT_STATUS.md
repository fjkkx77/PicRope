# PicRope 定制版 · 项目交接文档

> 给接手的会话（Claude Code / Codex）看的。**改代码前先读完本文件**，做完一步回来更新「已完成 / 坑 / 待办」。
> 只在 `custom` 分支里有这份文件；功能分支保持干净，方便以后单独提给上游。
> 最后更新：2026-11-04 晚

## 一、这是什么

- **PicRope（图绳）**：Windows 截图暂存 / 桌面贴图工具，C# + WPF，.NET Framework 4.8。
- 上游：`Renly1994/PicRope`（MIT；它移植自 Tendedero，**公开发布改版必须换名字和图标**，自用无限制）。
- 本仓库：用户的 fork `fjkkx77/PicRope`（**公开**），origin = 用户，upstream = 原作者。
- 用户：编程零基础，用 PixPin 截图，主力屏 2560×1600、150% 缩放。

## 二、位置

| 什么 | 路径 |
|---|---|
| 源码 | `C:\Users\Administrator\CodeProjects\PicRope` |
| 安装位置 | `C:\Users\Administrator\AppData\Local\Programs\PicRope` |
| 用户数据（**别动**） | 安装位置下 `data\`：`state.json` 设置和文字记录、`Captures\` 收集的截图 |
| 官方原版 exe 备份 | `C:\Users\Administrator\.claude\backups\2026-11-04_1414_PicRope_1.2.2_official.exe` |

## 三、分支结构（2026-11-04 全部已推送，核对过与远端一致）

每项定制一个分支，**都基于上游 `5fa1ac0`（= v1.2.2）**；`custom` = 上游 + 全部功能分支合并 + 本文件，**本机装的就是 `custom` 的构建**。

| 分支 | 做了什么 | 关键代码 / 测试 |
|---|---|---|
| `feat/screenshot-tools-filedrop` | PixPin、QQ 等截图软件复制的截图也能上绳（原版带文件的复制只认微信）；「更多操作 → 收集 PixPin 等截图软件的截图」开关，默认开 | `Core.cs` 的 `ClipboardRules.IsImage`、`SavedState.CollectScreenshotTools`；`tests/ClipboardImageTests.cs` |
| `feat/top-left-corner-reveal` | 鼠标只在**屏幕左上角**停留才展开（原版整个上沿）。宽度 `CornerWidth = 28` 逻辑像素（初版 40，用户嫌太易误碰） | `Core.cs` 的 `RevealRules`；`CoreTests` 3 项 |
| `feat/custom-update-warning` | 版本号显示「v1.2.2 定制版」；官方更新窗口加提醒，「下载并安装」要点两次 | `AppUpdates.DisplayVersion`、`UpdateWindow.cs` |
| `feat/drop-files-onto-rope` | 图片文件拖到绳子上就挂上（复制进收集文件夹，原文件不动）；拖着文件到左上角也会展开，拖**窗口**时不展开（不挡 Windows 角落贴靠） | `ShelfWindow` 的 `DragFiles`/`CatchDrops`、`DropRules`、`WindowsNative.MovingWindow` |
| `feat/drag-reorder` | 沿绳子左右拖动排序；往下拖仍是原版「拖出去用」；两端自动滚动 + 拖动中可滚轮 | `ReorderRules`、`Gallery.Move`、`ShotCard` 的 Move/Follow |
| `fix/pinned-zoom` | 桌面贴图滚轮缩放：不乱跳、以光标为中心、平滑跟手；双击恢复原始大小；**11-04 第四轮「舞台」**：缩放中不再逐帧改窗口尺寸 | `PinnedImageWindow.cs` 的 `PrepareStage`/`PlaceOnStage`/`CommitStage`/`ResizeWithoutFlash`、`ZoomRules.AroundCursor` |
| `perf/flight-window-pool` | 截图上绳不再卡一下：飞行动画的整屏透明窗口提前备好、用完回收 | `ShotFlight.Take`/`Play`/`Stop`/`Prewarm`，`ShelfWindow.PlayFlight` |
| `feat/pinned-menu` | 桌面贴图右键菜单：复制、打开、编辑、保存副本、打开位置、挂回晾衣绳、删除（移到回收站） | `PinnedImageWindow.AttachMenu`/`BuildMenu`/`Delete`，构造函数新增两个可选参数 |
| `custom` | 以上全部 + 本文件 | — |

**加新定制的流程**：新分支从上游版本开 → 改 + 测 → 合并进 `custom` → 在 `custom` 上构建、替换安装 → 推送 → 回来更新本文件。

## 四、构建、测试、替换安装

```powershell
.\build.ps1                          # 构建 + 核心测试（系统自带 csc.exe 4.8，无需 SDK），产物 build\PicRope.exe
.\tests\ClipboardImageTests.ps1      # 剪贴板识别测试
.\tests\ClipboardImageTests.ps1 -Worker   # 剪贴板后台读取测试
```

- **别跑 DesktopTests / UpdateTests 等界面测试**：会在用户屏幕上弹窗口、移动鼠标。
- `build.ps1` 测试失败会直接 throw，**同一条命令后面的步骤不会执行**（踩过：`git stash` 后构建失败，stash 没 pop）。
- **替换安装**：
  1. 按**路径**找进程（进程号每次都变）：`Get-Process PicRope | ? Path -eq "<安装位置>\PicRope.exe"`，`CloseMainWindow()` 关掉；
  2. 只换 `PicRope.exe`，`data\` 不动，核对哈希；
  3. **用 `explorer.exe "<安装位置>\PicRope.exe"` 启动**，然后核对权限级别是 `0x2000`（普通）。

## 五、坑（都踩过）

1. **🔴 不能从 Claude Code 的终端直接 `Start-Process` 启动 PicRope。** 终端是管理员权限（High），启动的 PicRope 也会是管理员；Windows 的 UIPI 会**静默拦下普通程序（桌面、资源管理器）往管理员程序拖放**，表现为「拖文件上绳完全没反应」。用 explorer.exe 启动就是普通权限。开机自启（注册表 Run 键）本来就是普通权限。
2. **WPF 改窗口尺寸时会同步重绘，并在里面再触发 `CompositionTarget.Rendering`**：逐帧改窗口的动画必须防重入（`Position` 的 `positioning` 标记），否则调用一层套一层，退出时外层把过时尺寸设回去，窗口被拉回缩小（实测嵌套二三十层、单次 791ms）。
3. **WPF 里渲染回调优先级高于输入**：耗时的逐帧动画挂在 `CompositionTarget.Rendering` 上会把滚轮饿住（实测第 3 格延迟 631ms）。贴图缩放改成每步做完在 `ApplicationIdle` 优先级排下一步。
4. **分层窗口（AllowsTransparency）只有不透明像素能接住鼠标和拖放**：原版绳子空白处会让拖进来的文件穿到桌面；拖文件经过时临时给根 Grid 铺 alpha=2 的底色。
5. 原版每滚一格贴图窗口变 3 次（先改宽、再改高、最后挪位置），所以会左右跳；改为位置和大小一次 `SetWindowPos`，再把同尺寸同步给 WPF。
6. 剪贴板测试的 `--current-clipboard` 模式只适用于微信。
7. `PicRope.exe.config` 不要写死代理端口（Codex 装的时候写死过 8800，已删）；.NET Framework 不写代理配置就跟随系统代理。
8. 推送偶尔「Empty reply / schannel handshake」，网络抖动，重试即可，推完用 `git ls-remote` 核对。
9. 好几个分支都在 `Core.cs` 同一位置加新类、在 `CoreTests.cs` 同一位置加测试，合并进 `custom` 时会冲突；两边都保留即可。`ShelfWindow` 的展开条件合并后是 `nearCorner && (!down || draggingIn)`。
10. **🔴 分层透明窗口（AllowsTransparency）改尺寸很贵**：实测每次约 28ms，逐帧改 → 每秒只有 21~37 帧；窗口不动只改里面内容可稳 60 帧（屏幕 60Hz，WPF 也封顶 60）。动画要么在固定大小的窗口里做，要么像贴图缩放那样先撑「舞台」。
11. **普通 SetWindowPos 改分层窗口的位置+大小，系统会把旧画面放到新位置先显示一帧**（实测 16 次闪 3~4 次）。用 `SWP_NOCOPYBITS|SWP_NOREDRAW` 改、排好内容后再补一次 `FRAMECHANGED`（0x37），实测 32 次 0 闪——见 `PinnedImageWindow.ResizeWithoutFlash`。
12. **新建并显示一个整屏透明窗口要 50~100ms**（跟面积关系不大）；藏起来的窗口再显示只要约 2.5ms。所以飞行窗口做成池子。藏之前要等清空的画面真正画上去，否则再显示时先露出上次的最后一帧。
13. 测量时**别同时开截屏采样线程**（BitBlt+CAPTUREBLT 循环）量帧率，它会把分层窗口拖到 30 帧，得出错误结论（踩过）；截屏采样只用来查「闪没闪」。
14. 剩余已知问题：飞行动画本身（整屏透明窗口每帧回读）仍会偶尔掉帧（只有飞行窗口时 1 秒掉 3~7 帧，和绳子一起动时约 10~15 帧），**原版就有**，不是「上绳那一下卡」。缩小窗口面积能改善但收益有限，暂未做；DWM 透明窗口方案在 ToDesk 等远程/虚拟显示下有黑底风险，没采用。

## 六、怎么验证界面行为（没有现成测试的部分）

- **2026-11-04 用过的测量探针**（不发鼠标键盘输入，只在屏幕上短暂出窗口）：直接反射调 `ResizePicture` 模拟连续 5 格滚轮，统计「WM_WINDOWPOSCHANGED 里改尺寸的次数」+「UI 线程渲染回调去重后的帧间隔（>25ms 算掉帧）」；上绳用临时 profile 建 `ShelfWindow` + `Gallery`，计时 `gallery.Add` 和 `ShowNewCapture`。结果：缩放 5 格掉帧 8~13 次 → 0~2 次；`ShowNewCapture` 52~75ms → 3~12ms。探针没进仓库，要用时照这个思路重写。
- **贴图缩放**：临时写探针程序，和 `src\*.cs` 一起编译（参照 `build.ps1` 的引用和资源参数），贴一张纯色测试图：
  - 量窗口变化：Hook `WM_WINDOWPOSCHANGED`；
  - 量屏幕上实际显示：另开线程 `BitBlt`（要带 `CAPTUREBLT` 才截得到分层窗口）连续截屏，找纯色框；
  - 量输入延迟：用 `SendInput` 发**真实滚轮**（不移动指针），对比发出和收到的时间戳。**别用反射直接调处理函数**，那样绕过了消息队列，测不出排队。
  - 探针用的是用户真实的光标位置：**用户动了鼠标、或光标不在贴图上，结果会看起来异常**，看结果前先核对光标。
- **拖放、排序**：需要真实鼠标拖动，由用户手测；可以临时加诊断日志写到 `data\debug.log`，用完删掉。

## 七、跟进官方更新

- PicRope 每 12 小时查一次更新，有新版弹通知，用户点了才装；**装官方版会覆盖全部定制**（设置和截图保留）。定制版的更新窗口会先提醒、要点两次。
- 用户说「PicRope 更新了」时：`git fetch upstream` → 每个功能分支 rebase 到新版本 → 跑测试 → 重建 `custom` → 构建、替换安装（按第四节）→ 用户手测 → 推送 → 更新本文件。

## 八、用户的决定和待办

- **暂不给上游提 PR**（上游 issue #24 是同一个截图软件问题）。用户还要继续改别的，**别主动提**，等用户开口。
- 2026-11-04 晚：贴图缩放第四轮（舞台）、上绳窗口池、贴图右键菜单已装上（custom `da71295`），**等用户真机反馈**：①缩放顺不顺、开始/停下那一下有没有闪；②PixPin 截图上绳还卡不卡；③右键菜单各项（真鼠标）。嫌缩放过渡快或慢，调 `PinnedImageWindow.ZoomTimeConstant`（0.06 秒）。
- 右键菜单的后续候选（用户还没要）：透明度、鼠标穿透、恢复原始大小（现在靠双击）、关闭全部贴图。
- 替换安装时 `CloseMainWindow()` 关不掉（托盘程序没有主窗口），11-04 是按安装路径核对 PID 后 `Stop-Process -Id` 结束的；替换前的 exe 备份在 `.claude\backups\2026-11-04_1930_PicRope_custom_before_zoomstage.exe`。
- 2026-11-04 15:31 绳上 9 张截图被用户用「×」删进了回收站，问过用户、没回复，按「有意删除」处理，没动回收站。
- 剪贴板文字收集会把复制的文字（包括密码）明文存进 `data\state.json`，已提醒过用户。
