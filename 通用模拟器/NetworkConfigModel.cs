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
using System.IO.Ports;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
        ModbusRtuOverTcp
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
        public int[] IntervalValues => new int[] { 10, 20, 30, 50,60,80, 100, 150, 200, 300,400, 500,600,800, 1000, 1500, 2000, 3000, 5000 };
        public EnumProtocolType[] ProtocolTypeValues => Enum.GetValues<EnumProtocolType>();
        /// <summary>串口号列表（支持运行期刷新，见 RefreshPortNames）</summary>
        [JsonIgnore] public ObservableCollection<string> PortNameValues { get; } = new ObservableCollection<string>(SerialPort.GetPortNames());

        /// <summary>重新枚举可用串口（热插拔后调用），保留仍存在且仍被选中的项</summary>
        public void RefreshPortNames()
        {
            var names = SerialPort.GetPortNames();
            for (int i = PortNameValues.Count - 1; i >= 0; i--)
            {
                if (!names.Contains(PortNameValues[i])) PortNameValues.RemoveAt(i);
            }
            foreach (var name in names)
            {
                if (!PortNameValues.Contains(name)) PortNameValues.Add(name);
            }
        }
        public int[] BaudRateValues => new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
        public int[] DataBitsValues => new int[] { 5, 6, 7, 8 };
        public Parity[] ParityValues => Enum.GetValues<Parity>();
        public StopBits[] StopBitsValues => Enum.GetValues<StopBits>();

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
        [property: JsonIgnore][ObservableProperty] int tick = 0;
        /// <summary>运行期最近一次错误信息（不弹窗，避免按刷新间隔刷屏）</summary>
        [property: JsonIgnore][ObservableProperty] string? lastError = null;

        /// <summary>保护 SlaveNetwork 的创建/释放，避免停止与启动并发时互相踩踏</summary>
        [JsonIgnore] readonly object networkLock = new object();
        /// <summary>创建失败后的重试时间点（退避），避免按刷新间隔反复尝试</summary>
        [JsonIgnore] DateTime nextRetryTime = DateTime.MinValue;
        /// <summary>LastError 是否为“网络级”错误（不会被下一周期自动清除）</summary>
        bool lastErrorSticky;

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
            stationListHandler ??= (_, __) => OnPropertyChanged(nameof(StationList));
            StationList.ListChanged -= stationListHandler;
            StationList.ListChanged += stationListHandler;
        }

        partial void OnStationListChanged(BindingList<StationInformationModel> value) => HookStationList();

        [JsonIgnore] public IModbusSlaveNetwork? SlaveNetwork;
        [property: JsonIgnore]
        [RelayCommand]
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
        [RelayCommand]
        void RemoveStation(StationInformationModel? station)
        {
            if (station is null) return;
            if (StationList.Contains(station)) StationList.Remove(station);
        }

        bool isDsiposed = false;
        /// <summary>当前网络是否已被释放（释放后监听任务抛出的异常属预期行为，不应报错）</summary>
        bool released;
        public void Dispose()
        {
            isDsiposed = true;
            foreach (var item in StationList)
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
        void ReportError(string message, bool sticky = false)
        {
            Debug.WriteLine(message);
            errorReported = true;
            if (LastError == message) { lastErrorSticky |= sticky; return; }
            LastError = message;
            lastErrorSticky = sticky;
        }

        /// <summary>创建（或复用）从站网络；失败时记录错误并返回 null</summary>
        /// <remarks>耗时的创建过程放在锁外，避免阻塞 UI 线程上的 Dispose（如 TCP 连接超时）</remarks>
        IModbusSlaveNetwork? GetOrCreateNetwork()
        {
            lock (networkLock)
            {
                if (SlaveNetwork is not null) return SlaveNetwork;
            }

            // 退避：创建失败后至少间隔 2 秒再试，避免按刷新间隔（最快 10ms）反复尝试
            if (DateTime.Now < nextRetryTime) return null;

            IModbusSlaveNetwork network;
            try
            {
                network = ProtocolType switch
                {
                    EnumProtocolType.ModbusRtu => Tools.CreateRtuSlaveNetwork(PortName),
                    EnumProtocolType.ModbusTcp => Tools.CreateTcpSlaveNetwork(Port),
                    EnumProtocolType.ModbusRtuOverTcp => Tools.CreateRtuOverTcpSlaveNetwork(Port),
                    _ => throw new NotSupportedException($"Protocol type {ProtocolType} is not supported.")
                };
            }
            catch (Exception ex)
            {
                nextRetryTime = DateTime.Now.AddSeconds(2);
                ReportError(ex.Message);
                return null;
            }

            lock (networkLock)
            {
                if (SlaveNetwork is not null)
                {
                    // 并发情况下已被创建，丢弃本次创建的实例
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
            try
            {
                while (!token.IsCancellationRequested && !isDsiposed)
                {
                    try
                    {
                        await Task.Delay(Interval, token);
                        if (!IsEnable) continue;
                        CanEdit = false;

                        var network = GetOrCreateNetwork();
                        if (network is null) continue;

                        // 用快照遍历，避免运行期集合变动导致“集合已修改”异常
                        var stations = StationList.ToArray();
                        foreach (var station in stations)
                        {
                            if (!station.IsEnable) continue;
                            if (station.StationNo < 1 || station.StationNo > 247)
                            {
                                ReportError($"站号 {station.StationNo} 超出 1~247，已跳过站点「{station.Name}」");
                                continue;
                            }
                            if (stations.Count(s => s.IsEnable && s.StationNo == station.StationNo) > 1)
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
                                ruler.WriteRamdonData(station.Slave, station.StationNo);
                                ruler.Tick++;
                            }
                            station.Tick++;
                        }
                        Tick++;
                        // 本周期无错误则清除上一次的（非网络级）错误提示
                        if (!errorReported && LastError is not null && !lastErrorSticky) LastError = null;
                        errorReported = false;
                    }
                    catch (OperationCanceledException) { throw; }//取消：原样抛出，交给 finally 清理
                    catch (Exception ex)
                    {
                        // 网络已被释放（删除/导入替换/停止清理）时静默退出，按取消处理
                        if (IsReleased(null)) throw new OperationCanceledException();
                        ReportError(ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                // 无论取消、异常还是 isDsiposed 退出，都保证释放资源并恢复可编辑
                foreach (var item in StationList)
                {
                    item.CanEdit = true;
                    item.Slave = null;
                    item.Elapsed = 0;
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
        public int[] IntervalValues => new int[] { 1, 2, 3, 5, 6, 8, 10, 15, 20, 30, 50,60,70,80, 100, 150, 200, 300,400, 500,600,800, 1000, 1500, 2000, 3000, 5000 };

        public EnumEndian[] EndialValues => Enum.GetValues<EnumEndian>();
        [ObservableProperty] bool isEnable = true;
        [ObservableProperty] string name = "未命名";
        [ObservableProperty] string description = "";

        [ObservableProperty] int stationNo = 1;
        [ObservableProperty] BindingList<RulerModel> rulerList = new();
        [property: JsonIgnore][ObservableProperty] int tick = 0;
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

        public void Dispose()
        {
            Slave = null;
        }

        [property: JsonIgnore]
        [RelayCommand]
        void AddRuler()
        {
            RulerModel ruler = new RulerModel();
            ruler.Name = $"规则{RulerList.Count + 1}";
            RulerList.Add(ruler);
            SelectedRuler = ruler;  // 新增后自动选中，右侧直接可编辑
        }

        [property: JsonIgnore]
        [RelayCommand]
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
    }

    public partial class RulerModel:ObservableObject
    {
        public EnumRegisterType[] RegisterTypeValues => Enum.GetValues<EnumRegisterType>();
        public EnumDataType[] DataTypeValues => Enum.GetValues<EnumDataType>();

        [ObservableProperty] bool isEnable = true;
        [ObservableProperty] string name = "未命名";
        [ObservableProperty] string description = "";

        [ObservableProperty] EnumRegisterType registerType = EnumRegisterType.ReadWrite;
        [ObservableProperty] EnumDataType dataType = EnumDataType.UInt16;
        [ObservableProperty] int startAddress = 0;
        [ObservableProperty] int length = 1;
        [property: JsonIgnore][ObservableProperty] int tick = 0;
        Random random = new Random(Guid.NewGuid().GetHashCode());

        /// <summary>校验规则参数，返回错误信息（null 表示合法）</summary>
        public string? Validate()
        {
            if (StartAddress < 0 || StartAddress > 65535) return $"规则「{Name}」起始地址 {StartAddress} 超出 0~65535";
            if (Length < 1 || Length > 2000) return $"规则「{Name}」数量 {Length} 超出 1~2000";
            if (StartAddress + Length > 65536) return $"规则「{Name}」地址范围 {StartAddress}~{StartAddress + Length} 超出 65535";
            return null;
        }

        /// <summary>按规则向从站写入随机数据（Tick 由调用方递增）</summary>
        public void WriteRamdonData(IModbusSlave slave, int stationNo)
        {
            if (slave is null) return;

            int addr = StartAddress;
            int len = Length;
            bool readOnly = RegisterType == EnumRegisterType.ReadOnly;

            if (DataType == EnumDataType.Bool)
            {
                var values = Enumerable.Range(0, len).Select(_ => random.Next(0, 2) == 1).ToArray();
                if (readOnly) slave.WriteBoolR(addr, values);
                else slave.WriteBoolRW(addr, values);
                return;
            }

            if (DataType == EnumDataType.String)
            {
                var text = new string(Enumerable.Range(0, len)
                    .Select(_ => (char)random.Next('A', 'Z' + 1)).ToArray());
                if (readOnly) slave.WriteStringR(stationNo, addr, text);
                else slave.WriteStringRW(stationNo, addr, text);
                return;
            }

            double[] data = new double[len];
            for (int i = 0; i < len; i++)
            {
                data[i] = random.NextDouble() * 100;
            }

            if (readOnly)
            {
                switch (DataType)
                {
                    case EnumDataType.Int16:
                        slave.WriteInt16R(addr, data.Select(a => (Int16)a).ToArray());
                        break;
                    case EnumDataType.UInt16:
                        slave.WriteUInt16R(addr, data.Select(a => (UInt16)a).ToArray());
                        break;
                    case EnumDataType.Int32:
                        slave.WriteInt32R(addr, data.Select(a => (Int32)a).ToArray());
                        break;
                    case EnumDataType.UInt32:
                        slave.WriteUInt32R(addr, data.Select(a => (UInt32)a).ToArray());
                        break;
                    case EnumDataType.Float:
                        slave.WriteFloatR(addr, data.Select(a => (float)a).ToArray());
                        break;
                }
            }
            else
            {
                switch (DataType)
                {
                    case EnumDataType.Int16:
                        slave.WriteInt16RW(addr, data.Select(a => (Int16)a).ToArray());
                        break;
                    case EnumDataType.UInt16:
                        slave.WriteUInt16RW(addr, data.Select(a => (UInt16)a).ToArray());
                        break;
                    case EnumDataType.Int32:
                        slave.WriteInt32RW(addr, data.Select(a => (Int32)a).ToArray());
                        break;
                    case EnumDataType.UInt32:
                        slave.WriteUInt32RW(addr, data.Select(a => (UInt32)a).ToArray());
                        break;
                    case EnumDataType.Float:
                        slave.WriteFloatRW(addr, data.Select(a => (float)a).ToArray());
                        break;
                }
            }
        }
    }
}
