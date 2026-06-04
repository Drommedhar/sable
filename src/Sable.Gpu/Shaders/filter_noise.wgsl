// Noise filters (PLAN §16.5). mode 0 = add noise (monochrome, amount p0, seed p1).
// mode 1 = denoise (3x3 bilateral: average neighbours weighted by colour similarity, p0 = strength).

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct F { mode: u32, p0: f32, p1: f32, p2: f32, p3: f32, p4: f32, p5: f32, p6: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> f: F;
@group(0) @binding(2) var<storage, read>       src:  array<vec4<f32>>;
@group(0) @binding(3) var<storage, read_write>   outp: array<vec4<f32>>;

fn unpack(c: vec4<f32>) -> vec4<f32> { return c; }
fn pack(c: vec4<f32>) -> vec4<f32> { return c; }
fn hash(p: vec2<f32>) -> f32 {
    return fract(sin(dot(p, vec2<f32>(12.9898, 78.233))) * 43758.5453);
}
fn rgbaAt(ix: i32, iy: i32) -> vec4<f32> {
    let x = clamp(ix, 0, i32(dims.width) - 1);
    let y = clamp(iy, 0, i32(dims.height) - 1);
    return unpack(src[u32(y) * dims.width + u32(x)]);
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;
    let c = unpack(src[idx]);

    if (f.mode == 0u) {                     // add noise
        let n = (hash(vec2<f32>(f32(gid.x), f32(gid.y)) + f.p1) - 0.5) * 2.0 * f.p0;
        outp[idx] = pack(vec4<f32>(clamp(c.xyz + vec3<f32>(n), vec3<f32>(0.0), vec3<f32>(1.0)), c.w));
        return;
    }

    // denoise: 3x3 bilateral
    let sigma = max(0.02, f.p0);
    var sum = vec3<f32>(0.0); var wsum = 0.0;
    for (var dy = -1; dy <= 1; dy = dy + 1) {
        for (var dx = -1; dx <= 1; dx = dx + 1) {
            let nc = rgbaAt(i32(gid.x) + dx, i32(gid.y) + dy);
            let d = nc.xyz - c.xyz;
            let w = exp(-dot(d, d) / (2.0 * sigma * sigma));
            sum = sum + nc.xyz * w;
            wsum = wsum + w;
        }
    }
    outp[idx] = pack(vec4<f32>(sum / max(1e-4, wsum), c.w));
}
