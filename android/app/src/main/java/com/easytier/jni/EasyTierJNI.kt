package com.easytier.jni

fun interface ConfigServerEventCallback {
    fun onEvent(eventJson: String)
}

/**
 * EasyTier JNI 接口（对应 libeasytier_android_jni.so 导出的 native 方法）。
 *
 * 包名与类名必须保持 `com.easytier.jni.EasyTierJNI`，与 Rust 侧
 * `Java_com_easytier_jni_EasyTierJNI_*` 导出符号严格对齐。
 */
object EasyTierJNI {
    init {
        System.loadLibrary("easytier_android_jni")
    }

    @JvmStatic external fun setTunFd(instanceName: String, fd: Int): Int

    @JvmStatic external fun parseConfig(config: String): Int

    @JvmStatic external fun runNetworkInstance(config: String): Int

    @JvmStatic
    external fun startConfigServerClient(
        url: String,
        hostname: String?,
        machineId: String,
        secureMode: Boolean,
        callback: ConfigServerEventCallback?
    ): Int

    @JvmStatic external fun stopConfigServerClient(): Int

    @JvmStatic external fun isConfigServerClientConnected(): Boolean

    @JvmStatic external fun retainNetworkInstance(instanceNames: Array<String>?): Int

    @JvmStatic external fun deleteNetworkInstance(instanceName: String): Int

    @JvmStatic external fun collectNetworkInfos(maxLength: Int): String?

    @JvmStatic external fun listInstances(maxLength: Int): String?

    @JvmStatic
    external fun callJsonRpc(
        serviceName: String,
        methodName: String,
        domainName: String?,
        payloadJson: String
    ): String?

    @JvmStatic
    fun callJsonRpc(serviceName: String, methodName: String, payloadJson: String): String? {
        return callJsonRpc(serviceName, methodName, null, payloadJson)
    }

    @JvmStatic external fun getLastError(): String?

    @JvmStatic
    fun stopAllInstances(): Int = retainNetworkInstance(null)

    @JvmStatic
    fun retainSingleInstance(instanceName: String): Int = retainNetworkInstance(arrayOf(instanceName))
}
