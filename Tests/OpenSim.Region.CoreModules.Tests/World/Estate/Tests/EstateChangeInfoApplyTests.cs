/*
 * Unit tests for EstateManagementModule.ApplyEstateChangeInfo - the absent-vs-false semantics
 * of the EstateChangeInfo CAP save (Docs/KnownDefects.md, "Estate CAP save silently flips
 * TaxFree"; the same sweep found five more fields treating absence as false).
 *
 * This is the fourth instance of the omission/stale-retransmit pattern fixed in this fork, so
 * the contract is pinned: null leaves a server-owned value unchanged, a carried value applies
 * - INCLUDING a carried false, which a naive nullable fix gets wrong.
 *
 * Pure over EstateSettings (no Scene), plain xunit (no OpenSimTestCase base), so it runs where
 * the SceneHelpers-based harness cannot. Reaches the internal applier via the assembly's
 * InternalsVisibleTo("OpenSim.Region.CoreModules.Tests").
 */

using Xunit;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Estate;
using RegionFlags = OpenMetaverse.RegionFlags;

namespace OpenSim.Region.CoreModules.World.Estate.Tests;

public class EstateChangeInfoApplyTests
{
    private static EstateSettings Settings(bool taxFree, bool allowVoice, bool denyAnonymous)
        => new EstateSettings
        {
            TaxFree = taxFree,
            AllowVoice = allowVoice,
            DenyAnonymous = denyAnonymous,
            PublicAccess = true,
            AllowDirectTeleport = true,
            DenyMinors = false,
            AllowEnvironmentOverride = true,
        };

    private static void ApplyAllNull(EstateSettings es)
        => EstateManagementModule.ApplyEstateChangeInfo(es, null, null, null, null, null, null, null);

    // The documented defect: a save omitting override_public_access must leave TaxFree
    // unchanged (the old code flipped it via a negated-current default).
    [Fact]
    public void OmittedTaxFree_LeavesItUnchanged_BothPolarities()
    {
        EstateSettings on = Settings(taxFree: true, allowVoice: true, denyAnonymous: true);
        ApplyAllNull(on);
        Assert.True(on.TaxFree);

        EstateSettings off = Settings(taxFree: false, allowVoice: true, denyAnonymous: true);
        ApplyAllNull(off);
        Assert.False(off.TaxFree);
    }

    // The sweep's absent-as-false family: an omitted allow_voice_chat must not clear the
    // estate voice master switch. (The prompt named AllowLandmark here; that field is not
    // carried by this CAP and has no write site anywhere - see the sweep - so AllowVoice,
    // a genuinely carried server-owned field from the same handler, stands in.)
    [Fact]
    public void OmittedAllowVoice_LeavesItUnchanged()
    {
        EstateSettings es = Settings(taxFree: false, allowVoice: true, denyAnonymous: true);
        ApplyAllNull(es);
        Assert.True(es.AllowVoice);
    }

    [Fact]
    public void OmittedEverything_ChangesNothing()
    {
        EstateSettings es = Settings(taxFree: true, allowVoice: true, denyAnonymous: true);
        ApplyAllNull(es);
        Assert.True(es.TaxFree);
        Assert.True(es.AllowVoice);
        Assert.True(es.DenyAnonymous);
        Assert.True(es.PublicAccess);
        Assert.True(es.AllowDirectTeleport);
        Assert.False(es.DenyMinors);
        Assert.True(es.AllowEnvironmentOverride);
    }

    // A carried value applies.
    [Fact]
    public void CarriedTrue_Applies()
    {
        EstateSettings es = Settings(taxFree: false, allowVoice: false, denyAnonymous: false);
        EstateManagementModule.ApplyEstateChangeInfo(es,
            externallyVisible: null, allowDirectTeleport: null,
            denyAnonymous: true, denyAgeUnverified: null,
            alloVoiceChat: true, overridePublicAccess: true,
            allowEnvironmentOverride: null);
        Assert.True(es.DenyAnonymous);
        Assert.True(es.AllowVoice);
        Assert.True(es.TaxFree);
    }

    // The case a naive nullable fix gets wrong: a carried FALSE must apply as false,
    // never be treated as absent.
    [Fact]
    public void CarriedFalse_AppliesFalse_NotTreatedAsAbsent()
    {
        EstateSettings es = Settings(taxFree: true, allowVoice: true, denyAnonymous: true);
        EstateManagementModule.ApplyEstateChangeInfo(es,
            externallyVisible: null, allowDirectTeleport: null,
            denyAnonymous: false, denyAgeUnverified: null,
            alloVoiceChat: false, overridePublicAccess: false,
            allowEnvironmentOverride: null);
        Assert.False(es.DenyAnonymous);
        Assert.False(es.AllowVoice);
        Assert.False(es.TaxFree);
    }

    // The RegionFlags cascade: a setting the operator disabled must survive an unrelated
    // estate save AND still read disabled in the flags viewers receive. PackEstateFlags is
    // the pure packing behind GetEstateFlags (the detailed estate data); GetRegionFlags and
    // the RegionHandshake pack the same stored values, so this pins the whole cascade.
    [Fact]
    public void DisabledSetting_SurvivesUnrelatedSave_AndStaysDisabledInFlags()
    {
        EstateSettings es = Settings(taxFree: false, allowVoice: false, denyAnonymous: true);
        Assert.Equal(0u, EstateManagementModule.PackEstateFlags(es) & (uint)RegionFlags.AllowVoice);

        // An unrelated save: carries only deny_age_unverified, omits everything else.
        EstateManagementModule.ApplyEstateChangeInfo(es,
            externallyVisible: null, allowDirectTeleport: null,
            denyAnonymous: null, denyAgeUnverified: true,
            alloVoiceChat: null, overridePublicAccess: null,
            allowEnvironmentOverride: null);

        uint flags = EstateManagementModule.PackEstateFlags(es);
        Assert.Equal(0u, flags & (uint)RegionFlags.AllowVoice);                    // still disabled
        Assert.NotEqual(0u, flags & (uint)RegionFlags.DenyAgeUnverified);          // the carried change applied
        // TaxFree=false is !AllowAccessOverride=false, so the override bit packs SET —
        // and the unrelated save must not have disturbed it.
        Assert.NotEqual(0u, flags & (uint)RegionFlags.AllowParcelAccessOverride);
    }

    // Differential: PackEstateFlags must reflect its ARGUMENT, not any other settings
    // instance. Every field packed is set to the opposite of the EstateSettings ctor
    // default, and every resulting bit is asserted - so a mutation that ignores the
    // parameter (substituting a default instance, the stand-in for reading the scene,
    // which a static method cannot even compile) flips every assertion.
    [Fact]
    public void PackEstateFlags_ReflectsArgument_EveryFieldDiffersFromDefault()
    {
        EstateSettings es = new EstateSettings
        {
            AllowLandmark = false,            // default true
            AllowSetHome = false,             // default true
            ResetHomeOnTeleport = true,       // default false
            TaxFree = true,                   // default false (packs INVERTED)
            PublicAccess = false,             // default true (packs two bits)
            BlockDwell = true,                // default false
            AllowDirectTeleport = false,      // default true
            EstateSkipScripts = true,         // default false
            DenyAnonymous = true,             // default false
            DenyIdentified = true,            // default false
            DenyTransacted = true,            // default false
            AllowParcelChanges = false,       // default true
            AbuseEmailToEstateOwner = true,   // default false
            AllowVoice = false,               // default true
            DenyMinors = true,                // default false
            AllowEnvironmentOverride = true,  // default false
        };

        uint flags = EstateManagementModule.PackEstateFlags(es);

        Assert.Equal(0u, flags & (uint)RegionFlags.AllowLandmark);
        Assert.Equal(0u, flags & (uint)RegionFlags.AllowSetHome);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.ResetHomeOnTeleport);
        Assert.Equal(0u, flags & (uint)RegionFlags.AllowParcelAccessOverride);  // TaxFree=true -> bit CLEAR
        Assert.Equal(0u, flags & (uint)RegionFlags.PublicAllowed);
        Assert.Equal(0u, flags & (uint)RegionFlags.ExternallyVisible);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.BlockDwell);
        Assert.Equal(0u, flags & (uint)RegionFlags.AllowDirectTeleport);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.EstateSkipScripts);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.DenyAnonymous);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.DenyIdentified);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.DenyTransacted);
        Assert.Equal(0u, flags & (uint)RegionFlags.AllowParcelChanges);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.AbuseEmailToEstateOwner);
        Assert.Equal(0u, flags & (uint)RegionFlags.AllowVoice);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.DenyAgeUnverified);
        Assert.NotEqual(0u, flags & (uint)RegionFlags.AllowEnvironmentOverride);
    }

    // Mixed save: carried fields apply, omitted fields hold - in one call.
    [Fact]
    public void MixedSave_AppliesCarried_HoldsOmitted()
    {
        EstateSettings es = Settings(taxFree: true, allowVoice: true, denyAnonymous: false);
        EstateManagementModule.ApplyEstateChangeInfo(es,
            externallyVisible: false, allowDirectTeleport: null,
            denyAnonymous: null, denyAgeUnverified: true,
            alloVoiceChat: null, overridePublicAccess: null,
            allowEnvironmentOverride: null);
        Assert.False(es.PublicAccess);          // carried
        Assert.True(es.DenyMinors);             // carried
        Assert.True(es.TaxFree);                // omitted - held
        Assert.True(es.AllowVoice);             // omitted - held
        Assert.False(es.DenyAnonymous);         // omitted - held
        Assert.True(es.AllowDirectTeleport);    // omitted - held
    }
}
