// Unified adjustment pass over the accumulated backdrop (in-place transform).
// `kind` selects the adjustment; p0..p5 are its parameters. opacity * mask =
// strength. New adjustments add a case here + a packer on AdjustmentLayer.
//   kind 0 BrightnessContrast: p0=brightness(-1..1), p1=contrast(0..2)
//   kind 1 Levels:             p0=inBlack, p1=inWhite, p2=gamma, p3=outBlack, p4=outWhite
//   kind 2 HSL:                p0=hueShift(turns), p1=satScale, p2=lightShift
//   kind 3 Curves:             per-channel LUT (binding 5); 4×256 f32, ch0=composite,1=R,2=G,3=B
//   kind 4 Exposure:           p0=stops              kind 5 Vibrance: p0=amount(-1..1)
//   kind 6 Threshold:          p0=cut(0..1)          kind 7 Posterise: p0=levels  kind 8 Invert: (none)
//   kind 9 Black&White:        p0/p1/p2=R/G/B weights   kind 10 WhiteBalance: p0=temp, p1=tint

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
struct Adj { kind: u32, opacity: f32, p0: f32, p1: f32, p2: f32, p3: f32, p4: f32, p5: f32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> adj: Adj;
@group(0) @binding(2) var<storage, read>       src:  array<u32>;
@group(0) @binding(3) var<storage, read_write>   outp: array<u32>;
@group(0) @binding(4) var<storage, read>       mask: array<u32>;
@group(0) @binding(5) var<storage, read>       lut:  array<f32>;   // 4*256 curve LUT

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

fn lutSample(ch: u32, v: f32) -> f32 {
    let x = clamp(v, 0.0, 1.0) * 255.0;
    let i0 = u32(floor(x));
    let i1 = min(i0 + 1u, 255u);
    let f = x - f32(i0);
    let base = ch * 256u;
    return mix(lut[base + i0], lut[base + i1], f);
}

fn levels1(c: f32, inB: f32, inW: f32, gamma: f32) -> f32 {
    let denom = max(1e-4, inW - inB);
    let t = clamp((c - inB) / denom, 0.0, 1.0);
    return pow(t, 1.0 / max(1e-4, gamma));
}

fn rgb2hsl(c: vec3<f32>) -> vec3<f32> {
    let mx = max(c.x, max(c.y, c.z));
    let mn = min(c.x, min(c.y, c.z));
    let l = (mx + mn) * 0.5;
    var h = 0.0; var s = 0.0;
    let d = mx - mn;
    if (d > 1e-5) {
        s = d / (1.0 - abs(2.0 * l - 1.0) + 1e-5);
        if (mx == c.x) { h = (c.y - c.z) / d + select(0.0, 6.0, c.y < c.z); }
        else if (mx == c.y) { h = (c.z - c.x) / d + 2.0; }
        else { h = (c.x - c.y) / d + 4.0; }
        h = h / 6.0;
    }
    return vec3<f32>(h, s, l);
}
fn hue2rgb(p: f32, q: f32, tIn: f32) -> f32 {
    var t = tIn;
    if (t < 0.0) { t = t + 1.0; }
    if (t > 1.0) { t = t - 1.0; }
    if (t < 1.0 / 6.0) { return p + (q - p) * 6.0 * t; }
    if (t < 1.0 / 2.0) { return q; }
    if (t < 2.0 / 3.0) { return p + (q - p) * (2.0 / 3.0 - t) * 6.0; }
    return p;
}
fn hsl2rgb(hsl: vec3<f32>) -> vec3<f32> {
    let h = fract(hsl.x); let s = clamp(hsl.y, 0.0, 1.0); let l = clamp(hsl.z, 0.0, 1.0);
    if (s <= 1e-5) { return vec3<f32>(l); }
    let q = select(l + s - l * s, l * (1.0 + s), l < 0.5);
    let p = 2.0 * l - q;
    return vec3<f32>(hue2rgb(p, q, h + 1.0 / 3.0), hue2rgb(p, q, h), hue2rgb(p, q, h - 1.0 / 3.0));
}

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;
    let c = unpack(src[idx]);
    let m = unpack(mask[idx]).x;

    var rgb = c.xyz;
    switch adj.kind {
        case 1u: {  // Levels (in black/white/gamma, then output black/white remap)
            let ob = adj.p3; let ow = adj.p4;
            rgb = vec3<f32>(ob + levels1(rgb.x, adj.p0, adj.p1, adj.p2) * (ow - ob),
                            ob + levels1(rgb.y, adj.p0, adj.p1, adj.p2) * (ow - ob),
                            ob + levels1(rgb.z, adj.p0, adj.p1, adj.p2) * (ow - ob));
        }
        case 2u: {  // HSL
            var hsl = rgb2hsl(rgb);
            hsl.x = hsl.x + adj.p0;
            hsl.y = clamp(hsl.y * adj.p1, 0.0, 1.0);
            hsl.z = clamp(hsl.z + adj.p2, 0.0, 1.0);
            rgb = hsl2rgb(hsl);
        }
        case 3u: {  // Curves: per-channel then composite
            rgb = vec3<f32>(lutSample(0u, lutSample(1u, rgb.x)),
                            lutSample(0u, lutSample(2u, rgb.y)),
                            lutSample(0u, lutSample(3u, rgb.z)));
        }
        case 4u: {  // Exposure (gain = 2^stops)
            rgb = rgb * exp2(adj.p0);
        }
        case 5u: {  // Vibrance (boost low-saturation more)
            var hsl = rgb2hsl(rgb);
            let amt = adj.p0;
            hsl.y = clamp(hsl.y + amt * (1.0 - hsl.y), 0.0, 1.0);
            rgb = hsl2rgb(hsl);
        }
        case 6u: {  // Threshold (luminance cut to black/white)
            let l = dot(rgb, vec3<f32>(0.299, 0.587, 0.114));
            rgb = vec3<f32>(select(0.0, 1.0, l >= adj.p0));
        }
        case 7u: {  // Posterise (quantise to N levels)
            let n = max(2.0, round(adj.p0));
            rgb = round(rgb * (n - 1.0)) / (n - 1.0);
        }
        case 8u: {  // Invert
            rgb = vec3<f32>(1.0) - rgb;
        }
        case 9u: {  // Black & White (weighted luminance grayscale)
            let w = max(adj.p0 + adj.p1 + adj.p2, 1e-4);
            let g = dot(rgb, vec3<f32>(adj.p0, adj.p1, adj.p2) / w);
            rgb = vec3<f32>(g);
        }
        case 10u: { // White Balance (temperature/tint)
            rgb.x = clamp(rgb.x * (1.0 + adj.p0 * 0.5), 0.0, 1.0);
            rgb.z = clamp(rgb.z * (1.0 - adj.p0 * 0.5), 0.0, 1.0);
            rgb.y = clamp(rgb.y * (1.0 + adj.p1 * 0.5), 0.0, 1.0);
        }
        default: { // BrightnessContrast
            rgb = (rgb - vec3<f32>(0.5)) * adj.p1 + vec3<f32>(0.5) + vec3<f32>(adj.p0);
        }
    }
    rgb = clamp(rgb, vec3<f32>(0.0), vec3<f32>(1.0));
    let outRGB = mix(c.xyz, rgb, adj.opacity * m);
    outp[idx] = pack(vec4<f32>(outRGB, c.w));
}
