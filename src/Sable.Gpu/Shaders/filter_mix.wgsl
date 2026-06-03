// Crossfade an original backdrop (a) with its filtered copy (b) by t = opacity * mask.
// This gives a live filter REPLACE semantics: the filtered result is SHOWN instead of being
// composited OVER the original, so a blur actually softens edges (their alpha drops) rather
// than leaving the original sharp content visible underneath.
// binding 2 = original (a), 3 = filtered (b), 4 = output, 5 = mask (R = coverage; hasMask gate).

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct P { opacity: f32, hasMask: f32, _p2: f32, _p3: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> p: P;
@group(0) @binding(2) var<storage, read>       a:    array<u32>;
@group(0) @binding(3) var<storage, read>       b:    array<u32>;
@group(0) @binding(4) var<storage, read_write> outp: array<u32>;
@group(0) @binding(5) var<storage, read>       mask: array<u32>;

fn unpack(c: u32) -> vec4<f32> {
    return vec4<f32>(f32(c & 0xffu), f32((c >> 8u) & 0xffu),
        f32((c >> 16u) & 0xffu), f32((c >> 24u) & 0xffu)) / 255.0;
}
fn pack(c: vec4<f32>) -> u32 {
    let r = u32(clamp(c.x, 0.0, 1.0) * 255.0 + 0.5);
    let g = u32(clamp(c.y, 0.0, 1.0) * 255.0 + 0.5);
    let b2 = u32(clamp(c.z, 0.0, 1.0) * 255.0 + 0.5);
    let a2 = u32(clamp(c.w, 0.0, 1.0) * 255.0 + 0.5);
    return r | (g << 8u) | (b2 << 16u) | (a2 << 24u);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;
    var t = p.opacity;
    if (p.hasMask > 0.5) { t = t * (f32(mask[idx] & 0xffu) / 255.0); }
    outp[idx] = pack(mix(unpack(a[idx]), unpack(b[idx]), t));
}
