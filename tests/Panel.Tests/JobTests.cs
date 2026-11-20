using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Panel.Auth;
using Panel.Data;
using Panel.Features.Jobs;

namespace Panel.Tests;

public class JobTests
{
    private static async Task<Job> CreateAsync(PanelFactory f, Guid agentId, string permission = Permissions.ModsInstall)
    {
        using var scope = f.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var spec = new JobSpec(Guid.NewGuid(), JobKind.WorkshopDownload, "crow", null, null, [new WorkshopItem(450814997, 1000)]);
        return await jobs.CreateAsync(agentId, spec, "workshop.download", permission, "mods", null, null, new { ids = new[] { 450814997 } });
    }

    private static Task<JsonElement> Get(HttpClient c, Guid id) => c.GetFromJsonAsync<JsonElement>($"/api/jobs/{id}");

    private static async Task<JsonElement> WaitFor(HttpClient c, Guid id, Func<JsonElement, bool> until)
    {
        JsonElement job = default;
        await AgentKit.WaitUntilAsync(() => until(job = Get(c, id).GetAwaiter().GetResult()), 10);
        return job;
    }

    [Fact]
    public async Task Job_runs_and_finishes_through_the_agent()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;

        var job = await CreateAsync(f, agentId);
        Assert.Equal(job.Id, agent.LastJob().JobId);

        await agent.SendJobUpdateAsync(new JobUpdate(job.Id, 0.5, ["Downloading item 450814997 ..."], null));
        var running = await WaitFor(owner, job.Id, j => j.GetProperty("status").GetString() == "Running");
        Assert.Equal(0.5, running.GetProperty("progress").GetDouble());
        Assert.Contains("Downloading item 450814997", running.GetProperty("logText").GetString());

        await agent.SendJobFinishedAsync(new JobResult(job.Id, true, null, [new JobItemResult(450814997, true, null, 1000, [])]));
        var done = await WaitFor(owner, job.Id, j => j.GetProperty("status").GetString() == "Succeeded");
        Assert.Equal(1, done.GetProperty("progress").GetDouble());
        Assert.Contains((await owner.GetFromJsonAsync<JsonElement[]>("/api/jobs"))!, j => j.GetProperty("id").GetGuid() == job.Id);
    }

    [Fact]
    public async Task Prompt_turns_the_job_to_needs_input_and_the_answer_reaches_the_agent()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var job = await CreateAsync(f, agentId);

        await agent.SendJobUpdateAsync(new JobUpdate(job.Id, null, ["Cached credentials not found."], SteamPrompt.Password));
        var waiting = await WaitFor(owner, job.Id, j => j.GetProperty("status").GetString() == "NeedsInput");
        Assert.Equal("Password", waiting.GetProperty("prompt").GetString());

        var r = await owner.PostAsJsonAsync($"/api/jobs/{job.Id}/answer", new { answer = "pw-9" });

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains(agent.Calls, c => c.Method == nameof(IAgentClient.AnswerJob) && (Guid)c.Args[0]! == job.Id && (string)c.Args[1]! == "pw-9");
        Assert.Equal("Running", (await Get(owner, job.Id)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostAsJsonAsync($"/api/jobs/{job.Id}/answer", new { answer = "again" })).StatusCode);
    }

    [Fact]
    public async Task Steam_answers_are_never_stored_or_returned()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var job = await CreateAsync(f, agentId);
        await agent.SendJobUpdateAsync(new JobUpdate(job.Id, null, [], SteamPrompt.Password));
        await WaitFor(owner, job.Id, j => j.GetProperty("status").GetString() == "NeedsInput");
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync($"/api/jobs/{job.Id}/answer", new { answer = "hunter2-steam" })).StatusCode);

        // Even if an agent echoed it, the panel masks it before keeping or sending anything.
        await agent.SendJobUpdateAsync(new JobUpdate(job.Id, null, ["debug: hunter2-steam"], null));
        await agent.SendJobFinishedAsync(new JobResult(job.Id, false, "Steam sign-in failed: hunter2-steam.", []));
        var done = await WaitFor(owner, job.Id, j => j.GetProperty("status").GetString() == "Failed");

        Assert.DoesNotContain("hunter2-steam", done.GetRawText());
        Assert.Contains("debug: ****", done.GetProperty("logText").GetString());
        Assert.DoesNotContain("hunter2-steam", await owner.GetStringAsync("/api/jobs"));
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Assert.All(await db.Jobs.ToListAsync(), j => Assert.DoesNotContain("hunter2-steam", j.LogText + j.Error + j.PayloadJson));
        Assert.All(await db.Audit.ToListAsync(), a => Assert.DoesNotContain("hunter2-steam", a.Summary + a.DetailsJson));
    }

    [Fact]
    public async Task Running_jobs_are_interrupted_after_a_panel_restart()
    {
        using var f = new PanelFactory();
        f.CreateClient();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        db.Jobs.Add(new Job { Id = Guid.NewGuid(), Type = "workshop.download", Status = JobStatus.Running });
        db.Jobs.Add(new Job { Id = Guid.NewGuid(), Type = "workshop.download", Status = JobStatus.Succeeded });
        await db.SaveChangesAsync();

        await JobService.InterruptOpenJobsAsync(db, scope.ServiceProvider.GetServices<IJobResultHandler>()); // what Program runs at startup

        var statuses = (await db.Jobs.AsNoTracking().ToListAsync()).Select(j => j.Status).Order().ToArray();
        Assert.Equal([JobStatus.Succeeded, JobStatus.Interrupted], statuses);
    }

    [Fact]
    public async Task Jobs_missing_from_the_agents_hello_are_interrupted_and_listed_ones_revive()
    {
        using var f = new PanelFactory();
        var (owner, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var gone = await CreateAsync(f, agentId);
        var alive = await CreateAsync(f, agentId);

        await agent.HelloAsync(jobs: [alive.Id]);

        Assert.Equal("Interrupted", (await WaitFor(owner, gone.Id, j => j.GetProperty("status").GetString() == "Interrupted")).GetProperty("status").GetString());
        Assert.Equal("Queued", (await Get(owner, alive.Id)).GetProperty("status").GetString());
        // A result for an interrupted job still lands: the agent finished it after all.
        await agent.SendJobFinishedAsync(new JobResult(gone.Id, true, null, []));
        await WaitFor(owner, gone.Id, j => j.GetProperty("status").GetString() == "Succeeded");
    }

    [Fact]
    public async Task Only_the_creating_permission_can_answer_or_cancel()
    {
        using var f = new PanelFactory();
        var (_, agentId, agent, _) = await AgentKit.InstallAsync(f);
        await using var lease = agent;
        var job = await CreateAsync(f, agentId, Permissions.ServerUpdate);
        await agent.SendJobUpdateAsync(new JobUpdate(job.Id, null, [], SteamPrompt.Password));
        var modManager = await TestUsers.LoginAsync(f, "S3"); // sees jobs (mods.view), lacks server.update
        await WaitFor(modManager, job.Id, j => j.GetProperty("status").GetString() == "NeedsInput");

        Assert.Equal(HttpStatusCode.Forbidden, (await modManager.PostAsJsonAsync($"/api/jobs/{job.Id}/answer", new { answer = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await modManager.PostAsync($"/api/jobs/{job.Id}/cancel", null)).StatusCode);
        Assert.DoesNotContain(agent.Calls, c => c.Method == nameof(IAgentClient.AnswerJob));
    }

    [Fact]
    public async Task Agent_offline_fails_the_job_at_once()
    {
        using var f = new PanelFactory();
        var (_, agentId, _) = await AgentKit.PairAsync(f);

        var job = await CreateAsync(f, agentId);

        Assert.Equal(JobStatus.Failed, job.Status);
        Assert.Contains("isn't connected", job.Error);
    }

    [Fact]
    public async Task Steam_username_is_a_setting_and_checked()
    {
        using var f = new PanelFactory();
        var owner = await TestUsers.LoginAsync(f, "Owner", "Crow");
        var zeus = await TestUsers.LoginAsync(f, "S3", "Hawk");

        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/steam", new { username = "crow_1" })).StatusCode);
        Assert.Equal("crow_1", (await owner.GetFromJsonAsync<JsonElement>("/api/steam")).GetProperty("username").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync("/api/steam", new { username = "crow +quit" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await zeus.PutAsJsonAsync("/api/steam", new { username = "zeus" })).StatusCode);
    }
}
