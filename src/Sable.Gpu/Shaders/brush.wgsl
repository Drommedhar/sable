// GPU brush engine (improvement plan §2): stamps full-fidelity dabs straight into the
// active layer's f32 GPU buffer during a stroke — paint / erase / alpha-lock / 30 paint
// blend modes / elliptical + sampled-tip coverage / clone + heal / retouch
// (dodge/burn/sponge/blur/sharpen/smudge). The CPU computes per-dab scalars (jitter,
// pressure, spacing) and dispatches one bbox-sized `stamp` per dab; heal adds a
// `clearsums` + `reduce` pre-pass, smudge a 1-thread `post` carry update. Pixels are
// read back once at stroke end for undo + CPU authority. Keep the coverage and blend
// math in sync with Sable.Tools.BrushTool / Sable.Core.BlendOps.

struct BDims { lw: u32, lh: u32, bx: u32, by: u32 };   // layer size + dispatch bbox origin

struct BP {
    cx: f32, cy: f32, r: f32, inner: f32,
    cosA: f32, sinA: f32, round: f32, sa: f32,          // sa = flow*jitter*alpha*pressure
    colR: f32, colG: f32, colB: f32, strength: f32,
    mode: u32, blend: u32, flags: u32, tipW: u32,
    tipH: u32, originX: i32, originY: i32, clipW: u32,
    clipX0: f32, clipY0: f32, clipX1: f32, clipY1: f32,
    cloneOffX: f32, cloneOffY: f32, thx: f32, thy: f32, // tip half-extents (px)
    docH: u32, _p1: f32, _p2: f32, _p3: f32,
};

// flags bits
const F_ERASE: u32 = 1u;
const F_LOCKALPHA: u32 = 2u;
const F_PENCIL: u32 = 4u;
const F_TIP: u32 = 8u;
const F_CLONE: u32 = 16u;
const F_HEAL: u32 = 32u;
const F_CLIPMASK: u32 = 64u;
const F_CLIPRECT: u32 = 128u;

struct BState {
    carry: vec4<f32>,                 // smudge carried colour (w = initialised flag)
    sums: array<atomic<u32>, 7>,      // heal: destR,G,B, srcR,G,B, count (×256 fixed point)
};

@group(0) @binding(0) var<uniform> dims: BDims;
@group(0) @binding(1) var<uniform> p: BP;
@group(0) @binding(2) var<storage, read_write> buf: array<vec4<f32>>;
@group(0) @binding(3) var<storage, read> src: array<vec4<f32>>;    // stroke-start snapshot (clone/heal)
@group(0) @binding(4) var<storage, read> clipm: array<f32>;        // doc-sized selection coverage
@group(0) @binding(5) var<storage, read> tip: array<f32>;          // sampled tip coverage
@group(0) @binding(6) var<storage, read_write> state: BState;

fn has(f: u32) -> bool { return (p.flags & f) != 0u; }

// ---- blend helpers (mirror composite.wgsl / BlendOps) ----
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
        case 13u: { return vec3<f32>(b_overlay(cs.x, cb.x), b_overlay(cs.y, cb.y), b_overlay(cs.z, cb.z)); }
        case 14u: { return vec3<f32>(b_vividLight(cb.x, cs.x), b_vividLight(cb.y, cs.y), b_vividLight(cb.z, cs.z)); }
        case 16u: { return vec3<f32>(b_pinLight(cb.x, cs.x), b_pinLight(cb.y, cs.y), b_pinLight(cb.z, cs.z)); }
        case 28u: { return vec3<f32>(b_reflect(cb.x, cs.x), b_reflect(cb.y, cs.y), b_reflect(cb.z, cs.z)); }
        case 29u: { return vec3<f32>(b_reflect(cs.x, cb.x), b_reflect(cs.y, cb.y), b_reflect(cs.z, cb.z)); }
        default:  { return vec3<f32>(b_overlay(cb.x, cs.x), b_overlay(cb.y, cs.y), b_overlay(cb.z, cs.z)); }
    }
}
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
fn blend(cb: vec3<f32>, cs: vec3<f32>, mode: u32) -> vec3<f32> {
    switch mode {
        case 1u:  { return cb * cs; }
        case 2u:  { return cb + cs - cb * cs; }
        case 3u:  { return each(cb, cs, 3u); }
        case 4u:  { return min(cb, cs); }
        case 5u:  { return max(cb, cs); }
        case 6u:  { return min(cb + cs, vec3<f32>(1.0)); }
        case 7u:  { return each(cb, cs, 7u); }
        case 8u:  { return max(cb + cs - 1.0, vec3<f32>(0.0)); }
        case 9u:  { if (lum(cb) <= lum(cs)) { return cb; } return cs; }
        case 10u: { return each(cb, cs, 10u); }
        case 11u: { if (lum(cb) >= lum(cs)) { return cb; } return cs; }
        case 12u: { return each(cb, cs, 12u); }
        case 13u: { return each(cb, cs, 13u); }
        case 14u: { return each(cb, cs, 14u); }
        case 15u: { return clamp(cb + 2.0 * cs - 1.0, vec3<f32>(0.0), vec3<f32>(1.0)); }
        case 16u: { return each(cb, cs, 16u); }
        case 17u: { return step(vec3<f32>(0.5), each(cb, cs, 14u)); }
        case 18u: { return abs(cb - cs); }
        case 19u: { return cb + cs - 2.0 * cb * cs; }
        case 20u: { return max(cb - cs, vec3<f32>(0.0)); }
        case 21u: { return clamp(cb / max(cs, vec3<f32>(0.0001)), vec3<f32>(0.0), vec3<f32>(1.0)); }
        case 22u: { return setLum(setSat(cs, satv(cb)), lum(cb)); }
        case 23u: { return setLum(setSat(cb, satv(cs)), lum(cb)); }
        case 24u: { return setLum(cs, lum(cb)); }
        case 25u: { return setLum(cb, lum(cs)); }
        case 26u: { return (cb + cs) * 0.5; }
        case 27u: { return 1.0 - abs(1.0 - cb - cs); }
        case 28u: { return each(cb, cs, 28u); }
        case 29u: { return each(cb, cs, 29u); }
        default:  { return cs; }
    }
}

// ---- coverage: rotated ellipse falloff or sampled tip (mirror BrushTool.Stamp) ----
fn tipSample(u: f32, v: f32) -> f32 {
    let x0 = u32(u); let y0 = u32(v);
    let x1 = min(x0 + 1u, p.tipW - 1u); let y1 = min(y0 + 1u, p.tipH - 1u);
    let fx = u - f32(x0); let fy = v - f32(y0);
    let a = tip[y0 * p.tipW + x0] * (1.0 - fx) + tip[y0 * p.tipW + x1] * fx;
    let b = tip[y1 * p.tipW + x0] * (1.0 - fx) + tip[y1 * p.tipW + x1] * fx;
    return a * (1.0 - fy) + b * fy;
}

fn coverage(px: f32, py: f32) -> f32 {
    let dx = px + 0.5 - p.cx;
    let dy = py + 0.5 - p.cy;
    let rx = dx * p.cosA + dy * p.sinA;
    var ry = -dx * p.sinA + dy * p.cosA;
    if (has(F_TIP)) {
        if (abs(rx) > p.thx || abs(ry) > p.thy) { return 0.0; }
        let tu = (rx / p.thx * 0.5 + 0.5) * f32(p.tipW - 1u);
        let tv = (ry / p.thy * 0.5 + 0.5) * f32(p.tipH - 1u);
        var cov = tipSample(tu, tv);
        if (has(F_PENCIL)) { cov = select(0.0, 1.0, cov >= 0.5); }
        return cov;
    }
    ry = ry / p.round;
    let dist = sqrt(rx * rx + ry * ry);
    if (dist > p.r) { return 0.0; }
    if (has(F_PENCIL)) { return 1.0; }
    var t = select(1.0 - (dist - p.inner) / max(0.001, p.r - p.inner), 1.0, dist <= p.inner);
    t = clamp(t, 0.0, 1.0);
    return t * t * (3.0 - 2.0 * t);
}

// selection clip (doc space): 0 = blocked, else soft coverage
fn clipCov(x: u32, y: u32) -> f32 {
    let docx = i32(x) + p.originX;
    let docy = i32(y) + p.originY;
    if (has(F_CLIPRECT) &&
        (f32(docx) < p.clipX0 || f32(docy) < p.clipY0 || f32(docx) >= p.clipX1 || f32(docy) >= p.clipY1)) {
        return 0.0;
    }
    if (has(F_CLIPMASK)) {
        if (docx < 0 || docy < 0 || docx >= i32(p.clipW) || docy >= i32(p.docH)) { return 0.0; }
        return clipm[u32(docy) * p.clipW + u32(docx)];
    }
    return 1.0;
}

fn avg3(x: u32, y: u32) -> vec3<f32> {
    var sum = vec3<f32>(0.0);
    for (var oy: i32 = -1; oy <= 1; oy++) {
        for (var ox: i32 = -1; ox <= 1; ox++) {
            let sx = u32(clamp(i32(x) + ox, 0, i32(dims.lw) - 1));
            let sy = u32(clamp(i32(y) + oy, 0, i32(dims.lh) - 1));
            sum += buf[sy * dims.lw + sx].xyz;
        }
    }
    return sum / 9.0;
}

// ---- heal pre-passes: zero the sums, then accumulate dest/source means over the dab ----
@compute @workgroup_size(1)
fn clearsums(@builtin(global_invocation_id) gid: vec3<u32>) {
    for (var i = 0u; i < 7u; i++) { atomicStore(&state.sums[i], 0u); }
}

@compute @workgroup_size(16, 16)
fn reduce(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = dims.bx + gid.x;
    let y = dims.by + gid.y;
    if (x >= dims.lw || y >= dims.lh) { return; }
    let dx = f32(x) + 0.5 - p.cx;
    let dy = f32(y) + 0.5 - p.cy;
    if (dx * dx + dy * dy > p.r * p.r) { return; }
    let sx = i32(x) - i32(p.cloneOffX);
    let sy = i32(y) - i32(p.cloneOffY);
    if (sx < 0 || sy < 0 || sx >= i32(dims.lw) || sy >= i32(dims.lh)) { return; }
    let sc = src[u32(sy) * dims.lw + u32(sx)];
    if (sc.w <= 0.0) { return; }
    let d = buf[y * dims.lw + x];
    atomicAdd(&state.sums[0], u32(d.x * 255.0 * 256.0));
    atomicAdd(&state.sums[1], u32(d.y * 255.0 * 256.0));
    atomicAdd(&state.sums[2], u32(d.z * 255.0 * 256.0));
    atomicAdd(&state.sums[3], u32(sc.x * 255.0 * 256.0));
    atomicAdd(&state.sums[4], u32(sc.y * 255.0 * 256.0));
    atomicAdd(&state.sums[5], u32(sc.z * 255.0 * 256.0));
    atomicAdd(&state.sums[6], 1u);
}

// ---- smudge carry update (1 thread, after the stamp) ----
@compute @workgroup_size(1)
fn post(@builtin(global_invocation_id) gid: vec3<u32>) {
    let cx = u32(clamp(i32(p.cx), 0, i32(dims.lw) - 1));
    let cy = u32(clamp(i32(p.cy), 0, i32(dims.lh) - 1));
    let centre = buf[cy * dims.lw + cx].xyz;
    if (state.carry.w < 0.5) {
        state.carry = vec4<f32>(centre, 1.0);
    } else {
        let k = clamp(p.strength * 0.5, 0.0, 1.0);
        state.carry = vec4<f32>(mix(state.carry.xyz, centre, k), 1.0);
    }
}

// ---- the dab ----
@compute @workgroup_size(16, 16)
fn stamp(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = dims.bx + gid.x;
    let y = dims.by + gid.y;
    if (x >= dims.lw || y >= dims.lh) { return; }

    var cov = coverage(f32(x), f32(y));
    if (cov <= 0.0) { return; }
    let cc = clipCov(x, y);
    if (cc <= 0.0) { return; }

    let idx = y * dims.lw + x;
    let d = buf[idx];

    // retouch modes transform the existing pixel under the dab
    if (p.mode != 0u) {
        let amt = clamp(cov * cc * p.strength, 0.0, 1.0);
        if (amt <= 0.0) { return; }
        var c = d.xyz;
        switch p.mode {
            case 1u: { c = c + (vec3<f32>(1.0) - c) * amt; }                       // dodge
            case 2u: { c = c * (1.0 - amt); }                                      // burn
            case 3u: { let l = lum(c); c = mix(c, vec3<f32>(l), amt); }            // sponge
            case 4u: { c = mix(c, avg3(x, y), amt); }                              // blur
            case 5u: { let a = avg3(x, y); c = c + (c - a) * amt; }                // sharpen
            case 6u: {                                                            // smudge
                var carry = state.carry.xyz;
                if (state.carry.w < 0.5) {
                    let sx = u32(clamp(i32(p.cx), 0, i32(dims.lw) - 1));
                    let sy = u32(clamp(i32(p.cy), 0, i32(dims.lh) - 1));
                    carry = buf[sy * dims.lw + sx].xyz;
                }
                c = mix(c, carry, amt);
            }
            default: { }
        }
        buf[idx] = vec4<f32>(clamp(c, vec3<f32>(0.0), vec3<f32>(1.0)), d.w);
        return;
    }

    var c = vec3<f32>(p.colR, p.colG, p.colB);
    var sa = cov * cc * p.sa;
    if (sa <= 0.0) { return; }

    if (has(F_CLONE)) {
        let sx = i32(x) - i32(p.cloneOffX);
        let sy = i32(y) - i32(p.cloneOffY);
        if (sx < 0 || sy < 0 || sx >= i32(dims.lw) || sy >= i32(dims.lh)) { return; }
        let sc = src[u32(sy) * dims.lw + u32(sx)];
        c = sc.xyz;
        if (has(F_HEAL)) {
            let n = max(1.0, f32(atomicLoad(&state.sums[6])));
            let k = 1.0 / (n * 255.0 * 256.0);
            let offR = (f32(atomicLoad(&state.sums[0])) - f32(atomicLoad(&state.sums[3]))) * k;
            let offG = (f32(atomicLoad(&state.sums[1])) - f32(atomicLoad(&state.sums[4]))) * k;
            let offB = (f32(atomicLoad(&state.sums[2])) - f32(atomicLoad(&state.sums[5]))) * k;
            c = clamp(c + vec3<f32>(offR, offG, offB), vec3<f32>(0.0), vec3<f32>(1.0));
        }
        sa = sa * sc.w;
        if (sa <= 0.0) { return; }
    }

    if (has(F_ERASE)) {
        buf[idx] = vec4<f32>(d.xyz, d.w * (1.0 - sa));
        return;
    }

    // paint blend mode: blend against the backdrop weighted by its alpha
    if (p.blend != 0u && d.w > 0.0) {
        c = mix(c, blend(d.xyz, c, p.blend), d.w);
    }

    if (has(F_LOCKALPHA)) {
        if (d.w <= 0.0) { return; }
        buf[idx] = vec4<f32>(clamp(mix(d.xyz, c, sa), vec3<f32>(0.0), vec3<f32>(1.0)), d.w);
        return;
    }

    let outA = sa + d.w * (1.0 - sa);
    var rgb = c;
    if (outA > 0.0) { rgb = (c * sa + d.xyz * d.w * (1.0 - sa)) / outA; }
    buf[idx] = vec4<f32>(rgb, outA);
}
