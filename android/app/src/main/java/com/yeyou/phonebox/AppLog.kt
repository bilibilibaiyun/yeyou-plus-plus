package com.yeyou.phonebox

import android.util.Log
import java.text.SimpleDateFormat
import java.util.ArrayDeque
import java.util.Date
import java.util.Locale

/**
 * 应用内日志收集器：把关键日志同时写入 logcat 和内存环形缓冲区，
 * 供「报错时一键复制完整日志」使用。
 *
 * 使用方式：在关键代码路径用 [i] / [w] / [e] 替代 android.util.Log 的对应方法，
 * 之后调用 [dump] 即可拿到按时间排序的完整日志文本。
 */
object AppLog {

    private const val MAX_LINES = 400

    private val buffer = ArrayDeque<String>()
    private val timeFormat = SimpleDateFormat("HH:mm:ss.SSS", Locale.US)

    @Synchronized
    fun i(tag: String, msg: String) = add("I", tag, msg, null)

    @Synchronized
    fun w(tag: String, msg: String, t: Throwable? = null) = add("W", tag, msg, t)

    @Synchronized
    fun e(tag: String, msg: String, t: Throwable? = null) = add("E", tag, msg, t)

    private fun add(level: String, tag: String, msg: String, t: Throwable?) {
        buffer.addLast("${timeFormat.format(Date())} $level/$tag: $msg")
        if (t != null) {
            buffer.addLast(Log.getStackTraceString(t))
        }
        while (buffer.size > MAX_LINES) {
            buffer.removeFirst()
        }

        when (level) {
            "I" -> Log.i(tag, msg)
            "W" -> Log.w(tag, msg)
            "E" -> Log.e(tag, msg, t)
        }
    }

    /** 导出全部日志（按时间顺序，每条一行）。 */
    @Synchronized
    fun dump(): String = buffer.joinToString("\n")

    @Synchronized
    fun clear() {
        buffer.clear()
    }
}
