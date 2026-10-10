package com.yeyou.phonebox

import android.content.Context
import android.content.Intent
import android.util.Log
import com.easytier.jni.EasyTierJNI
import com.easytier.jni.EasyTierVpnService
import org.json.JSONObject

/**
 * 跨网络联机桥接（手机端）。
 *
 * 用 EasyTier JNI 库加入电脑端所在的虚拟网络，拿到本机虚拟 IP 后启动 VpnService 建立 TUN，
 * 之后即可用「电脑虚拟 IP」直连电脑的 TCP 控制通道 + UDP 推流（与局域网链路完全复用）。
 *
 * 与电脑端 `EasyTierManager`（easytier-core --network-name ... --dhcp true -e 公共节点）对齐：
 * 手机端通过 TOML 配置加入同一网络名 + 密钥，并连接相同的公共节点打洞 / 中继。
 */
object EasyTierBridge {

    private const val TAG = "EasyTierBridge"
    private const val INSTANCE_NAME = "phonebox"

    // 与电脑端一致的公共节点（打洞引导 + 兜底中继），最大化校园网等复杂网络穿透率。
    // 注意：官方节点 public.easytier.cn / public.easytier.top 已于 2026-02 停止服务，改用社区节点。
    private val PUBLIC_NODES = listOf(
        "tcp://easytier.weiai.org.cn:11010",
        "tcp://ros.scpsl.com.cn:11010",
        "tcp://boi.de5.net:11010"
    )

    /** 构造 EasyTier TOML 配置。 */
    private fun buildConfig(networkName: String, networkSecret: String): String {
        val peers = PUBLIC_NODES.joinToString("\n") { uri -> "[[peer]]\nuri = \"$uri\"\n" }
        return buildString {
            append("instance_name = \"$INSTANCE_NAME\"\n")
            append("\n")
            append("[network_identity]\n")
            append("network_name = \"$networkName\"\n")
            append("network_secret = \"$networkSecret\"\n")
            append("\n")
            append("dhcp = true\n")
            append("\n")
            append(peers)
        }
    }

    /** 启动组网实例，返回是否成功。 */
    fun start(networkName: String, networkSecret: String): Boolean {
        return try {
            val config = buildConfig(networkName, networkSecret)
            val result = EasyTierJNI.runNetworkInstance(config)
            if (result == 0) {
                Log.i(TAG, "EasyTier 实例启动成功")
                true
            } else {
                Log.e(TAG, "EasyTier 启动失败: $result, ${EasyTierJNI.getLastError()}")
                false
            }
        } catch (e: Throwable) {
            Log.e(TAG, "EasyTier 启动异常", e)
            false
        }
    }

    /**
     * 轮询查询本机（手机）虚拟 IP，返回 "10.x.x.x/24" 形式；超时返回 null。
     * collectNetworkInfos 返回 protobuf JSON：
     *   { "map": { "<inst>": { "running": true, "my_node_info": { "virtual_ipv4": {
     *       "address": { "addr": <uint32> }, "network_length": <int> } } } } }
     */
    fun queryVirtualIp(timeoutSeconds: Int = 25): String? {
        val deadline = System.currentTimeMillis() + timeoutSeconds * 1000L
        while (System.currentTimeMillis() < deadline) {
            try {
                val json = EasyTierJNI.collectNetworkInfos(10)
                if (!json.isNullOrEmpty()) {
                    val ip = parseVirtualIp(json)
                    if (ip != null) {
                        return ip
                    }
                }
            } catch (e: Throwable) {
                Log.w(TAG, "查询虚拟 IP 异常", e)
            }
            Thread.sleep(500)
        }
        return null
    }

    /** 从 collectNetworkInfos 的 JSON 里解析本机虚拟 IP。 */
    private fun parseVirtualIp(json: String): String? {
        return try {
            val root = JSONObject(json)
            val map = root.optJSONObject("map") ?: return null
            val info = map.optJSONObject(INSTANCE_NAME) ?: return null
            if (!info.optBoolean("running", false)) return null
            val myNode = info.optJSONObject("my_node_info") ?: return null
            val v4 = myNode.optJSONObject("virtual_ipv4") ?: return null
            val addrObj = v4.optJSONObject("address") ?: return null
            val addr = addrObj.optLong("addr", -1L)
            if (addr < 0) return null
            val networkLength = v4.optInt("network_length", 24)
            val ip = buildString {
                append((addr shr 24) and 0xFF).append('.')
                append((addr shr 16) and 0xFF).append('.')
                append((addr shr 8) and 0xFF).append('.')
                append(addr and 0xFF)
            }
            "$ip/$networkLength"
        } catch (e: Throwable) {
            Log.w(TAG, "解析虚拟 IP 失败", e)
            null
        }
    }

    /**
     * 启动 VpnService 建立 TUN（触发系统授权弹窗，用户允许后生效）。
     * @param selfIp 本机虚拟 IP（"10.x.x.x/24"）
     * @param routeIp 目标（电脑）虚拟 IP，仅此地址走隧道
     */
    fun startVpn(context: Context, selfIp: String, routeIp: String) {
        val intent = Intent(context, EasyTierVpnService::class.java)
        intent.putExtra("ipv4_address", selfIp)
        intent.putExtra("route_ip", routeIp)
        intent.putExtra("instance_name", INSTANCE_NAME)
        context.startService(intent)
        Log.i(TAG, "VpnService 已请求启动: $selfIp -> $routeIp")
    }

    /** 停止组网 + 关闭 VPN。 */
    fun stop(context: Context) {
        try {
            context.stopService(Intent(context, EasyTierVpnService::class.java))
        } catch (_: Throwable) {
        }
        try {
            EasyTierJNI.stopAllInstances()
        } catch (e: Throwable) {
            Log.w(TAG, "停止 EasyTier 异常", e)
        }
        Log.i(TAG, "跨网络组网已停止")
    }
}
