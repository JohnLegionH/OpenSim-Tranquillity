/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

// Fetches textures through a region as a logged-in avatar, the way a viewer does (login, circuit, the GetTexture capability), and saves the bytes the
// region served. It is the fetch step of region-texture-check.py, which compares them with the archive.
//
// usage: oar-texfetch --login-uri <uri> --first <name> --last <name> --region <name> --out <folder> <texture id>...
// The password is read from the environment variable OAR_CHECK_PASSWORD. Writes <folder>/<id>.bin and prints one line per texture.
// Exit codes: 0 every texture was served, 1 some texture was not, 2 could not log in or bad arguments.
using LibreMetaverse;
using LibreMetaverse.Assets;
using OpenSim.Archive.TextureFetch;
using OpenSim.ArchiveInspector;

const string PasswordVariable = "OAR_CHECK_PASSWORD";
string loginUri = null, first = null, last = null, region = null, outDir = null;
List<UUID> ids = new List<UUID>();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--login-uri" when i + 1 < args.Length: loginUri = args[++i]; break;
        case "--first" when i + 1 < args.Length: first = args[++i]; break;
        case "--last" when i + 1 < args.Length: last = args[++i]; break;
        case "--region" when i + 1 < args.Length: region = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--version": Console.WriteLine("oar-texfetch " + ToolRelease.Version); return 0;
        default:
            if (args[i].StartsWith("--", StringComparison.Ordinal) || !UUID.TryParse(args[i], out UUID id))
            {
                Console.Error.WriteLine("unexpected argument '" + args[i] + "'");
                return Usage();
            }
            ids.Add(id);
            break;
    }
}
if (loginUri == null || first == null || last == null || region == null || outDir == null || ids.Count == 0)
    return Usage();
string tlsProblem = TlsPrerequisite.Check(loginUri, OperatingSystem.IsLinux(), TlsPrerequisite.OpenSslAvailable);
if (tlsProblem != null)
{
    Console.Error.WriteLine(tlsProblem);
    return 2;
}
string password = Environment.GetEnvironmentVariable(PasswordVariable);
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("set the environment variable " + PasswordVariable + " to the avatar's password");
    return 2;
}
Directory.CreateDirectory(outDir);

GridClient client = new GridClient();
client.Settings.Agent.MultipleSims = false;
client.Network.LoginProgress += (_, e) => Console.WriteLine("login progress: " + e.Status);
LoginParams login = new LoginParams(client, first, last, password, "oar-texfetch", ToolRelease.Version, loginUri) { Start = "uri:" + region + "&128&120&30", Timeout = 90000 };
bool ok = await client.Network.LoginAsync(login, CancellationToken.None);
Console.WriteLine("login: " + (ok ? "ok" : "FAILED " + client.Network.LoginMessage));
if (!ok)
    return 2;

int failures = 0;
try
{
    // the circuit and the region's capabilities need a moment before the first request
    await Task.Delay(8000);
    Console.WriteLine("region: " + client.Network.CurrentSim?.Name);
    foreach (UUID id in ids)
    {
        byte[] data = null;
        string state;
        try
        {
            var asset = await client.Assets.RequestImageAsync(id, ImageType.Normal);
            data = asset?.AssetData;
            state = asset == null ? "NoAsset" : "Finished";
        }
        catch (Exception e) { state = "Failed: " + e.GetType().Name + " " + e.Message; }
        if (data != null && data.Length > 0)
            File.WriteAllBytes(Path.Combine(outDir, id.ToString() + ".bin"), data);
        Console.WriteLine("texture " + id + " " + state + " " + (data?.Length ?? 0) + " bytes");
        if (state != "Finished")
            failures++;
    }
}
finally
{
    client.Network.Logout();
    Console.WriteLine("logged out");
}
return failures == 0 ? 0 : 1;

static int Usage()
{
    Console.Error.WriteLine("usage: oar-texfetch --login-uri <uri> --first <name> --last <name> --region <name> --out <folder> <texture id>...  (password in OAR_CHECK_PASSWORD)");
    return 2;
}
