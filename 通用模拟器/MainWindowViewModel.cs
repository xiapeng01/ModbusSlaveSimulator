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

                // 销毁旧对象（释放网络/串口资源），再整体替换
                foreach (var item in NetworkList) item.Dispose();
                NetworkList.Clear();
                foreach (var item in list) NetworkList.Add(item);
                if (!string.IsNullOrWhiteSpace(file.Name)) ConfigName = file.Name;

                SelectedNetwork = NetworkList.FirstOrDefault();
                MessageBox.Show($"导入成功，共 {NetworkList.Count} 个网络。", "导入参数",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
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

        [RelayCommand(CanExecute = nameof(IsStopped))]
        void MoveNetworkUp() => MoveNetwork(-1);

        [RelayCommand(CanExecute = nameof(IsStopped))]
        void MoveNetworkDown() => MoveNetwork(1);

        void MoveNetwork(int offset)
        {
            var item = SelectedNetwork;
            if (item is null) return;
            int index = NetworkList.IndexOf(item);
            int target = index + offset;
            if (index < 0 || target < 0 || target >= NetworkList.Count) return;
            NetworkList.RemoveAt(index);
            NetworkList.Insert(target, item);
            SelectedNetwork = item;  // RemoveAt 会使 ListView 清空选中，这里恢复
        }

        CancellationTokenSource? cts;
        readonly List<Task> tasks = new();

        const string TimeFormat = "yyyy-MM-dd HH:mm:ss";
        readonly DispatcherTimer clockTimer;

        public void Dispose()
        {
            clockTimer.Stop();
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
            // BindingList 增删项不会触发 Count 相关绑定刷新，这里手动通知
            NetworkList.ListChanged += (_, __) => OnPropertyChanged(nameof(NetworkList));

            // 状态栏时钟：每秒刷新（DispatcherTimer 在 UI 线程触发，可直接改绑定源）
            clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            clockTimer.Tick += (_, __) => CurrentTimeText = DateTime.Now.ToString(TimeFormat);
            clockTimer.Start();
        }

        [RelayCommand]
        void Start()
        {
            if (cts is not null) return;

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
                // 启动即锁定配置（DoWork 里也会再置一次，这里是为了让 UI 立刻变灰）
                item.CanEdit = false;
                foreach (var station in item.StationList.ToArray()) station.CanEdit = false;

                tasks.Add(Task.Run(() => item?.DoWork(token)));
            }
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

        [RelayCommand]
        async Task Stop()
        {
            if (cts is null) return;
            var source = cts;
            source.Cancel();
            // 等待所有网络退出并释放资源后才置空：期间 IsRunning 仍为 true，
            // 用户无法点击“启动”，避免新旧网络抢同一端口
            try { await Task.WhenAll(tasks.ToArray()); } catch { }
            cts = null;
            source.Dispose();
            tasks.Clear();
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
                Debug.WriteLine($"保存配置失败：{ex.Message}");
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
                MessageBox.Show(ex.Message, "错误");
            }
        }

    }
}
