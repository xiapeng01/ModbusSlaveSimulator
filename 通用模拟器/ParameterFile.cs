using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.ComponentModel;

namespace 通用模拟器
{
    /// <summary>
    /// 参数文件的可序列化结构：参数名称 + 网络列表。
    /// 用于本地设置与导出文件，使名称随配置一起保存/携带。
    /// </summary>
    public class ParameterFile
    {
        public string? Name { get; set; }
        public BindingList<NetworkConfigModel>? NetworkList { get; set; }

        /// <summary>
        /// 兼容旧格式：旧版本文件是纯网络列表数组（没有名称），
        /// 读取时按数组解析并保留默认名称。
        /// </summary>
        public static ParameterFile Parse(string json)
        {
            var token = JToken.Parse(json);
            if (token is JArray array)
                return new ParameterFile { NetworkList = array.ToObject<BindingList<NetworkConfigModel>>() };

            var file = token.ToObject<ParameterFile>();
            if (file is null) throw new Exception("文件内容不是有效的参数配置");
            return file;
        }

        public string ToJson(Formatting formatting = Formatting.None)
            => JsonConvert.SerializeObject(this, formatting);
    }
}
