# hyper-light

把屏幕颜色同步到 HyperX QuadCast S 麦克风的 RGB 灯，让麦克风和 Wallpaper Engine 控制的 Razer 设备（键盘、鼠标垫、音响）颜色保持一致。

- 平台：Windows 10 / 11（64 位）
- 语言：C#（.NET 8）
- 设备通信：[HidSharp](https://www.nuget.org/packages/HidSharp)（Apache-2.0 许可）
- 截图和存图：[System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common)（微软官方，MIT 许可）
- 协议参考：[QuadcastRGB](https://github.com/Ors1mer/QuadcastRGB)（GPL-2.0 许可）。只参考协议格式和设备编号这类事实，没有使用它的代码。
- 不修改 Razer、Wallpaper Engine 的任何文件，不做 DLL 劫持。

## 进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| M0 | 列出 HID 设备，确认 QuadCast S 的 VID / PID | 完成：03F0:028C，接口 0 是灯光控制节点 |
| M1 | 发一个固定颜色给麦克风，确认能控制 | 完成：每秒 30 帧稳定；停止发送后麦克风立刻恢复自带灯效 |
| M2 | 截 Wallpaper Engine 的壁纸窗口取色，实时发给麦克风 | 进行中：已写好 `sync` 命令，等待验收 |
| M3 | 调色：提高饱和度接近键盘颜色；上下两个灯区分别取色 | 未开始 |
| M4 | 托盘程序：开机自启（和其他设备一起亮）、暂停 / 恢复、退出时灯恢复正常 | 未开始 |

## 使用

### 设备诊断（M0）

1. 双击 `HyperLight.exe`（等于执行 `HyperLight.exe list`）。
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

### 壁纸同步（M2）

```
HyperLight.exe sync
```

截 Wallpaper Engine 的壁纸取平均色，持续发给麦克风，按 Ctrl+C 停止。测试脚本：`manual-tests/m2/sync.bat`。

- **为什么截壁纸窗口，不截整个屏幕**：实测 Wallpaper Engine 给 Razer 设备的颜色取自壁纸本身，开着软件也不变；截整个屏幕会截到软件。
- **怎么截**：用 PrintWindow（完整内容模式）截壁纸的宿主窗口，壁纸被窗口挡住也能截到。会带上桌面图标，实测对颜色影响很小。
- **两个线程**：截图线程每 0.5 秒截一次；发送线程每 33 毫秒发一次。麦克风一停止接收就恢复自带灯效，所以发送不能被截图卡住。
- **不闪眼睛**：颜色变化小于门槛就当没变；真的变了就渐变过去。
- **找不到壁纸**（Wallpaper Engine 没开）：3 秒后停止发送，麦克风回到自带灯效；Wallpaper Engine 再打开，自动恢复。

设置保存在 exe 旁边的 `hyperlight.json`，第一次运行时自动生成，每一项上面都有中文说明。改完重新启动程序生效。

| 配置项 | 默认值 | 含义 |
|---|---|---|
| `captureIntervalMs` | 500 | 截图间隔（毫秒） |
| `sendIntervalMs` | 33 | 发送间隔（毫秒），范围 20～100 |
| `changeThreshold` | 12 | 颜色变化门槛（0～255），越大越不容易变 |
| `fadeMs` | 400 | 渐变时间（毫秒），0 表示立刻切换 |
| `wallpaperLostGraceMs` | 3000 | 找不到壁纸多久后停止发送（毫秒） |

（配置项的默认值和说明以 `src/HyperLight/Config/AppConfig.cs` 为准。）

### 壁纸窗口截图测试（方案 A）

`manual-tests/m2-probe/probe-wallpaper.bat`（对应 `HyperLight.exe probe`）：找到 Wallpaper Engine 画壁纸的窗口，用几种方法各截两次，
截图和报告存到 exe 旁边的 `probe-日期-时间` 文件夹。只读，不碰 Wallpaper Engine 的文件、进程和设置。

## 注意

- 发颜色之前必须完全退出 HyperX NGENUITY（两个程序会同时发数据，灯会乱闪）。程序检测到它在运行时会提示。

## 目录结构

```
hyper-light/
├─ HyperLight.sln
├─ manual-tests/        手动测试脚本（m1：固定颜色；m2：壁纸同步；m2-probe：壁纸窗口截图测试）
└─ src/HyperLight/
   ├─ Program.cs        入口：按命令行参数分发命令
   ├─ SyncEngine.cs     同步引擎：截图线程 + 发送线程（M4 托盘也用它）
   ├─ Rgb.cs            颜色类型（各模块共用）
   ├─ Commands/         命令
   │  ├─ ColorCommand.cs       color 命令（M1 固定颜色测试）
   │  ├─ SyncCommand.cs        sync 命令（M2 壁纸同步）
   │  └─ ProbeCommand.cs       probe 命令（方案 A 测试）
   ├─ Config/
   │  └─ AppConfig.cs          配置项、默认值、范围、说明（全项目唯一定义配置的地方）
   ├─ ColorProcessing/  颜色处理
   │  ├─ ColorAverager.cs      算平均色（隔几个像素取一个点）
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

M4 会加 `Tray/`（托盘）。

## 编码规范

- `.cs`、`.csproj`、`.sln`、`.md`：UTF-8 带 BOM + CRLF。由 `.editorconfig` 和 `.gitattributes` 保证。
- `.bat`：纯英文 + CRLF。
- 注释和说明用中文；必须写英文的地方附中文翻译。
- 编译警告一律当成错误（`TreatWarningsAsErrors`）。
- 只有截图读像素那一处用了 unsafe（直接读 Windows 分配的内存，不复制），其他地方不用。

## 编译

需要 .NET 8 SDK。打包成"自带运行库"的单个 exe（对方电脑不用装 .NET）：

```
dotnet publish src/HyperLight -c Release --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```
