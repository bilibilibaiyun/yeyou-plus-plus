package com.yeyou.phonebox

import java.math.BigInteger
import java.security.MessageDigest
import javax.crypto.Cipher
import javax.crypto.spec.SecretKeySpec

/**
 * 联机码解码器。
 *
 * 与电脑端 `JoinCodeGenerator` 精确对齐：
 *   - 联机码 = Base62( AES-128-ECB/PKCS7( [IPv4 4字节][端口 2字节大端][6位 ASCII 口令] ) )
 *   - 密钥 = SHA256("YeyouPlusPlus.PhoneBox.JoinCode.v1") 的前 16 字节（固定密钥）
 *   - Base62 字母表 = 0-9 A-Z a-z
 *
 * 注意：电脑端用 .NET BigInteger（小端字节数组）做 Base62 编解码。本类用 java.math.BigInteger
 * 复现其「大整数」数值语义，从而保证「Base62 字符串 → 16 字节密文」与电脑端
 * `JoinCodeGenerator.Base62Decode` 的结果一致。
 */
object JoinCodeDecoder {

    private const val KEY_SEED = "YeyouPlusPlus.PhoneBox.JoinCode.v1"
    private const val BASE62_ALPHABET =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"

    private const val CIPHER_LENGTH = 16
    private const val PLAINTEXT_LENGTH = 12
    private const val REMOTE_CIPHER_LENGTH = 32
    private const val REMOTE_PLAINTEXT_LENGTH = 26

    /** 解码结果：电脑端 IPv4、TCP 端口、鉴权口令；跨网络码附带网络名/密钥。 */
    data class JoinInfo(
        val ip: String,
        val port: Int,
        val token: String,
        val networkName: String?,
        val networkSecret: String?
    ) {
        /** 是否为跨网络联机码。 */
        val isRemote: Boolean get() = networkName != null
    }

    /**
     * 解码联机码（自动区分局域网码与跨网络码）。
     *
     * @return 解码成功返回 [JoinInfo]，失败返回 null。
     */
    fun decode(code: String?): JoinInfo? {
        if (code.isNullOrBlank()) {
            return null
        }

        return try {
            val trimmed = code.trim()
            // 根据联机码长度预判密文长度：局域网约 22 字符→16 字节；跨网络约 43 字符→32 字节。
            val targetCipherLen = if (trimmed.length <= 30) CIPHER_LENGTH else REMOTE_CIPHER_LENGTH
            val cipher = base62Decode(trimmed, targetCipherLen) ?: return null
            when (cipher.size) {
                CIPHER_LENGTH -> {
                    val plaintext = aesDecrypt(cipher, PLAINTEXT_LENGTH) ?: return null
                    parseLanPlaintext(plaintext)
                }
                REMOTE_CIPHER_LENGTH -> {
                    val plaintext = aesDecrypt(cipher, REMOTE_PLAINTEXT_LENGTH) ?: return null
                    parseRemotePlaintext(plaintext)
                }
                else -> null
            }
        } catch (e: Exception) {
            null
        }
    }

    /**
     * Base62 解码为指定长度的大端字节数组。
     * 先按「value = value * 62 + digit」累加出大整数，再转成固定长度的大端表示，
     * 语义等价于电脑端 `JoinCodeGenerator.Base62Decode`。
     */
    private fun base62Decode(code: String, length: Int): ByteArray? {
        if (code.isEmpty()) {
            return null
        }

        var value = BigInteger.ZERO
        val base = BigInteger.valueOf(BASE62_ALPHABET.length.toLong())
        for (ch in code) {
            val idx = BASE62_ALPHABET.indexOf(ch)
            if (idx < 0) {
                return null
            }
            value = value.multiply(base).add(BigInteger.valueOf(idx.toLong()))
        }

        return toFixedBigEndian(value, length)
    }

    /**
     * 把非负 BigInteger 转成指定长度的「大端」字节数组（右侧对齐，左侧补 0）。
     * 对应电脑端 `BigInteger.ToByteArray()`（小端最小表示）之后逆序写回 16 字节的逻辑。
     */
    private fun toFixedBigEndian(value: BigInteger, length: Int): ByteArray? {
        if (value.signum() < 0) {
            return null
        }

        // Java 的 toByteArray() 是大端最小表示；非负值可能带一个前导 0x00 符号字节。
        val raw = value.toByteArray()
        val magnitude = if (raw.size > 1 && raw[0].toInt() == 0) {
            raw.copyOfRange(1, raw.size)
        } else {
            raw
        }

        if (magnitude.size > length) {
            return null
        }

        val out = ByteArray(length)
        System.arraycopy(magnitude, 0, out, length - magnitude.size, magnitude.size)
        return out
    }

    /** AES-128-ECB/PKCS7 解密，返回指定长度的明文。 */
    private fun aesDecrypt(cipher: ByteArray, expectedPlaintextLength: Int): ByteArray? {
        val cipherObj = Cipher.getInstance("AES/ECB/PKCS5Padding")
        cipherObj.init(Cipher.DECRYPT_MODE, SecretKeySpec(deriveKey(), "AES"))
        val plaintext = cipherObj.doFinal(cipher)
        return if (plaintext.size == expectedPlaintextLength) plaintext else null
    }

    /** 固定密钥：SHA256(种子字符串) 前 16 字节。 */
    private fun deriveKey(): ByteArray {
        val digest = MessageDigest.getInstance("SHA-256")
        return digest.digest(KEY_SEED.toByteArray(Charsets.UTF_8)).copyOf(16)
    }

    /** 解析局域网明文：[IPv4 4字节][端口 2字节大端][6位 ASCII 口令]。 */
    private fun parseLanPlaintext(p: ByteArray): JoinInfo {
        val ip = buildString {
            append(p[0].toInt() and 0xFF).append('.')
            append(p[1].toInt() and 0xFF).append('.')
            append(p[2].toInt() and 0xFF).append('.')
            append(p[3].toInt() and 0xFF)
        }
        val port = ((p[4].toInt() and 0xFF) shl 8) or (p[5].toInt() and 0xFF)
        val token = String(p, 6, 6, Charsets.US_ASCII)
        return JoinInfo(ip = ip, port = port, token = token, networkName = null, networkSecret = null)
    }

    /** 解析跨网络明文：[虚拟IPv4 4字节][端口 2字节][网络名 12字节][密钥 8字节]。 */
    private fun parseRemotePlaintext(p: ByteArray): JoinInfo {
        val ip = buildString {
            append(p[0].toInt() and 0xFF).append('.')
            append(p[1].toInt() and 0xFF).append('.')
            append(p[2].toInt() and 0xFF).append('.')
            append(p[3].toInt() and 0xFF)
        }
        val port = ((p[4].toInt() and 0xFF) shl 8) or (p[5].toInt() and 0xFF)
        val networkName = String(p, 6, 12, Charsets.US_ASCII).trim()
        val networkSecret = String(p, 18, 8, Charsets.US_ASCII).trim()
        return JoinInfo(
            ip = ip,
            port = port,
            token = networkSecret,
            networkName = networkName,
            networkSecret = networkSecret
        )
    }
}
