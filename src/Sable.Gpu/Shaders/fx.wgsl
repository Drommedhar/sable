// Layer-effect source pass (PLAN §16.6). Reads the layer rendered in doc space
// (src, straight RGBA8) and emits an effect sprite (straight RGBA8):
//   mode 0 = tint:   rgb = effect colour, a = layer alpha   (overlay / shadow / glow source)
//   mode 1 = stroke: rgb = colour, a = edge band of the layer alpha (dilate/erode by size)
// Shadow/glow blur the mode-0 output afterwards (blur.wgsl); overlay/stroke blend directly.

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };
// mode: 0 tint · 1 stroke · 2 inner shadow · 3 inner glow · 4 gradient overlay · 5 bevel/emboss
// pos: 0 outside, 1 inside, 2 centre.  r2/g2/b2 = gradient end colour; angle in degrees;
// offX/offY = inner-shadow offset (doc px); size = stroke width / inner spread.
struct Fx {
    mode: u32, r: f32, g: f32, b: f32,
    size: f32, pos: f32, r2: f32, g2: f32,
    b2: f32, angle: f32, offX: f32, offY: f32,
};

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<uniform> fx: Fx;
@group(0) @binding(2) var<storage, read>       src:  array<vec4<f32>>;
@group(0) @binding(3) var<storage, read_write>   outp: array<vec4<f32>>;

fn alphaAt(ix: i32, iy: i32) -> f32 {
    if (ix < 0 || iy < 0 || ix >= i32(dims.width) || iy >= i32(dims.height)) { return 0.0; }
    return src[u32(iy) * dims.width + u32(ix)].w;
}
fn pack(c: vec4<f32>) -> vec4<f32> { return c; }

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;
    let col = vec3<f32>(fx.r, fx.g, fx.b);
    let a = alphaAt(i32(gid.x), i32(gid.y));

    if (fx.mode == 0u) {
        outp[idx] = pack(vec4<f32>(col, a));
        return;
    }

    if (fx.mode == 4u) {              // gradient overlay (linear, angle, clipped to layer alpha)
        let rad = fx.angle * 3.14159265 / 180.0;
        let dir = vec2<f32>(cos(rad), sin(rad));
        let uv = vec2<f32>(f32(gid.x) / f32(dims.width), f32(gid.y) / f32(dims.height));
        let t = clamp((dot(uv - vec2<f32>(0.5), dir) + 0.5), 0.0, 1.0);
        let gc = mix(col, vec3<f32>(fx.r2, fx.g2, fx.b2), t);
        outp[idx] = pack(vec4<f32>(gc, a));
        return;
    }

    let s = i32(clamp(fx.size, 1.0, 16.0));

    if (fx.mode == 5u) {              // bevel / emboss: light the alpha edge, clipped inside the layer
        let gx = alphaAt(i32(gid.x) + s, i32(gid.y)) - alphaAt(i32(gid.x) - s, i32(gid.y));
        let gy = alphaAt(i32(gid.x), i32(gid.y) + s) - alphaAt(i32(gid.x), i32(gid.y) - s);
        let rad = fx.angle * 3.14159265 / 180.0;
        let ld = vec2<f32>(cos(rad), sin(rad));
        let e = clamp((gx * ld.x + gy * ld.y) * fx.offX, -1.0, 1.0);   // offX = depth
        let hi = vec3<f32>(fx.r, fx.g, fx.b);
        let lo = vec3<f32>(fx.r2, fx.g2, fx.b2);
        let col = select(lo, hi, e > 0.0);
        outp[idx] = pack(vec4<f32>(col, a * abs(e)));
        return;
    }

    if (fx.mode == 2u || fx.mode == 3u) {   // inner shadow / glow: nearby transparency, clipped inside
        let ox = select(0, i32(round(fx.offX)), fx.mode == 2u);
        let oy = select(0, i32(round(fx.offY)), fx.mode == 2u);
        var outside = 0.0; var n = 0.0;
        for (var dy = -s; dy <= s; dy = dy + 1) {
            for (var dx = -s; dx <= s; dx = dx + 1) {
                if (dx * dx + dy * dy > s * s) { continue; }
                outside = outside + (1.0 - alphaAt(i32(gid.x) - ox + dx, i32(gid.y) - oy + dy));
                n = n + 1.0;
            }
        }
        let inner = a * clamp(outside / max(1.0, n) * 1.6, 0.0, 1.0);   // strongest near edge, clipped to layer
        outp[idx] = pack(vec4<f32>(col, inner));
        return;
    }

    // mode 1: stroke — dilate/erode the alpha within `size` to find the edge band
    var mx = a; var mn = a;
    for (var dy = -s; dy <= s; dy = dy + 1) {
        for (var dx = -s; dx <= s; dx = dx + 1) {
            if (dx * dx + dy * dy > s * s) { continue; }
            let na = alphaAt(i32(gid.x) + dx, i32(gid.y) + dy);
            mx = max(mx, na);
            mn = min(mn, na);
        }
    }
    var band = 0.0;
    if (fx.pos < 0.5) {               // outside: opaque neighbour, transparent centre
        band = clamp(mx - a, 0.0, 1.0);
    } else if (fx.pos < 1.5) {        // inside: opaque centre near a transparent neighbour
        band = clamp(a - mn, 0.0, 1.0);
    } else {                          // centre: straddle the edge
        band = clamp(min(mx - a, 1.0) + min(a - mn, 1.0), 0.0, 1.0);
    }
    outp[idx] = pack(vec4<f32>(col, band));
}
