using Newtonsoft.Json;

namespace 通用模拟器
{
    /// <summary>
    /// 列表项深拷贝：复制后立即在选中项后插入副本，无需剪贴板状态。
    /// 借助 Json.NET（与导入/导出/保存同一机制）：[JsonIgnore] 的运行期字段
    /// 如 SlaveNetwork、Slave、CanEdit、Tick、SelectedXxx、PortNameValues 均不会被复制，
    /// 子集合（站点/规则）也整棵复制，因此副本与原对象互不共享引用，不产生孤儿对象。
    /// </summary>
    static class ModelCloner
    {
        /// <summary>给副本名称加后缀（重复复制不叠加）</summary>
        public static string WithCopySuffix(string name)
        {
            const string suffix = " 副本";
            return name.EndsWith(suffix, StringComparison.Ordinal) ? name : name + suffix;
        }

        public static T Clone<T>(T source)
        {
            string json = JsonConvert.SerializeObject(source);
            T copy = JsonConvert.DeserializeObject<T>(json)!;

            // 稳定 ID 属于对象身份，副本必须重新生成，否则跨网络输出目标引用会指向原对象
            if (copy is NetworkConfigModel network)
            {
                network.Id = Guid.NewGuid();
                foreach (var station in network.StationList)
                {
                    station.Id = Guid.NewGuid();
                    foreach (var ruler in station.RulerList) RedirectRulerTarget(ruler);
                }
            }
            else if (copy is StationInformationModel station)
            {
                station.Id = Guid.NewGuid();
                foreach (var ruler in station.RulerList) RedirectRulerTarget(ruler);
            }

            return copy;
        }

        /// <summary>复制后清空副本的输入/输出目标引用，避免指向原对象造成隐式共享；由用户重新选择</summary>
        static void RedirectRulerTarget(RulerModel ruler)
        {
            //所有规则的输出目标都可能指向其它网络从站，一律清空
            ruler.TargetNetworkId = Guid.Empty;
            ruler.TargetStationId = Guid.Empty;
            if (ruler.RulerType is EnumRulerType.ServoFlowPump or EnumRulerType.ValueCopy)
            {
                ruler.InputNetworkId = Guid.Empty;
                ruler.InputStationId = Guid.Empty;
            }
        }
    }
}
