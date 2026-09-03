namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// Deterministic input hashing for bake reuse (ADR-004 <c>BakeHash:&lt;channel&gt;</c>).
/// The hash covers only the inputs that can influence the named channel, so a
/// change to, say, a shirt does not invalidate the lower-body bake.
/// </summary>
public static class BakeHash
{
    /// <summary>
    /// Compute a stable, lower-case hexadecimal hash of the parts of
    /// <paramref name="r"/> that affect <paramref name="ch"/>: the ordered set of
    /// wearable asset ids and types that contribute to the channel, the values of
    /// visual params that drive layers in the channel, the ids of textures the
    /// channel samples, and the bake size. Two requests that hash equal must bake
    /// to identical bytes on the same backend version.
    /// </summary>
    /// <param name="ch">The output channel being hashed.</param>
    /// <param name="r">The complete bake input.</param>
    /// <returns>A hexadecimal string; algorithm and length are an implementation detail.</returns>
    public static string Compute(BakeChannel ch, BakeRequest r)
        => throw new NotImplementedException("BakeHash.Compute is implemented in S0b.");
}
