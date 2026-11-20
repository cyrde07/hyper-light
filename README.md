# hyper-light

把屏幕颜色同步到 HyperX QuadCast S 麦克风的 RGB 灯，让麦克风和 Wallpaper Engine 控制的 Razer 设备（键盘、鼠标垫、音响）颜色保持一致。

- 平台：Windows 10 / 11（64 位）
- 语言：C#（.NET 8）
- 设备通信：[HidSharp](https://www.nuget.org/packages/HidSharp)（Apache-2.0 许可）
- 托盘界面、截图存图：Windows 自带的 WinForms 和 System.Drawing（.NET 官方组件）
- 协议参考：[QuadcastRGB](https://github.com/Ors1mer/QuadcastRGB)（GPL-2.0 许可）。只参考协议格式和设备编号这类事实，没有使用它的代码。
- 不修改 Razer、Wallpaper Engine 的任何文件，不做 DLL 劫持。

## 进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| M0 | 列出 HID 设备，确认 QuadCast S 的 VID / PID | 完成：03F0:028C，接口 0 是灯光控制节点 |
| M1 | 发一个固定颜色给麦克风，确认能控制 | 完成：每秒 30 帧稳定；停止发送后麦克风立刻恢复自带灯效 |
| M2 | 截 Wallpaper Engine 的壁纸窗口取色，实时发给麦克风 | 完成：开着软件也和键盘一致；CPU 0.1%、内存约 86 MB |
| M3 | 调色：提高饱和度接近键盘颜色；上下两个灯区分别取色 | 完成 |
| M4 | 托盘程序：开机自启（和其他设备一起亮）、暂停 / 恢复、退出时灯恢复正常 | 完成（2026-11-20 验收通过） |

## 使用

### 平时使用：托盘程序（M4）

双击 `HyperLight.exe`，程序会在屏幕右下角的托盘区运行，**不会弹出窗口**。

- **托盘图标**：一个小圆点，上半部分是上灯区的颜色，下半部分是下灯区的颜色；暂停时是灰色；还没有颜色（比如没找到壁纸）时是空心圆。
- **右键菜单**：

| 菜单项 | 作用 |
|---|---|
| 第一行（灰色） | 当前状态，比如"同步中：上 001E6C / 下 1A2B3C" |
| 暂停同步 / 恢复同步 | 暂停时不截图、不发送，麦克风回到自带灯效 |
| 增强颜色（模仿 Wallpaper Engine） | 打开 / 关闭饱和度增强（M3），会保存到设置文件 |
| 上下灯区分开取色 | 打开 / 关闭上下分区（M3），会保存到设置文件 |
| 开机自启 | 勾选后，以后开机自动在托盘运行（写入当前用户的启动项，不需要管理员权限）。只有你勾选才会打开 |
| 打开设置文件 | 用记事本打开 `hyperlight.json` |
| 重新读取设置 | 改完设置文件后点这里生效 |
| 退出 | 停止同步，麦克风马上回到自带灯效 |

- **防止重复运行**：同一时间只允许一个 HyperLight 控制麦克风。托盘程序在运行时，再双击 exe 只会提示"已经在运行"；`sync`、`color` 命令也会提示先退出托盘。
- **exe 换了位置**（比如解压了新版本）：打开过开机自启的话，新位置的程序启动时会自动把启动项改成新位置。
- **桌面快捷方式**：右键 `HyperLight.exe` →（Windows 11 先点「显示更多选项」）「发送到」→「桌面快捷方式」。
- **建议放在固定位置**：比如 `D:\Tools\HyperLight`，不要放在「下载」文件夹里，免得被误删后开机自启找不到程序。

### 诊断工具

带命令运行时是诊断工具。因为 `HyperLight.exe` 是窗口程序，请通过 `.bat` 文件运行（`.bat` 要和 exe 放在同一个文件夹）。
直接在命令行里敲命令也可以，但命令行不会等程序结束，输出会和提示符混在一起，这时可以用 `start /wait HyperLight.exe list`。

### 设备诊断（M0）

1. 运行 `manual-tests/m0/list.bat`（等于执行 `HyperLight.exe list`）。
2. 窗口里会列出所有 HID 设备，QuadCast S 候选用 ★ 标出，灯光控制节点后面有"← 灯光控制节点"。
3. 同样的内容会存成 exe 旁边的 `hid-list-日期-时间.txt`。

诊断只读取信息，不向麦克风发送数据。

### 固定颜色测试（M1）

```
HyperLight.exe color <上灯区颜色> [下灯区颜色] [--interval 毫秒] [--seconds 秒数]
```

`manual-tests/m1/` 里有 4 个测试脚本，打包时和 exe 放在一起，双击即可：

| 脚本 | 内容 |
|---|---|
| `test1-red.bat` | 纯红 15 秒。停止后观察灯怎么变化 |
| `test2-upper-red-lower-blue.bat` | 上灯区红、下灯区蓝 15 秒 |
| `test3-30fps.bat` | 纯绿，每秒约 30 帧（间隔 33 毫秒），15 秒 |
| `test4-unplug.bat` | 紫色 60 秒。中途拔掉麦克风再插回去，看能否自动恢复 |

脚本里只写英文：Windows 命令行默认按 GBK 读取 .bat，写 UTF-8 中文可能导致命令错乱。

### 壁纸同步（M2，命令行版）

```
HyperLight.exe sync
```

和托盘程序用的是同一个同步引擎，只是把状态显示在命令行窗口里（颜色色块、截图耗时、帧率、CPU、内存），方便排查问题。
按 Ctrl+C 停止。测试脚本：`manual-tests/m2/sync.bat`。

- **为什么截壁纸窗口，不截整个屏幕**：实测 Wallpaper Engine 给 Razer 设备的颜色取自壁纸本身，开着软件也不变；截整个屏幕会截到软件。
- **怎么截**：用 PrintWindow（完整内容模式）截壁纸的宿主窗口，壁纸被窗口挡住也能截到。会带上桌面图标，实测对颜色影响很小。
- **两个线程**：截图线程每 0.5 秒截一次；发送线程每 33 毫秒发一次。麦克风一停止接收就恢复自带灯效，所以发送不能被截图卡住。
- **不闪眼睛**：颜色变化小于门槛就当没变；真的变了就渐变过去。
- **增强颜色（M3）**：平均色通常偏灰，按 Wallpaper Engine 的做法提高饱和度（色调和亮度不变）。
- **上下分区（M3）**：上灯区取壁纸上半部分，下灯区取下半部分。
- **找不到壁纸**（Wallpaper Engine 没开）：3 秒后停止发送，麦克风回到自带灯效；Wallpaper Engine 再打开，自动恢复。

设置保存在 exe 旁边的 `hyperlight.json`，第一次运行时自动生成，每一项上面都有中文说明。改完重新启动程序生效。

| 配置项 | 默认值 | 含义 |
|---|---|---|
| `captureIntervalMs` | 500 | 截图间隔（毫秒） |
| `sendIntervalMs` | 33 | 发送间隔（毫秒），范围 20～100 |
| `changeThreshold` | 12 | 颜色变化门槛（0～255），越大越不容易变 |
| `fadeMs` | 400 | 渐变时间（毫秒），0 表示立刻切换 |
| `wallpaperLostGraceMs` | 3000 | 找不到壁纸多久后停止发送（毫秒） |
| `boostColors` | true | 增强颜色：提高饱和度，模仿 Wallpaper Engine 的「增强 LED 颜色」 |
| `saturationBoost` | 1.5 | 增强倍数，范围 1.0～3.0，越大越鲜艳 |
| `splitZones` | true | 上下灯区分开取色：上灯区取壁纸上半部分，下灯区取下半部分 |

（配置项的默认值和说明以 `src/HyperLight/Config/AppConfig.cs` 为准。）

### 壁纸窗口截图测试（方案 A）

`manual-tests/m2-probe/probe-wallpaper.bat`（对应 `HyperLight.exe probe`）：找到 Wallpaper Engine 画壁纸的窗口，用几种方法各截两次，
截图和报告存到 exe 旁边的 `probe-日期-时间` 文件夹。只读，不碰 Wallpaper Engine 的文件、进程和设置。

## 换电脑 / 重新安装

需要：Windows 10 / 11（64 位）、HyperX QuadCast S、Wallpaper Engine。不用装 .NET，也不用装 iCUE 等其他软件。

1. 把 `HyperLight` 文件夹拷过去（或者从仓库的 Releases 下载解压）。改过设置的话，`hyperlight.json` 要一起拷。
2. 双击 `HyperLight.exe`。
3. 托盘菜单里重新勾一次「开机自启」（开机自启是记在每台电脑上的）。

找不到麦克风时，运行 `list.bat` 看设备编号。新批次的麦克风如果编号不在 `Device/QuadcastDeviceIds.cs` 的表里，加一行就行。
多显示器没有测试过，程序默认取主显示器的壁纸。

编译出来的 exe 不放进 git（太大）。需要的话可以自己重新编译（见下面的「编译」），或者把打包好的 zip 放进 GitHub 的 Releases。

## 注意

- 发颜色之前必须完全退出 HyperX NGENUITY（两个程序会同时发数据，灯会乱闪）。程序检测到它在运行时会提示。

## 目录结构

```
hyper-light/
├─ HyperLight.sln
├─ manual-tests/        手动测试脚本（m0：设备诊断；m1：固定颜色；m2：壁纸同步；m2-probe：壁纸窗口截图测试）
└─ src/HyperLight/
   ├─ Program.cs        入口：不带参数启动托盘；带命令时是诊断工具
   ├─ SyncEngine.cs     同步引擎：截图线程 + 发送线程（托盘和 sync 命令共用）
   ├─ MicOwnerLock.cs   防止多个 HyperLight 同时控制麦克风
   ├─ Rgb.cs            颜色类型（各模块共用）
   ├─ Tray/             托盘程序（M4）
   │  ├─ TrayApp.cs            托盘图标、右键菜单、暂停 / 退出
   │  ├─ TrayIconPainter.cs    画托盘图标（上下两半显示两个灯区的颜色）
   │  └─ AutoStart.cs          开机自启（当前用户的启动项）
   ├─ Commands/         命令
   │  ├─ ColorCommand.cs       color 命令（M1 固定颜色测试）
   │  ├─ SyncCommand.cs        sync 命令（M2 壁纸同步）
   │  └─ ProbeCommand.cs       probe 命令（方案 A 测试）
   ├─ Config/
   │  └─ AppConfig.cs          配置项、默认值、范围、说明（全项目唯一定义配置的地方）
   ├─ ColorProcessing/  颜色处理
   │  ├─ ZoneColors.cs         上下两个灯区的颜色
   │  ├─ ColorAverager.cs      算平均色（隔几个像素取一个点；可以分上下两半）
   │  ├─ ColorEnhancer.cs      增强颜色（提高饱和度）
   │  └─ ColorChangeFilter.cs  变化门槛 + 渐变
   ├─ Capture/          截图
   │  ├─ NativeMethods.cs      Windows 系统函数声明
   │  ├─ WallpaperWindowFinder.cs  找壁纸窗口（全项目唯一写"哪个窗口是壁纸"的地方）
   │  ├─ WallpaperCapturer.cs  截壁纸（内存反复使用）
   │  └─ WallpaperWindowProbe.cs  方案 A 测试：找壁纸窗口、几种方法截图、自动判断
   └─ Device/           设备通信
      ├─ UsbId.cs              USB 设备编号（VID:PID）
      ├─ QuadcastDeviceIds.cs  麦克风编号表（全项目唯一写设备编号的地方）
      ├─ QuadcastProtocol.cs   灯光协议：拼数据包（全项目唯一写字节格式的地方）
      ├─ QuadcastMic.cs        灯光连接：找节点、打开、发颜色、断线重连
      ├─ NgenuityProcess.cs    检查 NGENUITY 是否在运行（全项目唯一写进程名的地方）
      └─ HidDiagnostics.cs     设备诊断（M0）
```


## 编码规范

- `.cs`、`.csproj`、`.sln`、`.md`：UTF-8 带 BOM + CRLF。由 `.editorconfig` 和 `.gitattributes` 保证。
- `.bat`：纯英文 + CRLF。
- 注释和说明用中文；必须写英文的地方附中文翻译。
- 编译警告一律当成错误（`TreatWarningsAsErrors`）。
- 只有截图读像素那一处用了 unsafe（直接读 Windows 分配的内存，不复制），其他地方不用。

## 编译

需要微软官方的 .NET 8 SDK（Windows 上装的就是，带 WinForms 组件；Ubuntu 软件源里的 .NET SDK 缺这个组件，编译不了托盘部分）。
打包成"自带运行库"的单个 exe（对方电脑不用装 .NET）：

```
dotnet publish src/HyperLight -c Release --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```
