/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// botRemoveOutfit removes the outfit notecard of that name from the calling object as well as the owner's in-memory
/// entry. It touches only an appearance notecard in the calling object: a plain notecard of the same name, and the
/// notecard of another object, stay. No process-wide state: each test has its own two regions, so the class runs in
/// parallel.
/// </summary>
public class BotRemoveOutfitTests
{
    private const int Marker = 25;           // a visual parameter this look sets, to tell one look from another
    private const byte Saved = 111;
    private const byte Changed = 22;

    private static (BotRegions R, ScenePresence Owner) Setup()
    {
        var r = new BotRegions();
        UUID id = UUID.Random();
        var sp = SceneHelpers.AddScenePresence(r.B.Scene, id);
        sp.Appearance.VisualParams[Marker] = Saved;
        r.B.Prim.OwnerID = id;
        return (r, sp);
    }

    private static TaskInventoryItem NotecardNamed(SceneObjectPart part, string name)
        => part.Inventory.GetInventoryItems().FirstOrDefault(i => i.Type == (int)AssetType.Notecard && i.Name == name);

    /// <summary>The look of a bot made with the named outfit.</summary>
    private static byte LookOfBotMadeWith(BotRegions r, string outfit)
    {
        string first = new string(Guid.NewGuid().ToString("N").Take(8).Select(c => (char)('a' + c % 26)).ToArray());   // names are unique
        BotRegions.Run(r.B, "key b = botCreateBot(\"" + first + "\", \"Est\", \"" + outfit + "\", <128, 128, 30>, 0); llSay(0, \"bot=\" + (string)b);");
        return r.B.Scene.GetScenePresence(BotRegions.KeyOf(r.B, "bot=")).Appearance.VisualParams[Marker];
    }

    [Fact]
    public void BotRemoveOutfitRemovesTheNotecardFromTheCallingObject()
    {
        var (r, owner) = Setup();
        using (r)
        {
            BotRegions.Run(r.B, "botSetOutfit(\"Priest\");");
            Assert.NotNull(NotecardNamed(r.B.Prim, "Priest"));
            owner.Appearance.VisualParams[Marker] = Changed;

            BotRegions.Run(r.B, "botRemoveOutfit(\"Priest\");");

            Assert.Null(NotecardNamed(r.B.Prim, "Priest"));
            // With the notecard and the in-memory entry both gone, the name is an unknown outfit: the owner's look.
            Assert.Equal(Changed, LookOfBotMadeWith(r, "Priest"));
        }
    }

    [Fact]
    public void BotRemoveOutfitLeavesTheNotecardOfAnotherObject()
    {
        var (r, owner) = Setup();
        using (r)
        {
            var other = SceneHelpers.AddSceneObject(r.B.Scene, "Other prim", owner.UUID).RootPart;
            r.B.RezScriptInto(other, "default { state_entry() { botSetOutfit(\"Priest\"); llSay(0, \"saved\"); } }");
            Assert.True(r.B.PumpUntil(() => r.B.Said.Contains("saved")), string.Join(" | ", r.B.Said));
            BotRegions.Run(r.B, "botSetOutfit(\"Priest\");");

            BotRegions.Run(r.B, "botRemoveOutfit(\"Priest\");");

            Assert.Null(NotecardNamed(r.B.Prim, "Priest"));
            Assert.NotNull(NotecardNamed(other, "Priest"));
        }
    }

    [Fact]
    public void BotRemoveOutfitLeavesAPlainNotecardOfTheSameName()
    {
        var (r, _) = Setup();
        using (r)
        {
            BotRegions.Run(r.B, "osMakeNotecard(\"Junk\", \"hello\");");
            Assert.NotNull(NotecardNamed(r.B.Prim, "Junk"));

            BotRegions.Run(r.B, "botRemoveOutfit(\"Junk\");");

            Assert.NotNull(NotecardNamed(r.B.Prim, "Junk"));
        }
    }

    [Fact]
    public void BotRemoveOutfitStillRemovesTheOwnersInMemoryEntry()
    {
        var (r, owner) = Setup();
        using (r)
        {
            // Saved from another object, so this object has no notecard of the name, only the owner's in-memory entry.
            var other = SceneHelpers.AddSceneObject(r.B.Scene, "Other prim", owner.UUID).RootPart;
            r.B.RezScriptInto(other, "default { state_entry() { botSetOutfit(\"Priest\"); llSay(0, \"saved\"); } }");
            Assert.True(r.B.PumpUntil(() => r.B.Said.Contains("saved")), string.Join(" | ", r.B.Said));
            owner.Appearance.VisualParams[Marker] = Changed;
            Assert.Equal(Saved, LookOfBotMadeWith(r, "Priest"));

            BotRegions.Run(r.B, "botRemoveOutfit(\"Priest\");");

            Assert.Equal(Changed, LookOfBotMadeWith(r, "Priest"));
        }
    }
}
