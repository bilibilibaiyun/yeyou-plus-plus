using System;
using System.Windows;
using System.Windows.Controls;

namespace YeyouPlusPlus
{
    /// <summary>
    /// 全局统一音量条：静音开关（喇叭图标）+ 0~100 滑块 + 百分比文本。
    /// 与标签条同生命周期常驻，不绑定具体标签页。
    /// </summary>
    public partial class VolumeBar : UserControl
    {
        /// <summary>音量变化（0~100）。</summary>
        public event Action<float> VolumeChanged;

        /// <summary>静音状态变化。</summary>
        public event Action<bool> MuteChanged;

        private bool updating;

        public VolumeBar()
        {
            InitializeComponent();
            UpdateMuteIcon();
        }

        /// <summary>外部设置音量值（0~100），不触发 VolumeChanged。</summary>
        public void SetValue(int percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            updating = true;
            VolumeSlider.Value = percent;
            updating = false;
            VolumeText.Text = percent + "%";
        }

        /// <summary>外部设置静音状态，不触发 MuteChanged。</summary>
        public void SetMuted(bool muted)
        {
            updating = true;
            MuteButton.IsChecked = muted;
            updating = false;
            UpdateMuteIcon();
        }

        private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            // XAML 解析设置 Value=100 时就会触发本事件，此时控件字段可能尚未就绪，需判空。
            if (VolumeText != null)
            {
                VolumeText.Text = ((int)e.NewValue) + "%";
            }
            if (updating || VolumeSlider == null)
            {
                return;
            }
            VolumeChanged?.Invoke((float)e.NewValue);
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (updating)
            {
                return;
            }
            UpdateMuteIcon();
            MuteChanged?.Invoke(MuteButton.IsChecked == true);
        }

        private void UpdateMuteIcon()
        {
            if (MuteButton == null)
            {
                return;
            }
            bool muted = MuteButton.IsChecked == true;
            // E767 = 有声，E74F = 静音。
            MuteButton.Content = muted ? "\uE74F" : "\uE767";
            MuteButton.ToolTip = muted ? "取消静音" : "静音";
        }
    }
}
