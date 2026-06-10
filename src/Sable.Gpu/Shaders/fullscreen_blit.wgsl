// Presents the composite texture into the surface with a viewport transform
// (aspect-fit + zoom + pan). Optionally draws a marching-ants overlay rectangle
// (layer bounds / marquee selection) in document coordinates.

struct Viewport {
    ox: f32,        // document top-left X in surface pixels
    oy: f32,        // document top-left Y in surface pixels
    invScale: f32,  // 1 / (pixels-per-doc-pixel)
    pasteR: f32,    // pasteboard (surround) colour, themed
    docW: f32,
    docH: f32,
    pasteG: f32,
    pasteB: f32,
    ovX: f32, ovY: f32, ovW: f32, ovH: f32,   // overlay rect (doc px)
    ovOn: f32, selHandles: f32, gridOn: f32, gridSp: f32,  // ovOn: rect; selHandles: grips; gridOn+spacing: doc grid
    // transform gizmo: 4 corners in SURFACE px (TL,TR,BR,BL), rotate-handle distance
    c0x: f32, c0y: f32, c1x: f32, c1y: f32, c2x: f32, c2y: f32, c3x: f32, c3y: f32,
    gizmoOn: f32, rotDist: f32,
    brushOn: f32, brushX: f32, brushY: f32, brushR: f32,   // brush cursor preview (surface px)
    brushColR: f32, brushColG: f32, brushColB: f32, brushErase: f32, brushHard: f32,
    maskOn: f32,
    gradOn: f32, gx0: f32, gy0: f32, gx1: f32, gy1: f32,   // gradient drag line (surface px)
    cropOn: f32,                                            // dim outside the overlay rect (crop preview)
    shapeOn: f32, shapeKind: f32,                           // shape drag outline (surface px)
    shx0: f32, shy0: f32, shx1: f32, shy1: f32,
    cloneOn: f32, clsx: f32, clsy: f32, pixGrid: f32,       // clone crosshair; pixGrid: 1 = 1px pixel grid
    caretOn: f32, caretX: f32, caretY0: f32, caretY1: f32,  // text caret (surface px)
    penOn: f32,                                            // pen-path node markers (geometry in binding 6)
    // customisable overlay colours (0..1), set from settings
    guideR: f32, guideG: f32, guideB: f32,                 // guide lines
    smartR: f32, smartG: f32, smartB: f32,                 // smart-guide alignment lines
    gridR: f32, gridG: f32, gridB: f32,                    // document/pixel grid
    qmR: f32, qmG: f32, qmB: f32,                          // quick-mask (rubylith) fill
    previewMode: f32,                                      // AI hover-select: 0 off, 1 blue(replace), 2 green(add), 3 red(subtract), 4 yellow(intersect)
    loupeOn: f32,                                          // eyedropper loupe (circular magnifier)
    loupeCx: f32, loupeCy: f32, loupeR: f32,               // loupe centre + radius (surface px)
    loupeDocX: f32, loupeDocY: f32, loupeZoom: f32,        // sample centre (doc px) + magnification (surface px per doc px)
    loupeColR: f32, loupeColG: f32, loupeColB: f32,        // the actual would-be-picked colour (rim fill)
    gridSub: f32,                                          // grid subdivisions (minor lines per major cell; 1 = none)
    chanView: f32,                                         // Channels panel: 0 normal, 1=R 2=G 3=B 4=A shown as grayscale
    chanMask: f32,                                         // RGB visibility bits (bit0=R,1=G,2=B), composite only; 7 = all
    ckSize: f32,                                           // transparency-checker cell size (doc px; <2 = built-in 16)
    ckAr: f32, ckAg: f32, ckAb: f32,                       // checker colour A (dark cell)
    crossCursor: f32,                                      // 1 = precise crosshair brush cursor (with the ring)
    ckBr: f32, ckBg: f32, ckBb: f32,                       // checker colour B (light cell)
    _gpad2: f32,                                           // pad to 16-byte alignment
};

@group(0) @binding(0) var tex: texture_2d<f32>;
@group(0) @binding(1) var samp: sampler;
@group(0) @binding(2) var<uniform> vp: Viewport;
@group(0) @binding(3) var maskTex: texture_2d<f32>;   // selection coverage (R8), doc UV
@group(0) @binding(4) var<storage, read> guides: array<f32>;   // [countX, countY, _, _, Xs..., Ys...] doc px
@group(0) @binding(5) var<storage, read> smart: array<f32>;    // smart-guide alignment lines (same layout)
@group(0) @binding(6) var<storage, read> pen: array<f32>;      // [nodeN, activeIdx, flatN, _, (ax,ay,inx,iny,outx,outy)×nodeN, (x,y)×flatN] surface px
@group(0) @binding(7) var previewTex: texture_2d<f32>;         // AI hover-select object preview coverage (R8, doc UV)

@vertex
fn vs(@builtin(vertex_index) vid: u32) -> @builtin(position) vec4<f32> {
    var p = array<vec2<f32>, 3>(
        vec2<f32>(-1.0, -1.0),
        vec2<f32>( 3.0, -1.0),
        vec2<f32>(-1.0,  3.0));
    return vec4<f32>(p[vid], 0.0, 1.0);
}

fn checker(frag: vec2<f32>) -> vec3<f32> {
    let custom = vp.ckSize >= 2.0;
    let sz = select(16.0, vp.ckSize, custom);
    let cx = i32(floor(frag.x / sz));
    let cy = i32(floor(frag.y / sz));
    let a = select(vec3<f32>(0.16), vec3<f32>(vp.ckAr, vp.ckAg, vp.ckAb), custom);
    let b = select(vec3<f32>(0.22), vec3<f32>(vp.ckBr, vp.ckBg, vp.ckBb), custom);
    return select(a, b, ((cx + cy) & 1) == 0);
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

    // flat pasteboard (themed) outside the document; checker only inside (shows transparency)
    var outc = vec3<f32>(vp.pasteR, vp.pasteG, vp.pasteB);
    if (u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let bg = checker(vec2<f32>(docX, docY));   // doc-anchored so it doesn't swim under the image when panning
        var col = textureSample(tex, samp, vec2<f32>(u, v));
        // Channels panel: isolate a single channel as grayscale, or mask RGB channel visibility
        if (vp.chanView > 0.5) {
            var g = col.r;
            if (vp.chanView > 3.5) { g = col.a; }
            else if (vp.chanView > 2.5) { g = col.b; }
            else if (vp.chanView > 1.5) { g = col.g; }
            col = vec4<f32>(g, g, g, 1.0);          // opaque grayscale of the chosen channel
        } else {
            let m = u32(vp.chanMask);
            if ((m & 1u) == 0u) { col.r = 0.0; }
            if ((m & 2u) == 0u) { col.g = 0.0; }
            if ((m & 4u) == 0u) { col.b = 0.0; }
        }
        outc = col.rgb * col.a + bg * (1.0 - col.a);
    }

    // document grid + pixel grid (inside the document only); drawn under the tool overlays
    if (u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let t = vp.invScale * 0.5;   // ~half a surface pixel expressed in doc px
        if (vp.gridOn > 0.5 && vp.gridSp > 0.0) {
            let gcol = vec3<f32>(vp.gridR, vp.gridG, vp.gridB);
            let mx = docX - round(docX / vp.gridSp) * vp.gridSp;
            let my = docY - round(docY / vp.gridSp) * vp.gridSp;
            // minor (subdivision) lines first, so the major lines draw over them
            if (vp.gridSub > 1.5) {
                let sp2 = vp.gridSp / vp.gridSub;
                let nx = docX - round(docX / sp2) * sp2;
                let ny = docY - round(docY / sp2) * sp2;
                if (abs(nx) <= t && abs(mx) > t) { outc = mix(outc, gcol, 0.18); }
                if (abs(ny) <= t && abs(my) > t) { outc = mix(outc, gcol, 0.18); }
            }
            if (abs(mx) <= t || abs(my) <= t) { outc = mix(outc, gcol, 0.45); }
        }
        // pixel grid only when zoomed in enough that 1 doc px > ~3 surface px
        if (vp.pixGrid > 0.5 && vp.invScale < 0.34) {
            let px = docX - round(docX);
            let py = docY - round(docY);
            if (abs(px) <= t || abs(py) <= t) { outc = mix(outc, vec3<f32>(vp.gridR, vp.gridG, vp.gridB), 0.25); }
        }
    }

    // guides (cyan lines at constant doc X / Y), inside the document
    if (u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let gt = vp.invScale * 0.6;
        let nx = u32(guides[0]); let ny = u32(guides[1]);
        let cap = (512u - 4u) / 2u;
        var ghit = false;
        for (var i = 0u; i < nx; i = i + 1u) { if (abs(docX - guides[4u + i]) <= gt) { ghit = true; } }
        for (var i = 0u; i < ny; i = i + 1u) { if (abs(docY - guides[4u + cap + i]) <= gt) { ghit = true; } }
        if (ghit) { outc = vec3<f32>(vp.guideR, vp.guideG, vp.guideB); }

        // smart-guide alignment lines (magenta), span the whole surface (not just doc)
        let snx = u32(smart[0]); let sny = u32(smart[1]);
        var shit = false;
        for (var i = 0u; i < snx; i = i + 1u) { if (abs(docX - smart[4u + i]) <= gt) { shit = true; } }
        for (var i = 0u; i < sny; i = i + 1u) { if (abs(docY - smart[4u + cap + i]) <= gt) { shit = true; } }
        if (shit) { outc = vec3<f32>(vp.smartR, vp.smartG, vp.smartB); }
    }

    // crop preview: dim the document outside the rect + a thin border
    if (vp.cropOn > 0.5 && u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let inside = docX >= vp.ovX && docX <= vp.ovX + vp.ovW && docY >= vp.ovY && docY <= vp.ovY + vp.ovH;
        if (!inside) { outc = outc * 0.35; }
        let t = vp.invScale * 0.75;
        // rule-of-thirds guides inside the pending crop
        if (inside) {
            let tx1 = vp.ovX + vp.ovW / 3.0; let tx2 = vp.ovX + vp.ovW * 2.0 / 3.0;
            let ty1 = vp.ovY + vp.ovH / 3.0; let ty2 = vp.ovY + vp.ovH * 2.0 / 3.0;
            if (abs(docX - tx1) <= t || abs(docX - tx2) <= t ||
                abs(docY - ty1) <= t || abs(docY - ty2) <= t) {
                outc = mix(outc, vec3<f32>(1.0), 0.35);
            }
        }
        let onL = abs(docX - vp.ovX) <= t; let onR = abs(docX - (vp.ovX + vp.ovW)) <= t;
        let onT = abs(docY - vp.ovY) <= t; let onB = abs(docY - (vp.ovY + vp.ovH)) <= t;
        let inX = docX >= vp.ovX - t && docX <= vp.ovX + vp.ovW + t;
        let inY = docY >= vp.ovY - t && docY <= vp.ovY + vp.ovH + t;
        if (((onL || onR) && inY) || ((onT || onB) && inX)) { outc = vec3<f32>(1.0); }
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
    // quick mask (maskOn == 2): rubylith — fill the selected area with translucent red
    if (vp.maskOn > 1.5 && u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let cov = textureSample(maskTex, samp, vec2<f32>(u, v)).r;
        outc = mix(outc, vec3<f32>(vp.qmR, vp.qmG, vp.qmB), cov * 0.5);
    }
    else if (vp.maskOn > 0.5 && u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
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

    // AI hover-select object preview: diagonal stripes over the hovered object,
    // blue = replace/first, green = add, red = subtract (PHASE8_AI §8.3b).
    if (vp.previewMode > 0.5 && u >= 0.0 && u < 1.0 && v >= 0.0 && v < 1.0) {
        let cov = textureSample(previewTex, samp, vec2<f32>(u, v)).r;
        if (cov > 0.5) {
            let on = ((i32(floor((frag.x + frag.y) / 9.0))) & 1) == 0;
            if (on) {
                var col = vec3<f32>(0.12, 0.5, 1.0);                         // blue (replace)
                if (vp.previewMode > 3.5) { col = vec3<f32>(0.95, 0.85, 0.2); }     // yellow (intersect)
                else if (vp.previewMode > 2.5) { col = vec3<f32>(1.0, 0.25, 0.3); } // red (subtract)
                else if (vp.previewMode > 1.5) { col = vec3<f32>(0.2, 0.9, 0.3); }  // green (add)
                outc = mix(outc, col, 0.6);
            }
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
        // edge midpoint handles (single-axis scale)
        let em0 = (c0 + c1) * 0.5; let em1 = (c1 + c2) * 0.5;
        let em2 = (c2 + c3) * 0.5; let em3 = (c3 + c0) * 0.5;
        if (inSquare(p, em0, 4.0) || inSquare(p, em1, 4.0) || inSquare(p, em2, 4.0) || inSquare(p, em3, 4.0)) {
            outc = vec3<f32>(0.85);             // edge handles (slightly dimmer)
        }
        if (length(p - rp) < 6.0) { outc = vec3<f32>(1.0); }   // rotate handle
    }

    // gradient tool drag line (start dot → end), surface px
    if (vp.gradOn > 0.5) {
        let a = vec2<f32>(vp.gx0, vp.gy0);
        let b = vec2<f32>(vp.gx1, vp.gy1);
        if (segDist(frag.xy, a, b) < 1.5) { outc = vec3<f32>(1.0) - outc; }
        if (length(frag.xy - a) < 4.0 || length(frag.xy - b) < 4.0) { outc = vec3<f32>(1.0); }
    }

    // shape tool drag outline (surface px)
    if (vp.shapeOn > 0.5) {
        let p = frag.xy;
        let minx = min(vp.shx0, vp.shx1); let maxx = max(vp.shx0, vp.shx1);
        let miny = min(vp.shy0, vp.shy1); let maxy = max(vp.shy0, vp.shy1);
        var hit = false;
        if (vp.shapeKind < 0.5) {           // rectangle outline
            let onX = (abs(p.x - minx) < 1.0 || abs(p.x - maxx) < 1.0) && p.y >= miny - 1.0 && p.y <= maxy + 1.0;
            let onY = (abs(p.y - miny) < 1.0 || abs(p.y - maxy) < 1.0) && p.x >= minx - 1.0 && p.x <= maxx + 1.0;
            hit = onX || onY;
        } else if (vp.shapeKind < 1.5) {    // ellipse outline
            let cx = (minx + maxx) * 0.5; let cy = (miny + maxy) * 0.5;
            let rx = max(0.5, (maxx - minx) * 0.5); let ry = max(0.5, (maxy - miny) * 0.5);
            let nx = (p.x - cx) / rx; let ny = (p.y - cy) / ry;
            let d = sqrt(nx * nx + ny * ny);
            hit = abs(d - 1.0) < (1.5 / min(rx, ry));
        } else {                            // line
            hit = segDist(p, vec2<f32>(vp.shx0, vp.shy0), vec2<f32>(vp.shx1, vp.shy1)) < 1.5;
        }
        if (hit) { outc = vec3<f32>(1.0) - outc; }
    }

    // clone source crosshair
    if (vp.cloneOn > 0.5) {
        let d = abs(frag.xy - vec2<f32>(vp.clsx, vp.clsy));
        if ((d.x < 7.0 && d.y < 1.0) || (d.y < 7.0 && d.x < 1.0)) { outc = vec3<f32>(1.0) - outc; }
    }

    // text caret
    if (vp.caretOn > 0.5) {
        if (abs(frag.x - vp.caretX) < 1.0 && frag.y >= vp.caretY0 && frag.y <= vp.caretY1) {
            outc = vec3<f32>(1.0) - outc;
        }
    }

    // pen-tool node markers: handle lines + anchor squares + handle diamonds (surface px)
    if (vp.penOn > 0.5) {
        let p = frag.xy;
        let n = u32(pen[0]);
        let activeIdx = i32(pen[1]);
        let flatN = u32(pen[2]);
        let flatBase = 4u + n * 6u;
        // spine: connect consecutive flattened points (the live curve)
        if (flatN >= 2u) {
            for (var i = 0u; i + 1u < flatN; i = i + 1u) {
                let s0 = vec2<f32>(pen[flatBase + i * 2u], pen[flatBase + i * 2u + 1u]);
                let s1 = vec2<f32>(pen[flatBase + (i + 1u) * 2u], pen[flatBase + (i + 1u) * 2u + 1u]);
                if (segDist(p, s0, s1) < 1.0) { outc = vec3<f32>(0.18, 0.7, 1.0); }
            }
        }
        for (var i = 0u; i < n; i = i + 1u) {
            let b = 4u + i * 6u;
            let a  = vec2<f32>(pen[b], pen[b + 1u]);
            let hi = vec2<f32>(pen[b + 2u], pen[b + 3u]);
            let ho = vec2<f32>(pen[b + 4u], pen[b + 5u]);
            // handle lines (thin), only when the handle is pulled out from the anchor
            if (length(hi - a) > 1.5 && segDist(p, a, hi) < 1.0) { outc = vec3<f32>(0.3, 0.7, 1.0); }
            if (length(ho - a) > 1.5 && segDist(p, a, ho) < 1.0) { outc = vec3<f32>(0.3, 0.7, 1.0); }
            // handle end diamonds (small circles)
            if (length(hi - a) > 1.5 && length(p - hi) < 3.5) { outc = vec3<f32>(0.3, 0.7, 1.0); }
            if (length(ho - a) > 1.5 && length(p - ho) < 3.5) { outc = vec3<f32>(0.3, 0.7, 1.0); }
            // anchor square — active/first node tinted, others white with dark border
            if (inSquare(p, a, 4.0)) {
                let isActive = i32(i) == activeIdx;
                outc = select(vec3<f32>(1.0), vec3<f32>(0.18, 0.7, 1.0), isActive);
            } else if (inSquare(p, a, 5.0)) {
                outc = vec3<f32>(0.05);
            }
        }
    }

    // brush cursor ring (the dab fill is previewed in the composite via the stamp pass)
    if (vp.brushOn > 0.5) {
        let d = length(frag.xy - vec2<f32>(vp.brushX, vp.brushY));
        if (abs(d - vp.brushR) <= 1.0) { outc = vec3<f32>(1.0) - outc; }   // contrast ring
        // precise crosshair at the dab centre (Preferences ▸ User Interface ▸ cursor)
        if (vp.crossCursor > 0.5) {
            let dx = abs(frag.x - vp.brushX);
            let dy = abs(frag.y - vp.brushY);
            if ((dx <= 0.75 && dy >= 2.0 && dy <= 6.0) || (dy <= 0.75 && dx >= 2.0 && dx <= 6.0)) {
                outc = vec3<f32>(1.0) - outc;
            }
        }
    }

    // eyedropper loupe: circular magnifier showing the pixels that would be sampled.
    // Centre cell outlines the exact sampled doc pixel; the rim is filled with that colour.
    if (vp.loupeOn > 0.5) {
        let lc = vec2<f32>(vp.loupeCx, vp.loupeCy);
        let d = length(frag.xy - lc);
        if (d <= vp.loupeR) {
            let rel = (frag.xy - lc) / vp.loupeZoom;            // doc-px offset from the sample centre
            let sdx = vp.loupeDocX + rel.x;
            let sdy = vp.loupeDocY + rel.y;
            let su = sdx / vp.docW;
            let sv = sdy / vp.docH;
            var lcol = vec3<f32>(vp.pasteR, vp.pasteG, vp.pasteB);
            if (su >= 0.0 && su < 1.0 && sv >= 0.0 && sv < 1.0) {
                let cbg = checker(frag.xy);
                let c = textureSampleLevel(tex, samp, vec2<f32>(su, sv), 0.0);
                lcol = c.rgb * c.a + cbg * (1.0 - c.a);
            }
            outc = lcol;
            // outline the exact sampled doc pixel sitting at the loupe centre
            let half = vp.loupeZoom * 0.5;
            let cd = abs(frag.xy - lc);
            if (cd.x <= half && cd.y <= half && (cd.x > half - 1.5 || cd.y > half - 1.5)) {
                outc = vec3<f32>(1.0) - lcol;
            }
            // rim filled with the actual would-be-picked colour, then a thin dark edge for contrast
            if (d >= vp.loupeR - 5.0 && d < vp.loupeR - 1.5) {
                outc = vec3<f32>(vp.loupeColR, vp.loupeColG, vp.loupeColB);
            }
            if (d >= vp.loupeR - 1.5) { outc = vec3<f32>(0.1); }
        }
    }
    return vec4<f32>(outc, 1.0);
}
