using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using NModbus.Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;

namespace 通用模拟器
{
    public partial class MainWindowViewModel:ObservableObject,IDisposable
    {
        [ObservableProperty] BindingList<NetworkConfigModel> networkList = new();
        [ObservableProperty] BindingList<NetworkTargetOption> networkTargets = new();
        [ObservableProperty] BindingList<StationTargetOption> stationTargets = new();
        [ObservableProperty] NetworkConfigModel? selectedNetwork;
        /// <summary>参数名称：仅在停止状态可由用户设定，导出时作为默认文件名</summary>
        [ObservableProperty] string configName = DefaultConfigName;
        /// <summary>状态栏显示的当前时间，由 DispatcherTimer 每秒刷新</summary>
        [ObservableProperty] string currentTimeText = DateTime.Now.ToString(TimeFormat);
        public bool IsRunning => cts is not null;
        public bool IsStopped => cts is null;

        protected override void OnPropertyChanged(PropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if(e.PropertyName is nameof(IsRunning))
            {
                OnPropertyChanged(nameof(IsStopped));
            }
        }

        [RelayCommand(CanExecute = nameof(IsStopped))]
        void AddNetwork()
        {
            NetworkConfigModel model = new NetworkConfigModel();
            NetworkList.Add(model);
            SelectedNetwork = model;  // 新增后自动选中，右侧直接可编辑
        }

        const string ConfigFilter = "参数文件 (*.lsts)|*.lsts|所有文件 (*.*)|*.*";
        const string DefaultConfigName = "通用模拟器";

        /// <summary>导入参数：读取文件并反序列化，替换整个参数名称与 NetworkList（仅停止状态可用）</summary>
        [RelayCommand(CanExecute = nameof(IsStopped))]
        void Import()
        {
            var dlg = new OpenFileDialog
            {
                Title = "导入参数",
                Filter = ConfigFilter,
                DefaultExt = ".lsts",
                CheckFileExists = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var js = File.ReadAllText(dlg.FileName);
                var file = ParameterFile.Parse(js);
                var list = file.NetworkList;
                if (list is null) throw new Exception("文件内容不是有效的参数配置");

                // 先对新配置补齐 ID 并完成校验：任何异常都发生在替换前，不影响当前正在使用的配置
                EnsureIds(list);

                // 校验通过后再整体替换；旧对象最后释放，避免中途失败留下半套配置
                var oldItems = NetworkList.ToArray();
                NetworkList.Clear();
                foreach (var item in list) NetworkList.Add(item);
                RefreshStationTargets();
                if (!string.IsNullOrWhiteSpace(file.Name)) ConfigName = file.Name;
                SelectedNetwork = NetworkList.FirstOrDefault();
                foreach (var item in oldItems) item.Dispose();

                MessageBox.Show($"导入成功，共 {NetworkList.Count} 个网络。", "导入参数",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                AppLogger.Exception("导入参数失败", ex);
                MessageBox.Show($"导入失败：{ex.Message}", "导入参数", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>导出参数：以参数名称作为默认文件名，并把名称随配置一起写入文件</summary>
        [RelayCommand]
        void Export()
        {
            var dlg = new SaveFileDialog
            {
                Title = "导出参数",
                Filter = ConfigFilter,
                DefaultExt = ".lsts",
                FileName = SafeFileName(ConfigName) + ".lsts",
                OverwritePrompt = true
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var file = new ParameterFile { Name = ConfigName, NetworkList = NetworkList };
                File.WriteAllText(dlg.FileName, file.ToJson(Formatting.Indented));
                MessageBox.Show("导出成功。", "导出参数", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "导出参数", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>过滤文件名中的非法字符，保证参数名称可直接作为默认文件名</summary>
        static string SafeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return DefaultConfigName;
            var chars = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => chars.Contains(c) ? '_' : c).ToArray()).Trim();
            return cleaned.Length == 0 ? DefaultConfigName : cleaned;
        }

        /// <summary>删除网络：仅停止状态可用，避免运行中释放正在使用的端口/串口</summary>
        [RelayCommand(CanExecute = nameof(IsStopped))]
        void RemoveNetwork(NetworkConfigModel? model)
        {
            if(model == null) return;
            if(MessageBox.Show("确定要删除吗？", "提示", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            if (NetworkList.Contains(model)) NetworkList.Remove(model);
            model?.Dispose();
        }

        /// <summary>复制并粘贴网络：深拷贝选中网络（含站点与规则）并插入其后，仅停止状态可用</summary>
        [RelayCommand(CanExecute = nameof(IsStopped))]
        void DuplicateNetwork(NetworkConfigModel? model)
        {
            if (model is null) return;
            var copy = ModelCloner.Clone(model);
            copy.Name = ModelCloner.WithCopySuffix(copy.Name);

            int index = NetworkList.IndexOf(model);
            if (index < 0) index = NetworkList.Count - 1;
            NetworkList.Insert(index + 1, copy);
            SelectedNetwork = copy;
        }

        /// <summary>复制网络到内存剪贴板：不修改原对象与列表，仅停止状态可用</summary>
        [RelayCommand(CanExecute = nameof(IsStopped))]
        void CopyNetwork(NetworkConfigModel? model)
        {
            if (model is null) return;
            ModelClipboard.Copy(model);
        }

        bool CanPasteNetwork => IsStopped && ModelClipboard.CanPaste<NetworkConfigModel>();

        /// <summary>粘贴网络：从剪贴板快照克隆新对象插入选中项之后；类型不匹配时菜单项已禁用</summary>
        [RelayCommand(CanExecute = nameof(CanPasteNetwork))]
        void PasteNetwork(NetworkConfigModel? target)
        {
            var copy = ModelClipboard.CreatePasteCopy<NetworkConfigModel>();
            if (copy is null) return;
            copy.Name = ModelCloner.WithCopySuffix(copy.Name);

            int index = target is not null ? NetworkList.IndexOf(target) : -1;
            if (index < 0) index = NetworkList.Count - 1;
            NetworkList.Insert(index + 1, copy);
            SelectedNetwork = copy;
        }

        [RelayCommand(CanExecute = nameof(IsStopped))]
        void MoveNetworkUp() => MoveNetwork(-1);

        [RelayCommand(CanExecute = nameof(IsStopped))]
        void MoveNetworkDown() => MoveNetwork(1);

        void MoveNetwork(int offset)
        {
            var item = SelectedNetwork;
            if (item is null) return;
            int index = NetworkList.IndexOf(item);
            if (index < 0) return;
            int target = index + offset;
            if (target < 0 || target >= NetworkList.Count) return;
            NetworkList.RemoveAt(index);
            // RemoveAt 会同步触发 ListChanged；重入可能改变集合长度，插入前重新钳制并在失败时回滚
            target = Math.Clamp(target, 0, NetworkList.Count);
            try { NetworkList.Insert(target, item); }
            catch { if (!NetworkList.Contains(item)) NetworkList.Add(item); }
            SelectedNetwork = item;  // RemoveAt 会使 ListView 清空选中，这里恢复
        }

        CancellationTokenSource? cts;
        readonly List<Task> tasks = new();

        const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
        readonly DispatcherTimer clockTimer;
        /// <summary>Tick 显示刷新：后台高频计数不通知，改由低频 UI 定时器批量刷新，避免跨线程通知风暴</summary>
        readonly DispatcherTimer tickRefreshTimer;
        static readonly TimeSpan TickRefreshInterval = TimeSpan.FromMilliseconds(200);

        public void Dispose()
        {
            clockTimer.Stop();
            tickRefreshTimer.Stop();
            var source = cts;
            cts = null;
            if (source is null) return;
            source.Cancel();
            // 关闭时同步等待（最多 3 秒），确保端口/串口被释放
            try { Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(3)); } catch { }
            source.Dispose();
            tasks.Clear();
        }

        public MainWindowViewModel()
        {
            LoadSetting();//加载配置
            EnsureIds();
            RefreshStationTargets();
            // 规则按稳定 ID 解析目标从站（运行期对象不入配置）
            RuleRuntime.ResolveSlave = (networkId, stationId) => FindStationByGuid(networkId, stationId)?.Slave;
            // BindingList 增删项不会触发 Count 相关绑定刷新，这里手动通知
            NetworkList.ListChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(NetworkList));
                RefreshStationTargets();
            };
            NetworkConfigModel.StationsChanged += (_, __) => RefreshStationTargets();
            // 剪贴板内容变化后刷新各级粘贴命令的可用状态
            ModelClipboard.Changed += (_, __) =>
            {
                PasteNetworkCommand.NotifyCanExecuteChanged();
                foreach (var n in NetworkList.ToArray())
                {
                    n.NotifyPasteCommandsChanged();
                    foreach (var s in n.StationList.ToArray()) s.NotifyPasteCommandsChanged();
                }
            };

            // 状态栏时钟：每秒刷新（DispatcherTimer 在 UI 线程触发，可直接改绑定源）
            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (_, __) => CurrentTimeText = DateTime.Now.ToString(TimeFormat);
            clockTimer.Start();

            // Tick 显示低频批量刷新：仅在运行时启用，停止时数据不变无需刷新
            tickRefreshTimer = new DispatcherTimer { Interval = TickRefreshInterval };
            tickRefreshTimer.Tick += (_, __) => RefreshTickDisplay();
        }

        /// <summary>按公开 GUID 查找站点对象；不依赖运行期才创建的 Slave，故可用于启动前校验</summary>
        StationInformationModel? FindStationByGuid(Guid networkId, Guid stationId)
        {
            // 本方法由后台 DoWork 经 ResolveSlave 调用，UI 可能同时增删网络/站点；
            // 用快照遍历，避免索引与 Count 之间集合缩短导致越界
            foreach (var network in NetworkList.ToArray())
            {
                if (network.Id != networkId) continue;
                foreach (var station in network.StationList.ToArray())
                    if (station.Id == stationId) return station;
                return null;
            }
            return null;
        }

        /// <summary>
        /// 在 UI 线程刷新 Tick 显示：按三层列表的实际可见范围通知——
        /// 左侧网络列表的全部网络、选中网络的全部站点、选中站点的全部规则；
        /// 未展开显示的其它网络站点/其它站点规则跳过，避免全量遍历造成通知风暴。
        /// </summary>
        void RefreshTickDisplay()
        {
            // 左侧网络列表：所有网络项都可见
            foreach (var n in NetworkList.ToArray()) n.NotifyTickChanged();

            // 站点列表：只显示选中网络的站点
            var network = SelectedNetwork;
            if (network is null) return;
            foreach (var s in network.StationList.ToArray()) s.NotifyTickChanged();

            // 规则列表：只显示选中站点的规则
            var station = network.SelectedStation;
            if (station is null) return;
            foreach (var r in station.RulerList.ToArray()) r.NotifyTickChanged();
        }

        [RelayCommand]
        void Start()
        {
            if (cts is not null) return;
            AppLogger.Info($"启动所有网络（共 {NetworkList.Count} 个）", nameof(Start));

            // 启动前校验配置，避免运行期才发现端口/站号冲突
            var error = ValidateConfiguration();
            if (error is not null)
            {
                MessageBox.Show(error, "配置有误", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            cts = new();
            var token = cts.Token;
            tasks.Clear();
            foreach (var item in NetworkList)
            {
                // 重置上一轮停止时留下的释放标记，否则重启后网络会被误判为已释放而无法创建
                item.PrepareForStart();
                // 启动即锁定配置（DoWork 里也会再置一次，这里是为了让 UI 立刻变灰）
                item.CanEdit = false;
                foreach (var station in item.StationList.ToArray()) station.CanEdit = false;

                tasks.Add(Task.Run(() => item?.DoWork(token)));
            }
            tickRefreshTimer.Start();
            OnPropertyChanged(nameof(IsRunning));
            NotifyStoppedCommands();
        }

        /// <summary>运行状态变化后刷新“仅停止状态可用”的命令</summary>
        void NotifyStoppedCommands()
        {
            AddNetworkCommand.NotifyCanExecuteChanged();
            RemoveNetworkCommand.NotifyCanExecuteChanged();
            ImportCommand.NotifyCanExecuteChanged();
            DuplicateNetworkCommand.NotifyCanExecuteChanged();
            MoveNetworkUpCommand.NotifyCanExecuteChanged();
            MoveNetworkDownCommand.NotifyCanExecuteChanged();
        }

        /// <summary>校验站号与端口/串口冲突，返回首个错误信息（null 表示无问题）</summary>
        string? ValidateConfiguration()
        {
            foreach (var network in NetworkList)
            {
                if (!network.IsEnable) continue;

                if (network.Interval < 10)
                    return $"网络「{network.Name}」刷新间隔 {network.Interval} ms 无效，应不小于 10 ms";
                var badIntervalStation = network.StationList
                    .Where(s => s.Interval < 1)
                    .Select(s => s.Name)
                    .FirstOrDefault();
                if (badIntervalStation is not null)
                    return $"网络「{network.Name}」的站点「{badIntervalStation}」间隔时间无效，应不小于 1 ms";

                // 跨网络引用的 GUID 可能悬空（导入/删除站点后），启动前一次性校验，避免运行期静默失效
                foreach (var station in network.StationList)
                {
                    foreach (var ruler in station.RulerList)
                    {
                        if (!ruler.IsEnable) continue;
                        if (ruler.PumpInputSource == EnumPumpInputSource.OtherStation &&
                            (ruler.InputNetworkId == Guid.Empty || ruler.InputStationId == Guid.Empty ||
                             FindStationByGuid(ruler.InputNetworkId, ruler.InputStationId) is null))
                            return $"规则「{ruler.Name}」未选择有效的输入源网络或从站";
                        if (ruler.PumpOutputTarget == EnumPumpOutputTarget.OtherStation &&
                            (ruler.TargetNetworkId == Guid.Empty || ruler.TargetStationId == Guid.Empty ||
                             FindStationByGuid(ruler.TargetNetworkId, ruler.TargetStationId) is null))
                            return $"规则「{ruler.Name}」未选择有效的输出目标网络或从站";
                    }
                }

                var invalid = network.StationList
                    .Where(s => s.IsEnable && (s.StationNo < 1 || s.StationNo > 247))
                    .Select(s => s.StationNo).Distinct().ToArray();
                if (invalid.Length > 0)
                    return $"网络「{network.Name}」存在无效站号：{string.Join("、", invalid)}（有效范围 1~247）";

                var duplicated = network.StationList
                    .Where(s => s.IsEnable)
                    .GroupBy(s => s.StationNo)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key).ToArray();
                if (duplicated.Length > 0)
                    return $"网络「{network.Name}」存在重复站号：{string.Join("、", duplicated)}";
            }

            var conflict = NetworkList
                .Where(n => n.IsEnable)
                .GroupBy(n => n.ProtocolType == EnumProtocolType.ModbusRtu ? $"串口:{n.PortName}" : $"{n.ProtocolType}:{n.IP}:{n.Port}")
                .FirstOrDefault(g => g.Count() > 1);
            if (conflict is not null)
                return $"多个启用的网络使用了相同的通信资源：{string.Join("、", conflict.Select(n => n.Name))}";

            return null;
        }

        [RelayCommand(AllowConcurrentExecutions = false)]
        async Task Stop()
        {
            if (cts is null) return;
            AppLogger.Info("停止所有网络", nameof(Stop));
            var source = cts;
            source.Cancel();
            // 等待所有网络退出并释放资源后才置空：期间 IsRunning 仍为 true，
            // 用户无法点击“启动”，避免新旧网络抢同一端口
            try { await Task.WhenAll(tasks.ToArray()); } catch { }
            cts = null;
            source.Dispose();
            tasks.Clear();
            tickRefreshTimer.Stop();
            OnPropertyChanged(nameof(IsRunning));
            NotifyStoppedCommands();
        } 

        const string fileName = "通用模拟器Setting.json";
        // 配置放在 %LOCALAPPDATA%\Modbus从站模拟器 下：
        // 安装到 C:\Program Files 后程序目录不可写，普通权限下会静默丢失配置
        static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Modbus从站模拟器");
        static string SettingPath => Path.Combine(AppDataDir, fileName);
        // 旧版本（安装在哪就存哪）的遗留位置，仅用于首次迁移
        static string LegacySettingPath => Path.Combine(AppContext.BaseDirectory, fileName);

        public void SaveSetting()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                var file = new ParameterFile { Name = ConfigName, NetworkList = NetworkList };
                File.WriteAllText(SettingPath, file.ToJson());
            }
            catch (Exception ex)
            {
                AppLogger.Exception("保存配置失败", ex);
            }
        }

        public void LoadSetting()
        {
            try
            {
                // 旧配置迁移：程序目录里有、%LOCALAPPDATA% 里没有时，复制一份过来
                if (!File.Exists(SettingPath) && File.Exists(LegacySettingPath))
                {
                    Directory.CreateDirectory(AppDataDir);
                    File.Copy(LegacySettingPath, SettingPath, overwrite: true);
                }

                if (!File.Exists(SettingPath)) return;
                var js = File.ReadAllText(SettingPath);
                var file = ParameterFile.Parse(js);
                if (!string.IsNullOrWhiteSpace(file.Name)) ConfigName = file.Name;
                var list = file.NetworkList;
                if (list is not null)
                {
                    NetworkList.Clear();
                    foreach (var item in list)
                    {
                        NetworkList.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                // 启动加载失败不能阻断程序：只记录日志，继续使用默认/当前配置
                AppLogger.Exception("加载本地配置失败", ex);
            }
        }

        /// <summary>旧配置补齐稳定 ID，保证规则跨网络引用可用</summary>
        void EnsureIds() => EnsureIds(NetworkList);

        /// <summary>对指定网络列表补齐稳定 ID（导入时先处理新列表，避免污染当前配置）</summary>
        void EnsureIds(IEnumerable<NetworkConfigModel> list)
        {
            foreach (var network in list)
            {
                if (network.Id == Guid.Empty) network.Id = Guid.NewGuid();
                foreach (var station in network.StationList)
                {
                    if (station.Id == Guid.Empty) station.Id = Guid.NewGuid();
                }
            }
        }

        bool refreshingStationTargets;

        /// <summary>刷新所有站点下拉选项（供伺服流量泵选择输出目标）</summary>
        public void RefreshStationTargets()
        {
            // ListChanged/StationsChanged 会在增删项时同步触发；重入时直接返回，
            // 避免在同一通知链里反复 Add/RemoveAt 导致索引失效与绑定集合枚举异常
            if (refreshingStationTargets) return;
            refreshingStationTargets = true;
            try
            {
                var networks = RuleRuntime.Networks;
                var stations = RuleRuntime.Stations;
                var networkList = NetworkList.ToArray();

                // 原地更新选项对象，保持引用稳定，避免 SelectedItem 因对象重建而丢失
                var networkIds = new HashSet<Guid>();
                foreach (var network in networkList)
                {
                    networkIds.Add(network.Id);
                    var opt = networks.FirstOrDefault(o => o.Id == network.Id);
                    if (opt is null) networks.Add(new NetworkTargetOption { Id = network.Id, Name = network.Name });
                    else opt.Name = network.Name;
                }
                for (int i = networks.Count - 1; i >= 0; i--)
                {
                    if (i >= networks.Count) continue;
                    if (!networkIds.Contains(networks[i].Id)) networks.RemoveAt(i);
                }

                var stationIds = new HashSet<Guid>();
                foreach (var network in networkList)
                {
                    foreach (var station in network.StationList.ToArray())
                    {
                        stationIds.Add(station.Id);
                        var opt = stations.FirstOrDefault(o => o.StationId == station.Id);
                        var display = $"{station.StationNo} ({station.Name})";
                        if (opt is null)
                        {
                            stations.Add(new StationTargetOption
                            {
                                NetworkId = network.Id, StationId = station.Id,
                                NetworkName = network.Name, StationNo = station.StationNo,
                                StationName = station.Name, DisplayName = display
                            });
                        }
                        else
                        {
                            opt.NetworkId = network.Id;
                            opt.NetworkName = network.Name;
                            opt.StationNo = station.StationNo;
                            opt.StationName = station.Name;
                            opt.DisplayName = display;
                        }
                    }
                }
                for (int i = stations.Count - 1; i >= 0; i--)
                {
                    if (i >= stations.Count) continue;
                    if (!stationIds.Contains(stations[i].StationId)) stations.RemoveAt(i);
                }

                // 保持集合实例稳定：RuleRuntime 的集合已被原地更新，只有首次（实例不同）才赋值，
                // 避免每次编辑站点名/站号都整体替换 BindingList 引发三层 ComboBox 级联重绑定
                if (!ReferenceEquals(NetworkTargets, networks)) NetworkTargets = networks;
                if (!ReferenceEquals(StationTargets, stations)) StationTargets = stations;

                // 配置加载/列表变化后，按 Guid 重新关联下拉选择
                foreach (var network in networkList)
                    foreach (var station in network.StationList.ToArray())
                        foreach (var ruler in station.RulerList.ToArray())
                            ruler.SyncTargetOptions();
            }
            finally { refreshingStationTargets = false; }
        }

    }
}
