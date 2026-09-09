using InWorldz.Phlox.Glue;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// PHLOX-3a. A script in a prim on Ebony crashes the compiler.
///
/// <para>
/// Every region start under 1.1.287 logged
/// <c>ERROR [PhloxCompile]: 4e51f068-...: Object reference not set to an instance of an object.</c>
/// followed by <c>ERROR [PhloxLoader]: Compilation failed for 1ee3b9b1-...</c> — at 04:37:36 and again
/// at 04:56:15, so it reproduces from the stored asset rather than from anything about that start.
/// </para>
///
/// <para>
/// The script is <c>Fixtures/lmap4.lsl</c>, a four-state lamp from a Legion Grid resident's prim. There is
/// nothing exotic in it, which is the point: whatever the compiler trips over is something ordinary.
/// </para>
///
/// <para>
/// <b>These tests call <see cref="CompilerFrontend"/> directly rather than through
/// <see cref="PhloxCompiler"/>.</b> That helper catches the exception and files it as an error message,
/// which is right for tests about what the compiler <i>says</i> — but it would hide the very thing this
/// file exists to show. A crash must arrive here as a crash, with its stack.
/// </para>
/// </summary>
public class CompilerCrashTests
{
    private readonly ITestOutputHelper _out;
    public CompilerCrashTests(ITestOutputHelper o) => _out = o;

    private static string FixturePath(string name) => Path.Combine(
        Path.GetDirectoryName(typeof(CompilerCrashTests).Assembly.Location)!, "Fixtures", name);

    private static string Lmap4 => File.ReadAllText(FixturePath("lmap4.lsl"));

    /// <summary>
    /// The whole requirement, and it is deliberately loose about which way it is met: a compiler faced
    /// with a script either compiles it or explains what is wrong with it **and where**. What it may not
    /// do is throw. If the construct turns out to be invalid LSL, this test still passes — as long as the
    /// owner is told a line number instead of being handed a null dereference.
    /// </summary>
    [Fact]
    public void TheLampScriptEitherCompilesOrFailsWithALineNumber()
    {
        var listener = new PhloxCompiler();
        var frontend = new CompilerFrontend(listener, templatePath: null);

        // No try/catch: a NullReferenceException out of here IS the defect, and the stack trace xunit
        // prints is the finding.
        var compiled = frontend.Compile(Lmap4);

        if (!listener.HasErrors())
        {
            Assert.NotNull(compiled);
            return;
        }

        _out.WriteLine(listener.Report);
        Assert.All(listener.Errors, e => Assert.Matches(@"\d+:\d+|line \d+", e));
    }

    /// <summary>
    /// The construct on its own, reduced from the fixture: four states where the last one returns to
    /// <c>default</c>. If the fixture crashes and this does not, the cause is elsewhere in the script and
    /// this test says so by passing.
    /// </summary>
    [Fact]
    public void ReturningToTheDefaultStateFromAnotherStateCompiles()
    {
        const string src = @"
default
{
    touch_start(integer n) { state onHigh; }
}

state onHigh
{
    touch_start(integer n) { state default; }
}
";
        var listener = new PhloxCompiler();
        var frontend = new CompilerFrontend(listener, templatePath: null);
        var compiled = frontend.Compile(src);

        Assert.False(listener.HasErrors(), listener.Report);
        Assert.NotNull(compiled);
    }
}
