package com.yeyou.phonebox

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.view.Gravity
import android.view.Surface
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.SeekBar
import android.widget.TextView
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.FileProvider
import com.easytier.jni.EasyTierVpnService
import java.io.File

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
    private lateinit var homePage: View
    private lateinit var joinCodeInput: EditText
    private lateinit var connectButton: Button
    private lateinit var homeSettingsButton: Button

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

    /** 待连接的跨网络目标（VPN 隧道建立后再连电脑虚拟 IP）。 */
    private var pendingRemoteInfo: JoinCodeDecoder.JoinInfo? = null

    /** 编辑面板当前选中的按键。 */
    private var selectedButton: GamepadButton? = null

    /** 回填滑块时置位，避免触发监听造成循环。 */
    private var updatingSliders = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // 投屏期间保持屏幕常亮，避免手机自动锁屏导致连接中断。
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)

        homePage = findViewById(R.id.home_page)
        gamePage = findViewById(R.id.game_page)
        joinCodeInput = findViewById(R.id.join_code_input)
        connectButton = findViewById(R.id.connect_button)
        homeSettingsButton = findViewById(R.id.home_settings_button)
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
        homeSettingsButton.setOnClickListener { showSettingsDialog() }
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

    // ---- 设置 / 内置更新 ----

    /** 弹出设置对话框：当前版本 + 检测更新 + 选择版本（与电脑端一致）。 */
    private fun showSettingsDialog() {
        AlertDialog.Builder(this)
            .setTitle(R.string.settings)
            .setMessage(getString(R.string.update_current, BuildConfig.VERSION_NAME))
            .setPositiveButton(R.string.update_check) { _, _ -> checkForUpdate() }
            .setNeutralButton(R.string.update_select_version) { _, _ -> showVersionList() }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** 后台「检测更新」：取最新版本，有更新则提示下载。 */
    private fun checkForUpdate() {
        val progress = showProgressDialog(R.string.update_checking)
        Thread {
            val result = try {
                UpdateChecker.check(BuildConfig.VERSION_NAME)
            } catch (e: Exception) {
                runOnUiThread {
                    progress.dismiss()
                    showUpdateResult(getString(R.string.update_failed, e.message ?: "网络错误"))
                }
                return@Thread
            }

            runOnUiThread {
                progress.dismiss()
                if (result == null) {
                    showUpdateResult(getString(R.string.update_latest))
                } else {
                    confirmDownload(result)
                }
            }
        }.start()
    }

    /** 后台「选择版本」：拉取全部版本列表供用户选择。 */
    private fun showVersionList() {
        val progress = showProgressDialog(R.string.update_checking)
        Thread {
            val releases = try {
                UpdateChecker.listReleases()
            } catch (e: Exception) {
                runOnUiThread {
                    progress.dismiss()
                    showUpdateResult(getString(R.string.update_failed, e.message ?: "网络错误"))
                }
                return@Thread
            }

            runOnUiThread {
                progress.dismiss()
                if (releases.isEmpty()) {
                    showUpdateResult(getString(R.string.update_no_version))
                    return@runOnUiThread
                }

                val current = BuildConfig.VERSION_NAME
                val labels = releases.map { item ->
                    val tag = if (item.prerelease) getString(R.string.update_tag_test) else getString(R.string.update_tag_stable)
                    val mark = if (item.apkVersion == current) getString(R.string.update_current_mark) else ""
                    "v${item.apkVersion} $tag$mark"
                }.toTypedArray()

                AlertDialog.Builder(this)
                    .setTitle(R.string.update_select_version)
                    .setItems(labels) { _, which -> confirmDownload(releases[which]) }
                    .setNegativeButton(android.R.string.cancel, null)
                    .show()
            }
        }.start()
    }

    /** 显示一个不可取消的「请稍候」进度对话框。 */
    private fun showProgressDialog(messageRes: Int): AlertDialog {
        val dialog = AlertDialog.Builder(this)
            .setMessage(messageRes)
            .setCancelable(false)
            .create()
        dialog.show()
        return dialog
    }

    /** 确认下载安装某个版本。 */
    private fun confirmDownload(item: UpdateChecker.ReleaseItem) {
        val tag = if (item.prerelease) getString(R.string.update_tag_test) else getString(R.string.update_tag_stable)
        AlertDialog.Builder(this)
            .setTitle(R.string.update_title)
            .setMessage(getString(R.string.update_found, "v${item.apkVersion} $tag"))
            .setPositiveButton(R.string.update_download) { _, _ -> startDownload(item) }
            .setNegativeButton(android.R.string.cancel, null)
            .show()
    }

    /** 后台下载 APK 并触发安装。 */
    private fun startDownload(item: UpdateChecker.ReleaseItem) {
        val progressBar = ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal)
        progressBar.max = 100
        progressBar.progress = 0

        val dialog = AlertDialog.Builder(this)
            .setTitle(R.string.update_downloading)
            .setView(progressBar)
            .setCancelable(false)
            .create()
        dialog.show()

        Thread {
            val dir = File(cacheDir, "apk")
            dir.mkdirs()
            val apkFile = File(dir, "update.apk")
            try {
                UpdateChecker.download(item.downloadUrl, apkFile) { p ->
                    runOnUiThread { progressBar.progress = p }
                }
            } catch (e: Exception) {
                runOnUiThread {
                    dialog.dismiss()
                    showUpdateResult(getString(R.string.update_install_failed, e.message ?: "下载失败"))
                }
                return@Thread
            }
            runOnUiThread {
                dialog.dismiss()
                installApk(apkFile)
            }
        }.start()
    }

    /** 通过 FileProvider 触发系统安装器安装 APK。 */
    private fun installApk(file: File) {
        val uri = FileProvider.getUriForFile(this, "com.yeyou.phonebox.fileprovider", file)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        // Android 8+ 需「安装未知来源」权限，已在 manifest 声明；
        // 未授予时系统会引导用户去设置开启。
        startActivity(intent)
    }

    /** 展示检查结果（已最新 / 失败 / 无版本）。 */
    private fun showUpdateResult(message: String) {
        AlertDialog.Builder(this)
            .setTitle(R.string.update_title)
            .setMessage(message)
            .setPositiveButton(android.R.string.ok, null)
            .show()
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

        if (info.isRemote) {
            // 跨网络联机：先组网 + 建立 VPN，再连电脑虚拟 IP。
            startRemoteJoin(info)
        } else {
            connectTo(info)
        }
    }

    /** 建立到电脑的 TCP 控制通道（局域网直连，或跨网络组网完成后连虚拟 IP）。 */
    private fun connectTo(info: JoinCodeDecoder.JoinInfo) {
        val ctrl = ControlChannel(info.ip, info.port, info.token)
        ctrl.listener = object : ControlChannel.Listener {
            override fun onConfig(config: ControlChannel.StreamConfig) {
                runOnUiThread {
                    this@MainActivity.config = config
                    showGamePage()
                    tryStartStreaming()
                    adjustSurfaceAspect(config.width, config.height)
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

    /** 跨网络联机：后台组网 → 查询虚拟 IP → 请求 VPN 授权，VPN 就绪后自动连接。 */
    private fun startRemoteJoin(info: JoinCodeDecoder.JoinInfo) {
        EasyTierVpnService.onReady = {
            runOnUiThread {
                val pending = pendingRemoteInfo
                if (pending != null) {
                    pendingRemoteInfo = null
                    connectTo(pending)
                }
            }
        }

        Thread {
            val selfIp = EasyTierBridge.deriveSelfIp(info.ip)
            if (selfIp == null) {
                runOnUiThread { showError(getString(R.string.status_error, "无法推断本机地址：${info.ip}")) }
                return@Thread
            }

            val err = EasyTierBridge.start(info.networkName!!, info.networkSecret!!, selfIp)
            if (err != null) {
                runOnUiThread { showError(getString(R.string.status_error, "组网启动失败：$err")) }
                return@Thread
            }

            runOnUiThread {
                pendingRemoteInfo = info
                EasyTierBridge.startVpn(this@MainActivity, "$selfIp/24", info.ip)
            }
        }.start()
    }

    private fun showGamePage() {
        homePage.visibility = View.GONE
        gamePage.visibility = View.VISIBLE
    }

    /**
     * 按视频宽高比调整 SurfaceView 尺寸，居中显示（四周留黑边），
     * 避免 MediaCodec 把视频强制拉伸填满整个屏幕导致画面变形。
     */
    private fun adjustSurfaceAspect(videoW: Int, videoH: Int) {
        if (videoW <= 0 || videoH <= 0) {
            return
        }
        gamePage.post {
            val screenW = gamePage.width
            val screenH = gamePage.height
            if (screenW <= 0 || screenH <= 0) {
                return@post
            }
            val videoRatio = videoW.toFloat() / videoH
            val screenRatio = screenW.toFloat() / screenH

            val targetW: Int
            val targetH: Int
            if (videoRatio > screenRatio) {
                // 视频比屏幕更「扁」：以屏宽为准，按比例收窄高度
                targetW = screenW
                targetH = (screenW / videoRatio).toInt()
            } else {
                // 视频比屏幕更「高」：以屏高为准，按比例收窄宽度
                targetH = screenH
                targetW = (screenH * videoRatio).toInt()
            }

            val lp = surfaceView.layoutParams as FrameLayout.LayoutParams
            lp.width = targetW
            lp.height = targetH
            lp.gravity = Gravity.CENTER
            surfaceView.layoutParams = lp
        }
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
        setStatusTransient(R.string.status_streaming)
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
        statusText.visibility = View.VISIBLE
    }

    /** 显示临时状态，几秒后自动隐藏（用于「正在投屏」等一闪而过的提示）。 */
    private fun setStatusTransient(resId: Int) {
        setStatus(resId)
        statusText.postDelayed({ statusText.visibility = View.GONE }, 3000)
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
        EasyTierVpnService.onReady = null
        resetSession()
        EasyTierBridge.stop(this)
    }
}
