namespace Sable.Plugin.Sdk.Pixels;

/// <summary>
/// Write access to the active pixel layer (capability <c>pixel.write.layer_output</c>). Null on
/// <see cref="Host.IHostContext.PixelWrites"/> when not granted. Each call is ONE undoable step
/// (or, inside an open <c>undo.transaction</c>, part of that batch) — plugins never touch the
/// layer buffer directly. Buffers are straight-alpha RGBA8, row-major, length = Width*Height*4.
/// Writing succeeds only when the active layer is a pixel layer; otherwise the call is a no-op
/// (returns false) so a plugin can probe without throwing.
/// </summary>
public interface IPixelWriteApi
{
    /// <summary>Replace the active pixel layer's whole buffer with <paramref name="buffer"/> (the
    /// layer takes the buffer's size; its document offset is preserved). False = no active pixel
    /// layer. Throws if the buffer length doesn't match its declared size.</summary>
    bool SetActiveLayerPixels(PixelBuffer buffer);

    /// <summary>Overwrite a rectangular region of the active pixel layer at layer-local
    /// (<paramref name="x"/>,<paramref name="y"/>) with <paramref name="buffer"/> — source pixels
    /// replace the destination (straight-alpha copy, no blend), clipped to the layer bounds. False =
    /// no active pixel layer. Throws if the buffer length doesn't match its declared size.</summary>
    bool WriteRegion(int x, int y, PixelBuffer buffer);
}
