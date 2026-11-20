using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ArmaFormats;

namespace Panel.Tests;

// Missions built in code: the user's real missions never go into fixtures (Phase 4 Global Constraints).
internal static class MissionKit
{
    public const string Sqm = """
        version=54;
        addons[]={"A3_Characters_F","cba_main"};
        class AddonsMetaData
        {
        	class List
        	{
        		items=1;
        		class Item0
        		{
        			className="cba_main";
        			name="Community Base Addons v3.19.0";
        			url="https://github.com/CBATeam/CBA_A3";
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

    public const string DescriptionExt = "onLoadName = \"Operation Escape\";\r\nclass Header\r\n{\r\n\tgameType = Coop;\r\n\tmaxPlayers = 10;\r\n};\r\n";

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static Dictionary<string, byte[]> Files(string? sqm = null) => new()
    {
        ["mission.sqm"] = Utf8(sqm ?? Sqm),
        ["description.ext"] = Utf8(DescriptionExt),
        ["scripts/init.sqf"] = Utf8("hint \"hi\";"),
    };

    public static byte[] Pbo(Dictionary<string, byte[]> files)
    {
        using var ms = new MemoryStream();
        ArmaFormats.Pbo.Write(new PboFile(new Dictionary<string, string>(), files.Select(kv => new PboEntry(kv.Key, kv.Value, 1_700_000_000)).ToList()), ms);
        return ms.ToArray();
    }

    public static byte[] Zip(Dictionary<string, byte[]> files, string? root)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in files)
            {
                using var e = zip.CreateEntry(root is null ? path : root + "/" + path).Open();
                e.Write(data);
            }
        return ms.ToArray();
    }

    public static MultipartFormDataContent Form(params (string FileName, byte[] Data)[] files)
    {
        var form = new MultipartFormDataContent();
        foreach (var (name, data) in files) form.Add(new ByteArrayContent(data), "files", name);
        return form;
    }

    public static Task<HttpResponseMessage> UploadAsync(HttpClient c, string fileName, byte[] data, Guid? missionId = null) =>
        c.PostAsync(missionId is { } id ? $"/api/missions/{id}/upload" : "/api/missions/upload", Form((fileName, data)));

    // Uploads a fresh mission PBO and returns its id.
    public static async Task<Guid> CreateAsync(HttpClient c, string name = "co10_Escape.Altis", Dictionary<string, byte[]>? files = null)
    {
        var r = await UploadAsync(c, name + ".pbo", Pbo(files ?? Files()));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("mission").GetProperty("id").GetGuid();
    }

    public static async Task<string> CodeAsync(HttpResponseMessage r) =>
        (await r.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("code", out var c) ? c.GetString()! : "";
}
