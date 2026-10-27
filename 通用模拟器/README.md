# 通用模拟器

基于 WPF 的 **Modbus 从站（Slave）模拟器**。用「网络 → 站点 → 数据规则」三层结构描述一台或多台设备，按设定周期自动向寄存器写入随机数据，供上位机 / 主站（Master）连接调试；也提供手动写入窗口，用于精确写入指定地址。

典型用途：没有真实 PLC / 仪表时，用它冒充从站，验证主站的采集逻辑、字节序与地址映射是否正确。

---

## 目录

- [功能特性](#功能特性)
- [技术栈](#技术栈)
- [快速开始](#快速开始)
- [界面与操作](#界面与操作)
- [数据模型](#数据模型)
- [运行机制](#运行机制)
- [错误提示与保护](#错误提示与保护)
- [版本号规则](#版本号规则)
- [持续集成 / 自动发布](#持续集成--自动发布)
- [目录结构](#目录结构)
- [已知限制](#已知限制)
- [许可证](#许可证)

---

## 功能特性

**模拟能力**

- 三种链路，可多网络并行（每个网络一个独立后台任务）：
  - `ModbusRtu` — 串口从站
  - `ModbusTcp` — TCP 服务端
  - `ModbusRtuOverTcp` — TCP 客户端 + RTU 协议（对接串口服务器 / 透传网关）
- 每个网络下多个站点，按站号 1~247 注册
- 每个站点下多条数据规则：寄存器类型（只读 / 读写）× 数据类型（`Bool / Int16 / UInt16 / Int32 / UInt32 / Float / String`）× 起始地址 × 数量
- 按周期自动写入随机数据；网络 / 站点 / 规则三级都有心跳计数（Tick），一眼看出是否还在刷
- 站点级字节序（`ABCD / BADC / CDAB / DCBA`，默认 `CDAB`），修改后即时生效

**工程能力**

- 配置自动持久化到程序目录的 `Setting.json`，退出保存、启动加载；支持**导入 / 导出**参数文件
- 启动前校验配置（无效站号、重复站号、端口 / 串口冲突），有问题直接拦截
- 运行中锁定编辑（`CanEdit`），停止时等待所有网络任务真正退出后释放资源，避免新旧网络抢同一端口
- 运行期错误不弹窗刷屏，改为列表项红点 + 状态栏文本，故障排除后自动消失
- 现代化无边框 UI：自绘标题栏、卡片布局、统一主题资源；串口号下拉展开时重新枚举（支持热插拔）

---

## 技术栈

| 项目 | 说明 |
| --- | --- |
| 框架 | .NET 10（`net10.0-windows`）、WPF |
| 语言 | C#（`Nullable` / `ImplicitUsings` 已启用） |
| MVVM | CommunityToolkit.Mvvm 8.4.2 —— `ObservableObject`、`[ObservableProperty]`、`[RelayCommand]` |
| Modbus | NModbus.Extensions 0.2.0 —— `Tools.Create*SlaveNetwork` 创建网络，`Write*R / Write*RW` 扩展写数据，字节序扩展 `SetEndian` |
| 序列化 | Newtonsoft.Json 13.0.4 |

---

## 快速开始

**环境**：Windows（WPF + `System.IO.Ports`）、.NET 10 SDK。

```bash
cd 通用模拟器
dotnet build        # 编译（会自动生成版本号，见“版本号规则”）
dotnet run          # 运行
```

发布单目录产物：

```bash
dotnet publish 通用模拟器.csproj -c Release -o publish
```

> Modbus TCP 常用 502 端口；Windows 下监听 502 可能需要管理员权限，或已被其它服务占用，建议先用 5020 之类端口测试。

**三步跑起来**：左侧 `+` 新增网络 → 选协议（如 `ModbusTcp`，端口 5020）→ 站点列表 `+` 新增站点（站号 1）→ 规则列表 `+` 新增规则（起始地址 0，数量 10）→ 底部 **启动**。主站连 `127.0.0.1:5020` 读保持寄存器即可。

---

## 界面与操作

```
┌──────────────┬───────────────────────────────────────────┐
│ 网络列表      │ 网络配置（名称 / 协议 / 地址端口或串口参数） │
│  [导入][导出] │ 站点列表  │ 站点配置 + 数据规则列表 + 规则配置│
│  [+] [−]     │           │                                 │
├──────────────┴───────────┴───────────────────────────────┤
│ ● 运行中 / 已停止   网络数：N   [错误文本]     [启动][停止] │
└──────────────────────────────────────────────────────────┘
```

### 网络（左栏 + 「网络配置」卡片）

| 项 | 说明 |
| --- | --- |
| 启用 | 停用后该网络不再刷新，也不创建链路 |
| 名称 / 备注 | 自定义标识 |
| 协议类型 | `ModbusRtu` / `ModbusTcp` / `ModbusRtuOverTcp` |
| IP 地址 / 端口 | 网口模式的参数（当前版本的生效情况见[已知限制](#已知限制)） |
| 串口号 / 波特率 / 数据位 / 校验位 / 停止位 | 串口模式的参数，串口号下拉展开时重新枚举 |
| 刷新间隔(ms) | 自动写入周期，默认 500ms |
| 站点数量 | 只读统计 |

列表项副标题形如 `ModbusTcp (5020)`、`ModbusRtu (COM1)`；项尾徽章是本网络的心跳计数；出错时名称后会出现红点（悬停看原因）。

### 站点（「站点列表」）

- **站号**：1~247，新增时自动取当前未占用的最小值
- **字节序**：与主站保持一致，默认 `CDAB`
- **间隔时间(ms)**：站点级节流阈值，累计到该值才写入一次（不会快于网络的刷新间隔）
- 列表项显示启用勾选、名称、站号、心跳计数、规则数量

### 数据规则（「数据规则」）

- **寄存器类型**：`ReadOnly` → 输入寄存器 / 离散输入（主站只读）；`ReadWrite` → 保持寄存器 / 线圈
- **数据类型 / 起始地址 / 数量**：数值类型 0~100 随机，`Bool` 随机真假，`String` 随机 A~Z（字符数 = 数量）
- 列表项显示数量徽章与写入次数（Tick）

### 导入 / 导出参数

网络列表标题栏的 **导入** / **导出**：

- **导入**：打开文件对话框 → 读取 JSON → 反序列化 → **整体替换**当前配置（旧对象先 `Dispose()` 释放资源）。**仅停止状态可用**，运行中按钮自动禁用
- **导出**：保存文件对话框 → 把当前配置序列化为格式化 JSON。运行 / 停止状态均可

两者与启动自动加载、退出自动保存使用同一套 JSON 结构，可互相通用；失败会弹窗提示原因，不会中断程序。

### 启动 / 停止

底部状态栏的 **启动** / **停止**：

- 启动前做一次配置校验；通过后每个网络各起一个后台任务
- 运行中配置项置灰（不可编辑），状态灯呼吸闪烁
- 停止时取消令牌，并**等待所有网络任务真正退出**后才允许再次启动，避免新旧网络抢同一端口；随后释放链路、清空站点从站引用、恢复编辑

### 手动写入

在站点列表中**双击站点**打开「写入寄存器」窗口：

- **套用规则**：下拉选择该站点的规则，自动填充寄存器类型、数据类型、起始地址；仅当发送内容为空时按数量预填 `0,0,...`（不会覆盖已输入内容）
- **发送内容**：多个值用 `,` 或 `;` 分隔，如 `1,2,3`、`true,false`；首尾多余的 `[]`、`,`、`;` 会自动去除
- 起始地址非整数、字符串内容为空等输入问题会直接提示，不会静默失败

---

## 数据模型

```
NetworkConfigModel          一个网络（一条链路）
├─ StationInformationModel  一个从站（站号）
│  └─ RulerModel            一条数据规则（地址区）
```

| 模型 | 主要属性 |
| --- | --- |
| `NetworkConfigModel` | `Name`、`ProtocolType`、`IP`、`Port`、`PortName`、`BaudRate`、`DataBits`、`Parity`、`StopBits`、`Interval`、`IsEnable`、`Tick`、`StationList` |
| `StationInformationModel` | `Name`、`StationNo`、`Endian`、`Interval`、`IsEnable`、`Tick`、`RulerList` |
| `RulerModel` | `Name`、`RegisterType`、`DataType`、`StartAddress`、`Length`、`IsEnable`、`Tick` |

`Setting.json` 示例（数组，元素即网络）：

```json
[
  {
    "IsEnable": true,
    "Name": "ModbusTCP",
    "ProtocolType": 1,
    "IP": "127.0.0.1",
    "Port": 5020,
    "Interval": 500,
    "StationList": [
      {
        "IsEnable": true,
        "Name": "站点1",
        "StationNo": 1,
        "Endian": 1,
        "Interval": 500,
        "RulerList": [
          {
            "IsEnable": true,
            "Name": "规则1",
            "RegisterType": 1,
            "DataType": 2,
            "StartAddress": 0,
            "Length": 10
          }
        ]
      }
    ]
  }
]
```

> - 枚举按序号序列化：`ProtocolType` `0=Rtu`、`1=Tcp`、`2=RtuOverTcp`；`RegisterType` `0=ReadOnly`、`1=ReadWrite`；`DataType` `0=Bool`…`6=String`；`Endian` `0=DCBA`、`1=CDAB`、`2=BADC`、`3=ABCD`
> - `SlaveNetwork`、`Slave`、`Tick`、`CanEdit`、`LastError` 等运行时状态均标了 `[JsonIgnore]`，不写入文件
> - 文件位于**程序所在目录**（`AppContext.BaseDirectory`），不随工作目录变化

---

## 运行机制

`NetworkConfigModel.DoWork(CancellationToken)` 是核心循环（每个网络一个 Task）：

```
while (未取消 && 未释放)
    await Task.Delay(网络 Interval)
    若未启用 → continue
    CanEdit = false
    network = GetOrCreateNetwork()        # 锁外创建；失败后 2 秒退避重试
    若创建失败 → 记录错误 → continue
    foreach 启用的站点（快照遍历，避免集合被改）
        站号不在 1~247 / 与同网络其它站点重复 → 记录错误并跳过
        从站为空 → network.CreateSlave(站号) + SetEndian(字节序)
        Elapsed += 网络 Interval；未达到站点 Interval → 跳过本次写入（站点级节流）
        foreach 启用的规则
            参数校验失败（地址 / 数量越界）→ 记录错误并跳过
            写随机数据 + 规则 Tick++
        站点 Tick++
    网络 Tick++
    本周期无错误 → 清除上一次的（非网络级）错误提示
finally（取消 / 异常 / 释放，任一路径都会执行）
    标记 released，恢复 CanEdit，清空各站点 Slave / Elapsed，释放并置空 SlaveNetwork
```

几个关键设计：

- **创建在锁外**：链路创建（尤其 RTU over TCP 的 TCP 连接）可能耗时，放在 `lock` 外执行，避免卡住 UI 线程上的 `Dispose`；只在赋值瞬间加锁并做双检
- **失败退避**：创建失败后至少间隔 2 秒再试，不会按刷新间隔（最快 10ms）反复重试
- **停止是干净的**：停止 / 释放时先置 `released` 标记再 `Dispose`，监听任务因此产生的收尾异常被识别为预期行为，不再误报
- **停止后重建**：释放时把站点 `Slave` 置空，下次启动重新注册到新网络（否则主站连得上但读超时）
- 监听由 `Tools` 内部后台启动，主站连接即可读写

---

## 错误提示与保护

后台线程不弹 `MessageBox`（可能被主窗口遮挡，且会按刷新间隔刷屏），最后一次错误写入 `NetworkConfigModel.LastError`：

- 网络列表项显示**红点**（悬停看原因），状态栏同时显示当前选中网络的错误文本
- 相同错误不重复刷新；故障排除、下一周期成功执行后自动清除
- 监听线程的错误是「网络级」错误（sticky），保留到网络重建成功为止
- 启动前校验：无效站号、重复站号、多个启用网络占用同一端口 / 串口 —— 直接弹窗阻止启动
- 全局兜底：`App.xaml.cs` 接了 `DispatcherUnhandledException`（记录 + 提示后继续运行）、`TaskScheduler.UnobservedTaskException`、`AppDomain.UnhandledException`

常见错误：端口 / 串口被占用或不存在、站号越界或重复、规则地址越界、协议不支持。

---

## 版本号规则

每次生成自动计算（`通用模拟器.csproj` 中的 `ComputeVersion` 目标）：

```
主版本.次版本.自 2000-01-01 起的天数.当日 0 点起的秒数 / 2
1     .0     .9796                 .38108
```

除以 2 是因为 .NET 程序集版本每段上限 65535（一天 86400 秒，86400 / 2 = 43200 < 65535）。
要调整基线改 `VersionMajor` / `VersionMinor` / `VersionEpoch` 三个属性即可。

---

## 持续集成 / 自动发布

`.github/workflows/release.yml`：向 `main` / `master` **每次推送**自动执行

1. 安装 .NET 10 → 还原 → `dotnet publish -c Release` 到 `publish/`
2. 从产物 `通用模拟器.dll` 读 `FileVersion`（即上面的版本号）
3. 打包为 `通用模拟器-v<版本>.zip`（含 `README.md`）
4. `gh release create v<版本> <zip> --generate-notes`，发布说明自动生成

同一秒内的再次推送会自动追加 `.<运行号>` 保证标签唯一。使用内置 `GITHUB_TOKEN`，无需额外密钥；若仓库 Actions 权限为「只读」，需在 Settings → Actions → General → Workflow permissions 改为 **Read and write**。也可在 Actions 页面手动触发。

---

## 目录结构

```
通用模拟器.slnx
└─ 通用模拟器/
   ├─ App.xaml(.cs)             # 入口：合并主题资源 + 全局异常兜底
   ├─ MainWindow.xaml(.cs)      # 主窗口：无边框标题栏、三栏配置区、状态栏
   ├─ MainWindowViewModel.cs    # 网络列表、启停、导入导出、配置存取
   ├─ NetworkConfigModel.cs     # 三层模型 + 业务循环 DoWork
   ├─ Window1.xaml(.cs)         # 手动写入寄存器窗口
   ├─ Styles/
   │  ├─ Theme.xaml             # 配色 / 画笔 / 圆角 / 阴影等设计令牌
   │  └─ Controls.xaml          # 按钮 / 输入框 / 下拉框 / 列表等控件样式
   ├─ AssemblyInfo.cs           # ThemeInfo
   ├─ 通用模拟器.csproj          # 含自动版本号目标
   ├─ Setting.json              # 运行时生成的配置（已 gitignore）
   ├─ .gitignore
   └─ LICENSE                   # MIT
.github/workflows/release.yml   # 推送即构建并发 Release
```

---

## 已知限制

- **串口参数与 IP 地址当前未接入链路创建**：网络创建走 `Tools.Create*SlaveNetwork`，其中 RTU 固定 `115200 / 8 / N / 1`，TCP 监听所有网卡（`IPAddress.Any`），RTU over TCP 连接 `127.0.0.1`。UI 上这些项可编辑但暂不生效
- **站点刷新间隔不会快于网络刷新间隔**：`StationInformationModel.Interval` 是站点级节流阈值，最小粒度就是网络周期
- **随机数据范围固定**：数值为 0~100，`String` 生成 A~Z 随机字符
- **停用站点仍会响应主站**：`IsEnable=false` 只停止刷新，从站仍注册在网络上，主站可读到最后一次写入的值
- **端口冲突校验只在本程序内**：被其它程序（或本程序另一个实例）占用的端口只能在运行时报错
- **RTU over TCP 的连接是同步的**：远端不可达时会有等待，期间该网络周期被阻塞

---

## 许可证

[MIT](./LICENSE) © 2026 XiaPeng @ MicaApps
