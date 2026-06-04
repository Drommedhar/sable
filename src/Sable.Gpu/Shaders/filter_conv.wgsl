// 3x3 convolution filters (PLAN §16.5). mode 0 = sharpen (cross kernel, strength p0).
// Operates on straight colour, preserves alpha.

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct F { mode: u32, p0: f32, p1: f32, p2: f32, p3: f32, p4: f32, p5: f32, p6: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> f: F;
@group(0) @binding(2) var<storage, read>       src:  array<vec4<f32>>;
@group(0) @binding(3) var<storage, read_write>   outp: array<vec4<f32>>;

fn unpack(c: vec4<f32>) -> vec4<f32> { return c; }
fn pack(c: vec4<f32>) -> vec4<f32> { return c; }
fn rgbAt(ix: i32, iy: i32) -> vec3<f32> {
    let x = clamp(ix, 0, i32(dims.width) - 1);
    let y = clamp(iy, 0, i32(dims.height) - 1);
    return unpack(src[u32(y) * dims.width + u32(x)]).xyz;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;
    let c = unpack(src[idx]);
    let a = f.p0;     // strength
    let centre = rgbAt(i32(gid.x), i32(gid.y));
    let cross = rgbAt(i32(gid.x) - 1, i32(gid.y)) + rgbAt(i32(gid.x) + 1, i32(gid.y))
              + rgbAt(i32(gid.x), i32(gid.y) - 1) + rgbAt(i32(gid.x), i32(gid.y) + 1);
    let rgb = clamp(centre * (1.0 + 4.0 * a) - cross * a, vec3<f32>(0.0), vec3<f32>(1.0));
    outp[idx] = pack(vec4<f32>(rgb, c.w));
}
