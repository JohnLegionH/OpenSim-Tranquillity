/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using System.Linq;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace OpenSim.Region.CoreModules.World.Land.Tests;

/// <summary>
/// Parcels loaded into a region whose size differs from the size they were stored for (a region resized in place).
/// The load stretches a lone parcel over the whole region; the parcel's Area, its stored bitmap and the Ends
/// corner have to follow, and the parcel has to be saved again, otherwise the stale values come back at every start.
/// "Saved" is read from the land-object-added event, which is what the simulation data store is subscribed to.
/// </summary>
// No process-wide state beyond what the other land tests use: the class runs in parallel.
public class LandResizeLoadTests : OpenSimTestCase
{
    private const int LegacyCells = 64;      // a 256 m region is 64 x 64 land units of 4 m
    private const int LegacyBitmapBytes = LegacyCells * LegacyCells / 8;

    private sealed class SavedParcel
    {
        public int Area;
        public byte[] Bitmap;
    }

    private sealed class Rig
    {
        public readonly SceneHelpers Helpers = new();
        public readonly LandManagementModule Module = new();
        public readonly Scene Scene;
        public readonly UUID OwnerId = TestHelpers.ParseTail(0x1);

        // Every save the region requested during the load, in order, as it stood at that moment.
        public readonly List<KeyValuePair<UUID, SavedParcel>> Saves = new();

        public Rig(uint size)
        {
            Scene = Helpers.SetupScene("Resized region", UUID.Random(), 1000, 1000, size, size, new IniConfigSource());
            SceneHelpers.SetupSceneModules(Scene, Module);
            Scene.EventManager.OnLandObjectAdded += parcel =>
            {
                lock (Saves)
                    Saves.Add(new KeyValuePair<UUID, SavedParcel>(parcel.LandData.GlobalID,
                        new SavedParcel { Area = parcel.LandData.Area, Bitmap = (byte[])parcel.LandData.Bitmap.Clone() }));
            };
        }

        /// <summary>Stores a parcel the way a 256 m region wrote it: a 512 byte bitmap of 64 x 64 land units.</summary>
        public UUID StoreLegacyParcel(string name, int firstRow, int endRow)
        {
            byte[] bitmap = new byte[LegacyBitmapBytes];
            int set = 0;
            for (int y = firstRow; y < endRow; y++)
                for (int x = 0; x < LegacyCells; x++)
                {
                    int bit = y * LegacyCells + x;
                    bitmap[bit / 8] |= (byte)(1 << (bit % 8));
                    set++;
                }

            LandObject lo = new LandObject(OwnerId, false, Scene);
            lo.LandData.Name = name;
            lo.LandData.Bitmap = bitmap;
            lo.LandData.Area = set * Constants.LandUnit * Constants.LandUnit;
            Helpers.SimDataService.StoreLandObject(lo);
            return lo.LandData.GlobalID;
        }

        public SavedParcel LastSave(UUID globalId)
        {
            lock (Saves)
                return Saves.Last(s => s.Key == globalId).Value;
        }
    }

    private static int CountSet(byte[] bitmap)
    {
        int n = 0;
        foreach (byte b in bitmap)
            for (int bit = 0; bit < 8; bit++)
                if ((b & (1 << bit)) != 0)
                    n++;
        return n;
    }

    private static int CountSet(bool[,] bitmap)
    {
        int n = 0;
        foreach (bool b in bitmap)
            if (b)
                n++;
        return n;
    }

    [Fact]
    public void TestSingleSmallParcelLoadedIntoLargerRegionHasFullAreaAndBitmapInMemoryAndAsSaved()
    {
        TestHelpers.InMethod();

        Rig rig = new(1024);
        UUID id = rig.StoreLegacyParcel("only", 0, LegacyCells);

        rig.Scene.loadAllLandObjectsFromStorage(rig.Scene.RegionInfo.RegionID);

        ILandObject lo = rig.Module.GetLandObject(1000, 1000);
        Assert.Equal(id, lo.LandData.GlobalID);
        Assert.Equal(1024 * 1024, lo.LandData.Area);
        Assert.Equal(256 * 256, CountSet(lo.GetLandBitmap()));
        Assert.Equal(1024f, lo.EndPoint.X);
        Assert.Equal(1024f, lo.EndPoint.Y);

        SavedParcel saved = rig.LastSave(id);
        Assert.Equal(1024 * 1024, saved.Area);
        Assert.Equal(256 * 256 / 8, saved.Bitmap.Length);
        Assert.Equal(256 * 256, CountSet(saved.Bitmap));
    }

    [Fact]
    public void TestSeveralSmallParcelsLoadedIntoLargerRegionAreSavedWithAreaOfTheirBitmap()
    {
        TestHelpers.InMethod();

        Rig rig = new(1024);
        UUID low = rig.StoreLegacyParcel("low", 0, LegacyCells / 2);
        UUID high = rig.StoreLegacyParcel("high", LegacyCells / 2, LegacyCells);

        rig.Scene.loadAllLandObjectsFromStorage(rig.Scene.RegionInfo.RegionID);

        List<ILandObject> parcels = rig.Module.AllParcels();
        Assert.Equal(3, parcels.Count);

        int savedAreaTotal = 0;
        foreach (ILandObject lo in parcels)
        {
            SavedParcel saved = rig.LastSave(lo.LandData.GlobalID);
            Assert.Equal(CountSet(saved.Bitmap) * Constants.LandUnit * Constants.LandUnit, saved.Area);
            Assert.Equal(CountSet(lo.GetLandBitmap()) * Constants.LandUnit * Constants.LandUnit, saved.Area);
            savedAreaTotal += saved.Area;
        }

        Assert.Equal(1024 * 1024, savedAreaTotal);
        Assert.Equal(256 * 128, rig.LastSave(low).Area);
        Assert.Equal(256 * 128, rig.LastSave(high).Area);
    }

    [Fact]
    public void TestParcelsThatAlreadyFitTheRegionAreSavedOnceEachAndUnchanged()
    {
        TestHelpers.InMethod();

        Rig rig = new((uint)Constants.RegionSize);
        UUID low = rig.StoreLegacyParcel("low", 0, LegacyCells / 2);
        UUID high = rig.StoreLegacyParcel("high", LegacyCells / 2, LegacyCells);

        rig.Scene.loadAllLandObjectsFromStorage(rig.Scene.RegionInfo.RegionID);

        Assert.Equal(2, rig.Saves.Count);
        Assert.Equal(256 * 128, rig.LastSave(low).Area);
        Assert.Equal(256 * 128, rig.LastSave(high).Area);
        Assert.Equal(LegacyBitmapBytes, rig.LastSave(low).Bitmap.Length);
    }

    [Fact]
    public void TestOneParcelThatAlreadyFillsALargeRegionIsSavedOnce()
    {
        TestHelpers.InMethod();

        Rig rig = new(1024);
        LandObject lo = new LandObject(rig.OwnerId, false, rig.Scene);
        lo.SetLandBitmap(lo.GetSquareLandBitmap(0, 0, 1024, 1024));
        rig.Helpers.SimDataService.StoreLandObject(lo);

        rig.Scene.loadAllLandObjectsFromStorage(rig.Scene.RegionInfo.RegionID);

        Assert.Single(rig.Saves);
        Assert.Equal(1024 * 1024, rig.Saves[0].Value.Area);
        Assert.Equal(256 * 256, CountSet(rig.Saves[0].Value.Bitmap));
    }
}
