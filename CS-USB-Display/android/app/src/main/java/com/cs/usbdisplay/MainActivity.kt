package com.cs.usbdisplay

import android.Manifest
import android.annotation.SuppressLint
import android.app.Activity
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaRecorder
import android.media.audiofx.AcousticEchoCanceler
import android.media.audiofx.NoiseSuppressor
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.View
import android.view.WindowManager
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.math.max

class MainActivity : Activity() {
    companion object {
        private const val REQUEST_MIC = 7
        private const val PACKET_VIDEO = 1
        private const val PACKET_AUDIO = 2
        private const val PACKET_MIC = 3
        private const val USB_PORT = 27183
    }

    private lateinit var displayView: StreamView
    private val running = AtomicBoolean(false)
    private val micRunning = AtomicBoolean(false)
    private var worker: Thread? = null
    private var micThread: Thread? = null
    private val audioPlayer = UsbAudioPlayer()

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
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            startReceiver()
        } else {
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), REQUEST_MIC)
        }
    }

    override fun onStop() {
        running.set(false)
        micRunning.set(false)
        worker?.interrupt()
        micThread?.interrupt()
        worker = null
        micThread = null
        audioPlayer.release()
        super.onStop()
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == REQUEST_MIC) {
            startReceiver()
        }
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
                socket.connect(InetSocketAddress("127.0.0.1", USB_PORT), 3000)

                val input = DataInputStream(
                    BufferedInputStream(socket.getInputStream(), 512 * 1024)
                )

                startMicSender(socket)
                handler.post { displayView.setStatus("USB conectado — vídeo + áudio + microfone") }

                while (running.get()) {
                    val type = input.readUnsignedByte()
                    val length = input.readInt()

                    if (length < 0 || length > 20_000_000) {
                        throw IllegalStateException("Pacote USB inválido: $length bytes")
                    }

                    val bytes = ByteArray(length)
                    input.readFully(bytes)

                    when (type) {
                        PACKET_VIDEO -> {
                            val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                                ?: throw IllegalStateException("Falha ao decodificar quadro")
                            handler.post { displayView.setFrame(bitmap) }
                        }

                        PACKET_AUDIO -> {
                            if (bytes.size >= 4) {
                                val sampleRate = ByteBuffer.wrap(bytes, 0, 4)
                                    .order(ByteOrder.BIG_ENDIAN)
                                    .int
                                audioPlayer.play(sampleRate, bytes, 4, bytes.size - 4)
                            }
                        }
                    }
                }
            } catch (_: Exception) {
                handler.post { displayView.setStatus("Aguardando programa do Windows…") }
                try {
                    Thread.sleep(1000)
                } catch (_: InterruptedException) {
                    break
                }
            } finally {
                micRunning.set(false)
                micThread?.interrupt()
                micThread = null
                audioPlayer.release()
                try {
                    socket?.close()
                } catch (_: Exception) {
                }
            }
        }
    }

    @SuppressLint("MissingPermission")
    private fun startMicSender(socket: Socket) {
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            return
        }
        if (!micRunning.compareAndSet(false, true)) return

        micThread = Thread({
            var record: AudioRecord? = null
            var echo: AcousticEchoCanceler? = null
            var noise: NoiseSuppressor? = null

            try {
                val sampleRate = 48_000
                val minBuffer = AudioRecord.getMinBufferSize(
                    sampleRate,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT
                )
                val bufferSize = max(minBuffer * 2, 4096)

                record = AudioRecord.Builder()
                    .setAudioSource(MediaRecorder.AudioSource.VOICE_COMMUNICATION)
                    .setAudioFormat(
                        AudioFormat.Builder()
                            .setSampleRate(sampleRate)
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setChannelMask(AudioFormat.CHANNEL_IN_MONO)
                            .build()
                    )
                    .setBufferSizeInBytes(bufferSize)
                    .build()

                if (AcousticEchoCanceler.isAvailable()) {
                    echo = AcousticEchoCanceler.create(record.audioSessionId)
                    echo?.enabled = true
                }

                if (NoiseSuppressor.isAvailable()) {
                    noise = NoiseSuppressor.create(record.audioSessionId)
                    noise?.enabled = true
                }

                val output = DataOutputStream(
                    BufferedOutputStream(socket.getOutputStream(), 64 * 1024)
                )
                val buffer = ByteArray(1920)

                record.startRecording()

                while (running.get() && micRunning.get() && !socket.isClosed) {
                    val read = record.read(buffer, 0, buffer.size)
                    if (read > 0) {
                        output.writeByte(PACKET_MIC)
                        output.writeInt(read)
                        output.write(buffer, 0, read)
                        output.flush()
                    }
                }
            } catch (_: Exception) {
                // Video and PC audio can continue if microphone capture is unavailable.
            } finally {
                try {
                    record?.stop()
                } catch (_: Exception) {
                }
                echo?.release()
                noise?.release()
                record?.release()
                micRunning.set(false)
            }
        }, "usb-display-microphone").also { it.start() }
    }

    private inner class UsbAudioPlayer {
        private var track: AudioTrack? = null
        private var currentRate = 0

        @Synchronized
        fun play(sampleRate: Int, data: ByteArray, offset: Int, length: Int) {
            if (sampleRate !in 8_000..192_000 || length <= 0) return

            if (track == null || currentRate != sampleRate) {
                release()
                val minBuffer = AudioTrack.getMinBufferSize(
                    sampleRate,
                    AudioFormat.CHANNEL_OUT_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT
                )
                val bufferSize = max(minBuffer * 2, 8192)

                track = AudioTrack.Builder()
                    .setAudioAttributes(
                        AudioAttributes.Builder()
                            .setUsage(AudioAttributes.USAGE_MEDIA)
                            .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                            .build()
                    )
                    .setAudioFormat(
                        AudioFormat.Builder()
                            .setSampleRate(sampleRate)
                            .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                            .setChannelMask(AudioFormat.CHANNEL_OUT_STEREO)
                            .build()
                    )
                    .setBufferSizeInBytes(bufferSize)
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
                    .build()
                    .also { it.play() }

                currentRate = sampleRate
            }

            track?.write(data, offset, length, AudioTrack.WRITE_BLOCKING)
        }

        @Synchronized
        fun release() {
            try {
                track?.stop()
            } catch (_: Exception) {
            }
            track?.release()
            track = null
            currentRate = 0
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
                canvas.drawBitmap(
                    bmp,
                    null,
                    android.graphics.RectF(left, top, left + drawW, top + drawH),
                    paint
                )
            }
            if (status.isNotEmpty()) {
                canvas.drawText(status, 36f, 64f, textPaint)
            }
        }
    }
}
