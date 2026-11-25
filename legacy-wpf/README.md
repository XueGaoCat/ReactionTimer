# 反应力测试 — 源码复原

本目录是从 `D:\反应力测试.exe` 逆向还原出的完整 C# 源码。

## 程序信息

| 项目 | 值 |
| --- | --- |
| 程序集名 | 反应力测试（AssemblyName）/ `ReactionTimer`（命名空间） |
| 技术栈 | C# + WPF（界面 XAML 以资源形式内嵌、运行时加载） |
| 原始目标框架 | .NET Framework v4.0.30319 |
| 版本 | AssemblyVersion 1.0.0.0，FileVersion 1.1.0.0 |
| 体积 | 4.84 MB，其中 4.78 MB 是 `EasterEgg.png` |

## 复原完整度

程序集没有被混淆，元数据与 IL 全部保留，因此还原是接近无损的：类名、方法名、字段名、控件 `x:Name`、字符串常量、颜色值、样式模板都是原始名称。

反编译过程中唯一的损失是编译器生成的局部变量名（`flag`、`val`、`num2`、`application2` 之类）和注释。这些已按语义重新命名并补充注释，行为未改动。

原始工程共有 4 个类型：

```
ReactionTimer.Program            入口，加载内嵌 XAML 与图片
ReactionTimer.ReactionController 全部交互逻辑与界面状态机
ReactionTimer.ReactionSession    成绩模型（轮次、平均、最快、抢跑）
ReactionTimer.MonitorRefresh     读取显示器实际刷新率（CCD + EnumDisplaySettings）
```

## 目录结构

```
RatingTest/
├── ReactionTimer.csproj
├── ReactionTimer.xaml        界面（内嵌资源，非 Page）
├── ReactionTimer.ico         窗口图标
├── EasterEgg.png             彩蛋插画
├── app.ico                   程序图标（win32 资源）
├── app.manifest              DPI 感知与兼容性声明
├── Properties/
│   └── AssemblyInfo.cs
├── Program.cs
├── ReactionController.cs
├── ReactionSession.cs
└── MonitorRefresh.cs
```

## 构建

```bash
dotnet build -c Release
```

输出 `bin/Release/net48/反应力测试.exe`。项目里引用了 `Microsoft.NETFramework.ReferenceAssemblies`，因此没装 Visual Studio 也能构建 .NET Framework 目标。

## 改动说明

1. **目标框架改为 `net48`**。原始二进制编译于 .NET 4.0，但 SDK 风格工程 + `UseWPF` 对 net40 支持不完整。改用 net48 后代码不需要任何修改即可编译，运行行为一致。若要严格对齐原始框架，把 `TargetFramework` 改回 `net40` 并改用旧式 csproj 格式即可。
2. **`EnableDefaultPageItems` 设为 false**。`ReactionTimer.xaml` 是运行时用 `XamlReader.Load` 加载的内嵌资源，不带 `x:Class`。若按默认规则当成 `Page` 编译，会因缺少 `x:Class` 而构建失败。
3. 变量重命名与注释补充，语义逐行对齐原 IL。

## 注意

- `ReactionTimer.xaml` 的 `LogicalName` 必须保持 `ReactionTimer.xaml`，`Program.Resource()` 按这个名字查找资源。
- XAML 中所有 `x:Name` 都由 `ReactionController` 通过 `window.FindName` 取用，改名会直接抛空引用。
- 彩蛋触发条件是单轮反应时间 ≤ 150 ms（`ReactionSession.EasterEggThreshold`）。
