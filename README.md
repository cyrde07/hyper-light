# hyper-light

把屏幕颜色同步到 HyperX QuadCast S 麦克风的 RGB 灯，让麦克风和 Wallpaper Engine 控制的 Razer 设备（键盘、鼠标垫、音响）颜色保持一致。

- 平台：Windows 10 / 11（64 位）
- 语言：C#（.NET 8）
- 设备通信：[HidSharp](https://www.nuget.org/packages/HidSharp)（Apache-2.0 许可）
- 协议参考：[QuadcastRGB](https://github.com/Ors1mer/QuadcastRGB)（GPL-2.0 许可）。只参考协议格式和设备编号这类事实，没有使用它的代码。
- 不修改 Razer、Wallpaper Engine 的任何文件，不做 DLL 劫持。

## 进度

| 阶段 | 内容 | 状态 |
|---|---|---|
| M0 | 列出 HID 设备，确认 QuadCast S 的 VID / PID | 进行中 |
| M1 | 发一个固定颜色给麦克风，确认能控制 | 未开始 |
| M2 | 截取 PG32UCDM 屏幕，算主色调，实时发给麦克风 | 未开始 |
| M3 | 调色：提高饱和度接近键盘颜色；上下两个灯区分别取色 | 未开始 |
| M4 | 托盘程序：开机自启（和其他设备一起亮）、暂停 / 恢复、退出时灯恢复正常 | 未开始 |

## 使用（M0）

1. 双击 `HyperLight.exe`（等于执行 `HyperLight.exe list`）。
2. 窗口里会列出所有 HID 设备，QuadCast S 候选用 ★ 标出。
3. 同样的内容会存成 exe 旁边的 `hid-list-日期-时间.txt`。

M0 只做诊断：不向麦克风发送任何数据，麦克风不会变色。

## 注意

- 从 M1 开始，运行本工具前必须完全退出 HyperX NGENUITY（两个程序会抢麦克风）。

## 目录结构

```
hyper-light/
├─ HyperLight.sln
└─ src/HyperLight/
   ├─ Program.cs        入口：按命令行参数分发命令
   └─ Device/           设备通信
      ├─ UsbId.cs              USB 设备编号（VID:PID）
      ├─ QuadcastDeviceIds.cs  麦克风编号表（全项目唯一写设备编号的地方）
      └─ HidDiagnostics.cs     M0 设备诊断
```

以后的阶段会加 `Capture/`（截屏）、`ColorProcessing/`（颜色处理）、`Config/`（配置）、`Tray/`（托盘）。

## 编码规范

- `.cs`、`.csproj`、`.sln`、`.md`：UTF-8 带 BOM + CRLF。由 `.editorconfig` 和 `.gitattributes` 保证。
- 注释和说明用中文；必须写英文的地方附中文翻译。
- 编译警告一律当成错误（`TreatWarningsAsErrors`）。

## 编译

需要 .NET 8 SDK。打包成"自带运行库"的单个 exe（对方电脑不用装 .NET）：

```
dotnet publish src/HyperLight -c Release --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```
