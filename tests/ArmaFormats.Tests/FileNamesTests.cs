using ArmaFormats;

namespace ArmaFormats.Tests;

// Phase 4b Ruling 3: one name rule for every path the file manager touches.
public class FileNamesTests
{
    [Theory, InlineData("CON"), InlineData("nul.txt"), InlineData(@"BattlEye\COM1.pbo"), InlineData("lpt9"), InlineData("Aux.cfg")]
    public void Reserved_names_are_refused(string path) => Assert.NotNull(FileNames.Problem(path));

    [Theory, InlineData("bans."), InlineData("bans.txt "), InlineData(@"folder.\x.txt")]
    public void Trailing_dot_or_space_is_refused(string path) => Assert.NotNull(FileNames.Problem(path));

    [Theory, InlineData("a:b"), InlineData("a|b"), InlineData("a\tb"), InlineData("a<b"), InlineData("a?b"), InlineData("a*b"), InlineData("a\"b"), InlineData(@"C:\x")]
    public void Streams_and_bad_characters_are_refused(string path) => Assert.NotNull(FileNames.Problem(path));

    [Theory, InlineData(@"..\x"), InlineData(@"a\..\b"), InlineData(@".\a"), InlineData(@"\a"), InlineData("/a"), InlineData(@"a\\b"), InlineData(@"\\host\share")]
    public void Dot_segments_rooted_and_empty_segments_are_refused(string path) => Assert.NotNull(FileNames.Problem(path));

    [Fact]
    public void Long_paths_are_refused()
    {
        Assert.NotNull(FileNames.Problem(new string('a', 201)));
        Assert.NotNull(FileNames.Problem(string.Join('\\', Enumerable.Repeat(new string('a', 40), 6)) + @"\x")); // 6 × 41 + 1 = 247 > 240
        Assert.Null(FileNames.Problem(new string('a', 200)));
    }

    [Theory, InlineData(".13c-tmp"), InlineData(@"x\.13C-TMP\y")]
    public void The_temp_folder_name_is_refused(string path) => Assert.NotNull(FileNames.Problem(path));

    [Theory, InlineData("ace_3.21.2.113.bikey"), InlineData("helitest.VR.pbo"), InlineData(@"main\BattlEye\bans.txt"), InlineData("arma%203%20cinematic.altis.pbo"),
     InlineData("console.rpt"), InlineData("@CBA_A3"), InlineData("console.cfg"), InlineData("COM10"), InlineData("")]
    public void Ordinary_names_pass(string path) => Assert.Null(FileNames.Problem(path));

    [Fact]
    public void Forward_slashes_count_as_separators() => Assert.NotNull(FileNames.Problem("a/../b"));
}
