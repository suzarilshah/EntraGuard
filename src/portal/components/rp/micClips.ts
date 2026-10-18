'use client';

// Shared with entraguard-gemini/web/mic.js. Microphone capture as independent ~4-second WAV clips (16 kHz, mono, 16-bit PCM).
//
// Each clip is a complete file, so Gemini can decode it on its own. Noise suppression is
// deliberately OFF: it is built to remove exactly the quiet second voice we want Gemini
// to hear.

const TARGET_RATE = 16000;

function downsample(chunks: Float32Array[], inputRate: number) {
  const length = chunks.reduce((n, c) => n + c.length, 0);
  const joined = new Float32Array(length);
  let offset = 0;
  for (const c of chunks) { joined.set(c, offset); offset += c.length; }
  const ratio = inputRate / TARGET_RATE;
  const out = new Float32Array(Math.floor(length / ratio));
  for (let i = 0; i < out.length; i++) {
    const start = Math.floor(i * ratio);
    const end = Math.min(length, Math.floor((i + 1) * ratio));
    let sum = 0;
    for (let j = start; j < end; j++) sum += joined[j];
    out[i] = sum / Math.max(1, end - start);
  }
  return out;
}

function toWavBase64(samples: Float32Array) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const write = (o: number, s: string) => { for (let i = 0; i < s.length; i++) view.setUint8(o + i, s.charCodeAt(i)); };
  write(0, 'RIFF'); view.setUint32(4, 36 + samples.length * 2, true); write(8, 'WAVE');
  write(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, TARGET_RATE, true); view.setUint32(28, TARGET_RATE * 2, true);
  view.setUint16(32, 2, true); view.setUint16(34, 16, true);
  write(36, 'data'); view.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i++) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(44 + i * 2, s < 0 ? s * 0x8000 : s * 0x7fff, true);
  }
  const bytes = new Uint8Array(buffer);
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) binary += String.fromCharCode(...Array.from(bytes.subarray(i, i + 0x8000)));
  return btoa(binary);
}

function rms(samples: Float32Array) {
  let sum = 0;
  for (const s of samples) sum += s * s;
  return Math.sqrt(sum / Math.max(1, samples.length));
}

/**
 * Start capturing. onClip(base64Wav) is awaited, so clips never overlap in flight; audio
 * captured while a clip is being assessed simply joins the next one.
 */
export async function startMicrophone({ onClip, onLevel, clipSeconds = 4, maxClipSeconds = 8 }: {
  onClip: (wavBase64: string) => Promise<void>;
  onLevel?: (level: number) => void;
  clipSeconds?: number;
  maxClipSeconds?: number;
}) {
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: { channelCount: 1, echoCancellation: true, noiseSuppression: false, autoGainControl: true },
  });
  const context = new AudioContext();
  const source = context.createMediaStreamSource(stream);
  const processor = context.createScriptProcessor(4096, 1, 1);
  let chunks: Float32Array[] = [];
  let captured = 0;
  let busy = false;
  let stopped = false;

  async function flush(force = false): Promise<void> {
    if (busy || chunks.length === 0) return;
    const seconds = captured / context.sampleRate;
    if (!force && seconds < clipSeconds) return;
    const take = chunks;
    chunks = [];
    captured = 0;
    let samples = downsample(take, context.sampleRate);
    if (samples.length > maxClipSeconds * TARGET_RATE) samples = samples.slice(-maxClipSeconds * TARGET_RATE);
    if (rms(samples) < 0.004) return; // silence: nothing for Gemini to hear
    busy = true;
    try { await onClip(toWavBase64(samples)); } finally { busy = false; }
  }

  processor.onaudioprocess = (event: AudioProcessingEvent) => {
    if (stopped) return;
    const input = event.inputBuffer.getChannelData(0);
    chunks.push(new Float32Array(input));
    captured += input.length;
    onLevel?.(Math.min(1, rms(input) * 6));
    flush();
  };
  source.connect(processor);
  processor.connect(context.destination);

  return {
    async stop() {
      stopped = true;
      processor.disconnect();
      source.disconnect();
      stream.getTracks().forEach((t) => t.stop());
      while (busy) await new Promise((r) => setTimeout(r, 100));
      await flush(true);
      await context.close();
      onLevel?.(0);
    },
  };
}
