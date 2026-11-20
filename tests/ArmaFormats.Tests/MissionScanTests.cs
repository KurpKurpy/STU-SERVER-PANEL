using System.Text;

namespace ArmaFormats.Tests;

public class MissionScanTests
{
    // The shape Eden writes: addons[] at the root, then AddonsMetaData.List.Item<n> with the mod each addon came from.
    private const string TextSqm = """
        version=54;
        class EditorData
        {
        	moveGridStep=1;
        };
        addons[]=
        {
        	"A3_Characters_F",
        	"cba_main"
        };
        class AddonsMetaData
        {
        	class List
        	{
        		items=2;
        		class Item0
        		{
        			className="A3_Characters_F";
        			name="Arma 3 Alpha - Characters and Clothing";
        			author="Bohemia Interactive";
        			url="https://www.arma3.com";
        		};
        		class Item1
        		{
        			className="cba_main";
        			name="Community Base Addons v3.19.0";
        		};
        	};
        };
        class Mission
        {
        	class Entities
        	{
        		items=0;
        	};
        };
        """;

    [Fact]
    public void Scan_reads_addons_and_metadata_from_text_and_rap()
    {
        var text = MissionScan.Addons(Encoding.UTF8.GetBytes(TextSqm));
        var rap = MissionScan.Addons(RapBuilder.Build(ConfigDocument.Parse(TextSqm).Items));

        foreach (var scan in new[] { text, rap })
        {
            Assert.Equal(["A3_Characters_F", "cba_main"], scan.Addons);
            Assert.Equal(new AddonMeta("A3_Characters_F", "Arma 3 Alpha - Characters and Clothing", "https://www.arma3.com"), scan.Meta[0]);
            Assert.Equal(new AddonMeta("cba_main", "Community Base Addons v3.19.0", null), scan.Meta[1]);
        }
        Assert.False(text.Binarized);
        Assert.True(rap.Binarized);
    }

    [Fact]
    public void Scan_handles_a_bom_and_a_mission_without_addons()
    {
        var scan = MissionScan.Addons([0xEF, 0xBB, 0xBF, .. "version=54;\r\nclass Mission {};\r\n"u8]);

        Assert.Empty(scan.Addons);
        Assert.Empty(scan.Meta);
    }

    [Fact]
    public void Broken_text_is_a_syntax_error()
    {
        Assert.Throws<ConfigSyntaxException>(() => MissionScan.Addons("addons[]={\"a\""u8.ToArray()));
    }
}
