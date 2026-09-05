using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSimNGC.Appearance.Baking;
using Microsoft.Extensions.Logging;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// Server-side baking (Design Brief §4.2 C2, ADR-002/004/005). S1: the orchestrator plus one region console
/// command. S2 adds the ADR-004 bake index in the avatar service and the input-hash skip, so a second bake of an
/// unchanged outfit composites nothing. Still no login/COF/cap trigger and no wire changes. Config:
/// <code>
/// [Appearance]
///     ServerSideBaking = false   ; the wire flag (RegionProtocols bit 0, AppearanceData) — parsed and logged, not acted on in S1
///     BakeSize = 1024            ; 512, 1024 or 2048
///     BakeQuality = 0.85
/// </code>
/// The module always loads so that <c>appearance serverbake &lt;first&gt; &lt;last&gt;</c> exists on every region console.
/// </summary>
public class ServerSideBakingModule : ISharedRegionModule, IServerSideBaker
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(ServerSideBakingModule));

    public const string ConfigSection = "Appearance";

    private readonly List<Scene> m_scenes = new();
    private IBakeBackend m_backend;
    private TexLayerCompositor m_compositor;

    /// <summary>The wire flag as configured. Not read by anything in S1; S3 gates RegionHandshake/AppearanceData on it.</summary>
    public bool ServerSideBakingEnabled { get; private set; }
    public int BakeSize { get; private set; } = 1024;
    public double BakeQuality { get; private set; } = 0.85;

    public string Name => "ServerSideBakingModule";
    public Type ReplaceableInterface => null;

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs[ConfigSection];
        if (config is not null)
        {
            ServerSideBakingEnabled = config.GetBoolean("ServerSideBaking", false);
            BakeSize = config.GetInt("BakeSize", 1024);
            BakeQuality = config.GetDouble("BakeQuality", 0.85);
        }
        if (BakeSize is not (512 or 1024 or 2048))
        {
            m_log.LogWarning("[SSB]: [Appearance] BakeSize {Size} is not 512, 1024 or 2048; using 1024", BakeSize);
            BakeSize = 1024;
        }
        BakeQuality = Math.Clamp(BakeQuality, 0.1, 1.0);
        m_log.LogInformation("[SSB]: ServerSideBaking={Flag} (wire flag; not acted on before S3), BakeSize={Size}, BakeQuality={Quality}",
            ServerSideBakingEnabled, BakeSize, BakeQuality);
    }

    public void PostInitialise() { }
    public void Close() { }

    public void AddRegion(Scene scene)
    {
        scene.RegisterModuleInterface<IServerSideBaker>(this);
    }

    public void RemoveRegion(Scene scene)
    {
        scene.UnregisterModuleInterface<IServerSideBaker>(this);
        lock (m_scenes) m_scenes.Remove(scene);
    }

    public void RegionLoaded(Scene scene)
    {
        lock (m_scenes) m_scenes.Add(scene);
        scene.AddCommand(
            "Users", this, "appearance serverbake",
            "appearance serverbake <first-name> <last-name>",
            "Bake the avatar's current wearables on the server, store the bakes as assets, write them into the avatar's "
            + "texture entry and send the appearance. (Not 'appearance rebake', which asks the viewer to re-upload its own bakes.)",
            HandleServerBakeCommand);
    }

    /// <summary>The backend is created on first use so a region without any bake never loads the compositor's resources.</summary>
    private IBakeBackend Backend
    {
        get
        {
            if (m_backend is null)
            {
                lock (m_scenes)
                {
                    if (m_backend is null)
                    {
                        m_compositor = new TexLayerCompositor();
                        m_backend = new SkiaBakeBackend(m_compositor) { Quality = BakeQuality };
                    }
                }
            }
            return m_backend;
        }
    }

    // ------------------------------------------------------------------ IServerSideBaker

    public async Task<BakeOutcome> BakeAsync(ScenePresence sp, BakeReason reason, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sp);
        if (sp.IsChildAgent) throw new InvalidOperationException($"{sp.Name} is a child agent here");
        var scene = sp.Scene;
        var backend = Backend;

        var cofVersion = CofVersionOf(scene, sp.UUID);

        // steps 2, 4-6, scene-free; the ADR-004 index in the avatar service is read for the reuse decision and
        // written back at the end of the run
        var outcome = await Task.Run(() => BakeOrchestrator.Run(sp.UUID, reason, sp.Appearance.Wearables, sp.Appearance.VisualParams, sp.Appearance,
            scene.AssetService, scene.AvatarService, backend, m_compositor, BakeSize, cofVersion, ct), ct).ConfigureAwait(false);

        // step 7: send to everyone in view and to self. A reused channel is sent exactly like a fresh one — the
        // reason for the bake may be that nobody has seen it yet.
        //
        // No QueueAppearanceSave here, deliberately. A bake changes only the baked faces of the TextureEntry, and
        // the avatar service does not persist those at all: AvatarData(AvatarAppearance) carries the serial,
        // height, wearables, visual params and attachments and nothing else (IAvatarService.cs:142-189). What a
        // save would do is destroy this bake's index, because AvatarService.SetAvatar deletes every row for the
        // agent before rewriting those keys (AvatarService.cs:93). The bake index written above IS the
        // persistence of the baked faces.
        if (outcome.Count(ChannelStatus.Baked) + outcome.Count(ChannelStatus.Reused) > 0)
        {
            sp.SendAppearanceToAllOtherAgents();
            sp.SendAppearanceToAgent(sp);
        }

        // step 8: one INFO line per bake; the fidelity evidence at DEBUG
        m_log.LogInformation("[SSB]: bake for {Name} ({Agent}) reason={Reason}: {Summary} in {Ms} ms",
            sp.Name, sp.UUID, reason, Summarise(outcome), outcome.ElapsedMs);
        if (m_log.IsEnabled(LogLevel.Debug))
            foreach (var c in outcome.Channels)
            {
                if (c.Fidelity.Refusals.Count > 0) m_log.LogDebug("[SSB]: {Channel} refusals: {R}", c.Channel, string.Join("; ", c.Fidelity.Refusals));
                if (c.Fidelity.MissingTextures.Count > 0) m_log.LogDebug("[SSB]: {Channel} missing textures: {T}", c.Channel, string.Join(", ", c.Fidelity.MissingTextures));
                if (c.Fidelity.UnsupportedLayers.Count > 0) m_log.LogDebug("[SSB]: {Channel} unsupported layers: {L}", c.Channel, string.Join(" | ", c.Fidelity.UnsupportedLayers));
                foreach (var note in c.Fidelity.Notes) m_log.LogDebug("[SSB]: {Channel} layer {Note}", c.Channel, note);
            }
        return outcome;
    }

    private static string Summarise(BakeOutcome o)
        => string.Join(", ", o.Channels.Where(c => c.Status != ChannelStatus.Skipped).Select(c => $"{c.Channel}={c.Status}{(c.Status == ChannelStatus.Failed ? $"({c.Reason})" : "")}"))
           + $" [{o.Count(ChannelStatus.Skipped)} skipped]"
           + $" reused {o.Count(ChannelStatus.Reused)}/{o.Count(ChannelStatus.Baked) + o.Count(ChannelStatus.Reused)}"
           + (o.Superseded.Count > 0 ? $", superseded {o.Superseded.Count}" : "")
           + (o.IndexWritten ? "" : ", index NOT written");

    /// <summary>
    /// The Current Outfit folder's version, stored with the bake as <c>BakeCOFVersion</c> (ADR-006: the sim reads
    /// the COF folder's own <c>Version</c> and needs no AIS). Zero when there is no inventory service or no COF.
    /// </summary>
    private static int CofVersionOf(Scene scene, UUID agentId)
    {
        try
        {
            var cof = scene.InventoryService?.GetFolderForType(agentId, FolderType.CurrentOutfit);
            return cof?.Version ?? 0;
        }
        catch (Exception ex)
        {
            m_log.LogDebug(ex, "[SSB]: could not read the COF version for {Agent}", agentId);
            return 0;
        }
    }

    // ------------------------------------------------------------------ console

    private void HandleServerBakeCommand(string module, string[] cmd)
    {
        if (cmd.Length != 4)
        {
            MainConsole.Instance.Output("Usage: appearance serverbake <first-name> <last-name>");
            return;
        }
        string firstname = cmd[2], lastname = cmd[3];
        List<Scene> scenes;
        lock (m_scenes) scenes = new List<Scene>(m_scenes);
        var found = false;
        foreach (var scene in scenes)
        {
            ScenePresence sp = scene.GetScenePresence(firstname, lastname);
            if (sp is null || sp.IsChildAgent) continue;
            found = true;
            BakeOutcome outcome;
            try { outcome = BakeAsync(sp, BakeReason.Console, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                MainConsole.Instance.Output("Server bake for {0} in {1} threw: {2}", sp.Name, scene.RegionInfo.RegionName, ex.Message);
                m_log.LogError(ex, "[SSB]: console bake for {Name} threw", sp.Name);
                continue;
            }
            var sb = new StringBuilder();
            sb.AppendLine($"Server bake for {sp.Name} in {scene.RegionInfo.RegionName}: {outcome.ElapsedMs} ms, size {BakeSize}");
            sb.AppendLine($"{"channel",-8} {"face",4} {"status",-8} {"asset",-36} detail");
            foreach (var c in outcome.Channels)
            {
                var detail = c.Status switch
                {
                    ChannelStatus.Reused => $"hash {c.InputHash[..Math.Min(12, c.InputHash.Length)]}; inputs unchanged, not recomputed",
                    ChannelStatus.Baked => $"hash {c.InputHash[..Math.Min(12, c.InputHash.Length)]}"
                        + (c.Fidelity.MissingTextures.Count > 0 ? $"; missing textures {c.Fidelity.MissingTextures.Count}" : "")
                        + (c.Fidelity.UnsupportedLayers.Count > 0 ? $"; unsupported layers {c.Fidelity.UnsupportedLayers.Count}" : ""),
                    _ => c.Reason,
                };
                sb.AppendLine($"{c.Channel,-8} {BakeOrchestrator.FaceOf(c.Channel),4} {c.Status,-8} {(c.AssetId.IsZero() ? "-" : c.AssetId.ToString()),-36} {detail}");
            }
            sb.AppendLine($"reused {outcome.Count(ChannelStatus.Reused)} of {outcome.Count(ChannelStatus.Baked) + outcome.Count(ChannelStatus.Reused)} live channels; "
                + $"superseded {outcome.Superseded.Count} old asset(s); index {(outcome.IndexWritten ? "written" : "NOT written")}, COF version {CofVersionOf(scene, sp.UUID)}");
            foreach (var note in outcome.Notes) sb.AppendLine($"  note: {note}");
            MainConsole.Instance.Output(sb.ToString().TrimEnd());
        }
        if (!found) MainConsole.Instance.Output("No root agent named {0} {1} in any region here", firstname, lastname);
    }
}
