using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace YeyouPlusPlus.PhoneBox.Client
{
    /// <summary>
    /// 从机端透明虚拟按键悬浮层 + 窗口键盘捕获。
    ///
    /// 两种输入都支持：
    ///   - 鼠标：点击 / 按住画面上的半透明圆钮（按键位置、大小、透明度来自主机 KeyLayout）。
    ///   - 物理键盘：方向键 / Z / X / C / V / 空格 / 回车映射为玩家 2 按键。
    ///
    /// 按键统一通过 <see cref="KeyAction"/>（keyCode, scanCode, isDown）抛给上层发送到主机；
    /// scanCode 填 0，由主机端 MapVirtualKey 兜底换算。
    /// </summary>
    public sealed class VirtualKeyOverlay : Canvas
    {
        /// <summary>按键动作（参数：KeyCode、ScanCode、是否按下）。</summary>
        public event Action<int, int, bool> KeyAction;

        private readonly List<ClientKeyDefinition> keys = new List<ClientKeyDefinition>();
        private readonly Dictionary<int, ClientKeyDefinition> keyDefs = new Dictionary<int, ClientKeyDefinition>();
        private readonly Dictionary<int, Border> buttons = new Dictionary<int, Border>();
        private readonly Dictionary<int, bool> buttonPressed = new Dictionary<int, bool>();
        private readonly HashSet<int> keyboardPressed = new HashSet<int>();

        private Window attachedWindow;

        public VirtualKeyOverlay()
        {
            Background = Brushes.Transparent;
        }

        /// <summary>设置虚拟按键布局（替换现有按钮）。</summary>
        public void SetKeys(IReadOnlyList<ClientKeyDefinition> newKeys)
        {
            ClearButtons();

            if (newKeys != null)
            {
                foreach (var key in newKeys)
                {
                    AddButton(key);
                }
            }

            LayoutButtons();
        }

        /// <summary>把窗口键盘捕获挂到指定窗口。</summary>
        public void AttachKeyboard(Window window)
        {
            if (window == null || attachedWindow == window)
            {
                return;
            }

            DetachKeyboard(null);
            attachedWindow = window;
            window.PreviewKeyDown += OnPreviewKeyDown;
            window.PreviewKeyUp += OnPreviewKeyUp;
        }

        /// <summary>解除窗口键盘捕获（参数传 null 表示解除当前窗口）。</summary>
        public void DetachKeyboard(Window window)
        {
            var target = window ?? attachedWindow;
            if (target == null)
            {
                return;
            }

            target.PreviewKeyDown -= OnPreviewKeyDown;
            target.PreviewKeyUp -= OnPreviewKeyUp;
            if (ReferenceEquals(attachedWindow, target))
            {
                attachedWindow = null;
            }
        }

        /// <summary>释放所有已按下状态（断开 / 关闭时避免按键卡住）。</summary>
        public void ReleaseAll()
        {
            foreach (var kv in new List<KeyValuePair<int, bool>>(buttonPressed))
            {
                if (kv.Value)
                {
                    SetButtonPressed(kv.Key, false);
                    RaiseKey(kv.Key, 0, false);
                }
            }

            foreach (int keyCode in new List<int>(keyboardPressed))
            {
                keyboardPressed.Remove(keyCode);
                RaiseKey(keyCode, 0, false);
            }
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            LayoutButtons();
        }

        private void AddButton(ClientKeyDefinition key)
        {
            if (key == null || key.KeyCode <= 0 || buttons.ContainsKey(key.KeyCode))
            {
                return;
            }

            var text = new TextBlock
            {
                Text = key.Label,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };

            byte alpha = OpacityToByte(key.Opacity);
            var button = new Border
            {
                Tag = key.KeyCode,
                Background = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(1000),
                Child = text
            };
            button.MouseLeftButtonDown += OnButtonMouseDown;
            button.MouseLeftButtonUp += OnButtonMouseUp;

            keys.Add(key);
            keyDefs[key.KeyCode] = key;
            buttons[key.KeyCode] = button;
            buttonPressed[key.KeyCode] = false;
            Children.Add(button);
        }

        private void ClearButtons()
        {
            foreach (var button in buttons.Values)
            {
                button.MouseLeftButtonDown -= OnButtonMouseDown;
                button.MouseLeftButtonUp -= OnButtonMouseUp;
            }
            buttons.Clear();
            keyDefs.Clear();
            buttonPressed.Clear();
            keys.Clear();
            Children.Clear();
        }

        private void LayoutButtons()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 0 || h <= 0)
            {
                return;
            }

            double minDim = Math.Min(w, h);
            foreach (var key in keys)
            {
                Border button;
                if (!buttons.TryGetValue(key.KeyCode, out button))
                {
                    continue;
                }

                double radius = key.Size * minDim;
                double size = radius * 2;
                double left = key.X * w - radius;
                double top = key.Y * h - radius;

                Canvas.SetLeft(button, left);
                Canvas.SetTop(button, top);
                button.Width = size;
                button.Height = size;
                button.CornerRadius = new CornerRadius(radius);

                var text = button.Child as TextBlock;
                if (text != null)
                {
                    text.FontSize = Math.Max(10, radius * 0.9);
                }
            }
        }

        private void OnButtonMouseDown(object sender, MouseButtonEventArgs e)
        {
            var button = sender as Border;
            if (button == null)
            {
                return;
            }

            int keyCode = (int)button.Tag;
            button.CaptureMouse();
            SetButtonPressed(keyCode, true);
            RaiseKey(keyCode, 0, true);
            e.Handled = true;
        }

        private void OnButtonMouseUp(object sender, MouseButtonEventArgs e)
        {
            var button = sender as Border;
            if (button == null)
            {
                return;
            }

            int keyCode = (int)button.Tag;
            button.ReleaseMouseCapture();
            SetButtonPressed(keyCode, false);
            RaiseKey(keyCode, 0, false);
            e.Handled = true;
        }

        private void SetButtonPressed(int keyCode, bool pressed)
        {
            ClientKeyDefinition def;
            if (!keyDefs.TryGetValue(keyCode, out def))
            {
                return;
            }

            buttonPressed[keyCode] = pressed;
            byte alpha = OpacityToByte(def.Opacity);
            var brush = pressed
                ? new SolidColorBrush(Color.FromArgb(alpha, 76, 175, 80))
                : new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255));

            Border button;
            if (buttons.TryGetValue(keyCode, out button))
            {
                button.Background = brush;
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            int keyCode = MapPhysicalKey(e.Key);
            if (keyCode <= 0)
            {
                return;
            }

            if (keyboardPressed.Contains(keyCode))
            {
                // 自动重复的 KeyDown 只拦截，不重复下发。
                e.Handled = true;
                return;
            }

            keyboardPressed.Add(keyCode);
            RaiseKey(keyCode, 0, true);
            e.Handled = true;
        }

        private void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            int keyCode = MapPhysicalKey(e.Key);
            if (keyCode <= 0)
            {
                return;
            }

            if (keyboardPressed.Remove(keyCode))
            {
                RaiseKey(keyCode, 0, false);
            }
            e.Handled = true;
        }

        private void RaiseKey(int keyCode, int scanCode, bool isDown)
        {
            var handler = KeyAction;
            if (handler != null)
            {
                handler(keyCode, scanCode, isDown);
            }
        }

        private static int MapPhysicalKey(Key key)
        {
            // 电脑端从机：透传键盘按键（不做玩家 2 键位白名单映射），按什么键就发什么键。
            try
            {
                return KeyInterop.VirtualKeyFromKey(key);
            }
            catch
            {
                return 0;
            }
        }

        private static byte OpacityToByte(double opacity)
        {
            double clamped = opacity < 0.05 ? 0.05 : (opacity > 1.0 ? 1.0 : opacity);
            return (byte)Math.Round(clamped * 255);
        }
    }
}
