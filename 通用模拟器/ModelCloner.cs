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
            return JsonConvert.DeserializeObject<T>(json)!;
        }
    }
}
