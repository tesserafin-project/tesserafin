// DSP-1 audit probe. Runs the pinned upstream JS, NOT a real WASM/browser.
// Usage: node docs/audio/oneamp-worklet-probe.mjs /path/to/oneamp-worklet.js
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(process.argv[2]);
const blob = createHash('sha1').update(`blob ${source.length}\0`).update(source).digest('hex');
assert.equal(blob, '242d39fbbcb0a51f0d1e8c9a0ce3b99fa664a970', 'supply the audited upstream blob');
let Processor;
let capacity;
let requested;
let returned;
const memory = { buffer: new ArrayBuffer(65536) };
const exports = {
    memory,
    oneamp_new(_rate, channels, frames) { capacity = frames * channels; return 1; },
    oneamp_output_channels() { return 2; },
    oneamp_buffer_frames() { return capacity / 2; },
    oneamp_buffer() { return 0; },
    // Model the pinned ABI's clamp and sample-count return; no DSP emulation.
    oneamp_process(_h, frames) { requested = frames; returned = Math.min(frames * 2, capacity); return returned; }
};
vm.runInNewContext(source.toString('utf8'), {
    AudioWorkletProcessor: class { constructor() { this.port = {}; } },
    WebAssembly: { Instance: class { constructor() { this.exports = exports; } } },
    sampleRate: 48000,
    registerProcessor(_name, constructor) { Processor = constructor; }
}, { filename: 'pinned-oneamp-worklet.js' });

const processor = new Processor({ processorOptions: { module: {}, channels: 2 } });
const results = [];
for (const frames of [64, 128, 256]) {
    const input = Array.from({ length: 2 }, () => new Float32Array(frames).fill(0.25));
    const output = Array.from({ length: 2 }, () => new Float32Array(frames));
    assert.equal(processor.process([input], [output]), true);
    const nonFiniteSamples = output.reduce((n, ch) => n + ch.reduce((m, x) => m + !Number.isFinite(x), 0), 0);
    assert.equal(requested, frames);
    assert.equal(returned, Math.min(frames * 2, 256));
    assert.equal(nonFiniteSamples, frames === 256 ? 256 : 0);
    results.push({ frames, returnedSamples: returned, nonFiniteSamples });
}
console.log(JSON.stringify({ evidence: 'mock ABI; actual pinned JS; no browser or real WASM executed', results }, null, 2));
