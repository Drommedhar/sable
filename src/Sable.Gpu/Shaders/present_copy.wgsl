// Copy the packed RGBA8 accumulator buffer into an rgba8unorm storage texture.
// Used instead of CopyBufferToTexture so the document width is unconstrained
// (CopyBufferToTexture requires bytesPerRow % 256 == 0; textureStore does not).

struct Dims { width: u32, height: u32, _p0: u32, _p1: u32 };

@group(0) @binding(0) var<uniform> dims: Dims;
@group(0) @binding(1) var<storage, read> buf: array<u32>;
@group(0) @binding(2) var outTex: texture_storage_2d<rgba8unorm, write>;

@compute @workgroup_size(16, 16)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= dims.width || gid.y >= dims.height) { return; }
    let idx = gid.y * dims.width + gid.x;
    let c = buf[idx];
    let col = vec4<f32>(
        f32(c & 0xffu),
        f32((c >> 8u) & 0xffu),
        f32((c >> 16u) & 0xffu),
        f32((c >> 24u) & 0xffu)) / 255.0;
    textureStore(outTex, vec2<i32>(i32(gid.x), i32(gid.y)), col);
}
