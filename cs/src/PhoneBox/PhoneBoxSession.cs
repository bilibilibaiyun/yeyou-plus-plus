using System;
using System.Net;

namespace YeyouPlusPlus.PhoneBox
{
    /// <summary>手机盒子会话状态。</summary>
    public enum PhoneBoxState
    {
        /// <summary>初始 / 未连接。</summary>
        Idle = 0,

        /// <summary>手机 TCP 已连接，握手进行中。</summary>
        Connected = 1,

        /// <summary>手机已发送 Ready，可以开始推流。</summary>
        Ready = 2,

        /// <summary>正在推流。</summary>
        Streaming = 3,

        /// <summary>已停止 / 已断开。</summary>
        Stopped = 4
    }

    /// <summary>
    /// 手机盒子会话状态机。
    ///
    /// 状态流转：Idle → Connected → Ready → Streaming → Stopped → Idle（重连复位）。
    /// 同时记录手机远端 IP:Port，供 UdpRtpTransport.SetRemote 使用。
    /// </summary>
    public sealed class PhoneBoxSession
    {
        private readonly object sync = new object();

        private PhoneBoxState state = PhoneBoxState.Idle;
        private IPEndPoint remote;

        /// <summary>状态变化事件（参数：旧状态、新状态）。</summary>
        public event Action<PhoneBoxState, PhoneBoxState> StateChanged;

        /// <summary>当前状态。</summary>
        public PhoneBoxState State
        {
            get
            {
                lock (sync)
                {
                    return state;
                }
            }
        }

        /// <summary>手机远端地址（未连接时为 null）。</summary>
        public IPEndPoint Remote
        {
            get
            {
                lock (sync)
                {
                    return remote;
                }
            }
        }

        /// <summary>记录手机远端地址，并进入 Connected 状态。</summary>
        public void SetRemote(IPEndPoint endpoint)
        {
            if (endpoint == null)
            {
                throw new ArgumentNullException(nameof(endpoint));
            }

            lock (sync)
            {
                remote = endpoint;
            }
            TransitionTo(PhoneBoxState.Connected);
        }

        /// <summary>手机已就绪，进入 Ready 状态。</summary>
        public void MarkReady()
        {
            TransitionTo(PhoneBoxState.Ready);
        }

        /// <summary>开始推流，进入 Streaming 状态。</summary>
        public void MarkStreaming()
        {
            TransitionTo(PhoneBoxState.Streaming);
        }

        /// <summary>停止 / 断开，进入 Stopped 状态。</summary>
        public void MarkStopped()
        {
            TransitionTo(PhoneBoxState.Stopped);
        }

        /// <summary>复位会话（清空远端地址，回到 Idle），用于下次重连。</summary>
        public void Reset()
        {
            lock (sync)
            {
                remote = null;
            }
            TransitionTo(PhoneBoxState.Idle);
        }

        /// <summary>执行状态转换；非法顺序仅记录告警，不抛出异常以保持会话稳健。</summary>
        private void TransitionTo(PhoneBoxState next)
        {
            PhoneBoxState old;
            lock (sync)
            {
                old = state;
                if (old == next)
                {
                    return;
                }

                // 除“复位 / 停止”这类终态操作外，其余按固定顺序流转。
                bool valid =
                    next == PhoneBoxState.Stopped ||
                    next == PhoneBoxState.Idle ||
                    (old == PhoneBoxState.Idle && next == PhoneBoxState.Connected) ||
                    (old == PhoneBoxState.Connected && next == PhoneBoxState.Ready) ||
                    (old == PhoneBoxState.Ready && next == PhoneBoxState.Streaming);

                if (!valid)
                {
                    PhoneBoxLog.Warn(
                        "非法会话状态流转：" + old + " → " + next + "，已忽略。");
                    return;
                }

                state = next;
            }

            PhoneBoxLog.Info("会话状态：" + old + " → " + next);

            var handler = StateChanged;
            if (handler != null)
            {
                handler(old, next);
            }
        }
    }
}
