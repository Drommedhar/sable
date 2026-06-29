// N-layer compositor pass: blend one layer (src) over the accumulator (dst) using
// a blend mode + layer opacity, writing the new accumulator (outp). Run once per
// visible layer, bottom->top, ping-ponging dst/outp.
//
// Pixels packed as array<u32> (byte0=R..byte3=A, straight alpha). Blend math follows
// the W3C compositing model: blended color B(cs,cd), then src-over with mixed alpha.

// width/height = output (document) grid; srcW/srcH = the source layer buffer's own size
// (may differ from the document for layers with independent bounds).
struct Dims { width: u32, height: u32, srcW: u32, srcH: u32 };
// clip: 0 = off; 1 = clip to backdrop alpha (running accumulator — used inside isolated/nested
// groups where the backdrop IS the clip base); 2 = clip to the base layer's standalone alpha
// (binding 8 `clipBase`) — true Photoshop clipping-mask semantics (clip to the base layer only,
// ignoring whatever is composited below it). m*/b* = inverse (doc→layer) affine for the transform.
struct Params {
    mode: u32, opacity: f32, clip: f32,
    m00: f32, m01: f32, m10: f32, m11: f32, b0: f32, b1: f32,
    fillOpacity: f32, hasMask: f32,
    h6: f32, h7: f32, h8: f32,   // perspective row (affine → 0,0,1)
    srcMode: u32,                // 0 = contiguous `src` buffer; 1 = tiled atlas (binding 6/7)
    // Blend-If "underlying layer" ramps: the layer fades IN from black between lo0..lo1 and
    // OUT to white between hi0..hi1 of the backdrop luminance (defaults 0,0,1,1 = off).
    bifLo0: f32, bifLo1: f32, bifHi0: f32, bifHi1: f32,
    _p4: f32,
};

// Working space is linear float: every pixel buffer is array<vec4<f32>> (RGBA, straight alpha).
@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> params: Params;
@group(0) @binding(2) var<storage, read>        dst:  array<vec4<f32>>;
@group(0) @binding(3) var<storage, read>        src:  array<vec4<f32>>;
@group(0) @binding(4) var<storage, read_write>   outp: array<vec4<f32>>;
@group(0) @binding(5) var<storage, read>        mask: array<vec4<f32>>;   // R channel = coverage
// tiled storage (PLAN §3): a layer's 256×256 tiles live in a shared atlas; tileTable maps the
// layer's tile grid → atlas slot (0xffffffff = empty/transparent tile, no slot). srcMode=1 only.
@group(0) @binding(6) var<storage, read>        tileTable: array<u32>;   // [gridW, gridH, slot per tile row-major]
@group(0) @binding(7) var<storage, read>        atlas:     array<vec4<f32>>;   // resident tiles, 65536 px (256×256) each
// doc-sized standalone alpha of the clip-chain BASE layer (clip mode 2). Indexed by doc pixel idx.
@group(0) @binding(8) var<storage, read>        clipBase:  array<vec4<f32>>;

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

// --- per-channel blend helpers (cb = backdrop, cs = source) ---
fn b_overlay(cb: f32, cs: f32) -> f32 {
    if (cb <= 0.5) { return 2.0 * cb * cs; }
    return 1.0 - 2.0 * (1.0 - cb) * (1.0 - cs);
}
fn b_colorBurn(cb: f32, cs: f32) -> f32 {
    if (cs <= 0.0) { return 0.0; }
    return 1.0 - min(1.0, (1.0 - cb) / cs);
}
fn b_colorDodge(cb: f32, cs: f32) -> f32 {
    if (cs >= 1.0) { return 1.0; }
    return min(1.0, cb / (1.0 - cs));
}
fn b_softLight(cb: f32, cs: f32) -> f32 {
    if (cs <= 0.5) { return cb - (1.0 - 2.0 * cs) * cb * (1.0 - cb); }
    var d = sqrt(cb);
    if (cb <= 0.25) { d = ((16.0 * cb - 12.0) * cb + 4.0) * cb; }
    return cb + (2.0 * cs - 1.0) * (d - cb);
}
fn b_vividLight(cb: f32, cs: f32) -> f32 {
    if (cs <= 0.5) { return b_colorBurn(cb, 2.0 * cs); }
    return b_colorDodge(cb, 2.0 * cs - 1.0);
}
fn b_pinLight(cb: f32, cs: f32) -> f32 {
    if (cs <= 0.5) { return min(cb, 2.0 * cs); }
    return max(cb, 2.0 * cs - 1.0);
}
fn b_reflect(cb: f32, cs: f32) -> f32 {
    if (cs >= 1.0) { return 1.0; }
    return min(1.0, cb * cb / (1.0 - cs));
}
fn each(cb: vec3<f32>, cs: vec3<f32>, mode: u32) -> vec3<f32> {
    switch mode {
        case 7u:  { return vec3<f32>(b_colorBurn(cb.x, cs.x), b_colorBurn(cb.y, cs.y), b_colorBurn(cb.z, cs.z)); }
        case 10u: { return vec3<f32>(b_colorDodge(cb.x, cs.x), b_colorDodge(cb.y, cs.y), b_colorDodge(cb.z, cs.z)); }
        case 12u: { return vec3<f32>(b_softLight(cb.x, cs.x), b_softLight(cb.y, cs.y), b_softLight(cb.z, cs.z)); }
        case 13u: { return vec3<f32>(b_overlay(cs.x, cb.x), b_overlay(cs.y, cb.y), b_overlay(cs.z, cb.z)); } // HardLight = Overlay(cs,cb)
        case 14u: { return vec3<f32>(b_vividLight(cb.x, cs.x), b_vividLight(cb.y, cs.y), b_vividLight(cb.z, cs.z)); }
        case 16u: { return vec3<f32>(b_pinLight(cb.x, cs.x), b_pinLight(cb.y, cs.y), b_pinLight(cb.z, cs.z)); }
        case 28u: { return vec3<f32>(b_reflect(cb.x, cs.x), b_reflect(cb.y, cs.y), b_reflect(cb.z, cs.z)); }
        case 29u: { return vec3<f32>(b_reflect(cs.x, cb.x), b_reflect(cs.y, cb.y), b_reflect(cs.z, cb.z)); } // Glow = Reflect(cs,cb)
        default:  { return vec3<f32>(b_overlay(cb.x, cs.x), b_overlay(cb.y, cs.y), b_overlay(cb.z, cs.z)); } // Overlay
    }
}

// --- non-separable (W3C) helpers ---
fn lum(c: vec3<f32>) -> f32 { return dot(c, vec3<f32>(0.299, 0.587, 0.114)); }
fn clipColor(c: vec3<f32>) -> vec3<f32> {
    let l = lum(c);
    let n = min(min(c.x, c.y), c.z);
    let x = max(max(c.x, c.y), c.z);
    var r = c;
    if (n < 0.0) { r = l + (r - l) * l / (l - n); }
    if (x > 1.0) { r = l + (r - l) * (1.0 - l) / (x - l); }
    return r;
}
fn setLum(c: vec3<f32>, l: f32) -> vec3<f32> { return clipColor(c + (l - lum(c))); }
fn satv(c: vec3<f32>) -> f32 { return max(max(c.x, c.y), c.z) - min(min(c.x, c.y), c.z); }
fn setSat(c: vec3<f32>, s: f32) -> vec3<f32> {
    let mn = min(min(c.x, c.y), c.z);
    let mx = max(max(c.x, c.y), c.z);
    if (mx > mn) { return (c - mn) * s / (mx - mn); }
    return vec3<f32>(0.0);
}

// blend function B(cb, cs)
fn blend(cb: vec3<f32>, cs: vec3<f32>, mode: u32) -> vec3<f32> {
    switch mode {
        case 1u:  { return cb * cs; }                                  // Multiply
        case 2u:  { return cb + cs - cb * cs; }                        // Screen
        case 3u:  { return each(cb, cs, 3u); }                         // Overlay
        case 4u:  { return min(cb, cs); }                              // Darken
        case 5u:  { return max(cb, cs); }                              // Lighten
        case 6u:  { return min(cb + cs, vec3<f32>(1.0)); }             // Add / LinearDodge
        case 7u:  { return each(cb, cs, 7u); }                         // ColorBurn
        case 8u:  { return max(cb + cs - 1.0, vec3<f32>(0.0)); }       // LinearBurn
        case 9u:  { if (lum(cb) <= lum(cs)) { return cb; } return cs; }// DarkerColor
        case 10u: { return each(cb, cs, 10u); }                        // ColorDodge
        case 11u: { if (lum(cb) >= lum(cs)) { return cb; } return cs; }// LighterColor
        case 12u: { return each(cb, cs, 12u); }                        // SoftLight
        case 13u: { return each(cb, cs, 13u); }                        // HardLight
        case 14u: { return each(cb, cs, 14u); }                        // VividLight
        case 15u: { return clamp(cb + 2.0 * cs - 1.0, vec3<f32>(0.0), vec3<f32>(1.0)); } // LinearLight
        case 16u: { return each(cb, cs, 16u); }                        // PinLight
        case 17u: { return step(vec3<f32>(0.5), each(cb, cs, 14u)); } // HardMix = threshold(VividLight) at 0.5 (true PS)
        case 18u: { return abs(cb - cs); }                             // Difference
        case 19u: { return cb + cs - 2.0 * cb * cs; }                  // Exclusion
        case 20u: { return max(cb - cs, vec3<f32>(0.0)); }             // Subtract
        case 21u: { return clamp(cb / max(cs, vec3<f32>(0.0001)), vec3<f32>(0.0), vec3<f32>(1.0)); } // Divide
        case 22u: { return setLum(setSat(cs, satv(cb)), lum(cb)); }    // Hue
        case 23u: { return setLum(setSat(cb, satv(cs)), lum(cb)); }    // Saturation
        case 24u: { return setLum(cs, lum(cb)); }                      // Color
        case 25u: { return setLum(cb, lum(cs)); }                      // Luminosity
        case 26u: { return (cb + cs) * 0.5; }                          // Average
        case 27u: { return 1.0 - abs(1.0 - cb - cs); }                 // Negation
        case 28u: { return each(cb, cs, 28u); }                        // Reflect
        case 29u: { return each(cb, cs, 29u); }                        // Glow
        default:  { return cs; }                                       // Normal
    }
}

fn srcTexel(ix: i32, iy: i32) -> vec4<f32> {
    if (ix < 0 || iy < 0 || ix >= i32(dims.srcW) || iy >= i32(dims.srcH)) { return vec4<f32>(0.0); }
    if (params.srcMode == 0u) {
        return src[u32(iy) * dims.srcW + u32(ix)];
    }
    // tiled atlas: locate the tile, look up its slot, index within the 256×256 slot
    let gw = tileTable[0];
    let tx = u32(ix) >> 8u;
    let ty = u32(iy) >> 8u;
    let slot = tileTable[2u + ty * gw + tx];
    if (slot == 0xffffffffu) { return vec4<f32>(0.0); }   // empty tile → transparent
    let inTile = (u32(iy) & 255u) * 256u + (u32(ix) & 255u);
    return atlas[slot * 65536u + inTile];
}
fn maskTexel(ix: i32, iy: i32) -> f32 {
    if (ix < 0 || iy < 0 || ix >= i32(dims.srcW) || iy >= i32(dims.srcH)) { return 0.0; }
    return mask[u32(iy) * dims.srcW + u32(ix)].x;
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;

    let d = dst[idx];

    // inverse (homography) map this doc pixel into the layer, then bilinear sample.
    // affine layers pass h6=h7=0,h8=1 → w=1 (no perspective divide).
    let dx = f32(gid.x); let dy = f32(gid.y);
    let w = params.h6 * dx + params.h7 * dy + params.h8;
    let valid = w > 1e-6;                              // w<=0 = at/behind the perspective horizon
    let iw = select(0.0, 1.0 / w, valid);
    let lx = (params.m00 * dx + params.m01 * dy + params.b0) * iw;
    let ly = (params.m10 * dx + params.m11 * dy + params.b1) * iw;
    let x0 = i32(floor(lx)); let y0 = i32(floor(ly));
    let fx = lx - f32(x0); let fy = ly - f32(y0);
    let s = mix(mix(srcTexel(x0, y0), srcTexel(x0 + 1, y0), fx),
                mix(srcTexel(x0, y0 + 1), srcTexel(x0 + 1, y0 + 1), fx), fy);
    var m = 1.0;
    if (params.hasMask > 0.5) {
        m = mix(mix(maskTexel(x0, y0), maskTexel(x0 + 1, y0), fx),
                mix(maskTexel(x0, y0 + 1), maskTexel(x0 + 1, y0 + 1), fx), fy);
    }
    let da = d.w;
    // clip mode: 0 off, 1 = backdrop alpha (nested/group), 2 = base-layer standalone alpha (PS)
    var clipMul = 1.0;
    if (params.clip > 1.5) { clipMul = clipBase[idx].w; }
    else if (params.clip > 0.5) { clipMul = da; }
    let validF = select(0.0, 1.0, valid);    // beyond the horizon → fully transparent (no smeared sample)
    // Blend-If: gate the layer by the UNDERLYING (backdrop) luminance with smooth knees
    var bif = 1.0;
    if (params.bifLo1 > 0.0 || params.bifHi0 < 1.0) {
        let bl = dot(d.xyz, vec3<f32>(0.299, 0.587, 0.114));
        bif = smoothstep(params.bifLo0, max(params.bifLo1, params.bifLo0 + 1e-4), bl)
            * (1.0 - smoothstep(min(params.bifHi0, params.bifHi1 - 1e-4), params.bifHi1, bl));
    }
    let sa = s.w * params.opacity * params.fillOpacity * m * clipMul * validF * bif;   // effective source alpha

    // blended color, then where backdrop is opaque use blended, else raw source
    let b = blend(d.xyz, s.xyz, params.mode);
    let cs = mix(s.xyz, b, da);

    let outA = sa + da * (1.0 - sa);
    var outRGB = vec3<f32>(0.0);
    if (outA > 0.0) {
        outRGB = (cs * sa + d.xyz * da * (1.0 - sa)) / outA;
    }
    outp[idx] = vec4<f32>(outRGB, outA);
}
