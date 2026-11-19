/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

#nullable disable

using System.Reflection;
using OpenSim.Region.ScriptEngine.Shared.Api;
using OpenSim.Region.ScriptEngine.Shared.ScriptBase;

namespace OpenSim.Region.ScriptEngine.Shared.Tests;

/// <summary>
/// The constants a script uses with the game_control event and llRequestPermissions. The values are Linden Lab's
/// (secondlife/lsl-definitions, lsl_definitions.yaml): a script written for Second Life must see the same numbers.
/// </summary>
public class GameControlConstantsTests
{
    [Fact]
    public void PermissionGameControlIsTheSecondLifeBit()
    {
        FieldInfo field = typeof(ScriptBaseClass).GetField("PERMISSION_GAME_CONTROL", BindingFlags.Public | BindingFlags.Static);
        Assert.True(field is not null, "PERMISSION_GAME_CONTROL is not defined for scripts");
        Assert.Equal(0x100000, (int)field.GetRawConstantValue());
    }

    [Theory]
    [InlineData("GAME_CONTROL_AXIS_LEFTX", 0)]
    [InlineData("GAME_CONTROL_AXIS_STRAFE", 0)]
    [InlineData("GAME_CONTROL_AXIS_TRUCK", 0)]
    [InlineData("GAME_CONTROL_AXIS_ADVANCE", 1)]
    [InlineData("GAME_CONTROL_AXIS_DOLLY", 1)]
    [InlineData("GAME_CONTROL_AXIS_LEFTY", 1)]
    [InlineData("GAME_CONTROL_AXIS_PAN", 2)]
    [InlineData("GAME_CONTROL_AXIS_RIGHTX", 2)]
    [InlineData("GAME_CONTROL_AXIS_TURN", 2)]
    [InlineData("GAME_CONTROL_AXIS_LOOK", 3)]
    [InlineData("GAME_CONTROL_AXIS_RIGHTY", 3)]
    [InlineData("GAME_CONTROL_AXIS_TILT", 3)]
    [InlineData("GAME_CONTROL_AXIS_BOOM", 4)]
    [InlineData("GAME_CONTROL_AXIS_LEFT_TRIGGER", 4)]
    [InlineData("GAME_CONTROL_AXIS_RISE", 4)]
    [InlineData("GAME_CONTROL_AXIS_TRIGGERLEFT", 4)]
    [InlineData("GAME_CONTROL_AXIS_RIGHT_TRIGGER", 5)]
    [InlineData("GAME_CONTROL_AXIS_ROLL", 5)]
    [InlineData("GAME_CONTROL_AXIS_TRIGGERRIGHT", 5)]
    [InlineData("GAME_CONTROL_AXIS_ZOOM", 6)]
    [InlineData("GAME_CONTROL_BUTTON_A", 1)]
    [InlineData("GAME_CONTROL_BUTTON_JUMP", 1)]
    [InlineData("GAME_CONTROL_BUTTON_SOUTH", 1)]
    [InlineData("GAME_CONTROL_BUTTON_ZOOM_OUT", 1)]
    [InlineData("GAME_CONTROL_BUTTON_B", 2)]
    [InlineData("GAME_CONTROL_BUTTON_CROUCH", 2)]
    [InlineData("GAME_CONTROL_BUTTON_EAST", 2)]
    [InlineData("GAME_CONTROL_BUTTON_PAN_RIGHT", 2)]
    [InlineData("GAME_CONTROL_BUTTON_PAN_LEFT", 4)]
    [InlineData("GAME_CONTROL_BUTTON_SIT", 4)]
    [InlineData("GAME_CONTROL_BUTTON_WEST", 4)]
    [InlineData("GAME_CONTROL_BUTTON_X", 4)]
    [InlineData("GAME_CONTROL_BUTTON_NORTH", 8)]
    [InlineData("GAME_CONTROL_BUTTON_Y", 8)]
    [InlineData("GAME_CONTROL_BUTTON_ZOOM_IN", 8)]
    [InlineData("GAME_CONTROL_BUTTON_BACK", 16)]
    [InlineData("GAME_CONTROL_BUTTON_SELECT", 16)]
    [InlineData("GAME_CONTROL_BUTTON_TOGGLE_ALT_ZOOM", 16)]
    [InlineData("GAME_CONTROL_BUTTON_TOGGLE_CURSOR", 16)]
    [InlineData("GAME_CONTROL_BUTTON_GUIDE", 32)]
    [InlineData("GAME_CONTROL_BUTTON_HOME", 32)]
    [InlineData("GAME_CONTROL_BUTTON_TOGGLE_SPEAK", 32)]
    [InlineData("GAME_CONTROL_BUTTON_START", 64)]
    [InlineData("GAME_CONTROL_BUTTON_TOGGLE_FOLLOW", 64)]
    [InlineData("GAME_CONTROL_BUTTON_TOGGLE_MOUSELOOK", 64)]
    [InlineData("GAME_CONTROL_BUTTON_LEFTSTICK", 128)]
    [InlineData("GAME_CONTROL_BUTTON_RESET", 128)]
    [InlineData("GAME_CONTROL_BUTTON_RIGHTSTICK", 256)]
    [InlineData("GAME_CONTROL_BUTTON_TOGGLE_FLYCAM", 256)]
    [InlineData("GAME_CONTROL_BUTTON_CURSOR_BUTTON_LEFT", 512)]
    [InlineData("GAME_CONTROL_BUTTON_LEFTSHOULDER", 512)]
    [InlineData("GAME_CONTROL_BUTTON_ROLL_CCW", 512)]
    [InlineData("GAME_CONTROL_BUTTON_CURSOR_BUTTON_RIGHT", 1024)]
    [InlineData("GAME_CONTROL_BUTTON_RIGHTSHOULDER", 1024)]
    [InlineData("GAME_CONTROL_BUTTON_ROLL_CW", 1024)]
    [InlineData("GAME_CONTROL_BUTTON_ADVANCE_FORWARD", 2048)]
    [InlineData("GAME_CONTROL_BUTTON_DOLLY_FORWARD", 2048)]
    [InlineData("GAME_CONTROL_BUTTON_DPAD_UP", 2048)]
    [InlineData("GAME_CONTROL_BUTTON_ADVANCE_BACK", 4096)]
    [InlineData("GAME_CONTROL_BUTTON_DOLLY_BACK", 4096)]
    [InlineData("GAME_CONTROL_BUTTON_DPAD_DOWN", 4096)]
    [InlineData("GAME_CONTROL_BUTTON_DPAD_LEFT", 8192)]
    [InlineData("GAME_CONTROL_BUTTON_STRAFE_LEFT", 8192)]
    [InlineData("GAME_CONTROL_BUTTON_TRUCK_LEFT", 8192)]
    [InlineData("GAME_CONTROL_BUTTON_DPAD_RIGHT", 16384)]
    [InlineData("GAME_CONTROL_BUTTON_STRAFE_RIGHT", 16384)]
    [InlineData("GAME_CONTROL_BUTTON_TRUCK_RIGHT", 16384)]
    [InlineData("GAME_CONTROL_BUTTON_MISC1", 32768)]
    [InlineData("GAME_CONTROL_BUTTON_PADDLE1", 65536)]
    [InlineData("GAME_CONTROL_BUTTON_PADDLE2", 131072)]
    [InlineData("GAME_CONTROL_BUTTON_PADDLE3", 262144)]
    [InlineData("GAME_CONTROL_BUTTON_PADDLE4", 524288)]
    [InlineData("GAME_CONTROL_BUTTON_TOUCHPAD", 1048576)]
    [InlineData("GAME_CONTROL_MODE_AVATAR", 0)]
    [InlineData("GAME_CONTROL_MODE_MOUSELOOK", 1)]
    [InlineData("GAME_CONTROL_MODE_FLYCAM", 2)]
    [InlineData("GAME_CONTROL_MODE_CAPTIVE", 3)]
    [InlineData("GAME_CONTROL_MODE_CURSOR", 4)]
    public void ConstantHasTheSecondLifeValue(string name, int expected)
    {
        FieldInfo field = typeof(ScriptBaseClass).GetField(name, BindingFlags.Public | BindingFlags.Static);
        Assert.True(field is not null, name + " is not defined for scripts");
        Assert.Equal(expected, (int)field.GetRawConstantValue());
    }

    [Fact]
    public void NoGameControlConstantIsMissingFromTheTable()
    {
        // Every GAME_CONTROL_* constant defined must be one the table above checks (78 of them).
        int defined = typeof(ScriptBaseClass)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Count(f => f.Name.StartsWith("GAME_CONTROL_", StringComparison.Ordinal));
        Assert.Equal(78, defined);
    }
}
