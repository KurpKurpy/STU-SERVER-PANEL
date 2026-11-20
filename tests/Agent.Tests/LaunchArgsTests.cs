using Contracts;

namespace Agent.Tests;

public class LaunchArgsTests
{
    [Theory, InlineData(""), InlineData("-autoInit"), InlineData("-maxMem=8192 -cpuCount=4"), InlineData("-LOADMISSIONTOMEMORY -enableHT")]
    public void Validate_accepts_allowlisted_args(string text) => Assert.Null(Arma3LaunchArgs.Validate(Arma3LaunchArgs.Parse(text)));

    [Theory, InlineData(@"-profiles=C:\x"), InlineData("-config=x.cfg"), InlineData("-mod=@cba"), InlineData("-serverMod=x"), InlineData("-bepath=x"),
     InlineData("-port=2302"), InlineData(@"-maxMem=..\x"), InlineData(@"-malloc=C:\evil.dll"), InlineData("-autoInit=1"), InlineData("cmd.exe"), InlineData("-unknownThing")]
    public void Validate_rejects_paths_unknown_and_generated_args(string text) => Assert.NotNull(Arma3LaunchArgs.Validate(Arma3LaunchArgs.Parse(text)));

    [Fact]
    public void Validate_message_names_the_bad_arg() => Assert.Contains("-mod=@cba", Arma3LaunchArgs.Validate(Arma3LaunchArgs.Parse("-autoInit -mod=@cba")));
}
