/*
 * Pure unit tests for the UseBanList re-assert that fixes the "properties save clears UseBanList"
 * defect (Docs/KnownDefects.md). These exercise the real production computation
 * LandObject.ComputeSavedFlags (the flag word UpdateLandProperties persists) and the membership
 * decision LandObject.HasBanEntry behind it, against real LandData / LandAccessEntry shapes with NO
 * Scene - so they run where the SceneHelpers-based integration harness cannot. Removing the
 * re-assert from ComputeSavedFlags makes ComputeSavedFlags_ReassertsUseBanList... fail. The
 * full-path counterpart is LandManagementModuleTests.TestPropertiesSaveOmittingUseBanListPreservesBanFlag.
 */

using Xunit;
using OpenMetaverse;
using OpenSim.Framework;

namespace OpenSim.Region.CoreModules.World.Land.Tests;

public class LandObjectBanFlagTests
{
    private static readonly UUID Someone = new UUID("00000000-0000-0000-0000-00000000000a");

    private static LandData WithEntry(AccessList kind, int expires)
    {
        LandData ld = new LandData();
        ld.ParcelAccessList.Add(
            new LandAccessEntry { AgentID = Someone, Flags = kind, Expires = expires });
        return ld;
    }

    // ---- ComputeSavedFlags: the production computation UpdateLandProperties persists ----------

    [Fact]
    public void ComputeSavedFlags_ReassertsUseBanList_WhenClientOmitsItWithBanPresent()
    {
        // The exact defect: a non-Access properties save ships the whole flag word with UseBanList
        // cleared while a ban entry is present and the ban bit is in allowedDelta. The saved word
        // must keep UseBanList. This is the assertion that fails if the re-assert is removed.
        LandData banned = WithEntry(AccessList.Ban, expires: 0);
        uint allowedDelta = (uint)ParcelFlags.UseBanList;
        uint current = (uint)ParcelFlags.UseBanList;   // flag is currently set
        uint client = 0u;                               // client omits it

        uint saved = LandObject.ComputeSavedFlags(current, client, allowedDelta, banned);

        Assert.True((saved & (uint)ParcelFlags.UseBanList) != 0);
    }

    [Fact]
    public void ComputeSavedFlags_DoesNotSetUseBanList_WhenNoBanEntry()
    {
        // No ban entry: the re-assert must not invent the flag. Client omits it, so it stays off.
        LandData noBan = new LandData();
        uint allowedDelta = (uint)ParcelFlags.UseBanList;

        uint saved = LandObject.ComputeSavedFlags(0u, 0u, allowedDelta, noBan);

        Assert.True((saved & (uint)ParcelFlags.UseBanList) == 0);
    }

    [Fact]
    public void ComputeSavedFlags_LeavesUseAccessListToClient_WhenAccessEntryPresent()
    {
        // Scoping guard: an access list (no ban) must NOT force UseAccessList on. The client omits
        // it - a valid "public access on, list retained" save - so the saved word must omit it too.
        LandData access = WithEntry(AccessList.Access, expires: 0);
        uint allowedDelta = (uint)(ParcelFlags.UseBanList | ParcelFlags.UseAccessList);
        uint current = (uint)ParcelFlags.UseAccessList;   // currently restricting
        uint client = 0u;                                  // client turns public access on

        uint saved = LandObject.ComputeSavedFlags(current, client, allowedDelta, access);

        Assert.True((saved & (uint)ParcelFlags.UseAccessList) == 0);
        Assert.True((saved & (uint)ParcelFlags.UseBanList) == 0); // and no ban entry -> no ban flag
    }

    // ---- HasBanEntry: the membership decision behind the re-assert ---------------------------

    [Fact]
    public void HasBanEntry_TrueWhenBanPresent()
    {
        Assert.True(LandObject.HasBanEntry(WithEntry(AccessList.Ban, expires: 0)));
    }

    [Fact]
    public void HasBanEntry_FalseWhenNoEntries()
    {
        Assert.False(LandObject.HasBanEntry(new LandData()));
    }

    [Fact]
    public void HasBanEntry_TrueForExpiredBan_MembershipNotExpiry()
    {
        // The gate is set when a ban is added and cleared only on a delete-all, so an expired
        // entry still keeps UseBanList until it is explicitly removed. Mirror that here.
        Assert.True(LandObject.HasBanEntry(WithEntry(AccessList.Ban, expires: 1)));
    }

    [Fact]
    public void HasBanEntry_FalseForAccessEntryOnly()
    {
        Assert.False(LandObject.HasBanEntry(WithEntry(AccessList.Access, expires: 0)));
    }
}
