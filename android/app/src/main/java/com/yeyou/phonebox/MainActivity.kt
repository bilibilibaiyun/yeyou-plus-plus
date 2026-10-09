package com.yeyou.phonebox

import android.os.Bundle
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity

/**
 * 页游++ 手机盒子主界面。
 *
 * 流程：主页输入联机码 → 连接 → 游戏页（投屏画面 + 可自定义虚拟按键）。
 * 虚拟按键支持「大小 / 位置 / 透明度 / 映射键」自定义，布局本地持久化。
 */
class MainActivity : AppCompatActivity(), SurfaceHolder.Callback {

    companion object {
        /** 手机端 UDP 监听端口，与电脑端 PhoneBoxConfig.UdpPort 一致。 */
        private const val UDP_PORT = 8761

        private const val SIZE_MIN = 0.04f
        private const val SIZE_MAX = 0.30f
        private const val OPACITY_MIN = 0.05f
        private const val OPACITY_MAX = 1.0f
    }

    // 主页
    private lateinit var homePage: LinearLayout
    private lateinit var joinCodeInput: EditText
    private lateinit var connectButton: Button

    // 游戏页
    private lateinit var gamePage: FrameLayout
    private lateinit var surfaceView: SurfaceView
    private lateinit var gamepadView: VirtualGamepadView
    private lateinit var statusText: TextView
    private lateinit var settingsButton: Button

    // 编辑面板
    private lateinit var editPanel: LinearLayout
    private lateinit var editSelectedLabel: TextView
    private lateinit var editMapKeyButton: Button
    private lateinit var editSizeSeek: SeekBar
    private lateinit var editOpacitySeek: SeekBar
    private lateinit var editAddButton: Button
    private lateinit var editDeleteButton: Button
    private lateinit var editResetButton: Button
    private lateinit var editDoneButton: Button

    private var surface: Surface? = null
    private var config: ControlChannel.StreamConfig? = null

    private var channel: ControlChannel? = null
    private var keySender: KeyEventSender? = null
    private var decoder: VideoDecoder? = null
    private var udpReceiver: UdpStreamReceiver? = null

    /** 编辑面板当前选中的按键。 */
    private var selectedButton: GamepadButton? = null

    /** 回填滑块时置位，避免触发监听造成循环。 */
    private var updatingSliders = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        homePage = findViewById(R.id.home_page)
        gamePage = findViewById(R.id.game_page)
        joinCodeInput = findViewById(R.id.join_code_input)
        connectButton = findViewById(R.id.connect_button)
        surfaceView = findViewById(R.id.surface_view)
        gamepadView = findViewById(R.id.gamepad_view)
        statusText = findViewById(R.id.status_text)
        settingsButton = findViewById(R.id.settings_button)
        editPanel = findViewById(R.id.edit_panel)
        editSelectedLabel = findViewById(R.id.edit_selected_label)
        editMapKeyButton = findViewById(R.id.edit_map_key_button)
        editSizeSeek = findViewById(R.id.edit_size_seek)
        editOpacitySeek = findViewById(R.id.edit_opacity_seek)
        editAddButton = findViewById(R.id.edit_add_button)
        editDeleteButton = findViewById(R.id.edit_delete_button)
        editResetButton = findViewById(R.id.edit_reset_button)
        editDoneButton = findViewById(R.id.edit_done_button)

        surfaceView.holder.addCallback(this)

        // 载入本地已保存的按键布局，无则用默认。
        gamepadView.setButtons(GamepadLayoutStore.load(this) ?: GamepadLayoutStore.defaultButtons())

        gamepadView.listener = object : VirtualGamepadView.Listener {
            override fun onButtonDown(button: GamepadButton) {
                keySender?.sendDown(button.keyCode)
            }

            override fun onButtonUp(button: GamepadButton) {
                keySender?.sendUp(button.keyCode)
            }

            override fun onButtonSelected(button: GamepadButton?) {
                onSelectButton(button)
            }

            override fun onLayoutChanged(buttons: List<GamepadButton>) {
                GamepadLayoutStore.save(this@MainActivity, buttons)
            }
        }

        connectButton.setOnClickListener {
            handleJoinCode(joinCodeInput.text.toString().trim())
        }
        settingsButton.setOnClickListener { enterEditMode() }
        editDoneButton.setOnClickListener { exitEditMode() }
        editMapKeyButton.setOnClickListener { showKeyPicker() }
        editAddButton.setOnClickListener { addButton() }
        editDeleteButton.setOnClickListener { deleteSelected() }
        editResetButton.setOnClickListener { resetLayout() }

        editSizeSeek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(seekBar: SeekBar, progress: Int, fromUser: Boolean) {
                if (updatingSliders || !fromUser) return
                val b = selectedButton ?: return
                val size = SIZE_MIN + progress / 100f * (SIZE_MAX - SIZE_MIN)
                gamepadView.updateButton(b.copy(size = size))
            }

            override fun onStartTrackingTouch(seekBar: SeekBar) {}
            override fun onStopTrackingTouch(seekBar: SeekBar) {}
        })

        editOpacitySeek.setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
            override fun onProgressChanged(seekBar: SeekBar, progress: Int, fromUser: Boolean) {
                if (updatingSliders || !fromUser) return
                val b = selectedButton ?: return
                val opacity = OPACITY_MIN + progress / 100f * (OPACITY_MAX - OPACITY_MIN)
                gamepadView.updateButton(b.copy(opacity = opacity))
            }

            override fun onStartTrackingTouch(seekBar: SeekBar) {}
            override fun onStopTrackingTouch(seekBar: SeekBar) {}
        })
    }

    // ---- 主页 / 连接 ----

    private fun handleJoinCode(code: String) {
        val info = JoinCodeDecoder.decode(code)
        if (info == null) {
            showError(getString(R.string.invalid_join_code))
            return
        }

        setStatus(R.string.status_connecting)

        // 断开旧会话再建立新会话。
        resetSession()

        val ctrl = ControlChannel(info.ip, info.port, info.token)
        ctrl.listener = object : ControlChannel.Listener {
            override fun onConfig(config: ControlChannel.StreamConfig) {
                runOnUiThread {
                    this@MainActivity.config = config
                    showGamePage()
                    tryStartStreaming()
                }
            }

            override fun onError(message: String) {
                runOnUiThread {
                    showError(getString(R.string.status_error, message))
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

    private fun showGamePage() {
        homePage.visibility = View.GONE
        gamePage.visibility = View.VISIBLE
    }

    private fun showHomePage() {
        exitEditMode()
        gamePage.visibility = View.GONE
        homePage.visibility = View.VISIBLE
        setStatus(R.string.status_idle)
    }

    private fun showError(message: String) {
        resetSession()
        statusText.text = message
        AlertDialog.Builder(this)
            .setTitle(R.string.status_error_title)
            .setMessage(message)
            .setCancelable(false)
            .setPositiveButton(android.R.string.ok) { _, _ -> showHomePage() }
            .show()
    }

    // ---- 推流 / 解码 ----

    private fun tryStartStreaming() {
        val s = surface ?: return
        val cfg = config ?: return
        if (decoder != null) {
            return
        }

        val videoDecoder = VideoDecoder(s)
        if (!videoDecoder.start(cfg.width, cfg.height)) {
            showError(getString(R.string.status_error, "视频解码器启动失败"))
            return
        }
        decoder = videoDecoder

        val depacketizer = RtpDepacketizer()
        val receiver = UdpStreamReceiver(UDP_PORT, depacketizer, videoDecoder)
        receiver.listener = object : UdpStreamReceiver.Listener {
            override fun onError(message: String) {
                runOnUiThread { setStatus(R.string.status_disconnected) }
            }
        }
        receiver.start()
        udpReceiver = receiver

        channel?.sendReady()
        setStatus(R.string.status_streaming)
    }

    private fun stopStreaming() {
        udpReceiver?.stop()
        udpReceiver = null
        decoder?.stop()
        decoder = null
    }

    private fun resetSession() {
        stopStreaming()
        config = null
        channel?.disconnect()
        channel = null
        keySender = null
    }

    // ---- 按键编辑 ----

    private fun enterEditMode() {
        gamepadView.editMode = true
        editPanel.visibility = View.VISIBLE
        onSelectButton(null)
    }

    private fun exitEditMode() {
        gamepadView.editMode = false
        editPanel.visibility = View.GONE
        onSelectButton(null)
    }

    private fun onSelectButton(button: GamepadButton?) {
        selectedButton = button
        if (button == null) {
            editSelectedLabel.text = getString(R.string.edit_none_selected)
            setSlidersEnabled(false)
        } else {
            editSelectedLabel.text = getString(
                R.string.edit_selected_fmt,
                button.label,
                KeyCodes.labelOf(button.keyCode)
            )
            setSlidersEnabled(true)
            refreshSliders(button)
        }
    }

    private fun setSlidersEnabled(enabled: Boolean) {
        editSizeSeek.isEnabled = enabled
        editOpacitySeek.isEnabled = enabled
        editMapKeyButton.isEnabled = enabled
        editDeleteButton.isEnabled = enabled
    }

    private fun refreshSliders(button: GamepadButton) {
        updatingSliders = true
        editSizeSeek.progress =
            ((button.size - SIZE_MIN) / (SIZE_MAX - SIZE_MIN) * 100).toInt().coerceIn(0, 100)
        editOpacitySeek.progress =
            ((button.opacity - OPACITY_MIN) / (OPACITY_MAX - OPACITY_MIN) * 100).toInt().coerceIn(0, 100)
        updatingSliders = false
    }

    private fun showKeyPicker() {
        val b = selectedButton ?: return
        val labels = KeyCodes.all.map { it.label }.toTypedArray()
        AlertDialog.Builder(this)
            .setTitle(R.string.pick_key_title)
            .setItems(labels) { _, which ->
                val opt = KeyCodes.all[which]
                gamepadView.updateButton(b.copy(keyCode = opt.code, label = opt.label))
                onSelectButton(b.copy(keyCode = opt.code, label = opt.label))
            }
            .show()
    }

    private fun addButton() {
        val nextId = (gamepadView.buttons.maxOfOrNull { it.id } ?: 0) + 1
        val def = KeyCodes.all.firstOrNull { it.code == 32 } ?: KeyCodes.all[0]
        val button = GamepadButton(
            id = nextId,
            label = def.label,
            keyCode = def.code,
            x = 0.5f,
            y = 0.65f,
            size = 0.09f,
            opacity = 0.45f
        )
        val newButtons = gamepadView.buttons + button
        gamepadView.setButtons(newButtons)
        GamepadLayoutStore.save(this, newButtons)
        gamepadView.select(button.id)
        onSelectButton(button)
    }

    private fun deleteSelected() {
        val b = selectedButton ?: return
        val newButtons = gamepadView.buttons.filter { it.id != b.id }
        gamepadView.setButtons(newButtons)
        GamepadLayoutStore.save(this, newButtons)
        gamepadView.select(-1)
        onSelectButton(null)
    }

    private fun resetLayout() {
        val defaults = GamepadLayoutStore.defaultButtons()
        gamepadView.setButtons(defaults)
        GamepadLayoutStore.save(this, defaults)
        gamepadView.select(-1)
        onSelectButton(null)
    }

    // ---- SurfaceHolder.Callback ----

    override fun surfaceCreated(holder: SurfaceHolder) {
        surface = holder.surface
        tryStartStreaming()
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
        surface = null
        stopStreaming()
    }

    // ---- 辅助 ----

    private fun setStatus(resId: Int) {
        statusText.text = getString(resId)
    }

    override fun onBackPressed() {
        when {
            gamepadView.editMode -> exitEditMode()
            gamePage.visibility == View.VISIBLE -> {
                resetSession()
                showHomePage()
            }
            else -> super.onBackPressed()
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        resetSession()
    }
}
