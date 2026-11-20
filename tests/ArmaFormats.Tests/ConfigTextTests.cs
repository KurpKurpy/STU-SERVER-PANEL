using ArmaFormats;

namespace ArmaFormats.Tests;

public class ConfigTextTests
{
    [Fact]
    public void Parses_assignments_arrays_and_classes()
    {
        var doc = ConfigDocument.Parse("hostname = \"A \"\"B\"\"\";\nmaxPlayers = 40;\nmotd[] = {\"x\", \"y\"};\nclass Missions { class M1 { template = \"a.Altis\"; }; };\n");
        var items = doc.Items.Where(i => i is not CfgTrivia).ToList();

        Assert.Equal(4, items.Count);
        var host = Assert.IsType<CfgAssignment>(items[0]);
        Assert.Equal("hostname", host.Name);
        Assert.Equal(new CfgString("A \"B\""), host.Value);
        Assert.Equal(1, host.Line);
        var max = Assert.IsType<CfgAssignment>(items[1]);
        Assert.Equal(40, Assert.IsType<CfgNumber>(max.Value).Value);
        var motd = Assert.IsType<CfgAssignment>(items[2]);
        Assert.True(motd.IsArray);
        Assert.Equal(2, Assert.IsType<CfgArray>(motd.Value).Items.Count);
        var missions = Assert.IsType<CfgClass>(items[3]);
        Assert.Equal("Missions", missions.Name);
        Assert.Equal(4, missions.Line);
        var m1 = Assert.IsType<CfgClass>(Assert.Single(missions.Body!, i => i is not CfgTrivia));
        var template = Assert.IsType<CfgAssignment>(Assert.Single(m1.Body!, i => i is not CfgTrivia));
        Assert.Equal(new CfgString("a.Altis"), template.Value);
    }

    [Fact]
    public void Unknown_items_are_written_back_byte_for_byte()
    {
        const string text = "/* block\r\n comment */\r\n#include \"x.hpp\"\r\n\thostname\t=  \"S\" ;  // trailing\r\n"
            + "kickTimeout[] = { {0, -1}, {1, 180} };\r\nx[] += {1};\r\n\r\nclass AdvancedOptions : Base\r\n{\r\n\tlogObjectNotFound = false;\r\n};\r\nclass Fwd;\r\nlast = 1;";
        var doc = ConfigDocument.Parse(text);

        Assert.Equal(text, string.Concat(doc.Items.Select(doc.SourceOf)));
        var append = doc.Items.OfType<CfgAssignment>().Single(a => a.Name == "x");
        Assert.True(append.Append);
        var cls = doc.Items.OfType<CfgClass>().First();
        Assert.Equal("Base", cls.Base);
        Assert.Null(doc.Items.OfType<CfgClass>().Single(c => c.Name == "Fwd").Body);
    }

    [Fact]
    public void Comments_inside_arrays_are_skipped()
    {
        var doc = ConfigDocument.Parse("disableChannels[] =\n{\n\t{\n\t\t0,\t// channel ID\n\t\tfalse\t/* text */\n\t},\n\t{ 3, true, true }\n};\n");
        var value = Assert.IsType<CfgArray>(doc.Items.OfType<CfgAssignment>().Single().Value);
        Assert.Equal(2, value.Items.Count);
        Assert.Equal(new CfgWord("false"), Assert.IsType<CfgArray>(value.Items[0]).Items[1]);
    }

    [Fact]
    public void Format_escapes_quotes_and_writes_arrays()
    {
        Assert.Equal("\"a \"\"b\"\"\"", ConfigText.Format(new CfgString("a \"b\"")));
        Assert.Equal("{\"x\", 2, {true}}", ConfigText.Format(new CfgArray([new CfgString("x"), new CfgNumber(2, ""), new CfgArray([new CfgWord("true")])])));
        Assert.Equal("10000000000", ConfigText.Format(new CfgNumber(10000000000, "")));
        Assert.Equal("0.001", ConfigText.Format(new CfgNumber(0.001, "")));
        Assert.Equal("motd[] = {\"a\"};", ConfigText.Assignment("motd", new CfgArray([new CfgString("a")])));
        Assert.Equal("hostname = \"x\";", ConfigText.Assignment("hostname", new CfgString("x")));
        Assert.Equal("skillAI=0.5;", ConfigText.Assignment("skillAI", new CfgNumber(0.5, ""), compact: true));
    }

    [Fact]
    public void ReplaceClass_replaces_only_that_class()
    {
        const string text = "version=1;\r\nclass DifficultyPresets { a=1; };\r\nblood=1;\r\n";
        var replaced = ConfigDocument.Parse(text).ReplaceClass("difficultypresets", "class DifficultyPresets\r\n{\r\n\tb=2;\r\n};\r\n");
        Assert.Equal("version=1;\r\nclass DifficultyPresets\r\n{\r\n\tb=2;\r\n};\r\nblood=1;\r\n", replaced);

        var appended = ConfigDocument.Parse("version=1;\r\nblood=1;").ReplaceClass("DifficultyPresets", "class DifficultyPresets {};");
        Assert.Equal("version=1;\r\nblood=1;\r\nclass DifficultyPresets {};\r\n", appended);
        Assert.Equal("class DifficultyPresets {};\r\n", ConfigDocument.Parse("").ReplaceClass("DifficultyPresets", "class DifficultyPresets {};"));
    }

    [Fact]
    public void Syntax_errors_name_the_line()
    {
        var unclosed = Assert.Throws<ConfigSyntaxException>(() => ConfigDocument.Parse("a = 1;\nb = 2;\nclass X {\n c = 3;\n"));
        Assert.Equal(3, unclosed.Line);
        var missing = Assert.Throws<ConfigSyntaxException>(() => ConfigDocument.Parse("hostname = \"x\"\nmaxPlayers = 4;\n"));
        Assert.Equal(1, missing.Line);
        var unquoted = Assert.Throws<ConfigSyntaxException>(() => ConfigDocument.Parse("a = 1;\nmaxPlayers = 4\nfoo = 5;\n"));
        Assert.Equal(2, unquoted.Line);
        Assert.StartsWith("Line 1: ", Assert.Throws<ConfigSyntaxException>(() => ConfigDocument.Parse("hostname = \"x")).Message);
        // Arma refuses an upper-case Class keyword, so the parser does too.
        Assert.Throws<ConfigSyntaxException>(() => ConfigDocument.Parse("Class Missions {};"));
    }

    [Fact]
    public void Deep_nesting_is_a_syntax_error_not_a_crash()
    {
        // Review C2: unbounded recursion would overflow the stack, which .NET can't catch.
        Assert.Throws<ConfigSyntaxException>(() => ConfigDocument.Parse("x[] = " + new string('{', 100_000)));
        Assert.Throws<ConfigSyntaxException>(() => ConfigDocument.Parse(string.Concat(Enumerable.Repeat("class a {", 100_000))));
        ConfigDocument.Parse("a[] = {{{1}}};\r\nclass A { class B { class C { x = 1; }; }; };"); // ordinary nesting still parses
    }

    [Fact]
    public void Both_line_endings_parse()
    {
        var lf = ConfigDocument.Parse("a = 1;\nclass B\n{\n\tc = \"d\";\n};\n");
        var crlf = ConfigDocument.Parse("a = 1;\r\nclass B\r\n{\r\n\tc = \"d\";\r\n};\r\n");
        Assert.Equal(lf.Items.OfType<CfgClass>().Single().Line, crlf.Items.OfType<CfgClass>().Single().Line);
        Assert.Equal(2, crlf.Items.OfType<CfgClass>().Single().Line);
    }

    [Fact]
    public void Eden_multiline_strings_join_with_line_breaks()
    {
        // Eden's text mission.sqm writes a multi-line string as pieces joined by a bare \n token (a backslash and an n).
        var doc = ConfigDocument.Parse("onActivation=\"if (a) then { \" \\n \" b;\" \\n \"};\";\r\nnext=1;\r\n");

        var value = (CfgString)((CfgAssignment)doc.Items.First(i => i is CfgAssignment)).Value;

        Assert.Equal("if (a) then { \n b;\n};", value.Value);
        Assert.Equal("\"a\" \\n \"b\"", ConfigText.Format(new CfgString("a\nb")));
        Assert.Equal("a\nb", ((CfgString)((CfgAssignment)ConfigDocument.Parse("x=" + ConfigText.Format(new CfgString("a\nb")) + ";").Items.First(i => i is CfgAssignment)).Value).Value);
        Assert.Equal(2, doc.Items.Count(i => i is CfgAssignment)); // next=1 still parses
    }
}
