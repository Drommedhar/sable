// Separable Gaussian blur (one axis per dispatch). Run twice: horizontal then
// vertical. Reads the backdrop, writes the blurred result. Edge pixels clamp.

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct Blur { radius: f32, dirX: f32, dirY: f32, _p: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> blur: Blur;
@group(0) @binding(2) var<storage, read>       src:  array<u32>;
@group(0) @binding(3) var<storage, read_write>   outp: array<u32>;

fn unpack(c: u32) -> vec4<f32> {
    return vec4<f32>(f32(c & 0xffu), f32((c >> 8u) & 0xffu),
        f32((c >> 16u) & 0xffu), f32((c >> 24u) & 0xffu)) / 255.0;
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

    let radius = i32(blur.radius);
    let dx = i32(blur.dirX);
    let dy = i32(blur.dirY);
    let sigma = max(1.0, blur.radius * 0.5);
    let twoSigma2 = 2.0 * sigma * sigma;

    // premultiply by alpha so transparent neighbors don't bleed dark color
    var sum = vec4<f32>(0.0);
    var wsum = 0.0;
    for (var k = -radius; k <= radius; k = k + 1) {
        let sx = clamp(i32(gid.x) + dx * k, 0, i32(dims.width) - 1);
        let sy = clamp(i32(gid.y) + dy * k, 0, i32(dims.height) - 1);
        let c = unpack(src[u32(sy) * dims.width + u32(sx)]);
        let w = exp(-f32(k * k) / twoSigma2);
        sum = sum + vec4<f32>(c.xyz * c.w, c.w) * w;   // premultiplied
        wsum = wsum + w;
    }
    var outc = sum / max(1e-5, wsum);
    if (outc.w > 1e-5) { outc = vec4<f32>(outc.xyz / outc.w, outc.w); }  // un-premultiply
    outp[gid.y * dims.width + gid.x] = pack(outc);
}
