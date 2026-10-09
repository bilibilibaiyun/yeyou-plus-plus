package com.easytier.jni

import android.content.Intent
import android.net.VpnService
import android.os.ParcelFileDescriptor
import android.util.Log
import kotlin.concurrent.thread

/**
 * 跨网络联机 VPN 服务。
 *
 * 用 Android VpnService 建立 TUN 接口，并把 fd 交给 EasyTier 实例（setTunFd），
 * 让发往电脑虚拟 IP 的流量走 EasyTier 隧道（打洞 / 公共节点中继）。
 *
 * Intent 参数：
 *   - ipv4_address：本机（手机）虚拟 IP，形如 "10.x.x.x/24"
 *   - route_ip：目标（电脑）虚拟 IP，仅此地址走隧道（精确 /32 路由）
 *   - instance_name：EasyTier 实例名，用于 setTunFd
 */
class EasyTierVpnService : VpnService() {

    private var vpnInterface: ParcelFileDescriptor? = null
    private var isRunning = false
    private var instanceName: String? = null

    companion object {
        private const val TAG = "EasyTierVpnService"
        private const val SELF_PACKAGE = "com.yeyou.phonebox"

        /** VPN 隧道建立（setTunFd 成功）后的回调，由 MainActivity 注册。 */
        @Volatile
        var onReady: (() -> Unit)? = null
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val ipv4Address = intent?.getStringExtra("ipv4_address")
        val routeIp = intent?.getStringExtra("route_ip")
        instanceName = intent?.getStringExtra("instance_name")

        if (ipv4Address == null || instanceName == null) {
            Log.e(TAG, "缺少必要参数: ipv4Address=$ipv4Address, instanceName=$instanceName")
            stopSelf()
            return START_NOT_STICKY
        }

        Log.i(TAG, "启动 VPN - 本机 $ipv4Address -> 目标 $routeIp, 实例 $instanceName")

        thread {
            try {
                setupVpnInterface(ipv4Address, routeIp)
            } catch (t: Throwable) {
                Log.e(TAG, "VPN 设置失败", t)
                stopSelf()
            }
        }
        return START_STICKY
    }

    private fun setupVpnInterface(ipv4Address: String, routeIp: String?) {
        try {
            val (ip, networkLength) = parseIpv4Address(ipv4Address)

            val builder = Builder()
            builder.setSession("YeyouPlusPlus 跨网络联机")
                .addAddress(ip, networkLength)
                .addDnsServer("223.5.5.5")
                // 排除本 App，避免组网/推流的控制流量也走隧道造成回环。
                .addDisallowedApplication(SELF_PACKAGE)

            // 只把「到电脑虚拟 IP」的流量路由进隧道，其余走系统网络。
            if (!routeIp.isNullOrBlank()) {
                builder.addRoute(routeIp, 32)
            }

            val established = builder.establish()
            if (established == null) {
                Log.e(TAG, "建立 VPN 接口失败（用户可能拒绝授权）")
                return
            }
            vpnInterface = established

            val name = instanceName
            if (name != null) {
                val fd = vpnInterface!!.fd
                val result = EasyTierJNI.setTunFd(name, fd)
                Log.i(TAG, "setTunFd($name, fd=$fd) = $result")
                if (result == 0) {
                    onReady?.invoke()
                }
            }

            isRunning = true
            while (isRunning && vpnInterface != null) {
                Thread.sleep(1000)
            }
        } catch (t: Throwable) {
            Log.e(TAG, "VPN 接口设置异常", t)
        } finally {
            cleanup()
        }
    }

    /** 解析 "10.x.x.x/24" -> (ip, 24)；无前缀默认 /24。 */
    private fun parseIpv4Address(ipv4Address: String): Pair<String, Int> {
        return if (ipv4Address.contains("/")) {
            val parts = ipv4Address.split("/")
            Pair(parts[0], parts[1].toInt())
        } else {
            Pair(ipv4Address, 24)
        }
    }

    private fun cleanup() {
        isRunning = false
        try {
            vpnInterface?.close()
        } catch (_: Exception) {
        }
        vpnInterface = null
        Log.i(TAG, "VPN 接口已清理")
    }

    override fun onDestroy() {
        super.onDestroy()
        cleanup()
    }
}
