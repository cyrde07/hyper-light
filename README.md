# hyper-light

把壁纸颜色同步到 HyperX QuadCast S 麦克风、技嘉主板上的灯（机箱风扇、水冷、主板 AORUS 字）和华硕 RTX 4090 显卡灯，让它们和 Wallpaper Engine 控制的 Razer 设备（键盘、鼠标垫、音响）颜色保持一致。也可以不跟壁纸，自己给每个设备选固定颜色。

- 平台：Windows 10 / 11（64 位）
- 语言：C#（.NET 8）
- 设备通信：[HidSharp](https://www.nuget.org/packages/HidSharp)（Apache-2.0 许可）
- 托盘界面、截图存图：Windows 自带的 WinForms 和 System.Drawing（.NET 官方组件）
- 协议参考（只参考协议格式和设备编号这类事实，没有使用它们的代码）：
  - 麦克风：[QuadcastRGB](https://github.com/Ors1mer/QuadcastRGB)（GPL-2.0 许可）
  - 主板灯光芯片、显卡灯光芯片：[OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB)（GPL-2.0 许可）的 GigabyteRGBFusion2USBController、ENESMBusController、NVFC/nvapi
- 不修改 Razer、Wallpaper Engine 的任何文件，不做 DLL 劫持；不往主板、显卡的灯光芯片里永久保存任何东西（断电重启后恢复原样）。

## 进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| M0 | 列出 HID 设备，确认 QuadCast S 的 VID / PID | 完成：03F0:028C，接口 0 是灯光控制节点 |
| M1 | 发一个固定颜色给麦克风，确认能控制 | 完成：每秒 30 帧稳定；停止发送后麦克风立刻恢复自带灯效 |
| M2 | 截 Wallpaper Engine 的壁纸窗口取色，实时发给麦克风 | 完成：开着软件也和键盘一致；CPU 0.1%；省内存优化后约 20 MB |
| M3 | 调色：提高饱和度接近键盘颜色；上下两个灯区分别取色 | 完成 |
| M4 | 托盘程序：开机自启、暂停 / 恢复、退出时灯恢复正常 | 完成 |
| M5 | 主板灯：机箱灯、冷排灯、主板灯（AORUS 字）跟着壁纸变色；左右半区取色；双色、流动灯效；加大差异 | 完成（2026-11-23） |
| M6 | 自由选色：每个设备各选各的固定颜色，不截图；统一选色 | 完成（2026-11-23） |
| G | 显卡灯：G0 探测、G1 固定颜色测试、G2 接入同步（纯色、颜色加强、自由选色） | 完成（2026-11-23） |

## 支持的设备

| 设备 | 托盘里叫 | 接在哪 | 能做什么 |
|---|---|---|---|
| HyperX QuadCast S | 麦克风 | USB | 上下两个灯区，按「取色方式」取两个颜色 |
| 机箱风扇和灯条 | 机箱灯 | 机箱集线板 → 主板 `ARGB_V2_1`（协议通道 0x59） | 单色 / 双色 / 流动 |
| 瓦尔基里 V360 水冷 | 冷排灯 | 主板 `ARGB_V2_2`（协议通道 0x58） | 单色 / 双色 / 流动 |
| 主板 AORUS 字 | 主板灯 | 主板自带（技嘉 X670E AORUS PRO X，灯光芯片 ITE IT5702，048D:5702） | 单色，颜色加强 |
| 华硕 ROG STRIX RTX 4090 白色版 | 显卡灯 | 显卡上的 ENE 灯光芯片（I2C 端口 1、地址 0x67，芯片 AUMA0-E6K5-1110，30 颗灯），经 NVIDIA 驱动的 nvapi64.dll 访问 | 单色，颜色加强 |

内存条的灯不控制（要装驱动、用管理员权限，有风险）。

## 使用

### 平时使用：托盘程序

双击 `HyperLight.exe`，程序会在屏幕右下角的托盘区运行，**不会弹出窗口**。

- **托盘图标**：一个小圆点，上半部分是麦克风上灯区的颜色，下半部分是下灯区的颜色；暂停时是灰色；还没有颜色（比如没找到壁纸）时是空心圆。
- **右键菜单**：点开关和多选一的选项时菜单**不会关闭**，可以连续改好几个；点菜单外面就关闭。所有改动都会自动保存到设置文件。

| 菜单项 | 作用 |
|---|---|
| 第一行（灰色） | 麦克风和壁纸的状态，比如"同步中：上 001E6C / 下 1A2B3C" |
| 第二行（灰色） | 主板的状态，比如"主板：主板已连接，主板上的灯正在同步" |
| 第三行（灰色） | 显卡的状态，比如"显卡：显卡灯已连接，正在同步" |
| 暂停同步 / 恢复同步 | 暂停时不截图、不发送：麦克风回到自带灯效，主板和显卡的灯保持当前颜色 |
| 模式 | 跟壁纸 / 自由选色 |
| 自由选色 | 「统一选色」一次把所有设备设成同一个颜色；也可以给每个设备单独选（弹出 Windows 选色窗口），前面的小色块是现在的颜色。机箱灯、冷排灯各有两个颜色：单色用第一个，双色、流动用两个。选了颜色会自动切到自由选色模式 |
| 增强颜色（模仿 Wallpaper Engine） | 提高饱和度。自由选色时变灰 |
| 取色方式 | 麦克风两个灯区从壁纸哪里取色：上下 / 左右 / 整张。自由选色时变灰 |
| 麦克风 / 机箱灯 / 冷排灯 / 主板灯 / 显卡灯 | 每个设备的开关。关掉后：麦克风回到自带灯效；主板和显卡的灯停在最后的颜色 |
| 机箱灯取色 / 冷排灯取色 / 主板灯取色 / 显卡灯取色 | 单色时用壁纸哪一块：上半区 / 下半区 / 左半区 / 右半区 / 整张 |
| 主板灯颜色加强 / 显卡灯颜色加强（滑动条） | 淡颜色在 LED 上看起来发白，加强后亮度不变、颜色更浓（浅粉 F99396 → F94E52）。纯灰、纯白没有颜色可加强。拖到最左边关闭 |
| 机箱灯灯效 / 冷排灯灯效 | 单色 / 左右双色 / 上下双色 / 左右流动 / 上下流动 |
| 加大差异（滑动条） | 双色、流动时，两个颜色太像就自动拉开（有颜色的往色环两边挪，灰色变成一亮一暗）。拖到最左边关闭，停下 0.4 秒后生效 |
| 开机自启 | 勾选后，以后开机自动在托盘运行（写入当前用户的启动项，不需要管理员权限） |
| 打开设置文件 | 用记事本打开 `hyperlight.json` |
| 重新读取设置 | 改完设置文件后点这里生效 |
| 退出 | 停止同步：麦克风马上回到自带灯效，主板和显卡的灯保持最后的颜色 |

- **防止重复运行**：同一时间只允许一个 HyperLight 运行。托盘程序在运行时，再双击 exe 只会提示"已经在运行"。
- **exe 换了位置**（比如解压了新版本）：打开过开机自启的话，新位置的程序启动时会自动把启动项改成新位置。
- **桌面快捷方式**：右键 `HyperLight.exe` →（Windows 11 先点「显示更多选项」）「发送到」→「桌面快捷方式」。
- **建议放在固定位置**：比如 `D:\Tools\HyperLight`，不要放在「下载」文件夹里。

### 灯效说明

| 灯效 | 样子 | 用到的颜色 |
|---|---|---|
| 单色 | 整个灯区一个颜色 | 「取色」选的那一块 |
| 左右双色 / 上下双色 | 每个风扇一半一个颜色，不转 | 左半区 + 右半区 / 上半区 + 下半区 |
| 左右流动 / 上下流动 | 两个颜色连成渐变，在每个风扇上转圈（默认 2 秒一圈） | 同上 |

- 机箱集线板会把同一份数据**复制**给每个风扇，所以 5 个风扇永远显示同样的图案，没法各是各的颜色。
- 双色、流动的图案每 8 颗灯重复一次（底部、侧面风扇 8 颗灯正好一圈；后置风扇 16 颗是两圈）。
- 壁纸两边颜色差不多时（比如上粉下蓝的壁纸，左右两半都是"粉蓝混合"），左右流动会看不出来，要用上下流动，或者把「加大差异」调大。
- 单色、双色只在颜色变化时才发给主板（主板停止接收后会保持颜色）；流动每秒发约 20 帧。

## 主板灯的接线和设置（重要）

M5 实测的接线（2026-11-23）：

```
主板 ARGB_V2_2（右上，LED_C 正下方） ── 水冷的灯光线
主板 ARGB_V2_1（左下，前置音频旁边） ── 机箱集线板的输入线（一根母头灯光线，原来空着）
机箱集线板 ── 底部 3 个风扇 / 侧面风扇 / 后置风扇 + 灯条
CPU_FAN ── 水冷转速；SYS_FAN4 ── 后置风扇转速
```

- **机箱灯光按钮**：长按会在「集线板自己的灯效 → 关闭 → 主板控制」之间轮流切换。**要停在「主板控制」**，机箱灯才会听 HyperLight 的。断电重启后集线板可能会变回自己的灯效，再长按切回来就行。
- **OpenRGB、技嘉 GCC / RGB Fusion 要关掉**：它们也会控制主板灯光芯片，和 HyperLight 抢。HyperLight 检测到 OpenRGB 在运行会弹提醒。
- **灯珠数**：默认 64（`caseLedCount`、`radiatorLedCount`）。每个包大约 3 毫秒，256 颗灯一帧要 28 个包（约 90 毫秒），流动会卡；64 颗只要 8 个包（约 25 毫秒）。
- **已知硬件问题**：水冷侧面灯条三分之一处有一颗灯珠接触不良，中间那个风扇偶尔会闪，后面的灯常常收不到数据（`board range` 测试：第 1～64 颗之后全黑）。软件修不了，可以找售后。
- **显卡灯**：Armoury Crate、GPU Tweak 也能控制显卡灯，开着可能会抢。Armoury Crate 结束进程后会被它的后台服务重新拉起来；如果显卡灯被改回去，到 `services.msc` 里停掉它的服务（比如 LightingService），启动类型改成「手动」。
- **ARGB_V2_1 和 ARGB_V2_3 是同一个通道**（OpenRGB 资料），所以主板只有两个能分开控制的 ARGB 接口。OpenRGB 里叫 `ARGB_V2_3` 的就是 `ARGB_V2_1`。

## 设置文件

设置保存在 exe 旁边的 `hyperlight.json`，第一次运行时自动生成，每一项上面都有中文说明。托盘菜单里的改动也会写进这个文件。手动改完后，在托盘菜单点「重新读取设置」生效。

| 配置项 | 默认值 | 含义 |
|---|---|---|
| `captureIntervalMs` | 500 | 截图间隔（毫秒），范围 100～10000。改成 250，换壁纸时反应快一倍 |
| `sendIntervalMs` | 33 | 麦克风发送间隔（毫秒），范围 20～100 |
| `changeThreshold` | 12 | 颜色变化门槛（0～255），越大越不容易变 |
| `fadeMs` | 400 | 渐变时间（毫秒），0 表示立刻切换 |
| `confirmCaptures` | 2 | 新颜色要连续截到几次才切换，换壁纸时的过渡画面就不会传到灯上 |
| `wallpaperLostGraceMs` | 3000 | 找不到壁纸多久后麦克风停止发送（毫秒） |
| `boostColors` | true | 增强颜色（提高饱和度） |
| `saturationBoost` | 1.5 | 增强倍数，范围 1.0～3.0 |
| `zoneLayout` | topBottom | 麦克风取色方式：`topBottom` 上下 / `leftRight` 左右 / `whole` 整张 |
| `colorMode` | wallpaper | 模式：`wallpaper` 跟壁纸 / `free` 自由选色 |
| `micEnabled` / `caseEnabled` / `radiatorEnabled` / `onboardEnabled` / `gpuEnabled` | true | 五个设备的开关 |
| `onboardColorSource` / `gpuColorSource` | average | 主板灯、显卡灯取色：`upper` / `lower` / `left` / `right` / `average`（整张） |
| `onboardColorBoost` / `gpuColorBoost` | 60 | 主板灯、显卡灯颜色加强（0～100），0 关闭 |
| `caseColorSource` | lower | 机箱灯取色（单色时用） |
| `radiatorColorSource` | upper | 冷排灯取色（单色时用） |
| `caseEffect` / `radiatorEffect` | solid | 灯效：`solid` 单色 / `splitLeftRight` 左右双色 / `splitUpDown` 上下双色 / `flowLeftRight` 左右流动 / `flowUpDown` 上下流动 |
| `twoColorSpread` | 40 | 加大差异（0～100），0 关闭 |
| `flowCycleMs` | 2000 | 流动转一圈多少毫秒，范围 500～20000 |
| `freeMicColor` 等 7 项 | FF69B4 / 8A2BE2 | 自由选色的颜色，写成 6 位十六进制 |
| `flowPeriodLeds` | 8 | 双色、流动图案每几颗灯重复一次，范围 2～64 |
| `caseLedCount` / `radiatorLedCount` | 64 | 发给多少颗灯，范围 1～256 |

（以 `src/HyperLight/Config/AppConfig.cs` 为准。）

## 诊断工具

带命令运行时是诊断工具。因为 `HyperLight.exe` 是窗口程序，请通过 `.bat` 文件运行（`.bat` 要和 exe 放在同一个文件夹）。
**会写硬件的测试命令**（`color`、`board color/count/range/flow/onboard`、`gpu color/release`）**要先退出托盘里的 HyperLight**，否则程序会拒绝运行并提示；只读的诊断（`list`、`board`、`gpu`）可以随时运行。
脚本里只写英文：Windows 命令行默认按 GBK 读取 .bat，写 UTF-8 中文可能导致命令错乱。

| 命令 | 作用 | 测试脚本 |
|---|---|---|
| `list` | 列出所有 HID 设备，找出 QuadCast S，检查 NGENUITY（只读） | `manual-tests/m0/list.bat` |
| `color <上> [下]` | 给麦克风发固定颜色 | `manual-tests/m1/` |
| `sync` | 壁纸同步的命令行版，显示色块、耗时、CPU、内存 | `manual-tests/m2/sync.bat` |
| `probe` | 找 Wallpaper Engine 的壁纸窗口并截图（只读） | `manual-tests/m2-probe/` |
| `board` | 读取主板灯光芯片的信息（只读），报告存成 `board-info-日期-时间.txt` | `manual-tests/m5/board-info.bat` |
| `board color <机箱> [冷排]` | 机箱灯、冷排灯设成固定颜色 | `m5/test1`～`test5` |
| `board count` | 每 4 颗灯一段，按红绿蓝黄青紫循环上色，拍照数灯珠 | `m5/test6` |
| `board range <从> <到>` | 冷排只点亮这一段灯（白色） | `m5/test7`～`test10` |
| `board flow <颜色1> <颜色2>` | 机箱灯、冷排灯流动 10 秒，显示每帧发送耗时 | `m5/test11` |
| `board onboard <颜色> [下方颜色]` | 主板灯（I/O 挡板上、下）设成固定颜色 | `m5/test12`～`test16` |
| `gpu` | 找显卡、认灯光芯片、读芯片型号和灯珠数（不改灯光），报告存成 `gpu-info-日期-时间.txt` | `manual-tests/gpu/gpu-info.bat` |
| `gpu color <颜色>` | 显卡灯设成固定颜色（打开直接控制模式） | `gpu/test1`～`test3` |
| `gpu release` | 关闭直接控制模式，显卡回到自己的灯效 | `gpu/test4` |

### 同步是怎么工作的

- **截壁纸窗口，不截整个屏幕**：实测 Wallpaper Engine 给 Razer 设备的颜色取自壁纸本身，开着软件也不变。
- **怎么截**：用 PrintWindow（完整内容模式）截壁纸的宿主窗口，壁纸被窗口挡住也能截到。
- **一次算五块颜色**：把壁纸分成左上、右上、左下、右下四块扫描一遍，组合出上、下、左、右、整张五个颜色。每块各有一个颜色过滤器（变化门槛 + 确认次数 + 渐变）。
- **显卡灯被别的程序改掉时**：颜色不变时，每 3 秒读回来检查一次（直接控制模式还开着、第 1 颗灯还是我们的颜色），不对就改回来，并在日志里记一次。
- **四个线程**：截图线程（每 0.5 秒截一次）；麦克风发送线程（每 33 毫秒发一次，麦克风一停止接收就恢复自带灯效，所以不能被截图卡住）；主板线程（单色、双色只在颜色变化时发，流动每秒约 20 帧）；显卡线程（只在颜色变化时发，经 NVIDIA 驱动的 I2C 比较慢，单独一个线程不拖慢风扇）。
- **找不到壁纸**（Wallpaper Engine 没开）：3 秒后麦克风停止发送、回到自带灯效；主板和显卡的灯保持最后的颜色。

## 卡顿记录

程序会在出现异常时自动记一行到 exe 旁边的 `hyperlight-log.txt`（平时只记启动和状态变化，超过 1 MB 会自动换新文件）：

| 记录 | 说明 |
|---|---|
| `[启动]` | 开始同步，写明截图间隔、取色方式和每个设备的设置 |
| `[截图] 这次截图很慢` | 截一次壁纸超过 0.3 秒 |
| `[发送] 发送中断了 N 毫秒` | 麦克风发送线程被卡住，会写明是 USB 慢还是整个程序被卡住 |
| `[壁纸] 壁纸窗口变了 / 找不到壁纸窗口` | Wallpaper Engine 换壁纸、重新加载或者没开 |
| `[颜色] 颜色突变` | 颜色一下子变了很多，多半是换了壁纸；写明"已切换"还是"先不切换，等确认" |
| `[麦克风] 麦克风断开 / 已连上` | USB 断开又连上 |
| `[主板] 主板灯光芯片已连上 / 断开 / 发颜色很慢` | 主板连接情况；发一次超过 0.3 秒会记录；每次连上后第一次发主板灯的颜色 |
| `[显卡] 显卡灯已连上 / 断开 / 等待显卡灯 / 发颜色很慢` | 显卡连接情况（同样的问题只记一次） |

已知情况：Wallpaper Engine 的播放列表换壁纸时，屏幕可能会顿一下，所有灯也会闪一下。这是 Wallpaper Engine 加载新壁纸造成的，不是 HyperLight 引起的；可以拉长换壁纸的间隔，或者换掉加载慢的壁纸。

## 换电脑 / 重新安装

需要：Windows 10 / 11（64 位）、HyperX QuadCast S、Wallpaper Engine。不用装 .NET，也不用装 iCUE、OpenRGB 等其他软件。主板灯的部分只支持技嘉 IT5702 灯光芯片（本项目按 X670E AORUS PRO X 测试）。

1. 把 `HyperLight` 文件夹拷过去。改过设置的话，`hyperlight.json` 要一起拷。
2. 双击 `HyperLight.exe`。
3. 托盘菜单里重新勾一次「开机自启」（开机自启是记在每台电脑上的）。
4. 换了主板或机箱接线的话，先跑 `board-info.bat` 和 `m5` 的测试，确认机箱灯、冷排灯没有对调（`test5` 应该是机箱红、冷排蓝）。

找不到麦克风时，运行 `list.bat` 看设备编号。新批次的麦克风如果编号不在 `Device/QuadcastDeviceIds.cs` 的表里，加一行就行。
多显示器没有测试过，程序默认取主显示器的壁纸。

编译出来的 exe 不放进 git（太大）。需要的话可以自己重新编译（见下面的「编译」）。

## 注意

- 发颜色之前必须完全退出 HyperX NGENUITY（两个程序会同时控制麦克风，灯会乱闪）。程序检测到它在运行时会提示。
- 控制主板灯时必须关掉 OpenRGB 和技嘉的灯光软件，理由同上。
- 显卡灯只支持华硕 ROG STRIX RTX 4090 白色版（板卡编号 1043:88C3 / 88F1 / 88C4 / 88F2）。程序会先核对显卡编号，再做"认芯片检查"（寄存器 0xA0～0xAF 必须是 0～15），都通过才写；灯珠数超过 32 不控制；绝不写"保存设置"。NVIDIA 没有公开这些驱动函数，以后更新显卡驱动有可能不能用，那时只会显示"等待显卡灯"，不影响其他设备。

## 目录结构

```
hyper-light/
├─ HyperLight.sln
├─ tests/HyperLight.Tests/  自动测试（颜色处理、设置文件、设备协议，不需要硬件）
├─ manual-tests/        手动测试脚本（m0 设备诊断；m1 麦克风固定颜色；m2 壁纸同步；m2-probe 壁纸窗口截图；m5 主板灯；gpu 显卡灯）
└─ src/HyperLight/
   ├─ Program.cs        入口：不带参数启动托盘；带命令时是诊断工具
   ├─ SyncEngine.cs     同步引擎：截图线程 + 麦克风发送线程 + 主板线程 + 显卡线程（托盘和 sync 命令共用）
   ├─ MicOwnerLock.cs   防止多个 HyperLight 同时运行
   ├─ Rgb.cs            颜色类型（各模块共用）
   ├─ Diagnostics/
   │  └─ StallLog.cs           卡顿记录（hyperlight-log.txt）
   ├─ Tray/             托盘程序
   │  ├─ TrayApp.cs            托盘图标、右键菜单（开关、多选一、选色、滑动条）
   │  ├─ MenuSlider.cs         菜单里的 0～100 滑动条（加大差异、颜色加强共用）
   │  ├─ TrayIconPainter.cs    画托盘图标
   │  └─ AutoStart.cs          开机自启
   ├─ Commands/         命令
   │  ├─ ColorCommand.cs       color 命令（麦克风固定颜色）
   │  ├─ SyncCommand.cs        sync 命令（壁纸同步命令行版）
   │  ├─ ProbeCommand.cs       probe 命令（壁纸窗口截图测试）
   │  ├─ BoardCommand.cs       board 命令（主板灯诊断和测试）
   │  ├─ GpuCommand.cs         gpu 命令（显卡灯诊断和测试）
   │  └─ ReportFile.cs         把诊断报告存到 exe 旁边（list、board 共用）
   ├─ Config/
   │  ├─ AppConfig.cs          配置项、默认值、范围、说明（全项目唯一定义配置的地方）
   │  └─ RgbHexConverter.cs    颜色在设置文件里写成 "FF69B4"
   ├─ ColorProcessing/  颜色处理
   │  ├─ WallpaperColors.cs    一次截图算出的五块区域颜色
   │  ├─ ColorAverager.cs      算平均色（扫描一遍，分四块累加）
   │  ├─ ColorSource.cs        取色来源（上 / 下 / 左 / 右 / 整张）和中文名字
   │  ├─ ZoneLayout.cs         麦克风取色方式，以及它用哪两块区域
   │  ├─ ZoneColors.cs         麦克风上下两个灯区的颜色
   │  ├─ LightEffect.cs        灯效（单色 / 双色 / 流动）和图案算法
   │  ├─ ColorMode.cs          模式（跟壁纸 / 自由选色）
   │  ├─ ColorEnhancer.cs      增强颜色（提高饱和度）、加大差异、LED 颜色加强
   │  └─ ColorChangeFilter.cs  变化门槛 + 确认次数 + 渐变
   ├─ Capture/          截图
   │  ├─ NativeMethods.cs      Windows 系统函数声明
   │  ├─ WallpaperWindowFinder.cs  找壁纸窗口
   │  ├─ WallpaperCapturer.cs  截壁纸
   │  └─ WallpaperWindowProbe.cs  壁纸窗口截图测试
   └─ Device/           设备通信
      ├─ UsbId.cs              USB 设备编号（VID:PID）
      ├─ QuadcastDeviceIds.cs  麦克风编号表
      ├─ QuadcastProtocol.cs   麦克风灯光协议（字节格式）
      ├─ QuadcastMic.cs        麦克风连接：找节点、打开、发颜色、断线重连
      ├─ NgenuityProcess.cs    检查 NGENUITY 是否在运行
      ├─ HidDiagnostics.cs     设备诊断（list 命令）
      ├─ MotherboardLightProtocol.cs  主板灯光芯片协议：编号、灯区和通道、颜色包、灯效包（全项目唯一写这些的地方）
      ├─ MotherboardLight.cs   主板连接：读颜色顺序、关自带灯效、发颜色、断线重连
      ├─ MotherboardLightDiagnostics.cs  主板灯光芯片诊断（board 命令）
      ├─ OpenRgbProcess.cs     检查 OpenRGB 是否在运行
      ├─ NvApi.cs              NVIDIA 驱动（nvapi64.dll）：找显卡、读编号、读写显卡 I2C
      ├─ GpuLightProtocol.cs   显卡灯光芯片协议：认哪张卡、芯片位置、寄存器、颜色格式（全项目唯一写这些的地方）
      ├─ GpuLight.cs           显卡灯连接：认芯片检查、读写寄存器、直接控制模式、发颜色
      └─ GpuLightDiagnostics.cs  显卡灯光诊断（gpu 命令）
```

## 编码规范

- `.cs`、`.csproj`、`.sln`、`.md`：UTF-8 带 BOM + CRLF。由 `.editorconfig` 和 `.gitattributes` 保证。
- `.bat`：纯英文 + CRLF。
- 注释和说明用中文；必须写英文的地方附中文翻译。
- 编译警告一律当成错误（`TreatWarningsAsErrors`）。
- 只有截图读像素那一处用了 unsafe（直接读 Windows 分配的内存，不复制），其他地方不用。

## 自动测试

`tests/HyperLight.Tests` 是自动测试（xUnit），检查颜色过滤、取色、增强颜色、加大差异、灯效图案、设置文件读写，以及麦克风、主板、显卡的数据格式和安全检查（显卡型号白名单、认芯片检查、写入不越过颜色区等）。
**不需要连接任何硬件，也不需要 Windows**：它直接编译主程序里的纯逻辑源文件，测的就是主程序用的同一份代码。改完代码后运行：

```
dotnet test tests/HyperLight.Tests
```

## 编译

需要微软官方的 .NET 8 SDK（Windows 上装的就是，带 WinForms 组件；Ubuntu 软件源里的 .NET SDK 缺这个组件，编译不了托盘部分）。
打包成"自带运行库"的单个 exe（对方电脑不用装 .NET）。打包设置写在 `HyperLight.csproj` 里（不压缩，省运行时内存）：

```
dotnet publish src/HyperLight -c Release -o publish
```
