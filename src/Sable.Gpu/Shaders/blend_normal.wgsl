// M0 spike: composite two RGBA8 layers with Normal (src-over) blend on the GPU.
// Layers are packed as array<u32> (byte0=R, byte1=G, byte2=B, byte3=A; little-endian).

struct Dims { width: u32, height: u32, _pad0: u32, _pad1: u32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<storage, read>       bottom: array<u32>;
@group(0) @binding(2) var<storage, read>       top:    array<u32>;
@group(0) @binding(3) var<storage, read_write>  outp:   array<u32>;

fn unpack(c: u32) -> vec4<f32> {
    return vec4<f32>(
        f32(c & 0xffu),
        f32((c >> 8u) & 0xffu),
        f32((c >> 16u) & 0xffu),
        f32((c >> 24u) & 0xffu)) / 255.0;
}

fn pack(c: vec4<f32>) -> u32 {
    let r = u32(clamp(c.x, 0.0, 1.0) * 255.0 + 0.5);
    let g = u32(clamp(c.y, 0.0, 1.0) * 255.0 + 0.5);
    let b = u32(clamp(c.z, 0.0, 1.0) * 255.0 + 0.5);
    let a = u32(clamp(c.w, 0.0, 1.0) * 255.0 + 0.5);
    return r | (g << 8u) | (b << 16u) | (a << 24u);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;

    let d = unpack(bottom[idx]);
    let s = unpack(top[idx]);

    // Normal (src-over), premultiplied math on straight-alpha inputs.
    let outA = s.w + d.w * (1.0 - s.w);
    var outRGB = vec3<f32>(0.0, 0.0, 0.0);
    if (outA > 0.0) {
        outRGB = (s.xyz * s.w + d.xyz * d.w * (1.0 - s.w)) / outA;
    }
    outp[idx] = pack(vec4<f32>(outRGB, outA));
}
