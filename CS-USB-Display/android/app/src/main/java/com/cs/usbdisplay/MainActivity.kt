package com.cs.usbdisplay

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowManager
import java.io.BufferedInputStream
import java.io.DataInputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

class MainActivity : Activity() {
    private lateinit var displayView: StreamView
    private val running = AtomicBoolean(false)
    private var worker: Thread? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        @Suppress("DEPRECATION")
        window.decorView.systemUiVisibility = (
            View.SYSTEM_UI_FLAG_FULLSCREEN or
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
        )
        displayView = StreamView()
        setContentView(displayView)
    }

    override fun onStart() {
        super.onStart()
        startReceiver()
    }

    override fun onStop() {
        running.set(false)
        worker?.interrupt()
        worker = null
        super.onStop()
    }

    private fun startReceiver() {
        if (!running.compareAndSet(false, true)) return
        worker = Thread({ receiverLoop() }, "usb-display-receiver").also { it.start() }
    }

    private fun receiverLoop() {
        val handler = Handler(Looper.getMainLooper())
        while (running.get()) {
            var socket: Socket? = null
            try {
                handler.post { displayView.setStatus("Conectando via USB…") }
                socket = Socket()
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress("127.0.0.1", 27183), 3000)
                val input = DataInputStream(BufferedInputStream(socket.getInputStream(), 256 * 1024))
                handler.post { displayView.setStatus("USB conectado") }

                while (running.get()) {
                    val length = input.readInt()
                    if (length <= 0 || length > 20_000_000) {
                        throw IllegalStateException("Quadro inválido: $length bytes")
                    }
                    val bytes = ByteArray(length)
                    input.readFully(bytes)
                    val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                        ?: throw IllegalStateException("Falha ao decodificar quadro")
                    handler.post { displayView.setFrame(bitmap) }
                }
            } catch (_: Exception) {
                handler.post { displayView.setStatus("Aguardando programa do Windows…") }
                try { Thread.sleep(1000) } catch (_: InterruptedException) { break }
            } finally {
                try { socket?.close() } catch (_: Exception) { }
            }
        }
    }

    inner class StreamView : View(this) {
        private val paint = Paint(Paint.FILTER_BITMAP_FLAG)
        private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = Color.WHITE
            textSize = 34f
        }
        private var frame: Bitmap? = null
        private var status: String = "Aguardando programa do Windows…"

        fun setFrame(next: Bitmap) {
            val old = frame
            frame = next
            status = ""
            invalidate()
            if (old != null && old !== next && !old.isRecycled) old.recycle()
        }

        fun setStatus(value: String) {
            status = value
            invalidate()
        }

        override fun onDraw(canvas: Canvas) {
            super.onDraw(canvas)
            canvas.drawColor(Color.BLACK)
            val bmp = frame
            if (bmp != null && !bmp.isRecycled) {
                val scale = minOf(width.toFloat() / bmp.width, height.toFloat() / bmp.height)
                val drawW = bmp.width * scale
                val drawH = bmp.height * scale
                val left = (width - drawW) / 2f
                val top = (height - drawH) / 2f
                canvas.drawBitmap(bmp, null, android.graphics.RectF(left, top, left + drawW, top + drawH), paint)
            }
            if (status.isNotEmpty()) {
                canvas.drawText(status, 36f, 64f, textPaint)
            }
        }
    }
}
