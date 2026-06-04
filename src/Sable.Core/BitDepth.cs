namespace Sable.Core;

/// <summary>
/// Document working precision per channel (PLAN §6, Image ▸ Mode). The GPU compositor already blends
/// in linear float, so this selects the document's storage / IO precision rather than the blend maths.
/// Today 8-bit is the layer editing precision; 16/32-bit are carried through the document + IO and
/// become true editing precision as the float layer pipeline lands (bit-depth milestone, slices 2+).
/// The enum value is the bits-per-channel (8/16/32) — used directly for display + serialization.
/// </summary>
public enum BitDepth { Eight = 8, Sixteen = 16, ThirtyTwo = 32 }
