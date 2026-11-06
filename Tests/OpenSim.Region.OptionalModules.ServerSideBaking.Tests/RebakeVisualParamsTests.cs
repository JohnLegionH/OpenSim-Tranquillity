/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Globalization;
using System.Text;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;
using OpenSim.Tests.Common;
using OpenSimNGC.Appearance.Baking;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// A shape change followed by a server-side rebake. The wearables hold the avatar's parameters; the appearance
/// the region keeps for the avatar holds the parameters of the last outfit it knew, and it is the appearance that
/// goes to everyone in view. A rebake therefore has to recompute the parameters from the worn wearables before it
/// sends, or the others keep seeing the old shape while the bakes are new.
/// </summary>
// Process-wide state: SceneHelpers sets MainConsole.Instance and adds the scene to SceneManager. No other class in
// this project touches either, and the tests within one class run one at a time.
public class RebakeVisualParamsTests
{
    private const int VersionIndex = AvatarAppearance.APPEARANCE_VERSION_PARAM_INDEX;

    private sealed class CapturingClient : TestClient
    {
        public readonly List<(UUID Agent, byte[] Params, byte[] Te, int Cof)> Appearances = new();
        public CapturingClient(AgentCircuitData acd, Scene scene) : base(acd, scene) { }
        public override void SendAppearance(UUID agentID, byte[] visualParams, byte[] textureEntry, float hover, int cofVersion)
        {
            lock (Appearances) Appearances.Add((agentID, visualParams, textureEntry, cofVersion));
        }
        public override void SendAppearance(UUID agentID, byte[] visualParams, byte[] textureEntry, float hover)
        {
            lock (Appearances) Appearances.Add((agentID, visualParams, textureEntry, -1));
        }
        public (UUID Agent, byte[] Params, byte[] Te, int Cof) LastFor(UUID agent)
        {
            lock (Appearances) return Appearances.Last(a => a.Agent == agent);
        }
        public int CountFor(UUID agent)
        {
            lock (Appearances) return Appearances.Count(a => a.Agent == agent);
        }
    }

    private sealed record Rig(Scene Scene, ServerSideBakingModule Module, ScenePresence Presence, CapturingClient Observer, AvatarLad Lad);

    private static Rig Setup(bool? usersLocal = null)
    {
        var scene = new SceneHelpers().SetupScene();
        if (usersLocal is bool local)
        {
            var users = System.Reflection.DispatchProxy.Create<OpenSim.Framework.IUserManagement, CrossingAppearanceTests.StandInUsers>();
            ((CrossingAppearanceTests.StandInUsers)(object)users).Local = local;
            scene.RegisterModuleInterface<OpenSim.Framework.IUserManagement>(users);
        }

        var observerAcd = SceneHelpers.GenerateAgentData(UUID.Random());
        var observer = new CapturingClient(observerAcd, scene);
        SceneHelpers.AddScenePresence(scene, observer, observerAcd);

        var sp = SceneHelpers.AddScenePresence(scene, UUID.Random());
        sp.Appearance.VisualParams = new byte[253];   // the block a viewer uploads

        var config = new IniConfigSource();
        var section = config.AddConfig(ServerSideBakingModule.ConfigSection);
        section.Set("ServerSideBaking", "true");
        section.Set("BakeSize", "512");
        var module = new ServerSideBakingModule();
        SceneHelpers.SetupSceneModules(scene, config, module);

        return new Rig(scene, module, sp, observer, new AvatarBakeCompositor().Lad);
    }

    /// <summary>The first transmitted parameter the lad assigns to a wearable type.</summary>
    private static ParamDef ParamOf(AvatarLad lad, string wearable)
        => VisualParamEncoder.SendList(lad).First(p => p.Wearable == wearable && p.Group == 0);

    private static string WearableText(WearableKind kind, string name, IReadOnlyDictionary<TextureSlot, UUID> textures,
        IReadOnlyDictionary<int, float> parameters)
    {
        var text = BomAuxChannelTests.WearableText(kind, name, textures);
        var sb = new StringBuilder();
        sb.Append("parameters ").Append(parameters.Count).Append('\n');
        foreach (var (id, value) in parameters)
            sb.Append(id).Append(' ').Append(value.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
        return text.Replace("parameters 0\n", sb.ToString());
    }

    /// <summary>Wear a Shape holding the given parameters and a Skin; the assets are new each call, as an edited wearable's are.</summary>
    private static void Wear(Rig rig, IReadOnlyDictionary<int, float> shapeParams)
    {
        var scene = rig.Scene;
        var sp = rig.Presence;
        var wearables = new AvatarWearable[AvatarWearable.MAX_WEARABLES];
        for (var i = 0; i < wearables.Length; i++) wearables[i] = new AvatarWearable();

        void Put(UUID id, string name, AssetType type, byte[] data)
            => scene.AssetService.Store(new AssetBase(id, name, (sbyte)type, sp.UUID.ToString()) { Data = data });

        var skinTex = UUID.Random();
        Put(skinTex, "skin", AssetType.Texture, BomAuxChannelTests.Texture(200, 170, 140));

        var none = new Dictionary<int, float>();
        var shape = UUID.Random();
        Put(shape, "Shape", AssetType.Bodypart,
            Encoding.UTF8.GetBytes(WearableText(WearableKind.Shape, "Shape", new Dictionary<TextureSlot, UUID>(), shapeParams)));
        wearables[(int)WearableType.Shape].Add(UUID.Random(), shape);

        var skin = UUID.Random();
        Put(skin, "Skin", AssetType.Bodypart, Encoding.UTF8.GetBytes(WearableText(WearableKind.Skin, "Skin",
            new Dictionary<TextureSlot, UUID>
            {
                [TextureSlot.HeadBodypaint] = skinTex, [TextureSlot.UpperBodypaint] = skinTex, [TextureSlot.LowerBodypaint] = skinTex,
            }, none)));
        wearables[(int)WearableType.Skin].Add(UUID.Random(), skin);

        sp.Appearance.Wearables = wearables;
    }

    private static byte ByteOf(ParamDef p, float value) => Utils.FloatToByte(value, p.Min, p.Max);
    private static int IndexOf(AvatarLad lad, ParamDef p) => VisualParamEncoder.IndexOf(lad, p.Id);

    /// <summary>A value inside the parameter's range at the given fraction of it, so a recomputed byte cannot be a default by accident.</summary>
    private static float At(ParamDef p, float fraction) => p.Min + (p.Max - p.Min) * fraction;

    [Fact]
    public async Task ABakeTakesTheVisualParametersFromTheWornShape()
    {
        var rig = Setup();
        var height = ParamOf(rig.Lad, "shape");
        var value = At(height, 0.9f);
        Wear(rig, new Dictionary<int, float> { [height.Id] = value });

        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);

        Assert.Equal(ByteOf(height, value), rig.Presence.Appearance.VisualParams[IndexOf(rig.Lad, height)]);
    }

    /// <summary>The report's case: the shape is edited, the avatar rebakes, and the others must see the edit.</summary>
    [Fact]
    public async Task AShapeChangeFollowedByARebakeReachesTheObserver()
    {
        var rig = Setup();
        var height = ParamOf(rig.Lad, "shape");
        var before = At(height, 0.2f);
        var after = At(height, 0.8f);
        Assert.NotEqual(ByteOf(height, before), ByteOf(height, after));
        Wear(rig, new Dictionary<int, float> { [height.Id] = before });
        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);
        Assert.Equal(ByteOf(height, before), rig.Observer.LastFor(rig.Presence.UUID).Params[IndexOf(rig.Lad, height)]);

        Wear(rig, new Dictionary<int, float> { [height.Id] = after });
        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);

        Assert.Equal(ByteOf(height, after), rig.Observer.LastFor(rig.Presence.UUID).Params[IndexOf(rig.Lad, height)]);
    }

    /// <summary>
    /// The viewer reads the version from the AppearanceData block and from parameter 11000, prefers the parameter and
    /// drops the whole message when they disagree (an avatar left a cloud by exactly that). Recomputing the other
    /// parameters must leave the two in agreement.
    /// </summary>
    [Fact]
    public async Task TheVersionParameterAndTheAppearanceDataBlockStillAgreeAfterTheRecompute()
    {
        var rig = Setup();
        var height = ParamOf(rig.Lad, "shape");
        var value = At(height, 0.7f);
        Wear(rig, new Dictionary<int, float> { [height.Id] = value });

        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);

        var sent = rig.Observer.LastFor(rig.Presence.UUID);
        Assert.True(sent.Cof >= 0, "no AppearanceData block");
        Assert.Equal(1, sent.Params[VersionIndex]);
        Assert.Equal(ByteOf(height, value), sent.Params[IndexOf(rig.Lad, height)]);   // and the message is the recomputed one
    }

    [Fact]
    public async Task TheAppearanceSerialRisesOnlyWhenTheParametersChange()
    {
        var rig = Setup();
        var height = ParamOf(rig.Lad, "shape");
        Wear(rig, new Dictionary<int, float> { [height.Id] = At(height, 0.3f) });
        var start = rig.Presence.Appearance.Serial;

        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);
        var afterFirst = rig.Presence.Appearance.Serial;
        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);
        var afterSame = rig.Presence.Appearance.Serial;
        Wear(rig, new Dictionary<int, float> { [height.Id] = At(height, 0.6f) });
        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);
        var afterChange = rig.Presence.Appearance.Serial;

        Assert.Equal(start + 1, afterFirst);
        Assert.Equal(afterFirst, afterSame);
        Assert.Equal(afterSame + 1, afterChange);
    }

    /// <summary>A viewer keeps whatever the region last stored for a type it does not wear; so does the recompute.</summary>
    [Fact]
    public async Task AParameterOfATypeThatIsNotWornKeepsTheStoredValue()
    {
        var rig = Setup();
        var shape = ParamOf(rig.Lad, "shape");
        var shirt = ParamOf(rig.Lad, "shirt");
        Wear(rig, new Dictionary<int, float> { [shape.Id] = At(shape, 0.4f) });
        var stored = (byte[])rig.Presence.Appearance.VisualParams.Clone();
        stored[IndexOf(rig.Lad, shirt)] = 77;
        rig.Presence.Appearance.VisualParams = stored;

        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);

        Assert.Equal(ByteOf(shape, At(shape, 0.4f)), rig.Presence.Appearance.VisualParams[IndexOf(rig.Lad, shape)]);
        Assert.Equal(77, rig.Presence.Appearance.VisualParams[IndexOf(rig.Lad, shirt)]);
    }

    /// <summary>
    /// A stored block that is not the length of the parameter table cannot be indexed by it, so nothing in it is
    /// carried; the block that goes out is built whole from the worn wearables and the table's defaults.
    /// </summary>
    [Fact]
    public async Task AParameterBlockOfAnotherLengthIsReplacedByOneBuiltFromTheWornWearables()
    {
        var rig = Setup();
        var shape = ParamOf(rig.Lad, "shape");
        Wear(rig, new Dictionary<int, float> { [shape.Id] = At(shape, 0.4f) });
        rig.Presence.Appearance.VisualParams = new byte[100];

        await rig.Module.BakeAsync(rig.Presence, BakeReason.CofChanged, CancellationToken.None);

        Assert.Equal(253, rig.Presence.Appearance.VisualParams.Length);
        Assert.Equal(ByteOf(shape, At(shape, 0.4f)), rig.Presence.Appearance.VisualParams[IndexOf(rig.Lad, shape)]);
        Assert.True(rig.Observer.CountFor(rig.Presence.UUID) > 0);
    }

    [Fact]
    public async Task ABakeRunsForAUserOfThisGrid()
    {
        var rig = Setup(usersLocal: true);
        var height = ParamOf(rig.Lad, "shape");
        Wear(rig, new Dictionary<int, float> { [height.Id] = At(height, 0.9f) });

        await rig.Module.BakeAsync(rig.Presence, BakeReason.Login, CancellationToken.None);

        Assert.True(rig.Module.RegionOf(rig.Scene).BakedCofVersion(rig.Presence.UUID) >= 0);
        Assert.True(rig.Observer.CountFor(rig.Presence.UUID) > 0);
    }

    /// <summary>The visitor's own viewer fetches its bakes from its home grid's service, which would not hold a bake stored here.</summary>
    [Fact]
    public async Task NoBakeIsStartedForAVisitorFromAnotherGrid()
    {
        var rig = Setup(usersLocal: false);
        var height = ParamOf(rig.Lad, "shape");
        Wear(rig, new Dictionary<int, float> { [height.Id] = At(height, 0.9f) });
        var before = rig.Presence.Appearance.VisualParams.ToArray();
        var sentBefore = rig.Observer.CountFor(rig.Presence.UUID);

        var outcome = await rig.Module.BakeAsync(rig.Presence, BakeReason.Login, CancellationToken.None);

        Assert.Equal(-1, rig.Module.RegionOf(rig.Scene).BakedCofVersion(rig.Presence.UUID));
        Assert.Equal(sentBefore, rig.Observer.CountFor(rig.Presence.UUID));
        Assert.Equal(before, rig.Presence.Appearance.VisualParams);
        Assert.Equal(0, outcome.Count(ChannelStatus.Baked) + outcome.Count(ChannelStatus.Reused));
    }
}
