using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using NModbus;
using NModbus.Extensions;
using NModbus.IO;
using NModbus.Serial;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;

namespace 通用模拟器
{

    public enum EnumRegisterType
    {
        ReadOnly, ReadWrite
    }

    public enum EnumProtocolType
    {
        ModbusRtu,
        ModbusTcp,
        // 值 2 曾为同名的旧"外连模式"（TCP 客户端连 127.0.0.1，对接透传网关），已废弃；
        // 保留编号防止旧配置文件里的 2 被顶替而静默变行为
        /// <summary>RTU over TCP（监听模式）：本机端口监听，主站工具连入后线上跑 RTU 帧（NModbus 未内置，用 RtuOverTcpSlaveNetwork 补齐）</summary>
        ModbusRtuOverTcp = 3
    }

    public enum EnumDataType
    {
        Bool,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Float,
        String
    }

    public partial class NetworkConfigModel : ObservableObject, IDisposable
    {
        //静态缓存：下拉选项只读，避免 UI 每次重求值都分配数组
        static readonly int[] intervalValues = { 10, 20, 30, 50, 60, 80, 100, 150, 200, 300, 400, 500, 600, 800, 1000, 1500, 2000, 3000, 5000 };
        static readonly EnumProtocolType[] protocolTypeValues = Enum.GetValues<EnumProtocolType>();
        // 只读下拉选项不参与序列化：旧版导出文件含这些字段，反序列化会尝试向固定大小数组填充而抛异常
        [JsonIgnore] public int[] IntervalValues => intervalValues;
        [JsonIgnore] public EnumProtocolType[] ProtocolTypeValues => protocolTypeValues;
        /// <summary>串口号列表（支持运行期刷新，见 RefreshPortNames）</summary>
        [JsonIgnore] public ObservableCollection<string> PortNameValues { get; } = new ObservableCollection<string>(SerialPort.GetPortNames());

        /// <summary>重新枚举可用串口（热插拔后调用），保留仍存在且仍被选中的项</summary>
        public void RefreshPortNames()
        {
            var names = SerialPort.GetPortNames();
            // 先算待删除项快照再删，避免索引访问与集合修改交错导致越界
            foreach (var name in PortNameValues.Where(n => !names.Contains(n)).ToArray())
                PortNameValues.Remove(name);
            foreach (var name in names)
            {
                if (!PortNameValues.Contains(name)) PortNameValues.Add(name);
            }
        }
        static readonly int[] baudRateValues = { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
        static readonly int[] dataBitsValues = { 5, 6, 7, 8 };
        static readonly Parity[] parityValues = Enum.GetValues<Parity>();
        static readonly StopBits[] stopBitsValues = Enum.GetValues<StopBits>();
        [JsonIgnore] public int[] BaudRateValues => baudRateValues;
        [JsonIgnore] public int[] DataBitsValues => dataBitsValues;
        [JsonIgnore] public Parity[] ParityValues => parityValues;
        [JsonIgnore] public StopBits[] StopBitsValues => stopBitsValues;

        /// <summary>串口链路（ModbusRtu）：显示串口号、波特率、数据位、校验位、停止位</summary>
        [JsonIgnore]
        public bool IsSerialMode => ProtocolType == EnumProtocolType.ModbusRtu;

        /// <summary>网口链路（ModbusTcp / ModbusRtuOverTcp）：显示 IP 地址、端口</summary>
        [JsonIgnore]
        public bool IsTcpMode => !IsSerialMode;

        partial void OnProtocolTypeChanged(EnumProtocolType value)
        {
            OnPropertyChanged(nameof(IsSerialMode));
            OnPropertyChanged(nameof(IsTcpMode));
        }

        /// <summary>公开唯一标识（GUID）：供规则的跨网络输出目标引用，避免保存对象引用造成循环引用/孤儿对象</summary>
        [ObservableProperty] Guid id = Guid.NewGuid();
        [ObservableProperty] bool isEnable = true;
        [ObservableProperty] string name = "未命名";
        [ObservableProperty] string description = "";
        [ObservableProperty] EnumProtocolType protocolType = EnumProtocolType.ModbusRtu;
        [ObservableProperty] string iP = "127.0.0.1";
        [ObservableProperty] int port = 502;
        [ObservableProperty] string portName = "COM1";
        [ObservableProperty] int baudRate = 115200;
        [ObservableProperty] int dataBits = 8;
        [ObservableProperty] Parity parity = Parity.None;
        [ObservableProperty] StopBits stopBits = StopBits.One;
        [ObservableProperty] int interval = 500;//间隔时间
        [ObservableProperty] BindingList<StationInformationModel> stationList = new BindingList<StationInformationModel>();
        [property: JsonIgnore][ObservableProperty] bool canEdit = true;

        //写入次数：后台高频递增，改为不通知字段；由 UI 侧低频定时器统一刷新显示，避免跨线程通知风暴
        [JsonIgnore] int tick;
        [JsonIgnore] public int Tick => tick;
        /// <summary>后台递增计数（不触发 PropertyChanged）</summary>
        public void IncrementTick() => tick++;
        /// <summary>由 UI 定时器调用，通知界面刷新 Tick 显示</summary>
        public void NotifyTickChanged() => OnPropertyChanged(nameof(Tick));

        /// <summary>运行期最近一次错误信息（不弹窗，避免按刷新间隔刷屏）</summary>
        [property: JsonIgnore][ObservableProperty] string? lastError = null;

        /// <summary>保护 SlaveNetwork 的创建/释放，避免停止与启动并发时互相踩踏</summary>
        [JsonIgnore] readonly object networkLock = new object();
        /// <summary>创建失败后的重试时间点（退避），避免按刷新间隔反复尝试</summary>
        [JsonIgnore] DateTime nextRetryTime = DateTime.MinValue;
        /// <summary>从站监听是否存活；onError 触发后置 false，由 GetOrCreateNetwork 检测并重建</summary>
        [JsonIgnore] volatile bool listenerAlive = true;
        /// <summary>LastError 是否为“网络级”错误（不会被下一周期自动清除）</summary>
        bool lastErrorSticky;
        /// <summary>复用的站号计数表（单网络循环内独占，Clear 后重用，避免每周期分配）</summary>
        [JsonIgnore] readonly Dictionary<int, int> stationNoCount = new();

        [property: JsonIgnore][ObservableProperty] StationInformationModel? selectedStation;

        public NetworkConfigModel()
        {
            HookStationList();
        }

        // BindingList 增删项不会触发 Count 相关绑定刷新，这里手动通知；
        // 反序列化可能替换集合实例，故在 setter 中也重新挂接；用同一委托先 -= 再 += 防止重复挂接
        ListChangedEventHandler? stationListHandler;

        void HookStationList()
        {
            stationListHandler ??= (_, __) =>
            {
                OnPropertyChanged(nameof(StationList));
                StationsChanged?.Invoke(this, EventArgs.Empty);
            };
            StationList.ListChanged -= stationListHandler;
            StationList.ListChanged += stationListHandler;
        }

        /// <summary>站点集合发生增删时通知外部（如规则输出目标下拉列表）刷新</summary>
        public static event EventHandler? StationsChanged;

        internal static void NotifyStationsChanged(object sender) => StationsChanged?.Invoke(sender, EventArgs.Empty);

        partial void OnNameChanged(string value) => NotifyStationsChanged(this);

        partial void OnStationListChanged(BindingList<StationInformationModel> value) => HookStationList();

        [JsonIgnore] public IModbusSlaveNetwork? SlaveNetwork;
        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void AddStation()
        {
            StationInformationModel station = new StationInformationModel();
            // 取当前未被占用的最小站号，避免删除后再新增产生重复站号
            int no = 1;
            while (no < 247 && StationList.Any(s => s.StationNo == no)) no++;
            station.StationNo = no;
            StationList.Add(station);
            SelectedStation = station;  // 新增后自动选中，右侧直接可编辑
        }

        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void RemoveStation(StationInformationModel? station)
        {
            if (station is null) return;
            if (StationList.Contains(station)) StationList.Remove(station);
        }

        /// <summary>复制并粘贴站点：深拷贝选中站点（含规则）并插入其后，重新分配未占用站号，仅停止状态可用</summary>
        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void DuplicateStation(StationInformationModel? station)
        {
            if (station is null) return;
            var copy = ModelCloner.Clone(station);
            copy.Name = ModelCloner.WithCopySuffix(copy.Name);

            int no = Enumerable.Range(1, 247).FirstOrDefault(n => !StationList.Any(s => s.StationNo == n), 0);
            if (no == 0)
            {
                MessageBox.Show("站号 1~247 已全部占用，无法复制并粘贴站点。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            copy.StationNo = no;

            int index = StationList.IndexOf(station);
            if (index < 0) index = StationList.Count - 1;
            StationList.Insert(index + 1, copy);
            SelectedStation = copy;
        }

        /// <summary>复制站点到内存剪贴板：不修改原对象与列表，仅停止状态可用</summary>
        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void CopyStation(StationInformationModel? station)
        {
            if (station is null) return;
            ModelClipboard.Copy(station);
        }

        bool CanPasteStation => CanEdit && ModelClipboard.CanPaste<StationInformationModel>();

        /// <summary>粘贴站点：从快照克隆并分配未占用站号，插入选中项之后</summary>
        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanPasteStation))]
        void PasteStation(StationInformationModel? target)
        {
            var copy = ModelClipboard.CreatePasteCopy<StationInformationModel>();
            if (copy is null) return;

            int no = Enumerable.Range(1, 247).FirstOrDefault(n => !StationList.Any(s => s.StationNo == n), 0);
            if (no == 0)
            {
                AppLogger.Warning("站号 1~247 已全部占用，无法粘贴站点。", nameof(PasteStation));
                MessageBox.Show("站号 1~247 已全部占用，无法粘贴站点。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            copy.StationNo = no;
            copy.Name = ModelCloner.WithCopySuffix(copy.Name);

            int index = target is not null ? StationList.IndexOf(target) : -1;
            if (index < 0) index = StationList.Count - 1;
            StationList.Insert(index + 1, copy);
            SelectedStation = copy;
        }

        /// <summary>供外部在剪贴板变化时刷新本站点的粘贴命令状态</summary>
        public void NotifyPasteCommandsChanged() => PasteStationCommand.NotifyCanExecuteChanged();

        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void MoveStationUp() => MoveStation(-1);

        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void MoveStationDown() => MoveStation(1);

        void MoveStation(int offset)
        {
            var item = SelectedStation;
            if (item is null) return;
            int index = StationList.IndexOf(item);
            if (index < 0) return;
            int target = index + offset;
            if (target < 0 || target >= StationList.Count) return;
            StationList.RemoveAt(index);
            target = Math.Clamp(target, 0, StationList.Count);
            try { StationList.Insert(target, item); }
            catch { if (!StationList.Contains(item)) StationList.Add(item); }
            SelectedStation = item;  // RemoveAt 会使 ListView 清空选中，这里恢复
        }

        bool isDsiposed = false;
        /// <summary>当前网络是否已被释放（释放后监听任务抛出的异常属预期行为，不应报错）</summary>
        bool released;
        public void Dispose()
        {
            isDsiposed = true;
            foreach (var item in StationList.ToArray())
            {
                item.Dispose();
                item.CanEdit = true;
            }
            lock (networkLock)
            {
                released = true;
                try { SlaveNetwork?.Dispose(); } catch { }
                SlaveNetwork = null;
            }
            CanEdit = true;
        }

        /// <summary>启动前重置运行期标记，使新一轮可以重建从站网络</summary>
        public void PrepareForStart()
        {
            lock (networkLock)
            {
                released = false;
                nextRetryTime = DateTime.MinValue;
                listenerAlive = true;
            }
        }

        /// <summary>判断指定网络是否已释放（或已被新网络取代）</summary>
        bool IsReleased(IModbusSlaveNetwork? network)
        {
            lock (networkLock)
            {
                return released || (network is not null && !ReferenceEquals(SlaveNetwork, network));
            }
        }

        /// <summary>本周期是否报告过错误（决定周期结束时是否清除 LastError）</summary>
        bool errorReported;

        /// <summary>记录错误但不弹窗（后台线程 MessageBox 可能被主窗口遮挡且会按刷新间隔重复弹出）</summary>
        /// <param name="sticky">true 表示网络级错误，不会在下一周期被自动清除</param>
        void ReportError(string message, bool sticky = false, [CallerMemberName] string source = "")
        {
            errorReported = true;
            if (LastError == message) { lastErrorSticky |= sticky; return; }
            LastError = message;
            lastErrorSticky = sticky;
            AppLogger.Warning(message, source);
        }

        /// <summary>创建（或复用）从站网络；失败时记录错误并返回 null</summary>
        /// <remarks>耗时的创建过程放在锁外，避免阻塞 UI 线程上的 Dispose（如 TCP 连接超时）</remarks>
        IModbusSlaveNetwork? GetOrCreateNetwork()
        {
            // 监听已异常死亡：释放旧对象并置空，触发重建（只在此处释放，onError 回调不碰对象）
            if (!listenerAlive)
            {
                lock (networkLock)
                {
                    if (SlaveNetwork is not null)
                    {
                        try { SlaveNetwork.Dispose(); } catch { }
                        SlaveNetwork = null;
                    }
                    nextRetryTime = DateTime.Now.AddSeconds(1);
                }
            }

            lock (networkLock)
            {
                if (SlaveNetwork is not null) return SlaveNetwork;
            }

            // 退避：创建失败后至少间隔 2 秒再试，避免按刷新间隔（最快 10ms）反复尝试
            if (DateTime.Now < nextRetryTime) return null;

            // 本次要重建：重置存活标记（onError 可能在创建瞬间触发，需先置 true）
            listenerAlive = true;

            // 监听失败回调：只置标记+记录，重建交给 DoWork 线程，避免回调线程跨线程释放对象
            void OnListenerError(Exception ex)
            {
                // 停止/释放过程中的异常属预期收尾：TCP 的 AcceptTcpClientAsync 被 Dispose 取消、
                // 串口释放后监听任务访问已释放的 transport（NModbus 内部 NRE）。
                // 此时 released 已在 Dispose 前置 true，按 released 判断只记普通日志，
                // 不置死亡标记也不上报错误，避免正常停止时弹出误导性红色报错。
                if (IsReleased(null))
                {
                    AppLogger.Info($"网络「{Name}」停止时监听收尾异常（已忽略）：{ex.GetType().Name}: {ex.Message}",
                        nameof(GetOrCreateNetwork));
                    return;
                }
                listenerAlive = false;
                AppLogger.Exception($"网络「{Name}」监听异常终止，将在下一周期重建", ex, nameof(GetOrCreateNetwork));
                ReportError($"网络「{Name}」监听异常终止：{ex.Message}", sticky: true);
            }

            IModbusSlaveNetwork network;
            try
            {
                network = ProtocolType switch
                {
                    EnumProtocolType.ModbusRtu => Tools.CreateRtuSlaveNetwork(PortName, BaudRate, DataBits, Parity, StopBits, OnListenerError),
                    EnumProtocolType.ModbusTcp => Tools.CreateTcpSlaveNetwork(IP, Port, OnListenerError),
                    // 监听式 RTU over TCP：IP 作为绑定地址（127.0.0.1 仅本机，0.0.0.0 暴露到局域网）
                    EnumProtocolType.ModbusRtuOverTcp => new RtuOverTcpSlaveNetwork(IP, Port, OnListenerError),
                    _ => throw new NotSupportedException($"Protocol type {ProtocolType} is not supported.")
                };
            }
            catch (Exception ex)
            {
                nextRetryTime = DateTime.Now.AddSeconds(2);
                AppLogger.Exception($"网络「{Name}」创建失败", ex);
                ReportError(ex.Message);
                return null;
            }

            lock (networkLock)
            {
                // 创建期间可能已执行 Stop/Dispose：此时不能重新挂上实例，也不能把 released 改回 false
                if (isDsiposed || released || SlaveNetwork is not null)
                {
                    try { network.Dispose(); } catch { }
                    return SlaveNetwork;
                }
                SlaveNetwork = network;
                released = false;   // 新网络：重新开始报告错误
                LastError = null;
                lastErrorSticky = false;
                return SlaveNetwork;
            }
        } 

        public async Task DoWork(CancellationToken token)
        {
            AppLogger.Info($"网络「{Name}」开始运行（{ProtocolType} {IP}:{Port}）", nameof(DoWork));
            try
            {
                while (!token.IsCancellationRequested && !isDsiposed)
                {
                    try
                    {
                        await Task.Delay(Interval, token);
                        if (!IsEnable) continue;
                        CanEdit = false;
                        // 一并锁定所有站点（含未启用的），否则运行期仍可改站点 / 规则配置
                        foreach (var s in StationList.ToArray()) s.CanEdit = false;

                        var network = GetOrCreateNetwork();
                        if (network is null) continue;

                        // 用快照遍历，避免运行期集合变动导致“集合已修改”异常
                        var stations = StationList.ToArray();
                        //一次统计各启用站号出现次数，替代每站点一次 Count(lambda) 的 O(n²) 闭包分配
                        stationNoCount.Clear();
                        foreach (var s in stations)
                            if (s.IsEnable)
                                stationNoCount[s.StationNo] = stationNoCount.TryGetValue(s.StationNo, out var c) ? c + 1 : 1;

                        foreach (var station in stations)
                        {
                            if (!station.IsEnable) continue;
                            if (station.StationNo < 1 || station.StationNo > 247)
                            {
                                ReportError($"站号 {station.StationNo} 超出 1~247，已跳过站点「{station.Name}」");
                                continue;
                            }
                            if (stationNoCount.TryGetValue(station.StationNo, out var noCount) && noCount > 1)
                            {
                                ReportError($"站号 {station.StationNo} 重复，已跳过站点「{station.Name}」");
                                continue;
                            }

                            if (station.Slave is null)
                            {
                                station.Slave = network.CreateSlave((byte)station.StationNo);
                                station.Slave.SetEndian(station.Endian);
                            }
                            station.CanEdit = false;

                            // 站点级刷新间隔：不能快于网络周期
                            station.Elapsed += Interval;
                            if (station.Elapsed < station.Interval) continue;
                            station.Elapsed = 0;

                            foreach (var ruler in station.RulerList.ToArray())
                            {
                                if (!ruler.IsEnable) continue;
                                var err = ruler.Validate();
                                if (err is not null)
                                {
                                    ReportError(err);
                                    continue;
                                }
                                try
                                {
                                    ruler.WriteData(station.Slave, station.StationNo);
                                    ruler.IncrementTick();
                                }
                                catch (Exception ex)
                                {
                                    // 单条规则失败不影响同网络后续站点/规则的本周期执行
                                    ReportError($"站点「{station.Name}」规则「{ruler.Name}」执行失败：{ex.Message}");
                                }
                            }
                            station.IncrementTick();
                        }
                        IncrementTick();
                        // 本周期无错误则清除上一次的（非网络级）错误提示
                        if (!errorReported && LastError is not null && !lastErrorSticky) LastError = null;
                        errorReported = false;
                    }
                    catch (OperationCanceledException) { throw; }//取消：原样抛出，交给 finally 清理
                    catch (Exception ex)
                    {
                        // 网络已被释放（删除/导入替换/停止清理）时静默退出，按取消处理
                        if (IsReleased(null)) throw new OperationCanceledException();
                        AppLogger.Exception($"网络「{Name}」本周期执行异常", ex, nameof(DoWork));
                        ReportError(ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                AppLogger.Info($"网络「{Name}」已停止运行", nameof(DoWork));
                // 无论取消、异常还是 isDsiposed 退出，都保证释放资源并恢复可编辑
                // 用快照遍历，避免与 UI 线程的增删同时枚举 BindingList 抛“集合已修改”
                foreach (var item in StationList.ToArray())
                {
                    item.CanEdit = true;
                    item.Slave = null;
                    item.Elapsed = 0;
                    foreach (var ruler in item.RulerList.ToArray()) ruler.ResetRuntime();
                }
                CanEdit = true;
                lock (networkLock)
                {
                    released = true;    // 先标记再释放，让监听任务的收尾异常能被识别为预期行为
                    try { SlaveNetwork?.Dispose(); } catch { }
                    SlaveNetwork = null;
                }
            }
        }
    }

    public partial class StationInformationModel : ObservableObject, IDisposable
    {
        //静态缓存：下拉选项只读，避免 UI 每次重求值都分配数组
        static readonly int[] intervalValues = { 1, 2, 3, 5, 6, 8, 10, 15, 20, 30, 50, 60, 70, 80, 100, 150, 200, 300, 400, 500, 600, 800, 1000, 1500, 2000, 3000, 5000 };
        static readonly EnumEndian[] endialValues = Enum.GetValues<EnumEndian>();
        [JsonIgnore] public int[] IntervalValues => intervalValues;

        [JsonIgnore] public EnumEndian[] EndialValues => endialValues;
        /// <summary>公开唯一标识（GUID）：供规则的跨网络输出目标引用</summary>
        [ObservableProperty] Guid id = Guid.NewGuid();
        [ObservableProperty] bool isEnable = true;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        string name = "未命名";
        [ObservableProperty] string description = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DisplayName))]
        int stationNo = 1;

        /// <summary>列表显示文本：站号 + (名称)</summary>
        [JsonIgnore]
        public string DisplayName => $"{StationNo} ({Name})";
        [ObservableProperty] BindingList<RulerModel> rulerList = new();

        //写入次数：后台高频递增，改为不通知字段；由 UI 侧低频定时器统一刷新显示
        [JsonIgnore] int tick;
        [JsonIgnore] public int Tick => tick;
        public void IncrementTick() => tick++;
        public void NotifyTickChanged() => OnPropertyChanged(nameof(Tick));

        [ObservableProperty] int interval = 5;
        [JsonIgnore] public IModbusSlave? Slave;
        [ObservableProperty] NModbus.Extensions.EnumEndian endian = NModbus.Extensions.EnumEndian.CDAB;
        [property: JsonIgnore][ObservableProperty] bool canEdit = true;
        /// <summary>距上次写入累计的时间(ms)，用于实现站点级刷新间隔</summary>
        [JsonIgnore] public long Elapsed { get; set; }
        [property: JsonIgnore][ObservableProperty] RulerModel? selectedRuler;

        public StationInformationModel()
        {
            HookRulerList();
        }

        partial void OnNameChanged(string value) => NetworkConfigModel.NotifyStationsChanged(this);
        partial void OnStationNoChanged(int value) => NetworkConfigModel.NotifyStationsChanged(this);

        // 用同一委托先 -= 再 +=，防止重复挂接导致重复通知
        ListChangedEventHandler? rulerListHandler;

        void HookRulerList()
        {
            rulerListHandler ??= (_, __) => OnPropertyChanged(nameof(RulerList));
            RulerList.ListChanged -= rulerListHandler;
            RulerList.ListChanged += rulerListHandler;
        }

        partial void OnRulerListChanged(BindingList<RulerModel> value) => HookRulerList();

        // 字节序修改后立刻作用到已创建的从站
        partial void OnEndianChanged(NModbus.Extensions.EnumEndian value) => Slave?.SetEndian(value);

        // 站点锁定/解锁时同步到所有规则，使运行期规则详情可选中查看但不可编辑
        partial void OnCanEditChanged(bool value)
        {
            // 后台 DoWork 会写 CanEdit，UI 可能同时增删规则；用快照避免枚举时集合被修改
            foreach (var ruler in RulerList.ToArray()) ruler.CanEdit = value;
        }

        public void Dispose()
        {
            Slave = null;
        }

        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void AddRuler()
        {
            RulerModel ruler = new RulerModel { CanEdit = CanEdit };
            ruler.Name = $"生成随机数{RulerList.Count + 1}";
            RulerList.Add(ruler);
            SelectedRuler = ruler;  // 新增后自动选中，右侧直接可编辑
        }

        [property: JsonIgnore]
        [RelayCommand]
        void AddPumpRuler()
        {
            RulerModel ruler = new RulerModel
            {
                CanEdit = CanEdit,
                Name = $"伺服流量泵{RulerList.Count + 1}",
                RulerType = EnumRulerType.ServoFlowPump,
                DataType = EnumDataType.Float,
                InputDataType = EnumDataType.UInt16
            };
            RulerList.Add(ruler);
            SelectedRuler = ruler;
        }

        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void RemoverRuler(RulerModel? ruler)
        {
            try
            {
                if (ruler is null) return;
                if (RulerList.Contains(ruler)) RulerList.Remove(ruler);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message,"错误");
            }
        }

        /// <summary>复制并粘贴规则：深拷贝选中规则并插入其后（不携带运行期 Tick/Random），仅停止状态可用</summary>
        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void DuplicateRuler(RulerModel? ruler)
        {
            if (ruler is null) return;
            var copy = ModelCloner.Clone(ruler);
            copy.CanEdit = CanEdit;
            copy.Name = ModelCloner.WithCopySuffix(copy.Name);

            int index = RulerList.IndexOf(ruler);
            if (index < 0) index = RulerList.Count - 1;
            RulerList.Insert(index + 1, copy);
            SelectedRuler = copy;
        }

        /// <summary>复制规则到内存剪贴板：不修改原对象与列表，仅停止状态可用</summary>
        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void CopyRuler(RulerModel? ruler)
        {
            if (ruler is null) return;
            ModelClipboard.Copy(ruler);
        }

        bool CanPasteRuler => CanEdit && ModelClipboard.CanPaste<RulerModel>();

        /// <summary>粘贴规则：从快照克隆并插入选中项之后</summary>
        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanPasteRuler))]
        void PasteRuler(RulerModel? target)
        {
            var copy = ModelClipboard.CreatePasteCopy<RulerModel>();
            if (copy is null) return;
            copy.Name = ModelCloner.WithCopySuffix(copy.Name);

            int index = target is not null ? RulerList.IndexOf(target) : -1;
            if (index < 0) index = RulerList.Count - 1;
            RulerList.Insert(index + 1, copy);
            SelectedRuler = copy;
        }

        /// <summary>供外部在剪贴板变化时刷新本站点的规则粘贴命令状态</summary>
        public void NotifyPasteCommandsChanged() => PasteRulerCommand.NotifyCanExecuteChanged();

        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void MoveRulerUp() => MoveRuler(-1);

        [property: JsonIgnore]
        [RelayCommand(CanExecute = nameof(CanEdit))]
        void MoveRulerDown() => MoveRuler(1);

        void MoveRuler(int offset)
        {
            var item = SelectedRuler;
            if (item is null) return;
            int index = RulerList.IndexOf(item);
            if (index < 0) return;
            int target = index + offset;
            if (target < 0 || target >= RulerList.Count) return;
            RulerList.RemoveAt(index);
            target = Math.Clamp(target, 0, RulerList.Count);
            try { RulerList.Insert(target, item); }
            catch { if (!RulerList.Contains(item)) RulerList.Add(item); }
            SelectedRuler = item;  // RemoveAt 会使 ListView 清空选中，这里恢复
        }
    }

    public partial class RulerModel:ObservableObject
    {
        //静态缓存：下拉选项只读，避免 UI 每次重求值都 Clone 枚举数组
        static readonly EnumRegisterType[] registerTypeValues = Enum.GetValues<EnumRegisterType>();
        static readonly EnumDataType[] dataTypeValues = Enum.GetValues<EnumDataType>();
        static readonly EnumRulerType[] rulerTypeValues = Enum.GetValues<EnumRulerType>();
        static readonly EnumPumpOutputTarget[] pumpOutputTargetValues = Enum.GetValues<EnumPumpOutputTarget>();
        static readonly EnumPumpInputSource[] pumpInputSourceValues = Enum.GetValues<EnumPumpInputSource>();
        [JsonIgnore] public EnumRegisterType[] RegisterTypeValues => registerTypeValues;
        [JsonIgnore] public EnumDataType[] DataTypeValues => dataTypeValues;
        [JsonIgnore] public EnumRulerType[] RulerTypeValues => rulerTypeValues;
        [JsonIgnore] public EnumPumpOutputTarget[] PumpOutputTargetValues => pumpOutputTargetValues;
        [JsonIgnore] public EnumPumpInputSource[] PumpInputSourceValues => pumpInputSourceValues;
        /// <summary>伺服流量泵输入来源选项：不含固定值（固定值仅值拷贝支持）</summary>
        [JsonIgnore] public EnumPumpInputSource[] PumpInputSourceValuesForPump { get; } =
            new[] { EnumPumpInputSource.ThisStation, EnumPumpInputSource.OtherStation };

        [property: JsonIgnore] public BindingList<NetworkTargetOption> NetworkOptions => RuleRuntime.Networks;
        [property: JsonIgnore] public BindingList<StationTargetOption> StationOptions => RuleRuntime.Stations;

        [ObservableProperty] bool isEnable = true;
        [ObservableProperty] string name = "生成随机数";
        [ObservableProperty] string description = "";

        /// <summary>规则类型：旧配置缺省为生成随机数</summary>
        [ObservableProperty] EnumRulerType rulerType = EnumRulerType.Random;

        [ObservableProperty] EnumRegisterType registerType = EnumRegisterType.ReadWrite;
        [ObservableProperty] EnumDataType dataType = EnumDataType.UInt16;
        [ObservableProperty] int startAddress = 0;
        [ObservableProperty] int length = 1;
        [ObservableProperty] double scale = 100.0;//缩放
        [ObservableProperty] double offset = 0;//偏置

        /// <summary>伺服流量泵输入来源：默认本站（兼容旧配置）；其它站时用公开 GUID 引用</summary>
        [ObservableProperty] EnumPumpInputSource pumpInputSource = EnumPumpInputSource.ThisStation;
        [ObservableProperty] Guid inputNetworkId;
        [ObservableProperty] Guid inputStationId;

        /// <summary>读取输入/值拷贝源的寄存器（字节序按来源从站设置）</summary>
        [ObservableProperty] EnumRegisterType inputRegisterType = EnumRegisterType.ReadWrite;
        [ObservableProperty] EnumDataType inputDataType = EnumDataType.UInt16;
        [ObservableProperty] int inputAddress = 0;
        /// <summary>值拷贝源为"固定值"时填写的字符串，按目标数据类型解析</summary>
        [ObservableProperty] string fixedValueText = "0";
        /// <summary>值拷贝：源地址数量。1=点对点/一对多；>1=多对多（目标数量不生效）</summary>
        [ObservableProperty] int sourceLength = 1;
        [ObservableProperty] double inputScale = 1.0;//读取缩放
        [ObservableProperty] double inputOffset = 0;//读取偏置
        [ObservableProperty] double outputScale = 1.0;//目标站缩放
        [ObservableProperty] double outputOffset = 0;//目标站偏置

        //值拷贝专用：源/目标的缩放与偏置（与泵参数独立，避免互相干扰）
        /// <summary>值拷贝源缩放：源值 = 原始值 × 本缩放 + 本偏置</summary>
        [ObservableProperty] double copySourceScale = 1.0;
        /// <summary>值拷贝源偏置</summary>
        [ObservableProperty] double copySourceOffset = 0;
        /// <summary>值拷贝目标缩放：目标值 = 源值 × 本缩放 + 本偏置</summary>
        [ObservableProperty] double copyTargetScale = 1.0;
        /// <summary>值拷贝目标偏置</summary>
        [ObservableProperty] double copyTargetOffset = 0;
        //字节序不使用规则级设置：选择其它网络的从站时，直接使用该从站自身的字节序设置

        // 水泵流量计参数：把"指令（输入）量程"线性映射到"输出量程"，再做一阶惯性 + 纯滞后
        /// <summary>指令下限（输入量程下端），默认 -3000；与指令上限一起替代原硬编码 /3000*10 的映射</summary>
        [ObservableProperty] double commandLow = -3000;
        /// <summary>指令上限（输入量程上端），默认 3000</summary>
        [ObservableProperty] double commandHigh = 3000;
        /// <summary>输出下限（输出量程下端），默认 -10；改成 0 可配合 0~3000 做单向泵</summary>
        [ObservableProperty] double outputLow = -10;
        /// <summary>输出上限（输出量程上端），默认 10</summary>
        [ObservableProperty] double outputHigh = 10;
        /// <summary>一阶惯性时间常数 τ（秒），默认 0.45；小于等于 0 表示不滤波、直接跟随</summary>
        [ObservableProperty] double timeConstant = 0.45;
        /// <summary>纯滞后时间 Td（秒），默认 0（不启用）；单位秒可填小数，如 0.01</summary>
        [ObservableProperty] double lagTime = 0;

        /// <summary>伺服流量泵：输出目标；选择其它网络从站时，用网络与站点的公开 GUID 引用</summary>
        [ObservableProperty] EnumPumpOutputTarget pumpOutputTarget = EnumPumpOutputTarget.ThisStation;
        [ObservableProperty] Guid targetNetworkId;
        [ObservableProperty] Guid targetStationId;

        /// <summary>运行期只读标记：由所属站点在启动/停止时同步，不持久化</summary>
        [property: JsonIgnore][ObservableProperty] bool canEdit = true;

        partial void OnCanEditChanged(bool value)
        {
            OnPropertyChanged(nameof(CanEditTargetStation));
            OnPropertyChanged(nameof(CanEditOutputParams));
            OnPropertyChanged(nameof(CanEditInputStation));
            OnPropertyChanged(nameof(CanEditCopyTargetLength));
        }

        /// <summary>是否为需要选择网络/从站来源的规则（泵或值拷贝）</summary>
        [JsonIgnore]
        bool IsSourceTargetRule => RulerType == EnumRulerType.ServoFlowPump || RulerType == EnumRulerType.ValueCopy;

        /// <summary>目标网络/目标从站是否可编辑：输出目标为"其它网络的从站"时可编辑（所有规则类型）</summary>
        [JsonIgnore]
        public bool CanEditTargetStation => CanEdit && PumpOutputTarget == EnumPumpOutputTarget.OtherStation;

        /// <summary>输出参数是否可编辑：输出目标为"不写输出"时禁用（所有规则类型）</summary>
        [JsonIgnore]
        public bool CanEditOutputParams => CanEdit && PumpOutputTarget != EnumPumpOutputTarget.None;

        /// <summary>输入网络/输入从站是否可编辑：仅输入来源为"其它网络的从站"时可编辑</summary>
        [JsonIgnore]
        public bool CanEditInputStation => CanEdit && IsSourceTargetRule
            && PumpInputSource == EnumPumpInputSource.OtherStation;

        /// <summary>值拷贝目标数量是否可编辑：仅源数量=1且输出目标非"不写输出"时可编辑；源数量>1时多对多、目标数量不生效</summary>
        [JsonIgnore]
        public bool CanEditCopyTargetLength => CanEdit && RulerType == EnumRulerType.ValueCopy
            && SourceLength <= 1 && PumpOutputTarget != EnumPumpOutputTarget.None;

        partial void OnRulerTypeChanged(EnumRulerType value)
        {
            OnPropertyChanged(nameof(CanEditTargetStation));
            OnPropertyChanged(nameof(CanEditOutputParams));
            OnPropertyChanged(nameof(CanEditInputStation));
            OnPropertyChanged(nameof(CanEditCopyTargetLength));
        }

        partial void OnPumpOutputTargetChanged(EnumPumpOutputTarget value)
        {
            OnPropertyChanged(nameof(CanEditTargetStation));
            OnPropertyChanged(nameof(CanEditOutputParams));
            OnPropertyChanged(nameof(CanEditCopyTargetLength));
        }

        partial void OnPumpInputSourceChanged(EnumPumpInputSource value)
            => OnPropertyChanged(nameof(CanEditInputStation));

        partial void OnSourceLengthChanged(int value)
            => OnPropertyChanged(nameof(CanEditCopyTargetLength));

        /// <summary>UI 下拉框直接绑定的选项对象（不持久化，由 Guid 同步）</summary>
        [JsonIgnore] NetworkTargetOption? selectedNetworkOption;
        [JsonIgnore] StationTargetOption? selectedStationOption;

        /// <summary>
        /// 当前目标网络下的从站选项：稳定的集合实例，选项对象直接引用全局 StationOptions 中的实例。
        /// 不用 MultiBinding 动态 new List 作为 ItemsSource——那样每次网络变化都会替换集合实例，
        /// 令 SelectedItem 与集合短暂失配，交替出现验证红框。
        /// </summary>
        [JsonIgnore] public ObservableCollection<StationTargetOption> TargetStations { get; } = new();

        partial void OnTargetNetworkIdChanged(Guid value) => RefreshTargetStations();

        /// <summary>按目标网络原地刷新从站选项，保持集合实例与选项对象引用稳定</summary>
        public void RefreshTargetStations()
        {
            var wanted = StationOptions.Where(o => o.NetworkId == TargetNetworkId).ToList();
            for (int i = TargetStations.Count - 1; i >= 0; i--)
                if (!wanted.Contains(TargetStations[i])) TargetStations.RemoveAt(i);
            foreach (var item in wanted)
                if (!TargetStations.Contains(item)) TargetStations.Add(item);
            //选中项已不在当前网络时清空，避免 SelectedItem 指向集合外的对象
            if (SelectedStationOption is not null && !TargetStations.Contains(SelectedStationOption))
                SelectedStationOption = null;
        }

        [JsonIgnore] public NetworkTargetOption? SelectedNetworkOption
        {
            get => selectedNetworkOption;
            set
            {
                Guid oldId = TargetNetworkId;
                if (SetProperty(ref selectedNetworkOption, value))
                {
                    TargetNetworkId = value?.Id ?? Guid.Empty;
                    if (oldId != TargetNetworkId)
                    {
                        SelectedStationOption = null;
                        TargetStationId = Guid.Empty;
                    }
                }
            }
        }

        [JsonIgnore] public StationTargetOption? SelectedStationOption
        {
            get => selectedStationOption;
            set
            {
                if (SetProperty(ref selectedStationOption, value))
                    TargetStationId = value?.StationId ?? Guid.Empty;
            }
        }

        /// <summary>按持久化的 Guid 恢复下拉选择（配置加载后调用）</summary>
        public void SyncTargetOptions()
        {
            RefreshTargetStations();
            SelectedNetworkOption = NetworkOptions.FirstOrDefault(o => o.Id == TargetNetworkId);
            SelectedStationOption = TargetStations.FirstOrDefault(o => o.NetworkId == TargetNetworkId && o.StationId == TargetStationId);
            SyncInputOptions();
        }

        // ===== 输入来源（其它网络的从站）：与输出侧对称，用稳定集合 + Guid 引用 =====
        [JsonIgnore] NetworkTargetOption? selectedInputNetworkOption;
        [JsonIgnore] StationTargetOption? selectedInputStationOption;

        /// <summary>当前输入网络下的从站选项（稳定集合实例）</summary>
        [JsonIgnore] public ObservableCollection<StationTargetOption> InputStations { get; } = new();

        partial void OnInputNetworkIdChanged(Guid value) => RefreshInputStations();

        /// <summary>按输入网络原地刷新从站选项，保持集合实例与选项对象引用稳定</summary>
        public void RefreshInputStations()
        {
            var wanted = StationOptions.Where(o => o.NetworkId == InputNetworkId).ToList();
            for (int i = InputStations.Count - 1; i >= 0; i--)
                if (!wanted.Contains(InputStations[i])) InputStations.RemoveAt(i);
            foreach (var item in wanted)
                if (!InputStations.Contains(item)) InputStations.Add(item);
            if (SelectedInputStationOption is not null && !InputStations.Contains(SelectedInputStationOption))
                SelectedInputStationOption = null;
        }

        [JsonIgnore] public NetworkTargetOption? SelectedInputNetworkOption
        {
            get => selectedInputNetworkOption;
            set
            {
                Guid oldId = InputNetworkId;
                if (SetProperty(ref selectedInputNetworkOption, value))
                {
                    InputNetworkId = value?.Id ?? Guid.Empty;
                    if (oldId != InputNetworkId)
                    {
                        SelectedInputStationOption = null;
                        InputStationId = Guid.Empty;
                    }
                }
            }
        }

        [JsonIgnore] public StationTargetOption? SelectedInputStationOption
        {
            get => selectedInputStationOption;
            set
            {
                if (SetProperty(ref selectedInputStationOption, value))
                    InputStationId = value?.StationId ?? Guid.Empty;
            }
        }

        /// <summary>按持久化的 Guid 恢复输入下拉选择</summary>
        public void SyncInputOptions()
        {
            RefreshInputStations();
            SelectedInputNetworkOption = NetworkOptions.FirstOrDefault(o => o.Id == InputNetworkId);
            SelectedInputStationOption = InputStations.FirstOrDefault(o => o.NetworkId == InputNetworkId && o.StationId == InputStationId);
        }

        //写入次数：后台高频递增，改为不通知字段；由 UI 侧低频定时器统一刷新显示
        [JsonIgnore] int tick;
        [JsonIgnore] public int Tick => tick;
        public void IncrementTick() => tick++;
        public void NotifyTickChanged() => OnPropertyChanged(nameof(Tick));

        Random random = new Random(Guid.NewGuid().GetHashCode());
        /// <summary>伺服流量泵的一阶滤波状态（运行期，不持久化）</summary>
        [JsonIgnore] double currentSystemValue;
        /// <summary>水泵运算节拍：每 10ms 运算一次并把结果压入历史</summary>
        const double PumpStepSeconds = 0.01;
        /// <summary>水泵输出历史（末尾为最新），用于按滞后时间取样；运行期，不持久化</summary>
        [JsonIgnore] readonly List<double> pumpHistory = new();
        /// <summary>上次水泵运算的单调时钟（毫秒），累计满 10ms 推进一次</summary>
        [JsonIgnore] long pumpLastTickMs;
        /// <summary>不足一个运算节拍的累计时间（毫秒）</summary>
        [JsonIgnore] double pumpAccumMs;

        /// <summary>数据类型占用的寄存器字数（32 位与浮点占 2 个字）</summary>
        static int WordCount(EnumDataType type) =>
            type is EnumDataType.Int32 or EnumDataType.UInt32 or EnumDataType.Float ? 2 : 1;

        /// <summary>校验规则参数，返回错误信息（null 表示合法）</summary>
        public string? Validate()
        {
            bool noWrite = PumpOutputTarget == EnumPumpOutputTarget.None;

            if (RulerType == EnumRulerType.ValueCopy)
            {
                //值拷贝：源校验（固定值源无需地址/从站，仅校验字符串可按目标类型解析）
                bool sourceIsFixed = PumpInputSource == EnumPumpInputSource.FixedValue;
                if (sourceIsFixed)
                {
                    if (DataType == EnumDataType.String) return $"值拷贝「{Name}」目标数据类型不能为 String";
                    if (!TryParseFixedValue(FixedValueText, DataType, out _))
                        return $"值拷贝「{Name}」固定值「{FixedValueText}」无法按目标类型「{DataType}」解析";
                }
                else
                {
                    if (InputAddress < 0 || InputAddress > 65535) return $"值拷贝「{Name}」源地址 {InputAddress} 超出 0~65535";
                    if (InputDataType == EnumDataType.String) return $"值拷贝「{Name}」源数据类型不能为 String";
                    if (PumpInputSource == EnumPumpInputSource.OtherStation && (InputNetworkId == Guid.Empty || InputStationId == Guid.Empty))
                        return $"值拷贝「{Name}」未选择来源网络或从站";
                }
                if (SourceLength < 1 || SourceLength > 2000) return $"值拷贝「{Name}」源数量 {SourceLength} 超出 1~2000";
                if (!sourceIsFixed && InputAddress + SourceLength * WordCount(InputDataType) > 65536)
                    return $"值拷贝「{Name}」源地址范围超出 65535";

                //目标校验："不写输出"时不拷贝，跳过；源数量>1 时目标数量不生效，不校验
                if (!noWrite)
                {
                    if (StartAddress < 0 || StartAddress > 65535) return $"值拷贝「{Name}」目标地址 {StartAddress} 超出 0~65535";
                    if (DataType == EnumDataType.String) return $"值拷贝「{Name}」目标数据类型不能为 String";
                    if (SourceLength <= 1)
                    {
                        //目标数量 0=不拷贝、1~2000=有效
                        if (Length < 0 || Length > 2000) return $"值拷贝「{Name}」目标数量 {Length} 超出 0~2000";
                        if (Length > 0 && StartAddress + Length * WordCount(DataType) > 65536) return $"值拷贝「{Name}」目标地址范围超出 65535";
                    }
                    else
                    {
                        //多对多：按源数量写，校验目标地址+源数量范围
                        if (StartAddress + SourceLength * WordCount(DataType) > 65536) return $"值拷贝「{Name}」目标地址范围超出 65535";
                    }
                    if (PumpOutputTarget == EnumPumpOutputTarget.OtherStation && (TargetNetworkId == Guid.Empty || TargetStationId == Guid.Empty))
                        return $"值拷贝「{Name}」未选择目标网络或从站";
                }
                return null;
            }

            // 生成随机数 / 伺服流量泵：输出地址相关校验（"不写输出"时跳过）
            bool writeOutput = PumpOutputTarget != EnumPumpOutputTarget.None;
            if (writeOutput)
            {
                if (StartAddress < 0 || StartAddress > 65535) return $"规则「{Name}」输出起始地址 {StartAddress} 超出 0~65535";
                if (Length < 1 || Length > 2000) return $"规则「{Name}」数量 {Length} 超出 1~2000";
                if (StartAddress + Length * WordCount(DataType) > 65536) return $"规则「{Name}」输出地址范围 {StartAddress}~{StartAddress + Length * WordCount(DataType) - 1} 超出 65535";
                if (PumpOutputTarget == EnumPumpOutputTarget.OtherStation && (TargetNetworkId == Guid.Empty || TargetStationId == Guid.Empty))
                    return $"规则「{Name}」未选择输出目标网络或从站";
            }

            if (RulerType != EnumRulerType.ServoFlowPump) return null;

            if (InputAddress < 0 || InputAddress > 65535) return $"伺服流量泵「{Name}」输入地址 {InputAddress} 超出 0~65535";
            if (InputDataType == EnumDataType.String) return $"伺服流量泵「{Name}」输入数据类型不能为 String";
            if (writeOutput && DataType == EnumDataType.String) return $"伺服流量泵「{Name}」输出数据类型不能为 String";
            if (PumpInputSource == EnumPumpInputSource.OtherStation && (InputNetworkId == Guid.Empty || InputStationId == Guid.Empty))
                return $"伺服流量泵「{Name}」未选择输入源网络或从站";
            if (CommandHigh <= CommandLow) return $"伺服流量泵「{Name}」指令上限必须大于下限";
            if (double.IsNaN(LagTime) || double.IsInfinity(LagTime) || LagTime < 0 || LagTime > 3600)
                return $"伺服流量泵「{Name}」滞后时间 {LagTime} 超出 0~3600 秒";
            return null;
        }

        /// <summary>按规则向从站写入数据（Tick 由调用方递增）</summary>
        public void WriteData(IModbusSlave slave, int stationNo)
        {
            if (RulerType == EnumRulerType.ServoFlowPump)
            {
                WritePumpData(slave);
                return;
            }
            if (RulerType == EnumRulerType.ValueCopy)
            {
                WriteValueCopyData(slave);
                return;
            }
            WriteRamdonData(slave, stationNo);
        }

        /// <summary>
        /// 伺服流量泵（水泵流量计）：读取指令 → 按[指令下限,指令上限]→[输出下限,输出上限]线性映射并限幅
        /// → 时间常数 τ 的一阶惯性滤波 → 按滞后时间 Td 从历史取样 → 按输出目标写入。
        /// 每 10ms 运算一次；写入其它网络从站时字节序跟随目标从站设置。
        /// </summary>
        void WritePumpData(IModbusSlave localSlave)
        {
            //输入来源：本站用当前从站；其它网络从站按公开 GUID 解析，字节序跟随输入源从站
            double raw;
            if (PumpInputSource == EnumPumpInputSource.OtherStation)
            {
                IModbusSlave? inputSlave = RuleRuntime.ResolveSlave?.Invoke(InputNetworkId, InputStationId);
                raw = inputSlave is null ? 0 : ReadInputValue(inputSlave);
            }
            else
            {
                raw = ReadInputValue(localSlave);
            }
            double inValue = raw * InputScale + InputOffset;

            //1. 量程映射：指令区间 → 输出区间，并限幅到[输出下限,输出上限]
            double low = Math.Min(OutputLow, OutputHigh);
            double high = Math.Max(OutputLow, OutputHigh);
            double cmdSpan = CommandHigh - CommandLow;
            double idealValue = Math.Abs(cmdSpan) < double.Epsilon
                ? low
                : low + (inValue - CommandLow) / cmdSpan * (high - low);
            idealValue = Math.Max(low, Math.Min(high, idealValue));

            //2. 按真实经过时间补算若干个 10ms 步长（保证节拍与规则刷新周期无关）
            long now = Environment.TickCount64;
            if (pumpLastTickMs == 0) pumpLastTickMs = now;
            pumpAccumMs += now - pumpLastTickMs;
            pumpLastTickMs = now;
            //一阶惯性系数：α = 1 - e^(-dt/τ)；τ<=0 时不滤波直接跟随
            double alpha = TimeConstant > 0 ? 1 - Math.Exp(-PumpStepSeconds / TimeConstant) : 1.0;
            //滞后步数：Td 秒 / 10ms；历史不足时读数为 0
            int lagSteps = LagTime > 0 ? (int)Math.Round(LagTime / PumpStepSeconds) : 0;

            const int maxStepsPerTick = 500; //防止长时间暂停后一次性补算过多
            int steps = 0;
            while (pumpAccumMs >= PumpStepSeconds * 1000 && steps < maxStepsPerTick)
            {
                currentSystemValue += alpha * (idealValue - currentSystemValue);
                pumpHistory.Add(currentSystemValue);
                pumpAccumMs -= PumpStepSeconds * 1000;
                steps++;
            }
            if (steps == 0 && pumpHistory.Count == 0)
            {
                //尚未到第一个运算节拍：压入初值，避免首次取样恒为 0
                currentSystemValue += alpha * (idealValue - currentSystemValue);
                pumpHistory.Add(currentSystemValue);
            }

            //保留历史长度 = 滞后步数 + 1，删除更早的数据
            int keep = lagSteps + 1;
            if (pumpHistory.Count > keep) pumpHistory.RemoveRange(0, pumpHistory.Count - keep);

            double systemValue = lagSteps > 0 && pumpHistory.Count > lagSteps
                ? pumpHistory[pumpHistory.Count - 1 - lagSteps]
                : (lagSteps == 0 && pumpHistory.Count > 0 ? pumpHistory[pumpHistory.Count - 1] : 0.0);

            IModbusSlave? target = PumpOutputTarget switch
            {
                EnumPumpOutputTarget.ThisStation => localSlave,
                EnumPumpOutputTarget.OtherStation => RuleRuntime.ResolveSlave?.Invoke(TargetNetworkId, TargetStationId),
                _ => null
            };
            if (target is null) return;

            WriteScalar(target, systemValue * OutputScale + OutputOffset);
        }

        /// <summary>从本站从站的数据存储读取输入值，字节序按本站从站设置还原</summary>
        double ReadInputValue(IModbusSlave slave)
        {
            try
            {
                var endian = slave.GetEndian();
                if (InputDataType == EnumDataType.Bool)
                {
                    var bits = InputRegisterType == EnumRegisterType.ReadOnly
                        ? slave.DataStore.CoilInputs.ReadPoints((ushort)InputAddress, 1)
                        : slave.DataStore.CoilDiscretes.ReadPoints((ushort)InputAddress, 1);
                    return bits.Length > 0 && bits[0] ? 1.0 : 0.0;
                }

                int words = InputDataType is EnumDataType.Int32 or EnumDataType.UInt32 or EnumDataType.Float ? 2 : 1;
                var raw = InputRegisterType == EnumRegisterType.ReadOnly
                    ? slave.DataStore.InputRegisters.ReadPoints((ushort)InputAddress, (ushort)words)
                    : slave.DataStore.HoldingRegisters.ReadPoints((ushort)InputAddress, (ushort)words);
                if (raw.Length < words) return 0;

                return InputDataType switch
                {
                    EnumDataType.UInt16 => raw.ToTargetEndian(endian)[0],
                    EnumDataType.Int16 => raw.ToTargetEndian(endian).ToInt16()[0],
                    EnumDataType.UInt32 => raw.ToUInt32().ToTargetEndian(endian)[0],
                    EnumDataType.Int32 => raw.ToUInt32().ToTargetEndian(endian).ToInt32()[0],
                    EnumDataType.Float => raw.ToUInt32().ToTargetEndian(endian).ToFloat()[0],
                    _ => 0
                };
            }
            catch (Exception ex)
            {
                AppLogger.Exception($"伺服流量泵「{Name}」读取输入失败", ex);
                return 0;
            }
        }

        /// <summary>通用多值读取：按给定寄存器区/类型/起始地址读取 count 个值，字节序跟随来源从站</summary>
        double[] ReadValues(IModbusSlave slave, EnumRegisterType regType, EnumDataType dataType, int address, int count)
        {
            var result = new double[count];
            if (count <= 0) return result;
            try
            {
                var endian = slave.GetEndian();
                if (dataType == EnumDataType.Bool)
                {
                    var bits = regType == EnumRegisterType.ReadOnly
                        ? slave.DataStore.CoilInputs.ReadPoints((ushort)address, (ushort)count)
                        : slave.DataStore.CoilDiscretes.ReadPoints((ushort)address, (ushort)count);
                    for (int i = 0; i < count && i < bits.Length; i++) result[i] = bits[i] ? 1.0 : 0.0;
                    return result;
                }

                int words = dataType is EnumDataType.Int32 or EnumDataType.UInt32 or EnumDataType.Float ? 2 : 1;
                var raw = regType == EnumRegisterType.ReadOnly
                    ? slave.DataStore.InputRegisters.ReadPoints((ushort)address, (ushort)(count * words))
                    : slave.DataStore.HoldingRegisters.ReadPoints((ushort)address, (ushort)(count * words));
                int available = raw.Length / words;
                if (available <= 0) return result;

                var targetEndian = raw.ToTargetEndian(endian);
                if (dataType == EnumDataType.UInt16)
                {
                    for (int i = 0; i < count && i < available; i++) result[i] = targetEndian[i];
                }
                else if (dataType == EnumDataType.Int16)
                {
                    var conv = targetEndian.ToInt16();
                    for (int i = 0; i < count && i < available; i++) result[i] = conv[i];
                }
                else
                {
                    var u32 = raw.ToUInt32().ToTargetEndian(endian);
                    if (dataType == EnumDataType.UInt32)
                    {
                        for (int i = 0; i < count && i < available; i++) result[i] = u32[i];
                    }
                    else if (dataType == EnumDataType.Int32)
                    {
                        var conv = u32.ToInt32();
                        for (int i = 0; i < count && i < available; i++) result[i] = conv[i];
                    }
                    else if (dataType == EnumDataType.Float)
                    {
                        var conv = u32.ToFloat();
                        for (int i = 0; i < count && i < available; i++) result[i] = conv[i];
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Exception($"值拷贝「{Name}」读取失败", ex);
            }
            return result;
        }

        /// <summary>
        /// 解析值拷贝源的固定值字符串。
        /// 目标为 Bool：支持 true/false、1/0（不区分大小写、忽略前后空白）；
        /// 其它类型：先按浮点数解析（支持小数与指数），最终由写入管线转换为目标类型。
        /// </summary>
        public static bool TryParseFixedValue(string? text, EnumDataType targetType, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();
            if (targetType == EnumDataType.Bool)
            {
                if (bool.TryParse(s, out var b)) { value = b ? 1 : 0; return true; }
                if (s == "1") { value = 1; return true; }
                if (s == "0") { value = 0; return true; }
                return false;
            }
            return double.TryParse(s,
                NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out value);
        }

        /// <summary>通用多值写入：字节序由写入扩展方法按目标从站自身设置处理</summary>
        void WriteValues(IModbusSlave slave, EnumRegisterType regType, EnumDataType dataType, int address, double[] values)
        {
            int count = values.Length;
            if (count <= 0) return;
            bool readOnly = regType == EnumRegisterType.ReadOnly;
            switch (dataType)
            {
                case EnumDataType.Bool:
                    {
                        var data = new bool[count];
                        for (int i = 0; i < count; i++) data[i] = values[i] != 0;
                        if (readOnly) slave.WriteBoolR(address, data); else slave.WriteBoolRW(address, data);
                        return;
                    }
                case EnumDataType.Int16:
                    {
                        var data = new short[count];
                        for (int i = 0; i < count; i++) data[i] = (Int16)values[i];
                        if (readOnly) slave.WriteInt16R(address, data); else slave.WriteInt16RW(address, data);
                        return;
                    }
                case EnumDataType.UInt16:
                    {
                        var data = new ushort[count];
                        for (int i = 0; i < count; i++) data[i] = (UInt16)Math.Max(0, values[i]);
                        if (readOnly) slave.WriteUInt16R(address, data); else slave.WriteUInt16RW(address, data);
                        return;
                    }
                case EnumDataType.Int32:
                    {
                        var data = new int[count];
                        for (int i = 0; i < count; i++) data[i] = (Int32)values[i];
                        if (readOnly) slave.WriteInt32R(address, data); else slave.WriteInt32RW(address, data);
                        return;
                    }
                case EnumDataType.UInt32:
                    {
                        var data = new uint[count];
                        for (int i = 0; i < count; i++) data[i] = (UInt32)Math.Max(0, values[i]);
                        if (readOnly) slave.WriteUInt32R(address, data); else slave.WriteUInt32RW(address, data);
                        return;
                    }
                case EnumDataType.Float:
                    {
                        var data = new float[count];
                        for (int i = 0; i < count; i++) data[i] = (float)values[i];
                        if (readOnly) slave.WriteFloatR(address, data); else slave.WriteFloatRW(address, data);
                        return;
                    }
            }
        }

        /// <summary>
        /// 值拷贝：源(本站/其它从站)读取 → 目标(本站/其它从站)写入。
        /// 源数量=1：目标数量 0=不拷贝、1=点对点、>1=把源值连续写多个；
        /// 源数量>1：多对多，目标数量不生效，按源数量连续写。
        /// </summary>
        void WriteValueCopyData(IModbusSlave localSlave)
        {
            if (PumpOutputTarget == EnumPumpOutputTarget.None) return;

            bool sourceIsFixed = PumpInputSource == EnumPumpInputSource.FixedValue;
            bool sourceIsRemote = PumpInputSource == EnumPumpInputSource.OtherStation;
            bool targetIsRemote = PumpOutputTarget == EnumPumpOutputTarget.OtherStation;

            //1. 解析源从站：固定值源不需要从站
            IModbusSlave? sourceSlave = null;
            if (!sourceIsFixed)
            {
                sourceSlave = sourceIsRemote
                    ? RuleRuntime.ResolveSlave?.Invoke(InputNetworkId, InputStationId)
                    : localSlave;
                if (sourceSlave is null) return;
            }

            //2. 解析目标从站
            IModbusSlave? targetSlave = targetIsRemote
                ? RuleRuntime.ResolveSlave?.Invoke(TargetNetworkId, TargetStationId)
                : localSlave;
            if (targetSlave is null) return;

            //3. 读取源数据并应用源缩放/偏置；字节序跟随来源从站自身设置
            int srcCount = Math.Max(1, SourceLength);
            double[] source;
            if (sourceIsFixed)
            {
                //固定值源：按目标数据类型解析字符串，所有源位置写入同一个值
                if (!TryParseFixedValue(FixedValueText, DataType, out var fixedValue))
                {
                    AppLogger.Warning($"值拷贝「{Name}」固定值「{FixedValueText}」无法按目标类型「{DataType}」解析", nameof(WriteValueCopyData));
                    return;
                }
                source = new double[srcCount];
                for (int i = 0; i < source.Length; i++) source[i] = fixedValue;
            }
            else
            {
                source = ReadValues(sourceSlave!, InputRegisterType, InputDataType, InputAddress, srcCount);
            }
            for (int i = 0; i < source.Length; i++) source[i] = source[i] * CopySourceScale + CopySourceOffset;

            //4. 按源数量决定写入方式；字节序跟随目标从站自身设置
            if (srcCount <= 1)
            {
                int targetCount = Math.Max(0, Length);
                if (targetCount == 0) return; //目标数量为 0：不执行拷贝
                var data = new double[targetCount];
                for (int i = 0; i < targetCount; i++) data[i] = source[0] * CopyTargetScale + CopyTargetOffset;
                WriteValues(targetSlave, RegisterType, DataType, StartAddress, data);
            }
            else
            {
                //多对多：目标数量不生效，按源数量连续写；逐值应用目标缩放/偏置
                var data = new double[source.Length];
                for (int i = 0; i < source.Length; i++) data[i] = source[i] * CopyTargetScale + CopyTargetOffset;
                WriteValues(targetSlave, RegisterType, DataType, StartAddress, data);
            }
        }

        /// <summary>按现有规则设置项（寄存器类型/数据类型/起始地址/数量）把一个数值写入目标从站</summary>
        void WriteScalar(IModbusSlave slave, double value)
        {
            int addr = StartAddress;
            int count = Math.Max(1, Length);
            bool readOnly = RegisterType == EnumRegisterType.ReadOnly;

            //直接分配目标类型数组并原地填充，避免 Enumerable.Repeat/Select 迭代器与双份数组
            switch (DataType)
            {
                case EnumDataType.Bool:
                    {
                        var bits = new bool[count];
                        for (int i = 0; i < count; i++) bits[i] = value != 0;
                        if (readOnly) slave.WriteBoolR(addr, bits); else slave.WriteBoolRW(addr, bits);
                        return;
                    }
                case EnumDataType.Int16:
                    {
                        var data = new short[count];
                        for (int i = 0; i < count; i++) data[i] = (Int16)value;
                        if (readOnly) slave.WriteInt16R(addr, data); else slave.WriteInt16RW(addr, data);
                        return;
                    }
                case EnumDataType.UInt16:
                    {
                        var data = new ushort[count];
                        for (int i = 0; i < count; i++) data[i] = (UInt16)Math.Max(0, value);
                        if (readOnly) slave.WriteUInt16R(addr, data); else slave.WriteUInt16RW(addr, data);
                        return;
                    }
                case EnumDataType.Int32:
                    {
                        var data = new int[count];
                        for (int i = 0; i < count; i++) data[i] = (Int32)value;
                        if (readOnly) slave.WriteInt32R(addr, data); else slave.WriteInt32RW(addr, data);
                        return;
                    }
                case EnumDataType.UInt32:
                    {
                        var data = new uint[count];
                        for (int i = 0; i < count; i++) data[i] = (UInt32)Math.Max(0, value);
                        if (readOnly) slave.WriteUInt32R(addr, data); else slave.WriteUInt32RW(addr, data);
                        return;
                    }
                case EnumDataType.Float:
                    {
                        var data = new float[count];
                        for (int i = 0; i < count; i++) data[i] = (float)value;
                        if (readOnly) slave.WriteFloatR(addr, data); else slave.WriteFloatRW(addr, data);
                        return;
                    }
            }
        }

        /// <summary>停止时清零滤波状态与滞后历史，保证每次启动从 0 开始</summary>
        public void ResetRuntime()
        {
            currentSystemValue = 0;
            pumpHistory.Clear();
            pumpLastTickMs = 0;
            pumpAccumMs = 0;
        }

        /// <summary>按规则向从站写入随机数据（Tick 由调用方递增）；可写本站或其它网络从站</summary>
        public void WriteRamdonData(IModbusSlave slave, int stationNo)
        {
            if (slave is null) return;
            if (PumpOutputTarget == EnumPumpOutputTarget.None) return;

            //写入目标：本站用当前从站；其它网络从站按公开 GUID 解析，字节序跟随目标从站
            if (PumpOutputTarget == EnumPumpOutputTarget.OtherStation)
            {
                var target = RuleRuntime.ResolveSlave?.Invoke(TargetNetworkId, TargetStationId);
                if (target is null) return;
                slave = target;
            }

            int addr = StartAddress;
            int len = Length;
            bool readOnly = RegisterType == EnumRegisterType.ReadOnly;

            if (DataType == EnumDataType.Bool)
            {
                var values = new bool[len];
                for (int i = 0; i < len; i++) values[i] = random.Next(0, 2) == 1;
                if (readOnly) slave.WriteBoolR(addr, values);
                else slave.WriteBoolRW(addr, values);
                return;
            }

            if (DataType == EnumDataType.String)
            {
                var chars = new char[len];
                for (int i = 0; i < len; i++) chars[i] = (char)random.Next('A', 'Z' + 1);
                var text = new string(chars);
                if (readOnly) slave.WriteStringR(addr, text);
                else slave.WriteStringRW(addr, text);
                return;
            }

            //直接分配目标类型数组并原地生成随机数，避免 double[] 临时数组、Select 迭代器与闭包
            switch (DataType)
            {
                case EnumDataType.Int16:
                    {
                        var data = new short[len];
                        for (int i = 0; i < len; i++) data[i] = (Int16)(random.NextDouble() * Scale + Offset);
                        if (readOnly) slave.WriteInt16R(addr, data); else slave.WriteInt16RW(addr, data);
                        return;
                    }
                case EnumDataType.UInt16:
                    {
                        var data = new ushort[len];
                        for (int i = 0; i < len; i++) data[i] = (UInt16)Math.Max(0, random.NextDouble() * Scale + Offset);
                        if (readOnly) slave.WriteUInt16R(addr, data); else slave.WriteUInt16RW(addr, data);
                        return;
                    }
                case EnumDataType.Int32:
                    {
                        var data = new int[len];
                        for (int i = 0; i < len; i++) data[i] = (Int32)(random.NextDouble() * Scale + Offset);
                        if (readOnly) slave.WriteInt32R(addr, data); else slave.WriteInt32RW(addr, data);
                        return;
                    }
                case EnumDataType.UInt32:
                    {
                        var data = new uint[len];
                        for (int i = 0; i < len; i++) data[i] = (UInt32)Math.Max(0, random.NextDouble() * Scale + Offset);
                        if (readOnly) slave.WriteUInt32R(addr, data); else slave.WriteUInt32RW(addr, data);
                        return;
                    }
                case EnumDataType.Float:
                    {
                        var data = new float[len];
                        for (int i = 0; i < len; i++) data[i] = (float)(random.NextDouble() * Scale + Offset);
                        if (readOnly) slave.WriteFloatR(addr, data); else slave.WriteFloatRW(addr, data);
                        return;
                    }
            }
        }
    }
}
