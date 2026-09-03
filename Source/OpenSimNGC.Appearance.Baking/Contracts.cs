using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// One worn wearable, as the compositor needs it: the asset it came from, its
/// wearable type, and the raw <c>LLWearable</c> text (the asset body) that names
/// the textures and parameter values for that wearable.
/// </summary>
/// <param name="AssetId">Asset id of the wearable asset.</param>
/// <param name="WearableType">
/// Wearable type index as used on the wire and in <c>avatar_lad.xml</c>
/// (0 = shape, 1 = skin, 2 = hair, 3 = eyes, 4 = shirt, 5 = pants, ...).
/// </param>
/// <param name="RawText">Full text of the wearable asset, unparsed.</param>
public sealed record WearableInput(UUID AssetId, int WearableType, string RawText);

/// <summary>
/// One source texture referenced by a wearable, already fetched from the asset
/// service and still in its JPEG 2000 encoding.
/// </summary>
/// <param name="TextureId">Texture asset id.</param>
/// <param name="J2kBytes">JPEG 2000 codestream or JP2 file bytes.</param>
public sealed record TextureInput(UUID TextureId, byte[] J2kBytes);

/// <summary>
/// Everything one bake run needs. The request is complete and self-contained:
/// the compositor performs no I/O and no asset fetches of its own.
/// </summary>
/// <param name="Wearables">The wearables currently worn, in any order.</param>
/// <param name="VisualParams">
/// Visual parameter values keyed by parameter id, after wearable and shape
/// values have been merged by the caller. Values are in the parameter's native
/// range from <c>avatar_lad.xml</c>, not the 0..255 wire encoding.
/// </param>
/// <param name="Textures">
/// Source textures keyed by texture id. Any texture a wearable references that
/// is absent from this map is reported in <see cref="FidelityReport.MissingTextures"/>
/// and rendered with the layer's fallback colour.
/// </param>
/// <param name="BakeSize">Output edge size in pixels for every channel (ADR-008: 512 by default).</param>
public sealed record BakeRequest(
    IReadOnlyList<WearableInput> Wearables,
    IReadOnlyDictionary<int, float> VisualParams,
    IReadOnlyDictionary<UUID, TextureInput> Textures,
    int BakeSize);

/// <summary>
/// What the compositor could not reproduce faithfully for one output channel
/// (ADR-005: best-effort with a structured report; refusal only for corrupt input).
/// </summary>
/// <param name="UnsupportedLayers">
/// Names of <c>avatar_lad.xml</c> layers that were skipped because the backend
/// does not implement them (for example morph-masked or alpha-gradient layers).
/// </param>
/// <param name="MissingTextures">Texture ids a wearable referenced that were not supplied in the request.</param>
/// <param name="Notes">Free-form diagnostics for logs; never parsed.</param>
public sealed record FidelityReport(
    IReadOnlyList<string> UnsupportedLayers,
    IReadOnlyList<UUID> MissingTextures,
    IReadOnlyList<string> Notes);

/// <summary>
/// One finished bake.
/// </summary>
/// <param name="Channel">Which output channel this is.</param>
/// <param name="J2kBytes">The composited texture, JPEG 2000 encoded, ready to store as a texture asset.</param>
/// <param name="InputHash">
/// Deterministic hash of the inputs that produced this bake, as computed by
/// <see cref="BakeHash.Compute"/>. Stored alongside the asset so an unchanged
/// input set can be recognised without re-baking (ADR-004 <c>BakeHash:&lt;channel&gt;</c>).
/// </param>
/// <param name="Fidelity">What was and was not reproduced.</param>
public sealed record BakeResult(
    BakeChannel Channel,
    byte[] J2kBytes,
    string InputHash,
    FidelityReport Fidelity);
