using Newtonsoft.Json;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>
    /// 手机盒子协议消息类型。
    /// </summary>
    public enum MessageType
    {
        /// <summary>手机 → 电脑：握手。</summary>
        Hello = 1,

        /// <summary>电脑 → 手机：推流 / 按键布局配置。</summary>
        Config = 2,

        /// <summary>手机 → 电脑：就绪。</summary>
        Ready = 3,

        /// <summary>手机 → 电脑：按键事件。</summary>
        Key = 4
    }

    /// <summary>
    /// 手机 → 电脑握手消息。
    /// </summary>
    public class HelloMessage
    {
        [JsonProperty("ClientType")]
        public string ClientType { get; set; }

        [JsonProperty("Version")]
        public string Version { get; set; }
    }

    /// <summary>
    /// 电脑 → 手机配置消息：分辨率 / 帧率 / 码率 / 按键布局。
    /// </summary>
    public class ConfigMessage
    {
        [JsonProperty("Width")]
        public int Width { get; set; }

        [JsonProperty("Height")]
        public int Height { get; set; }

        [JsonProperty("FrameRate")]
        public int FrameRate { get; set; }

        [JsonProperty("BitrateKbps")]
        public int BitrateKbps { get; set; }

        [JsonProperty("KeyLayout")]
        public string KeyLayout { get; set; }
    }

    /// <summary>
    /// 手机 → 电脑就绪消息。
    /// </summary>
    public class ReadyMessage
    {
        [JsonProperty("Ready")]
        public bool Ready { get; set; }
    }

    /// <summary>
    /// 手机 → 电脑按键消息。
    /// </summary>
    public class KeyMessage
    {
        /// <summary>虚拟键码（Virtual-Key Code）。</summary>
        [JsonProperty("KeyCode")]
        public int KeyCode { get; set; }

        /// <summary>硬件扫描码，0 表示未提供。</summary>
        [JsonProperty("ScanCode")]
        public int ScanCode { get; set; }

        /// <summary>true = 按下，false = 释放。</summary>
        [JsonProperty("IsDown")]
        public bool IsDown { get; set; }

        /// <summary>手机端时间戳（毫秒）。</summary>
        [JsonProperty("Timestamp")]
        public long Timestamp { get; set; }
    }

    /// <summary>
    /// 手机盒子协议的 JSON 序列化辅助。
    /// </summary>
    public static class PhoneBoxProtocol
    {
        private static readonly JsonSerializerSettings SerializerSettings =
            new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };

        /// <summary>把消息对象序列化为 JSON 字符串。</summary>
        public static string Serialize<T>(T message)
        {
            return JsonConvert.SerializeObject(message, SerializerSettings);
        }

        /// <summary>把 JSON 字符串反序列化为消息对象。</summary>
        public static T Deserialize<T>(string json)
        {
            return JsonConvert.DeserializeObject<T>(json);
        }
    }
}
