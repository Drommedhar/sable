// Blends a single soft round brush dab into an RGBA8 accumulator buffer — used for
// the live brush preview, composited into the stack right after the active layer so
// higher layers occlude it (matches what painting will actually produce).

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct Dab { cx: f32, cy: f32, radius: f32, hardness: f32, r: f32, g: f32, b: f32, erase: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> dab: Dab;
@group(0) @binding(2) var<storage, read_write> buf: array<u32>;

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
    let dx = f32(gid.x) - dab.cx;
    let dy = f32(gid.y) - dab.cy;
    let dist = sqrt(dx * dx + dy * dy);
    if (dist > dab.radius) { return; }

    let inner = dab.radius * clamp(dab.hardness, 0.0, 0.99);
    var t = select(1.0 - (dist - inner) / max(1.0, dab.radius - inner), 1.0, dist <= inner);
    t = clamp(t, 0.0, 1.0);
    let cov = t * t * (3.0 - 2.0 * t);

    let idx = gid.y * dims.width + gid.x;
    let d = unpack(buf[idx]);
    if (dab.erase > 0.5) {
        buf[idx] = pack(vec4<f32>(d.xyz, d.w * (1.0 - cov)));
    } else {
        let sa = cov;
        let outA = sa + d.w * (1.0 - sa);
        var rgb = vec3<f32>(dab.r, dab.g, dab.b);
        if (outA > 0.0) { rgb = (rgb * sa + d.xyz * d.w * (1.0 - sa)) / outA; }
        buf[idx] = pack(vec4<f32>(rgb, outA));
    }
}
