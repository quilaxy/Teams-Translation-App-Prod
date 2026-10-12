// AudioWorklet processor — convert Float32 mic input ke PCM 16-bit little-endian
// Buffer size dikalibrasi untuk ~50ms per chunk @ 16000Hz
// = 800 samples per chunk (16000 * 0.05)
// Ini sama persis dengan BufferMilliseconds=50 di NAudio WaveInEvent desktop version

class PcmProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this._buffer = [];
        this._bufferSize = 0;
        // 800 samples = 50ms @ 16000Hz — sama dengan desktop NAudio BufferMilliseconds=50
        this._targetSamples = 800;
    }

    process(inputs) {
        const input = inputs[0];
        if (!input || !input[0]) return true;

        const float32 = input[0];

        // Accumulate samples
        for (let i = 0; i < float32.length; i++) {
            this._buffer.push(float32[i]);
        }
        this._bufferSize += float32.length;

        // Kirim saat sudah terkumpul ~50ms worth of samples
        while (this._bufferSize >= this._targetSamples) {
            const chunk = this._buffer.splice(0, this._targetSamples);
            this._bufferSize -= this._targetSamples;

            // Convert float32 → PCM 16-bit little-endian
            const pcm16 = new Int16Array(chunk.length);
            for (let i = 0; i < chunk.length; i++) {
                const s = Math.max(-1, Math.min(1, chunk[i]));
                pcm16[i] = s < 0 ? s * 0x8000 : s * 0x7fff;
            }

            // Transfer ownership (zero-copy)
            this.port.postMessage(pcm16.buffer, [pcm16.buffer]);
        }

        return true;
    }
}

registerProcessor('pcm-processor', PcmProcessor);
