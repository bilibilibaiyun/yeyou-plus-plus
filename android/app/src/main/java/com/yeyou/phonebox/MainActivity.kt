package com.yeyou.phonebox

import android.os.Bundle
import android.text.InputType
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.widget.EditText
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity

/**
 * 手机盒子主界面。
 *
 * 布局：SurfaceView（显示电脑画面）+ VirtualGamepadView（半透明悬浮虚拟按键）。
 * 流程：弹出「输入联机码」对话框 → 解码 → TCP 握手（Hello/Config/Ready）→ 启动 UDP 接收与硬解。
 */
class MainActivity : AppCompatActivity(), SurfaceHolder.Callback {

    companion object {
        /** 手机端 UDP 监听端口，与电脑端 PhoneBoxConfig.UdpPort 一致。 */
        private const val UDP_PORT = 8761
    }

    private lateinit var surfaceView: SurfaceView
    private lateinit var gamepadView: VirtualGamepadView
    private lateinit var statusText: TextView

    private var surface: Surface? = null
    private var config: ControlChannel.StreamConfig? = null

    private var channel: ControlChannel? = null
    private var keySender: KeyEventSender? = null
    private var decoder: VideoDecoder? = null
    private var udpReceiver: UdpStreamReceiver? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        surfaceView = findViewById(R.id.surface_view)
        gamepadView = findViewById(R.id.gamepad_view)
        statusText = findViewById(R.id.status_text)

        surfaceView.holder.addCallback(this)

        gamepadView.listener = object : VirtualGamepadView.Listener {
            override fun onButtonDown(button: VirtualGamepadView.GamepadButton) {
                keySender?.sendDown(button.keyCode)
            }

            override fun onButtonUp(button: VirtualGamepadView.GamepadButton) {
                keySender?.sendUp(button.keyCode)
            }
        }

        showJoinCodeDialog()
    }

    /** 弹出联机码输入对话框。 */
    private fun showJoinCodeDialog() {
        setStatus(R.string.status_idle)

        val input = EditText(this)
        input.inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        input.hint = getString(R.string.join_code_hint)
        input.isSingleLine = true
        val padding = (16 * resources.displayMetrics.density).toInt()
        input.setPadding(padding, padding, padding, padding)

        AlertDialog.Builder(this)
            .setTitle(R.string.join_code_title)
            .setView(input)
            .setCancelable(false)
            .setPositiveButton(R.string.connect) { _, _ ->
                handleJoinCode(input.text.toString().trim())
            }
            .show()
    }

    /** 解码联机码并启动连接流程。 */
    private fun handleJoinCode(code: String) {
        val info = JoinCodeDecoder.decode(code)
        if (info == null) {
            showErrorAndRetry(getString(R.string.invalid_join_code))
            return
        }

        setStatus(R.string.status_connecting)

        // 断开旧会话再建立新会话。
        resetSession()

        val ctrl = ControlChannel(info.ip, info.port, info.token)
        ctrl.listener = object : ControlChannel.Listener {
            override fun onConfig(cfg: ControlChannel.StreamConfig) {
                runOnUiThread {
                    config = cfg
                    gamepadView.applyKeyLayout(cfg.keyLayout)
                    tryStartStreaming()
                }
            }

            override fun onError(message: String) {
                runOnUiThread {
                    showErrorAndRetry(getString(R.string.status_error, message))
                }
            }

            override fun onDisconnected() {
                runOnUiThread {
                    if (channel === ctrl) {
                        stopStreaming()
                        config = null
                        setStatus(R.string.status_disconnected)
                    }
                }
            }
        }

        channel = ctrl
        keySender = KeyEventSender(ctrl)
        ctrl.connect()
    }

    /**
     * 当 Surface 与 Config 都就绪时启动「解码 + UDP 接收」，随后发送 Ready。
     * 先让 UDP 在听、解码器就绪，再发 Ready，避免丢失开头几帧。
     */
    private fun tryStartStreaming() {
        val s = surface ?: return
        val cfg = config ?: return
        if (decoder != null) {
            return
        }

        val videoDecoder = VideoDecoder(s)
        if (!videoDecoder.start(cfg.width, cfg.height)) {
            showErrorAndRetry(getString(R.string.status_error, "视频解码器启动失败"))
            return
        }
        decoder = videoDecoder

        val depacketizer = RtpDepacketizer()
        val receiver = UdpStreamReceiver(UDP_PORT, depacketizer, videoDecoder)
        receiver.listener = object : UdpStreamReceiver.Listener {
            override fun onError(message: String) {
                runOnUiThread {
                    // UDP 单包异常不弹窗打断，仅提示链路异常。
                    setStatus(R.string.status_disconnected)
                }
            }
        }
        receiver.start()
        udpReceiver = receiver

        channel?.sendReady()
        setStatus(R.string.status_streaming)
    }

    /** 停止解码与 UDP 接收（保留配置与控制通道，供 Surface 重建后恢复）。 */
    private fun stopStreaming() {
        udpReceiver?.stop()
        udpReceiver = null
        decoder?.stop()
        decoder = null
    }

    /** 完整复位会话：停止推流、断开控制通道、清空配置。 */
    private fun resetSession() {
        stopStreaming()
        config = null
        channel?.disconnect()
        channel = null
        keySender = null
    }

    // ---- SurfaceHolder.Callback ----

    override fun surfaceCreated(holder: SurfaceHolder) {
        surface = holder.surface
        tryStartStreaming()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        // 横屏全屏下尺寸变化无需额外处理。
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surface = null
        stopStreaming()
    }

    // ---- UI 辅助 ----

    private fun setStatus(resId: Int) {
        statusText.text = getString(resId)
    }

    private fun showErrorAndRetry(message: String) {
        resetSession()
        statusText.text = message

        AlertDialog.Builder(this)
            .setTitle(R.string.status_error_title)
            .setMessage(message)
            .setCancelable(false)
            .setPositiveButton(android.R.string.ok) { _, _ ->
                showJoinCodeDialog()
            }
            .show()
    }

    private fun hideSystemUi() {
        window.decorView.systemUiVisibility = (
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
                or View.SYSTEM_UI_FLAG_FULLSCREEN
                or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
                or View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                or View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            )
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            hideSystemUi()
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        resetSession()
    }
}
