// Combine an image with its blurred copy (PLAN §16.5). mode 0 = unsharp mask,
// 1 = high-pass, 2 = clarity (midtone-weighted local contrast). amount = p0.
// binding 2 = original, binding 3 = blurred, binding 4 = output. Preserves alpha.

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct F { mode: u32, p0: f32, p1: f32, p2: f32, p3: f32, p4: f32, p5: f32, p6: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> f: F;
@group(0) @binding(2) var<storage, read>       src:     array<vec4<f32>>;
@group(0) @binding(3) var<storage, read>       blurred: array<vec4<f32>>;
@group(0) @binding(4) var<storage, read_write>   outp:    array<vec4<f32>>;

fn unpack(c: vec4<f32>) -> vec4<f32> { return c; }
fn pack(c: vec4<f32>) -> vec4<f32> { return c; }

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;
    let s = unpack(src[idx]);
    let b = unpack(blurred[idx]);
    let high = s.xyz - b.xyz;
    var rgb = s.xyz;
    if (f.mode == 0u) {                     // unsharp mask
        rgb = s.xyz + f.p0 * high;
    } else if (f.mode == 1u) {              // high-pass
        rgb = vec3<f32>(0.5) + high;
    } else {                                // clarity (midtone-weighted)
        let l = dot(s.xyz, vec3<f32>(0.299, 0.587, 0.114));
        let w = 1.0 - abs(l - 0.5) * 2.0;
        rgb = s.xyz + f.p0 * w * high;
    }
    outp[idx] = pack(vec4<f32>(clamp(rgb, vec3<f32>(0.0), vec3<f32>(1.0)), s.w));
}
