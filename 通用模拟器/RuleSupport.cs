using CommunityToolkit.Mvvm.ComponentModel;
using NModbus;
using NModbus.Extensions;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;

namespace 通用模拟器
{
    /// <summary>规则类型。默认值为生成随机数，保证旧配置兼容。</summary>
    public enum EnumRulerType
    {
        [Description("生成随机数")] Random = 0,
        [Description("伺服流量泵")] ServoFlowPump = 1,
        [Description("值拷贝")] ValueCopy = 2
    }

    /// <summary>伺服流量泵规则的输出目标</summary>
    public enum EnumPumpOutputTarget
    {
        [Description("不写输出")] None = 0,
        [Description("本站")] ThisStation = 1,
        [Description("其它网络的从站")] OtherStation = 2
    }

    /// <summary>伺服流量泵/值拷贝规则的输入来源（默认本站，兼容旧配置）</summary>
    public enum EnumPumpInputSource
    {
        [Description("本站")] ThisStation = 0,
        [Description("其它网络的从站")] OtherStation = 1,
        /// <summary>值拷贝专用：源为一个固定字符串，按目标数据类型解析</summary>
        [Description("固定值")] FixedValue = 2
    }

    /// <summary>枚举显示名：读取 DescriptionAttribute，便于在 ComboBox 中显示中文。反射结果缓存。</summary>
    public class EnumDescriptionConverter : ConverterBase
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<Enum, string> cache = new();

        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is Enum e)
            {
                if (cache.TryGetValue(e, out var desc)) return desc;
                var field = e.GetType().GetField(e.ToString());
                var attr = field?.GetCustomAttribute<DescriptionAttribute>();
                var result = attr?.Description ?? e.ToString();
                cache[e] = result;
                return result;
            }
            return value?.ToString() ?? string.Empty;
        }

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    /// <summary>按规则类型控制控件显隐：ConverterParameter 传枚举名，前缀 ! 表示取反。参数解析缓存。</summary>
    public class RulerTypeVisibilityConverter : ConverterBase
    {
        sealed class Spec
        {
            public bool Invert;
            public string[] Types = Array.Empty<string>();
        }
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Spec> specCache = new();

        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var p = parameter?.ToString() ?? "";
            var spec = specCache.GetOrAdd(p, key =>
            {
                bool invert = key.StartsWith("!");
                var specText = key.TrimStart('!');
                //支持逗号分隔的多个类型，如 "ServoFlowPump,ValueCopy"
                return new Spec
                {
                    Invert = invert,
                    Types = specText.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim()).ToArray()
                };
            });
            var valueText = value?.ToString();
            bool match = spec.Types.Any(t => string.Equals(t, valueText, StringComparison.OrdinalIgnoreCase));
            return (match ^ spec.Invert) ? Visibility.Visible : Visibility.Collapsed;
        }

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    /// <summary>目标网络/从站下拉框显隐：仅伺服流量泵且输出目标为其它网络从站时显示</summary>
    public class TargetStationVisibilityConverter : ConverterBase
    {
        public override object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not RulerModel ruler) return Visibility.Collapsed;
            bool show = ruler.RulerType == EnumRulerType.ServoFlowPump
                     && ruler.PumpOutputTarget == EnumPumpOutputTarget.OtherStation;
            return show ? Visibility.Visible : Visibility.Collapsed;
        }

        public override object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => DependencyProperty.UnsetValue;
    }

    /// <summary>把站点列表按所选网络 GUID 过滤（MultiBinding：站点列表 + 目标网络 GUID）</summary>
    public class StationOptionsConverter : MarkupExtension, IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            var result = new List<StationTargetOption>();
            if (values.Length < 2 || values[0] is not IEnumerable<StationTargetOption> all) return result;
            Guid networkId;
            if (values[1] is Guid g) networkId = g;
            else if (values[1] is string s && Guid.TryParse(s, out var parsed)) networkId = parsed;
            else return result;
            result.AddRange(all.ToArray().Where(x => x.NetworkId == networkId));
            return result;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => targetTypes.Select(_ => (object)DependencyProperty.UnsetValue).ToArray();

        public override object ProvideValue(IServiceProvider serviceProvider) => this;
    }

    /// <summary>其它网络从站的下拉选项（保存 GUID，不保存对象引用，避免循环引用/孤儿对象）</summary>
    public partial class StationTargetOption : ObservableObject
    {
        [ObservableProperty] Guid networkId;
        [ObservableProperty] Guid stationId;
        [ObservableProperty] string networkName = "";
        [ObservableProperty] int stationNo;
        [ObservableProperty] string stationName = "";
        [ObservableProperty] string displayName = "";

        public override string ToString() => DisplayName;
    }

    /// <summary>网络下拉选项（GUID 标识）</summary>
    public partial class NetworkTargetOption : ObservableObject
    {
        [ObservableProperty] Guid id;
        [ObservableProperty] string name = "";

        public override string ToString() => Name;
    }

    /// <summary>
    /// 规则运行期解析服务：把配置中的 GUID 解析为运行期从站对象。
    /// 由 MainWindowViewModel 在启动时注入解析函数，避免 RulerModel 持有全局引用。
    /// </summary>
    static class RuleRuntime
    {
        public static Func<Guid, Guid, IModbusSlave?>? ResolveSlave { get; set; }

        /// <summary>全局选项列表：由 MainWindowViewModel 填充，RulerModel 直接暴露给 UI，避免跨视觉树 RelativeSource 绑定</summary>
        public static BindingList<NetworkTargetOption> Networks { get; } = new();
        public static BindingList<StationTargetOption> Stations { get; } = new();
    }
}
