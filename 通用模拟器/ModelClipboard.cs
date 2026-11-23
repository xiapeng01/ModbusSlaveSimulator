namespace 通用模拟器
{
    /// <summary>
    /// 列表项的内存剪贴板：复制时保存一份深拷贝快照，粘贴时再从快照克隆新对象。
    /// 复制不修改原对象与列表；一次复制可多次粘贴，各副本互不共享引用。
    /// 按类型隔离：只有类型匹配的列表才能粘贴。
    /// </summary>
    static class ModelClipboard
    {
        static object? content;

        /// <summary>剪贴板内容发生变更（复制或清空）后通知命令刷新可用状态</summary>
        public static event EventHandler? Changed;

        public static bool IsEmpty => content is null;

        /// <summary>复制：仅深拷贝一份快照存入剪贴板，不触碰原对象与任何列表</summary>
        public static void Copy<T>(T source) where T : class
        {
            if (source is null) return;
            content = ModelCloner.Clone(source);
            Changed?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>当前剪贴板内容是否为指定类型（用于粘贴前的类型校验）</summary>
        public static bool CanPaste<T>() where T : class => content is T;

        /// <summary>粘贴：从快照再克隆一份新对象返回；不消耗剪贴板，可重复调用</summary>
        public static T? CreatePasteCopy<T>() where T : class
        {
            if (content is not T typed) return null;
            return ModelCloner.Clone(typed);
        }

        public static void Clear()
        {
            if (content is null) return;
            content = null;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }
}
