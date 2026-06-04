// Directional blurs (PLAN §16.5). mode 0 = motion blur (along an angle),
// mode 1 = zoom blur (radial toward the document centre). Premultiplied averaging.

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct F { mode: u32, p0: f32, p1: f32, p2: f32, p3: f32, p4: f32, p5: f32, p6: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> f: F;
@group(0) @binding(2) var<storage, read>       src:  array<vec4<f32>>;
@group(0) @binding(3) var<storage, read_write>   outp: array<vec4<f32>>;

fn unpack(c: vec4<f32>) -> vec4<f32> { return c; }
fn pack(c: vec4<f32>) -> vec4<f32> { return c; }
fn sample(p: vec2<f32>) -> vec4<f32> {
    let x = clamp(i32(p.x), 0, i32(dims.width) - 1);
    let y = clamp(i32(p.y), 0, i32(dims.height) - 1);
    let c = unpack(src[u32(y) * dims.width + u32(x)]);
    return vec4<f32>(c.xyz * c.w, c.w);    // premultiplied
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let pos = vec2<f32>(f32(gid.x), f32(gid.y));
    let steps = 24;
    var sum = vec4<f32>(0.0);
    var n = 0.0;

    if (f.mode == 0u) {                     // motion: length p0 along angle p1
        let len = max(1.0, f.p0);
        let rad = f.p1 * 3.14159265 / 180.0;
        let dir = vec2<f32>(cos(rad), sin(rad));
        for (var k = -steps; k <= steps; k = k + 1) {
            let t = f32(k) / f32(steps) * len;
            sum = sum + sample(pos + dir * t);
            n = n + 1.0;
        }
    } else {                                // zoom: strength p0 toward centre
        let centre = vec2<f32>(f32(dims.width), f32(dims.height)) * 0.5;
        let v = pos - centre;
        let s = clamp(f.p0, 0.0, 1.0);
        for (var k = 0; k <= steps; k = k + 1) {
            let scale = 1.0 - s * (f32(k) / f32(steps));
            sum = sum + sample(centre + v * scale);
            n = n + 1.0;
        }
    }

    var o = sum / max(1.0, n);
    if (o.w > 1e-5) { o = vec4<f32>(o.xyz / o.w, o.w); }   // un-premultiply
    outp[gid.y * dims.width + gid.x] = pack(o);
}
