using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace YeyouPlusPlus.PhoneBox.Client
{
    /// <summary>
    /// 单个虚拟按键定义（从机侧，由主机下发的 KeyLayout 解析而来）。
    /// </summary>
    public sealed class ClientKeyDefinition
    {
        /// <summary>主机布局中的名称（如 up / down / action1）。</summary>
        public string Name { get; set; }

        /// <summary>按键上显示的文字。</summary>
        public string Label { get; set; }

        /// <summary>Windows 虚拟键码（Virtual-Key Code）。</summary>
        public int KeyCode { get; set; }

        /// <summary>归一化中心横坐标（0..1，相对画面宽）。</summary>
        public double X { get; set; }

        /// <summary>归一化中心纵坐标（0..1，相对画面高）。</summary>
        public double Y { get; set; }

        /// <summary>半径占画面短边的比例。</summary>
        public double Size { get; set; }

        /// <summary>透明度 0.05..1（越大越不透明）。</summary>
        public double Opacity { get; set; }
    }

    /// <summary>
    /// 解析主机下发的 KeyLayout JSON，并回退默认布局。
    ///
    /// 主机下发的默认布局仅含 name / keyCode；完整布局还含 label / x / y / size / opacity。
    /// 本类兼容两种字段：缺少的位置字段按已知名称映射到固定位置，未知名称按顺序铺开。
    /// </summary>
    public static class ClientKeyLayout
    {
        private const double DefaultSize = 0.09;
        private const double DefaultOpacity = 0.45;

        private static readonly Dictionary<string, double[]> NameToPosition =
            new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
            {
                { "up", new[] { 0.27, 0.50 } },
                { "down", new[] { 0.27, 0.80 } },
                { "left", new[] { 0.18, 0.65 } },
                { "right", new[] { 0.36, 0.65 } },
                { "action1", new[] { 0.82, 0.65 } },
                { "action2", new[] { 0.72, 0.80 } },
                { "action3", new[] { 0.66, 0.65 } },
                { "action4", new[] { 0.72, 0.50 } },
                { "a", new[] { 0.82, 0.65 } },
                { "b", new[] { 0.72, 0.80 } },
                { "c", new[] { 0.66, 0.65 } },
                { "d", new[] { 0.72, 0.50 } }
            };

        /// <summary>解析 KeyLayout JSON。失败或为空时返回 null（调用方回退 <see cref="Default"/>）。</summary>
        public static IReadOnlyList<ClientKeyDefinition> Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                var obj = JObject.Parse(json);
                var keys = obj["keys"] as JArray;
                if (keys == null || keys.Count == 0)
                {
                    return null;
                }

                var result = new List<ClientKeyDefinition>(keys.Count);
                for (int i = 0; i < keys.Count; i++)
                {
                    var k = keys[i] as JObject;
                    if (k == null)
                    {
                        continue;
                    }

                    int keyCode = k.Value<int?>("keyCode") ?? 0;
                    if (keyCode <= 0)
                    {
                        continue;
                    }

                    string name = k.Value<string>("name") ?? string.Empty;
                    string label = k.Value<string>("label") ?? name;
                    if (string.IsNullOrEmpty(label))
                    {
                        label = name;
                    }

                    double x = k.Value<double?>("x") ?? double.NaN;
                    double y = k.Value<double?>("y") ?? double.NaN;
                    if (double.IsNaN(x) || double.IsNaN(y))
                    {
                        double[] pos = ResolvePosition(name, i);
                        x = pos[0];
                        y = pos[1];
                    }

                    double size = k.Value<double?>("size") ?? DefaultSize;
                    double opacity = k.Value<double?>("opacity") ?? DefaultOpacity;

                    result.Add(new ClientKeyDefinition
                    {
                        Name = name,
                        Label = label,
                        KeyCode = keyCode,
                        X = Clamp(x, 0.02, 0.98),
                        Y = Clamp(y, 0.02, 0.98),
                        Size = Clamp(size, 0.04, 0.30),
                        Opacity = Clamp(opacity, 0.05, 1.0)
                    });
                }

                return result.Count > 0 ? result : null;
            }
            catch (Exception ex)
            {
                PhoneBoxLog.Warn("解析主机按键布局失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>默认布局：方向键 + 两个常用动作键（与主机默认 KeyLayout 对齐）。</summary>
        public static IReadOnlyList<ClientKeyDefinition> Default()
        {
            return new List<ClientKeyDefinition>
            {
                Create("up", "↑", 38),
                Create("down", "↓", 40),
                Create("left", "←", 37),
                Create("right", "→", 39),
                Create("action1", "X", 88),
                Create("action2", "Z", 90)
            };
        }

        private static ClientKeyDefinition Create(string name, string label, int keyCode)
        {
            double[] pos = NameToPosition[name];
            return new ClientKeyDefinition
            {
                Name = name,
                Label = label,
                KeyCode = keyCode,
                X = pos[0],
                Y = pos[1],
                Size = DefaultSize,
                Opacity = DefaultOpacity
            };
        }

        private static double[] ResolvePosition(string name, int index)
        {
            double[] pos;
            if (!string.IsNullOrEmpty(name) && NameToPosition.TryGetValue(name, out pos))
            {
                return pos;
            }

            // 未知名称按顺序在画面上部平均铺开。
            double x = index <= 0 ? 0.5 : 0.18 + 0.64 * ((index - 1) % 4) / 3.0;
            double y = index <= 0 ? 0.65 : 0.35;
            return new[] { x, y };
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min)
            {
                return min;
            }
            if (value > max)
            {
                return max;
            }
            return value;
        }
    }
}
