/*
 * Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the
 * Mozilla Public License, v. 2.0. If a copy of the MPL was not distributed
 * with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
 */
using System.Text;
using OpenMetaverse;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.CharacterPublish.Tests;

/// <summary>Writes a sample set for inspection, when asked. Nothing is uploaded anywhere.</summary>
public class SampleSetTests
{
    [RealAssetsFact("PUB_SAMPLES_DIR")]
    public void Writes_a_sample_set_that_every_reader_accepts()
    {
        var dir = Environment.GetEnvironmentVariable("PUB_SAMPLES_DIR")!;
        var perms = PermissionBlock.ForOwner(Fixtures.Creator, Fixtures.Owner, PermissionMasks.CopyModifyTransfer);
        var placeholder = (a: new UUID("00000000-0000-4000-8000-00000000000a"), b: new UUID("00000000-0000-4000-8000-00000000000b"), c: new UUID("00000000-0000-4000-8000-00000000000c"), iris: new UUID("00000000-0000-4000-8000-00000000000d"));

        var wearables = new (string File, WearableDocument Doc)[]
        {
            ("shape-default.wearable", WearableFactory.Shape("Example Shape", "Default values from avatar_lad.xml", perms)),
            ("skin-placeholder.wearable", WearableFactory.Skin("Example Skin", "Placeholder textures", perms, placeholder.a, placeholder.b, placeholder.c)),
            ("eyes-placeholder.wearable", WearableFactory.Eyes("Example Eyes", "Placeholder texture", perms, placeholder.iris)),
        };
        foreach (var (file, doc) in wearables)
        {
            var bytes = WearableWriter.WriteBytes(doc);
            File.WriteAllBytes(Path.Combine(dir, file), bytes);
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Equal(WearableWriter.Write(WearableReader.Read(text)), text);
            Assert.Equal(doc.Type, WearableParser.Parse(text).Kind);
        }

        // The attribution text comes from the base body's licence file, verbatim, so it is read at run time and not kept in the code.
        var licencePath = Environment.GetEnvironmentVariable("PUB_MAX_LICENSE");
        var licence = licencePath != null && File.Exists(licencePath) ? File.ReadAllText(licencePath) : "(licence text not supplied)\n";
        var note = new NotecardDocument
        {
            Text = "Base body credit\n\n" + licence.Replace("\r\n", "\n") +
                   "\nLicence text: https://creativecommons.org/licenses/by/4.0/legalcode\n" +
                   "This copy has been converted to the Second Life mesh asset format.\n",
        };
        var nb = NotecardWriter.WriteBytes(note);
        File.WriteAllBytes(Path.Combine(dir, "attribution.notecard"), nb);
        Assert.Equal(note.Text, NotecardReader.Read(nb).Text);
    }
}
