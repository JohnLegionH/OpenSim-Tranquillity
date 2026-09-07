using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-2. Built-in (<c>ll*</c> / <c>os*</c>) functions that OSSL overloads must resolve by
/// name **and** signature, as they do on every other engine that offers them.
///
/// <para>
/// The live case is `9898c41e-8e85-45ed-9235-be1cc6176936` on Ebony, which failed at load on
/// 2026-09-07 with <i>"Function 'osTeleportAgent' expects 4 arguments, got 3"</i> at lines
/// 16:12 and 20:12. Its call is the 3-argument local-teleport overload, which
/// <c>OSSL_Api.cs:1051</c> implements and Phlox's table does not carry
/// (<c>InWorldz.Phlox/Types/Defaults.cs:4733</c> holds only the 4-argument form).
/// </para>
///
/// <para>
/// <b>Scope, ruled:</b> this is about BUILT-IN overloads only. User-function overloading stays
/// rejected — SL rejects it, so Phlox's rule is the parity rule. <see cref="UserFunctionOverloadTests"/>
/// pins that, with the airship script as the example.
/// </para>
/// </summary>
public class BuiltinOverloadTests
{
    // ------------------------------------------------------------------ osTeleportAgent

    [Fact(Skip = "PHLOX-2 part 2 is NOT implemented. Phlox resolves calls by name alone; built-in overloads need name+signature resolution across Defaults.cs, SymbolTable, TypesVisitor and BytecodeGenerator. Remove this Skip when that lands - the test is the spec.")]
    public void The3ArgLocalTeleportOverloadCompiles()
    {
        // Verbatim from the failing script (asset 01d4448d-087e-49fb-9253-4b06ce522811, line 16).
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        vector opos = llGetPos();
        osTeleportAgent(id, opos + <0,0,-3>, <0,1,0>);");

        Assert.False(c.HasErrors(), $"osTeleportAgent(key, vector, vector) is OSSL_Api.cs:1051: {c.Report}");
    }

    [Fact(Skip = "PHLOX-2 part 2 is NOT implemented. Phlox resolves calls by name alone; built-in overloads need name+signature resolution across Defaults.cs, SymbolTable, TypesVisitor and BytecodeGenerator. Remove this Skip when that lands - the test is the spec.")]
    public void The5ArgGridCoordinateOverloadCompiles()
    {
        // OSSL_Api.cs:1015 - osTeleportAgent(string agent, int regionX, int regionY, vector, vector)
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, 1000, 1000, <128,128,25>, <0,1,0>);");

        Assert.False(c.HasErrors(), $"osTeleportAgent(key, int, int, vector, vector) is OSSL_Api.cs:1015: {c.Report}");
    }

    [Fact]
    public void The4ArgRegionNameOverloadStillCompiles()
    {
        // The one Phlox already had. It must survive the change that adds the other two.
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, ""Ebony"", <128,128,25>, <0,1,0>);");

        Assert.False(c.HasErrors(), $"the 4-argument form is the one Phlox has always had: {c.Report}");
    }

    // ------------------------------------------------------------------ llLinkPlaySound

    [Fact(Skip = "PHLOX-2 part 2 is NOT implemented. Phlox resolves calls by name alone; built-in overloads need name+signature resolution across Defaults.cs, SymbolTable, TypesVisitor and BytecodeGenerator. Remove this Skip when that lands - the test is the spec.")]
    public void LlLinkPlaySoundBothFormsCompile()
    {
        var three = PhloxCompiler.CompileInDefault(@"llLinkPlaySound(LINK_THIS, ""snd"", 1.0);");
        Assert.False(three.HasErrors(), $"llLinkPlaySound(int, string, float): {three.Report}");

        var four = PhloxCompiler.CompileInDefault(@"llLinkPlaySound(LINK_THIS, ""snd"", 1.0, 0);");
        Assert.False(four.HasErrors(), $"llLinkPlaySound(int, string, float, int): {four.Report}");
    }

    // ------------------------------------------------------------------ still rejected, and helpfully

    [Fact]
    public void AnArityThatMatchesNoOverloadIsStillRejected()
    {
        // Two arguments is none of the three forms. Overload resolution must not become
        // "accept anything called osTeleportAgent".
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, <0,1,0>);");

        Assert.True(c.HasErrors(), "a 2-argument osTeleportAgent matches no overload and must fail");
    }

    [Fact(Skip = "PHLOX-2 part 2 is NOT implemented. Phlox resolves calls by name alone; built-in overloads need name+signature resolution across Defaults.cs, SymbolTable, TypesVisitor and BytecodeGenerator. Remove this Skip when that lands - the test is the spec.")]
    public void TheRejectionListsTheAcceptedSignatures()
    {
        // The message that sent this session looking at the wrong thing said "expects 4
        // arguments, got 3" - true of one overload and misleading about the function. With
        // several accepted forms the error has to say what they are.
        var c = PhloxCompiler.CompileInDefault(@"
        key id = llDetectedKey(0);
        osTeleportAgent(id, <0,1,0>);");

        Assert.True(c.HasErrors());
        var joined = string.Join(" | ", c.Errors);
        Assert.Contains("osTeleportAgent", joined);
        Assert.True(
            joined.Contains("accepts") || joined.Contains("overload") || joined.Contains("signature"),
            $"the error should name the accepted signatures, not one arity: {joined}");
    }
}

/// <summary>
/// PHLOX-2, the other half of the scope ruling: <b>user-function overloading stays rejected.</b>
/// SL has no user-function overloading, so rejecting it is the parity behaviour and Phlox's
/// existing rule is correct. YEngine accepts it (its symbol table is keyed by name plus
/// signature, <c>MMRScriptVarDict.cs:132-151</c>), which makes the two engines in this tree
/// disagree; this test records that Phlox does not follow YEngine here, deliberately.
/// </summary>
public class UserFunctionOverloadTests
{
    [Fact]
    public void TwoUserFunctionsOfTheSameNameAreRejected()
    {
        // Reduced from the airship, asset b8079466-322a-47f5-ba8d-cd17d9e0da61, lines 92-96:
        //     SetVehicleSettings()          { SetVehicleSettings(""); }
        //     SetVehicleSettings(string f)  { ... }
        var c = PhloxCompiler.Compile(@"
SetVehicleSettings()
{
    SetVehicleSettings("""");
}
SetVehicleSettings(string filter)
{
    llSay(0, filter);
}
default
{
    state_entry()
    {
        SetVehicleSettings();
    }
}
");

        Assert.True(c.HasErrors(),
            "SL has no user-function overloading; Phlox rejecting it is the parity rule, not a defect");
        Assert.Contains("SetVehicleSettings", string.Join(" | ", c.Errors));
    }
}
