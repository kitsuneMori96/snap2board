package io.kitsune.snap2board

import android.content.Context
import java.security.SecureRandom

class Prefs(context: Context) {

    private val sp = context.getSharedPreferences("snap2board", Context.MODE_PRIVATE)

    var token: String
        get() = sp.getString("token", null) ?: newToken().also {
            sp.edit().putString("token", it).apply()
        }
        set(value) {
            sp.edit().putString("token", value).apply()
        }

    var pairCode: String
        get() = sp.getString("pair_code", null) ?: regeneratePairCode()
        set(value) {
            sp.edit().putString("pair_code", value).apply()
        }

    var port: Int
        get() = sp.getInt("port", 28642)
        set(value) {
            sp.edit().putInt("port", value).apply()
        }

    fun regeneratePairCode(): String {
        val code = (100000 + SecureRandom().nextInt(900000)).toString()
        sp.edit().putString("pair_code", code).apply()
        return code
    }

    private fun newToken(): String {
        val bytes = ByteArray(24)
        SecureRandom().nextBytes(bytes)
        return bytes.joinToString("") { "%02x".format(it) }
    }
}
