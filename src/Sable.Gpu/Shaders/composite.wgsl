// N-layer compositor pass: blend one layer (src) over the accumulator (dst) using
// a blend mode + layer opacity, writing the new accumulator (outp). Run once per
// visible layer, bottom->top, ping-ponging dst/outp.
//
// Pixels packed as array<u32> (byte0=R..byte3=A, straight alpha). Blend math follows
// the W3C compositing model: blended color B(cs,cd), then src-over with mixed alpha.

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
// clip: 1 = clip to backdrop alpha; m*/b* = inverse (doc→layer) affine for the layer transform
struct Params {
    mode: u32, opacity: f32, clip: f32,
    m00: f32, m01: f32, m10: f32, m11: f32, b0: f32, b1: f32,
    _p0: f32, _p1: f32, _p2: f32,
};

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> params: Params;
@group(0) @binding(2) var<storage, read>        dst:  array<u32>;
@group(0) @binding(3) var<storage, read>        src:  array<u32>;
@group(0) @binding(4) var<storage, read_write>   outp: array<u32>;
@group(0) @binding(5) var<storage, read>        mask: array<u32>;   // R channel = coverage

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

fn overlay1(cb: f32, cs: f32) -> f32 {
    if (cb <= 0.5) { return 2.0 * cb * cs; }
    return 1.0 - 2.0 * (1.0 - cb) * (1.0 - cs);
}

// blend function B(cb, cs): cb = backdrop, cs = source color
fn blend(cb: vec3<f32>, cs: vec3<f32>, mode: u32) -> vec3<f32> {
    switch mode {
        case 1u: { return cb * cs; }                                   // Multiply
        case 2u: { return cb + cs - cb * cs; }                         // Screen
        case 3u: { return vec3<f32>(overlay1(cb.x, cs.x), overlay1(cb.y, cs.y), overlay1(cb.z, cs.z)); } // Overlay
        case 4u: { return min(cb, cs); }                               // Darken
        case 5u: { return max(cb, cs); }                               // Lighten
        case 6u: { return min(cb + cs, vec3<f32>(1.0)); }              // Add
        default: { return cs; }                                        // Normal
    }
}

fn srcTexel(ix: i32, iy: i32) -> vec4<f32> {
    if (ix < 0 || iy < 0 || ix >= i32(dims.width) || iy >= i32(dims.height)) { return vec4<f32>(0.0); }
    return unpack(src[u32(iy) * dims.width + u32(ix)]);
}
fn maskTexel(ix: i32, iy: i32) -> f32 {
    if (ix < 0 || iy < 0 || ix >= i32(dims.width) || iy >= i32(dims.height)) { return 0.0; }
    return unpack(mask[u32(iy) * dims.width + u32(ix)]).x;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;

    let d = unpack(dst[idx]);

    // inverse-affine map this doc pixel into the layer, then bilinear sample
    let dx = f32(gid.x); let dy = f32(gid.y);
    let lx = params.m00 * dx + params.m01 * dy + params.b0;
    let ly = params.m10 * dx + params.m11 * dy + params.b1;
    let x0 = i32(floor(lx)); let y0 = i32(floor(ly));
    let fx = lx - f32(x0); let fy = ly - f32(y0);
    let s = mix(mix(srcTexel(x0, y0), srcTexel(x0 + 1, y0), fx),
                mix(srcTexel(x0, y0 + 1), srcTexel(x0 + 1, y0 + 1), fx), fy);
    let m = mix(mix(maskTexel(x0, y0), maskTexel(x0 + 1, y0), fx),
                mix(maskTexel(x0, y0 + 1), maskTexel(x0 + 1, y0 + 1), fx), fy);
    let da = d.w;
    let clipMul = mix(1.0, da, params.clip); // clip to backdrop alpha when clip=1
    let sa = s.w * params.opacity * m * clipMul;   // effective source alpha

    // blended color, then where backdrop is opaque use blended, else raw source
    let b = blend(d.xyz, s.xyz, params.mode);
    let cs = mix(s.xyz, b, da);

    let outA = sa + da * (1.0 - sa);
    var outRGB = vec3<f32>(0.0);
    if (outA > 0.0) {
        outRGB = (cs * sa + d.xyz * da * (1.0 - sa)) / outA;
    }
    outp[idx] = pack(vec4<f32>(outRGB, outA));
}
