using System.Diagnostics;
using Panel.Features.Mods;

namespace Panel.Tests;

public class LauncherPresetTests
{
    // The shape the Arma 3 Launcher writes (steam rows link to filedetails; local rows are marked from-local).
    public const string Preset = """
        <?xml version="1.0" encoding="utf-8"?>
        <html>
          <head>
            <meta name="arma:Type" content="preset" />
            <meta name="arma:PresetName" content="Main Ops &amp; Training" />
            <title>Arma 3</title>
          </head>
          <body>
            <div class="mod-list">
              <table>
                <tr data-type="ModContainer">
                  <td data-type="DisplayName">CBA_A3</td>
                  <td><span class="from-steam">Steam</span></td>
                  <td><a href="https://steamcommunity.com/sharedfiles/filedetails/?id=450814997" data-type="Link">https://steamcommunity.com/sharedfiles/filedetails/?id=450814997</a></td>
                </tr>
                <tr data-type="ModContainer">
                  <td data-type="DisplayName">ace</td>
                  <td><span class="from-steam">Steam</span></td>
                  <td><a href="http://steamcommunity.com/sharedfiles/filedetails/?id=463939057" data-type="Link">http://steamcommunity.com/sharedfiles/filedetails/?id=463939057</a></td>
                </tr>
                <tr data-type="ModContainer">
                  <td data-type="DisplayName">@unit_assets</td>
                  <td><span class="from-local">Local</span></td>
                  <td><span data-type="Link" data-meta="local:@unit_assets|@unit_assets|"></span></td>
                </tr>
              </table>
            </div>
          </body>
        </html>
        """;

    [Fact]
    public void Parses_a_launcher_preset_in_order()
    {
        var p = LauncherPreset.Parse(Preset);

        Assert.Equal("Main Ops & Training", p.Name);
        Assert.Equal([new PresetMod("CBA_A3", 450814997, null), new PresetMod("ace", 463939057, null), new PresetMod("@unit_assets", null, "@unit_assets")], p.Mods);
        Assert.Empty(p.Problems);
    }

    [Fact]
    public void Parses_pasted_ids_and_urls()
    {
        var p = LauncherPreset.Parse("450814997\r\n  https://steamcommunity.com/sharedfiles/filedetails/?id=463939057&searchtext=\nsomething else\n\n450814997");

        Assert.Null(p.Name);
        Assert.Equal([450814997L, 463939057L], p.Mods.Select(m => m.WorkshopId!.Value));
        Assert.Equal(["Line 3 isn't a Workshop ID or link: something else"], p.Problems);
    }

    [Fact]
    public void Launcher_preset_round_trips_with_escaping()
    {
        var html = LauncherPreset.Export("A <b> & \"c\"", [("CBA_A3", 450814997L, null), ("x & <y>", null, "@local mod")]);
        var p = LauncherPreset.Parse(html);

        Assert.Contains("A &lt;b&gt; &amp; &quot;c&quot;", html);
        Assert.Equal("A <b> & \"c\"", p.Name);
        Assert.Equal([new PresetMod("CBA_A3", 450814997, null), new PresetMod("x & <y>", null, "@local mod")], p.Mods);
    }

    [Fact]
    public void Hostile_input_is_bounded()
    {
        var sw = Stopwatch.StartNew();
        var p = LauncherPreset.Parse(string.Concat(Enumerable.Repeat("<tr data-type=\"ModContainer\"><td data-type=\"DisplayName\">", 20_000)));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.Empty(p.Mods);
    }

    [Fact]
    public void A_number_too_big_for_an_id_is_a_problem_not_a_crash()
    {
        var parsed = LauncherPreset.Parse("9999999999999999999\nhttps://steamcommunity.com/sharedfiles/filedetails/?id=9999999999999999999\n450814997");

        Assert.Equal([450814997L], parsed.Mods.Select(m => m.WorkshopId!.Value));
        Assert.Equal(2, parsed.Problems.Length);
    }

    private sealed class Replies(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    [Theory]
    [InlineData("""{"response":{"result":1,"resultcount":0}}""")]
    [InlineData("""{"response":[]}""")]
    [InlineData("""[]""")]
    public async Task A_steam_reply_without_item_details_is_a_json_error(string json)
    {
        // Every caller turns a JsonException into "Steam's Workshop service couldn't be reached" (502), never a 500.
        var client = new WorkshopClient(new HttpClient(new Replies(json)));

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => client.DetailsAsync([450814997]));
    }
}
