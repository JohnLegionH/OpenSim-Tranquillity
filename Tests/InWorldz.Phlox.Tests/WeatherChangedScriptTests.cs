/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.CoreModules.World.Wind;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// The weather journey with real scripts, in Phlox (iwSetWeather, iwGetWeather) and in YEngine (osSetWeather,
/// osGetWeather): an estate manager's script sets the weather; a script in another object, owned by someone else, hears
/// changed(CHANGED_WEATHER) once and reads the new weather; setting the same weather again is heard by nobody; a script
/// owned by anyone else is refused with WEATHER_ERR_NOT_ALLOWED and nothing is heard; with [Weather] off every set is
/// refused with WEATHER_ERR_DISABLED, the read is empty and nothing is heard.
/// The changes reach the scripts as they do in a region: the weather state raises its change, the wind module's
/// delivery (WindModule.TellScriptsWeatherChanged) raises the scene's script changed event, and the engine posts it.
/// </summary>
[Collection("phlox-yengine")]
public class WeatherChangedScriptTests
{
    private const string Phlox = "Phlox", YEngine = "YEngine";
    private const int Command = 7;

    private static readonly UUID EstateOwner = UUID.Random();

    private static string Set(string engine) => engine == Phlox ? "iwSetWeather" : "osSetWeather";
    private static string Get(string engine) => engine == Phlox ? "iwGetWeather" : "osGetWeather";

    // Listens for commands on channel 7: "set <tag>" sets storm 0.75, "same <tag>" sets it again, "get <tag>" reads.
    // Says "<tag> set <result>", "<tag> got <csv>". Hears changed(CHANGED_WEATHER) as "<tag> heard <bits> <csv>".
    private static string Script(string engine, string tag) => @"
        default {
            state_entry() { llListen(" + Command + @", """", NULL_KEY, """"); llSay(0, """ + tag + @" ready""); }
            listen(integer c, string n, key k, string m) {
                if (m == ""set " + tag + @""" || m == ""same " + tag + @""")
                    llSay(0, """ + tag + @" set "" + (string)" + Set(engine) + @"(""storm"", 0.75, 0.0));
                else if (m == ""get " + tag + @""")
                    llSay(0, """ + tag + @" got "" + llList2CSV(" + Get(engine) + @"()));
            }
            changed(integer c) {
                if (c & CHANGED_WEATHER)
                    llSay(0, """ + tag + @" heard "" + (string)c + "" "" + llList2CSV(" + Get(engine) + @"()));
            }
        }";

    private sealed class Rig : IDisposable
    {
        public readonly SchedulerHarness H;
        public readonly IWeatherState Weather;
        public readonly string Engine;

        public Rig(string engine, bool weatherOn)
        {
            Engine = engine;
            H = new SchedulerHarness(cfg =>
            {
                var ossl = cfg.AddConfig("OSSL");
                ossl.Set("AllowOSFunctions", "true");
                ossl.Set("OSFunctionThreatLevel", "Severe");
            }, withYEngine: engine == YEngine);
            // Both checks are registered: with no handler the scene's permission checks answer yes, and a region always
            // has a permissions module.
            H.Scene.Permissions.OnIssueEstateCommand += (user, ownerCommand) => user == EstateOwner;
            H.Scene.Permissions.OnIsAdministrator += user => false;

            var proxy = DispatchProxy.Create<IWindModule, WeatherFunctionTests.WeatherWind>();
            var wind = (WeatherFunctionTests.WeatherWind)(object)proxy;
            Weather = wind.Weather = new WeatherState(weatherOn, new WeatherLimits { MinChangeIntervalSeconds = 0f });
            // As the wind module does in a region (WindModule.AddRegion).
            Weather.WeatherChanged += _ => WindModule.TellScriptsWeatherChanged(H.Scene);
            H.Scene.RegisterModuleInterface<IWindModule>(proxy);
        }

        public void Rez(string tag, UUID owner)
        {
            SceneObjectPart part = SceneHelpers.AddSceneObject(H.Scene, tag, owner).RootPart;
            string source = Script(Engine, tag);
            if (Engine == Phlox)
            {
                H.RezScriptInto(part, source);
            }
            else
            {
                var item = TaskInventoryHelpers.AddScript(H.Scene.AssetService, part, UUID.Random(), UUID.Random(), tag, source);
                Assert.True(part.Inventory.CreateScriptInstance(item.ItemID, 0, false, H.YEngine.ScriptEngineName, 1));
                var errors = H.YEngine.GetScriptErrors(item.ItemID);
                Assert.True(errors.Count == 0, "YEngine did not compile it: " + string.Join(" | ", errors.Cast<object>()));
                part.ParentGroup.ResumeScripts();
            }
            Assert.True(PumpUntil(() => Count(tag + " ready") == 1), "not started: " + Said);
        }

        // Phlox's listens hear world chat; YEngine's are carried by the core WorldComm module (CrossEngineChatTests).
        public void Say(string msg)
        {
            if (Engine == Phlox)
                H.Scene.SimChat(msg, ChatTypeEnum.Region, Command, new Vector3(128, 128, 25), "tester", UUID.Random(), false);
            else
                H.WorldComm.DeliverMessage(ChatTypeEnum.Region, Command, "tester", UUID.Random(), msg);
        }

        public int Count(string line) => H.Said.Count(s => s == line);
        public int CountStart(string prefix) => H.Said.Count(s => s.StartsWith(prefix, StringComparison.Ordinal));
        public string Said => string.Join(" | ", H.Said);

        public bool PumpUntil(Func<bool> done, int seconds = 30)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(seconds);
            while (!done())
            {
                if (DateTime.UtcNow >= until) return false;
                if (!H.PumpOnceBusy()) Thread.Sleep(2);
            }
            return true;
        }

        /// <summary>A "did NOT happen" window: nothing new may start with the prefix while the engines run.</summary>
        public void Quiet(int ms, string prefix)
        {
            int before = CountStart(prefix);
            var until = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < until) { if (!H.PumpOnceBusy()) Thread.Sleep(2); }
            Assert.True(before == CountStart(prefix), "'" + prefix + "' was said in the quiet window: " + Said);
        }

        /// <summary>Sends a command and waits for the script's answer line, which it returns.</summary>
        public string Ask(string command, string answerPrefix)
        {
            int before = CountStart(answerPrefix);
            Say(command);
            Assert.True(PumpUntil(() => CountStart(answerPrefix) > before), "no answer to '" + command + "': " + Said);
            return H.Said.Last(s => s.StartsWith(answerPrefix, StringComparison.Ordinal));
        }

        public void Dispose() => H.Dispose();
    }

    [Theory]
    [InlineData(Phlox)]
    [InlineData(YEngine)]
    public void TheEstatesSetIsHeardOnceByEveryListenerAndARepeatIsNotHeard(string engine)
    {
        using var rig = new Rig(engine, weatherOn: true);
        rig.Rez("E", EstateOwner);           // the estate manager's controller
        rig.Rez("L", UUID.Random());         // a listener owned by someone else

        Assert.Equal("E set 1", rig.Ask("set E", "E set "));   // WEATHER_SET

        Assert.True(rig.PumpUntil(() => rig.CountStart("L heard ") == 1 && rig.CountStart("E heard ") == 1), "not heard: " + rig.Said);
        string heard = rig.H.Said.Single(s => s.StartsWith("L heard ", StringComparison.Ordinal));
        Assert.StartsWith("L heard 65536 storm, 0.75", heard);
        Assert.EndsWith(", 1", heard);                          // serial 1
        Assert.Equal(1, rig.Weather.Current.Serial);

        // The same weather again: WEATHER_UNCHANGED, and nobody hears anything.
        Assert.Equal("E set 0", rig.Ask("same E", "E set "));
        rig.Quiet(800, "L heard ");
        Assert.Equal(1, rig.CountStart("L heard "));
        Assert.Equal(1, rig.CountStart("E heard "));
    }

    [Theory]
    [InlineData(Phlox)]
    [InlineData(YEngine)]
    public void AScriptOfAnyoneElseIsRefusedAndNothingIsHeard(string engine)
    {
        using var rig = new Rig(engine, weatherOn: true);
        rig.Rez("G", UUID.Random());         // not the estate
        rig.Rez("L", UUID.Random());

        Assert.Equal("G set -2", rig.Ask("set G", "G set "));   // WEATHER_ERR_NOT_ALLOWED
        rig.Quiet(800, "L heard ");
        Assert.Equal(0, rig.CountStart("L heard "));
        Assert.Same(WeatherSnapshot.Initial, rig.Weather.Current);

        // Anyone may read; the weather is still the region's starting weather.
        Assert.Equal("L got , 0.000000, 0, 0", rig.Ask("get L", "L got "));
    }

    [Theory]
    [InlineData(Phlox)]
    [InlineData(YEngine)]
    public void WithWeatherOffTheEstateIsRefusedTheReadIsEmptyAndNothingIsHeard(string engine)
    {
        using var rig = new Rig(engine, weatherOn: false);
        rig.Rez("E", EstateOwner);
        rig.Rez("L", UUID.Random());

        Assert.Equal("E set -1", rig.Ask("set E", "E set "));   // WEATHER_ERR_DISABLED
        rig.Quiet(800, "L heard ");
        Assert.Equal(0, rig.CountStart("L heard "));
        Assert.Equal("L got ", rig.Ask("get L", "L got "));
    }
}
