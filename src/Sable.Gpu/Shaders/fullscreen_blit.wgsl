// Presents the composite texture into the surface with a viewport transform
// (aspect-fit + zoom + pan). Optionally draws a marching-ants overlay rectangle
// (layer bounds / marquee selection) in document coordinates.

struct Viewport {
    ox: f32,        // document top-left X in surface pixels
    oy: f32,        // document top-left Y in surface pixels
    invScale: f32,  // 1 / (pixels-per-doc-pixel)
    _pad: f32,
    docW: f32,
    docH: f32,
    _pad1: f32,
    _pad2: f32,
    ovX: f32, ovY: f32, ovW: f32, ovH: f32,   // overlay rect (doc px)
    ovOn: f32, selHandles: f32, _p4: f32, _p5: f32,  // ovOn: 1 = draw rect; selHandles: 1 = draw grips
    // transform gizmo: 4 corners in SURFACE px (TL,TR,BR,BL), rotate-handle distance
    c0x: f32, c0y: f32, c1x: f32, c1y: f32, c2x: f32, c2y: f32, c3x: f32, c3y: f32,
    gizmoOn: f32, rotDist: f32,
    brushOn: f32, brushX: f32, brushY: f32, brushR: f32,   // brush cursor preview (surface px)
    brushColR: f32, brushColG: f32, brushColB: f32, brushErase: f32, brushHard: f32,
    maskOn: f32, _h1: f32, _h2: f32, _h3: f32, _h4: f32,
};

@group(0) @binding(0) var tex: texture_2d<f32>;
@group(0) @binding(1) var samp: sampler;
@group(0) @binding(2) var<uniform> vp: Viewport;
@group(0) @binding(3) var maskTex: texture_2d<f32>;   // selection coverage (R8), doc UV

@vertex
fn vs(@builtin(vertex_index) vid: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 3.0, -1.0),
        vec2<f32>(-1.0,  3.0));
    return vec4<f32>(p[vid], 0.0, 1.0);
}

fn checker(frag: vec2<f32>) -> vec3<f32> {
    let cx = i32(floor(frag.x / 16.0));
    let cy = i32(floor(frag.y / 16.0));
    let g = select(0.16, 0.22, ((cx + cy) & 1) == 0);
    return vec3<f32>(g, g, g);
}

fn segDist(p: vec2<f32>, a: vec2<f32>, b: vec2<f32>) -> f32 {
    let pa = p - a; let ba = b - a;
    let h = clamp(dot(pa, ba) / max(1e-5, dot(ba, ba)), 0.0, 1.0);
    return length(pa - ba * h);
}
fn inSquare(p: vec2<f32>, c: vec2<f32>, hs: f32) -> bool {
    return abs(p.x - c.x) <= hs && abs(p.y - c.y) <= hs;
}

@fragment
fn fs(@builtin(position) frag: vec4<f32>) -> @location(0) vec4<f32> {
    let docX = (frag.x - vp.ox) * vp.invScale;
    let docY = (frag.y - vp.oy) * vp.invScale;
    let u = docX / vp.docW;
    let v = docY / vp.docH;

    var outc = checker(frag.xy);
    if (u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let col = textureSample(tex, samp, vec2<f32>(u, v));
        outc = col.rgb * col.a + outc * (1.0 - col.a);
    }

    // marching-ants overlay rectangle (selection bounding box)
    if (vp.ovOn > 0.5) {
        let t = vp.invScale * 0.6;   // ~1px line in surface space
        let onL = abs(docX - vp.ovX) <= t;
        let onR = abs(docX - (vp.ovX + vp.ovW)) <= t;
        let onT = abs(docY - vp.ovY) <= t;
        let onB = abs(docY - (vp.ovY + vp.ovH)) <= t;
        let inX = docX >= vp.ovX - t && docX <= vp.ovX + vp.ovW + t;
        let inY = docY >= vp.ovY - t && docY <= vp.ovY + vp.ovH + t;
        if (((onL || onR) && inY) || ((onT || onB) && inX)) {
            let on = ((i32(floor((frag.x + frag.y) / 4.0))) & 1) == 0;
            outc = select(vec3<f32>(0.0), vec3<f32>(1.0), on);
        }

        // GIMP-style resize grips: filled squares at 8 handle positions
        if (vp.selHandles > 0.5) {
            let hs = vp.invScale * 3.0;
            let x0 = vp.ovX; let x1 = vp.ovX + vp.ovW * 0.5; let x2 = vp.ovX + vp.ovW;
            let y0 = vp.ovY; let y1 = vp.ovY + vp.ovH * 0.5; let y2 = vp.ovY + vp.ovH;
            let nx0 = abs(docX - x0) <= hs; let nx1 = abs(docX - x1) <= hs; let nx2 = abs(docX - x2) <= hs;
            let ny0 = abs(docY - y0) <= hs; let ny1 = abs(docY - y1) <= hs; let ny2 = abs(docY - y2) <= hs;
            let onGrip =
                (nx0 && ny0) || (nx1 && ny0) || (nx2 && ny0) ||
                (nx0 && ny1) ||                 (nx2 && ny1) ||
                (nx0 && ny2) || (nx1 && ny2) || (nx2 && ny2);
            if (onGrip) { outc = vec3<f32>(1.0, 1.0, 1.0); }
        }
    }

    // non-rectangular selection: marching ants traced along the coverage-mask boundary
    if (vp.maskOn > 0.5 && u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let step = max(vp.invScale, 1.0);          // ~1 surface px expressed in doc px
        let du = step / vp.docW;
        let dv = step / vp.docH;
        let c  = textureSample(maskTex, samp, vec2<f32>(u, v)).r > 0.5;
        let cl = textureSample(maskTex, samp, vec2<f32>(u - du, v)).r > 0.5;
        let cr = textureSample(maskTex, samp, vec2<f32>(u + du, v)).r > 0.5;
        let ct = textureSample(maskTex, samp, vec2<f32>(u, v - dv)).r > 0.5;
        let cb = textureSample(maskTex, samp, vec2<f32>(u, v + dv)).r > 0.5;
        // one-sided: only the inner boundary pixels (selected, adjacent to outside) → ~1px line
        if (c && (!cl || !cr || !ct || !cb)) {
            let on = ((i32(floor((frag.x + frag.y) / 6.0))) & 1) == 0;
            outc = select(vec3<f32>(0.0), vec3<f32>(1.0), on);
        }
    }

    // transform gizmo (rotated box + corner handles + rotate handle)
    if (vp.gizmoOn > 0.5) {
        let p = frag.xy;
        let c0 = vec2<f32>(vp.c0x, vp.c0y);
        let c1 = vec2<f32>(vp.c1x, vp.c1y);
        let c2 = vec2<f32>(vp.c2x, vp.c2y);
        let c3 = vec2<f32>(vp.c3x, vp.c3y);
        let center = (c0 + c1 + c2 + c3) * 0.25;
        let topMid = (c0 + c1) * 0.5;
        let dir = normalize(topMid - center);
        let rp = topMid + dir * vp.rotDist;

        let edge = min(min(segDist(p, c0, c1), segDist(p, c1, c2)),
                       min(segDist(p, c2, c3), segDist(p, c3, c0)));
        if (edge < 1.5 || segDist(p, topMid, rp) < 1.5) {
            outc = vec3<f32>(0.18, 0.7, 1.0);   // cyan gizmo lines
        }
        if (inSquare(p, c0, 5.0) || inSquare(p, c1, 5.0) || inSquare(p, c2, 5.0) || inSquare(p, c3, 5.0)) {
            outc = vec3<f32>(1.0);              // corner handles
        }
        if (length(p - rp) < 6.0) { outc = vec3<f32>(1.0); }   // rotate handle
    }

    // brush cursor ring (the dab fill is previewed in the composite via the stamp pass)
    if (vp.brushOn > 0.5) {
        let d = length(frag.xy - vec2<f32>(vp.brushX, vp.brushY));
        if (abs(d - vp.brushR) <= 1.0) { outc = vec3<f32>(1.0) - outc; }   // contrast ring
    }
    return vec4<f32>(outc, 1.0);
}
