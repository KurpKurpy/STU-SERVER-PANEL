using ServerAgent;

namespace Agent.Tests;

public class InstallDetectorTests
{
    [Fact]
    public void ParseLibraryFolders_reads_every_path()
    {
        const string vdf = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"228980"		"493478137"
            		}
            	}
            	"1"
            	{
            		"path"		"K:\\Steam"
            		"label"		""
            	}
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"K:\Steam"], InstallDetector.ParseLibraryFolders(vdf));
    }
}
