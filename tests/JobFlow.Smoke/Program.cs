using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

var root = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(root, "JobFlow.slnx"))) throw new InvalidOperationException("Run from the repository root.");
if (args.Contains("--workspace-ui"))
{
    await WorkspaceUiSmoke.RunAsync(root);
    return;
}
var data = Path.Combine(root, "data", "smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", data);
if (args.Contains("--codex-visual"))
{
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 800, Height = 500 } });
    await page.SetContentAsync("""
        <html><body style='margin:0;background:white'>
        <button style='position:absolute;left:270px;top:180px;width:240px;height:70px;font-size:24px'
          onclick="document.querySelector('output').textContent='Opened successfully'">Open form</button><output></output></body></html>
        """);
    var image = await page.ScreenshotAsync(new() { Type = ScreenshotType.Png, Scale = ScreenshotScale.Css });
    var provider = new CodexCliProvider();
    var result = await provider.GenerateAsync(new(
        "Synthetic offline test: inspect the attached 800x500 screenshot and choose ONE click at the center of the visible Open form button. Return the visual action schema, CSS pixel x/y, empty value, deltaY=0. Do not use tools.",
        BrowserWorkflowDriver.VisualActionSchema, "gpt-6.1-terra", "low", 120) { ImagePng = image }, default);
    using var action = JsonDocument.Parse(result.Json);
    var x = action.RootElement.GetProperty("x").GetInt32(); var y = action.RootElement.GetProperty("y").GetInt32();
    if (action.RootElement.GetProperty("action").GetString() != "click" || x is < 270 or > 510 || y is < 180 or > 250)
        throw new InvalidOperationException("Real CLI did not locate the synthetic button from the attached screenshot.");
    await page.Mouse.ClickAsync(x, y);
    if (await page.Locator("output").InnerTextAsync() != "Opened successfully") throw new InvalidOperationException("Visual click did not open the synthetic form.");
    const string evidence = "Published today\nApply for \"C# developer\"";
    var choices = BrowserWorkflowDriver.FreshnessEvidenceChoices(evidence, ["Apply for \"C# developer\""]);
    var freshness = await provider.GenerateAsync(new(
        "Synthetic schema regression: return publishedAt empty, publicationEvidence Published today, availability Open, availabilityEvidence e1. Evidence choices: " + JsonSerializer.Serialize(choices) + ". Metadata: {\"directApply\":false}",
        BrowserWorkflowDriver.FreshnessSchemaFor(evidence, ["Apply for \"C# developer\""]), "gpt-6-luna", "low", 120), default);
    using var status = JsonDocument.Parse(freshness.Json);
    if (status.RootElement.GetProperty("availabilityEvidence").GetString() != "e1") throw new InvalidOperationException("Freshness evidence ID was not returned.");
    Console.WriteLine("PASS: real CLI received PNG, located and clicked a synthetic button; quoted freshness evidence used a safe ID. No live sites or applications.");
    return;
}
if (args.Contains("--codex-resume"))
{
    await using var db = new JobFlowDb(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<JobFlowDb>().UseSqlite(Storage.ConnectionString).Options);
    await db.Database.MigrateAsync();
    var user = new JobFlow.Domain.User();
    var candidate = new JobFlow.Domain.CandidateProfile { UserId = user.Id, Name = "Synthetic Candidate", Skills = "C#, SQL", Dossier = "C# developer. Built internal SQL reports." };
    var resume = new JobFlow.Domain.Resume { CandidateProfileId = candidate.Id, Name = "Synthetic source" };
    var source = new JobFlow.Domain.ResumeVersion { ResumeId = resume.Id, StorageKey = "synthetic.pdf", OriginalName = "synthetic.pdf", Version = 1 };
    var platform = new JobFlow.Domain.JobPlatform();
    var vacancy = new JobFlow.Domain.Vacancy { JobPlatformId = platform.Id, ExternalId = "synthetic", Title = "C# Developer", Description = "C# and SQL development", Url = "https://example.invalid/jobs/synthetic" };
    var provider = new JobFlow.Domain.ModelProvider { Enabled = true, TimeoutSeconds = 180 };
    var agent = new JobFlow.Domain.AgentProfile { CandidateProfileId = candidate.Id, ModelProviderId = provider.Id, Enabled = true, TaskType = "Resume", Reasoning = "low", SystemPrompt = "Follow JobFlow task instructions and use only supplied synthetic facts." };
    var application = new JobFlow.Domain.JobApplication { CandidateProfileId = candidate.Id, VacancyId = vacancy.Id, ResumeVersionId = source.Id, State = JobFlow.Domain.ApplicationState.Drafting };
    db.AddRange(user,candidate,resume,source,platform,vacancy,provider,agent,application); await db.SaveChangesAsync();
    Directory.CreateDirectory(Path.Combine(data,"resumes"));
    await File.WriteAllBytesAsync(ResumeAgentService.PathFor(source),await ResumeAgentService.RenderAsync(candidate.Name,[("Experience",candidate.Dossier)],default));
    var workflow = new JobFlow.Domain.WorkflowDefinition { CandidateProfileId = candidate.Id, ResumeVersionId = source.Id, ResumeMode = "tailor", ResumeAgentId = agent.Id };
    await new ResumeAgentService(db,new AgentExecutor(db,new CodexCliProvider())).PrepareAsync(workflow,candidate,vacancy,application,default);
    var generated = await db.ResumeVersions.SingleAsync(x=>x.Id==application.ResumeVersionId);
    if(generated.Origin!="AgentGenerated"||generated.SourceResumeVersionId!=source.Id||!ResumeAgentService.Extract(ResumeAgentService.PathFor(generated)).Contains("C#"))throw new InvalidOperationException("Real CLI resume pipeline failed.");
    Console.WriteLine("PASS: real Codex CLI selected, adapted and verified synthetic resume facts; a separate readable PDF and provenance were saved. No live applications sent.");
    return;
}
if (args.Contains("--codex-browser"))
{
    var prompt = JobFlow.Application.BrowserAgentTemplate.Prompt + "\n" + """
        This is a synthetic offline browser-action contract check. No site is open and no real application will be sent.
        Phase: search. Task: finish collecting the single observed vacancy and return action finish.
        Page URL: https://example.invalid/search. Visible link target a-1: title Synthetic C# Engineer,
        employer Synthetic Employer, href https://example.invalid/jobs/1. Description: Synthetic C# job.
        Return that one vacancy with the observed title, employer, URL and description. Leave unused strings empty.
        """;
    var result = await new CodexCliProvider().GenerateAsync(new(prompt, JobFlow.Application.BrowserAgentTemplate.Schema, "", "", 120), default);
    using var output = JsonDocument.Parse(result.Json);
    if (output.RootElement.GetProperty("action").GetString() != "finish" ||
        output.RootElement.GetProperty("vacancies")[0].GetProperty("url").GetString() != "https://example.invalid/jobs/1")
        throw new InvalidOperationException("Codex did not return the expected synthetic browser action.");
    Console.WriteLine("PASS: real Codex CLI returned a schema-validated browser action for synthetic page data.");
    return;
}
if (args.Contains("--codex"))
{
    var result = await new CodexCliProvider().GenerateAsync(new("Return summary connection-ok. This is a synthetic JobFlow connection check. Do not use tools.",
        """{"type":"object","properties":{"summary":{"type":"string"}},"required":["summary"],"additionalProperties":false}""", "", "", 90), default);
    using var json = JsonDocument.Parse(result.Json);
    if (json.RootElement.GetProperty("summary").GetString() != "connection-ok") throw new InvalidOperationException("Unexpected model output.");
    Console.WriteLine("PASS: real Codex CLI returned schema-constrained synthetic output.");
    return;
}
if (!File.Exists(Path.Combine(root, "src/JobFlow.Api/wwwroot/index.html"))) throw new InvalidOperationException("Run scripts/build.ps1 (or build.sh) first.");
if (System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == 5271)) throw new InvalidOperationException("Port 5271 is already in use.");
using var api = Start("JobFlow.Api");
using var worker = Start("JobFlow.Worker", "search");
using var monitor = Start("JobFlow.Worker", "monitor");
var apiOutput = api.StandardOutput.ReadToEndAsync(); var apiError = api.StandardError.ReadToEndAsync();
var workerOutput = worker.StandardOutput.ReadToEndAsync(); var workerError = worker.StandardError.ReadToEndAsync();
var monitorOutput = monitor.StandardOutput.ReadToEndAsync(); var monitorError = monitor.StandardError.ReadToEndAsync();
try
{
    using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri("http://127.0.0.1:5271"), Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
    var ready = false;
    for (var i = 0; i < 40; i++)
    {
        if (api.HasExited) throw new InvalidOperationException(await apiError + await apiOutput);
        try { var response = await http.GetAsync("/api/status"); if (response.IsSuccessStatusCode) { ready = true; break; } }
        catch (HttpRequestException) { }
        await Task.Delay(500);
    }
    if (!ready) throw new TimeoutException("API did not start.");
    using var hostClient = new HttpClient(new HttpClientHandler { UseProxy = false });
    hostClient.DefaultRequestHeaders.Add("X-JobFlow-Client", "local-ui");
    for(var i=0;i<80;i++)
    {
        if(worker.HasExited||monitor.HasExited)throw new InvalidOperationException(await workerError+await monitorError);
        var services=await http.GetFromJsonAsync<JsonElement[]>("/api/services");
        if(services!.All(x=>x.GetProperty("status").GetString()!="Offline"))break;
        if(i==79)throw new TimeoutException("Independent services did not start.");
        await Task.Delay(500);
    }
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1080 } });
    async Task NavigateAsync(string title)
    {
        var nav = page.GetByRole(AriaRole.Navigation, new() { Name = "Разделы", IncludeHidden = true });
        if (!await nav.IsVisibleAsync()) await page.GetByRole(AriaRole.Button, new() { Name = "Меню", Exact = true }).ClickAsync();
        var entry = nav.GetByRole(AriaRole.Button, new() { Name = title, Exact = true, IncludeHidden = true });
        if (!await entry.IsVisibleAsync())
            await nav.Locator(".nav-group").Filter(new() { Has = entry }).Locator(".nav-group-toggle").ClickAsync();
        await entry.ClickAsync();
    }
    var errors = new List<string>(); page.PageError += (_, e) => errors.Add(e);
    await page.GotoAsync("http://127.0.0.1:5271");
    if(args.Contains("--services-layout"))
    {
        await NavigateAsync("Службы");
        await page.SetViewportSizeAsync(390,844);
        Console.WriteLine(await page.EvaluateAsync<string>("""
            () => JSON.stringify({width:innerWidth,scroll:document.documentElement.scrollWidth,elements:[...document.querySelectorAll('*')].filter(el=>{const r=el.getBoundingClientRect();return r.right>innerWidth&&getComputedStyle(el).position==='absolute'}).map(el=>({tag:el.tagName,cls:el.className,right:el.getBoundingClientRect().right,text:el.textContent.slice(0,80)}))})
            """));
        return;
    }
    await page.GetByRole(HeadingRole(), new() { Name = "Начните с профиля кандидата" }).WaitForAsync();
    var review = Path.Combine(root, ".impeccable", "review"); Directory.CreateDirectory(review);
    await page.ScreenshotAsync(new() { Path = Path.Combine(review, "desktop.png"), FullPage = true });
    await page.SetViewportSizeAsync(390, 844);
    await page.ScreenshotAsync(new() { Path = Path.Combine(review, "mobile.png"), FullPage = true });
    if (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth")) throw new InvalidOperationException("Mobile horizontal overflow.");
    await page.GetByRole(AriaRole.Button, new() { Name = "Создать профиль", Exact = true }).ClickAsync();
    await page.GetByLabel("Название профиля").FillAsync("Synthetic UI candidate");
    await page.GetByLabel("Навыки через запятую").FillAsync("C#, SQL");
    await page.ScreenshotAsync(new() { Path = Path.Combine(review, "mobile-form.png"), FullPage = true });
    await page.GetByRole(AriaRole.Button, new() { Name = "Сохранить", Exact = true }).ClickAsync();
    await page.GetByRole(AriaRole.Cell, new() { Name = "Synthetic UI candidate", Exact = true }).WaitForAsync();
    await page.ReloadAsync();
    await page.GetByRole(AriaRole.Cell, new() { Name = "Synthetic UI candidate", Exact = true }).WaitForAsync();
    if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
    await NavigateAsync("Резюме");
    await page.WaitForURLAsync("**/#resumes");
    await page.GoBackAsync();
    Console.WriteLine("Back navigation: " + page.Url + " / " + await page.Locator("h1").InnerTextAsync());
    await page.GetByRole(AriaRole.Heading, new() { Name = "Профили кандидата", Exact = true }).WaitForAsync();
    await page.ScreenshotAsync(new() { Path = Path.Combine(review, "mobile-records.png"), FullPage = true });
    var profiles = await http.GetFromJsonAsync<JsonElement[]>("/api/profiles");
    var taskResponse = await http.PostAsJsonAsync("/api/tasks", new { name = "Independent worker", candidateProfileId = profiles![0].GetProperty("id").GetGuid(), kind = "HealthCheck", intervalMinutes = 60, timeZone = "UTC" });
    taskResponse.EnsureSuccessStatusCode();
    var task = await taskResponse.Content.ReadFromJsonAsync<JsonElement>();
    (await http.PostAsJsonAsync("/api/tasks/" + task.GetProperty("id").GetGuid() + "/run", new { })).EnsureSuccessStatusCode();
    var ran = false;
    for (var i = 0; i < 20; i++)
    {
        var runs = await http.GetFromJsonAsync<JsonElement[]>("/api/runs");
        if (runs!.Any(x => x.GetProperty("status").GetString() == "Succeeded")) { ran = true; break; }
        await Task.Delay(500);
    }
    if (!ran) throw new InvalidOperationException("Independent worker did not process the job.");
    var otherResponse = await http.PostAsJsonAsync("/api/profiles", new { name = "Other synthetic profile" });
    otherResponse.EnsureSuccessStatusCode();
    var other = await otherResponse.Content.ReadFromJsonAsync<JsonElement>();
    await page.ReloadAsync();
    await page.GetByLabel("Рабочий профиль").SelectOptionAsync(profiles![0].GetProperty("id").GetString()!);
    await NavigateAsync("Планировщик");
    await page.GetByRole(AriaRole.Cell, new() { Name = "Independent worker", Exact = true }).WaitForAsync();
    await page.GetByLabel("Рабочий профиль").SelectOptionAsync(other.GetProperty("id").GetString()!);
    await page.GetByRole(AriaRole.Heading, new() { Name = "Здесь пока нет записей", Exact = true }).WaitForAsync();
    if (await page.GetByRole(AriaRole.Cell, new() { Name = "Independent worker", Exact = true }).CountAsync() != 0) throw new InvalidOperationException("Profile filtering failed.");
    await page.GetByLabel("Рабочий профиль").SelectOptionAsync(profiles[0].GetProperty("id").GetString()!);
    await page.GetByRole(AriaRole.Button, new() { Name = "Запустить", Exact = true }).ClickAsync();
    await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Задание поставлено в очередь" }).WaitForAsync();
    await page.SetViewportSizeAsync(1440, 1080);
    await page.ScreenshotAsync(new() { Path = Path.Combine(review, "desktop-records.png"), FullPage = true });
    var profileId=profiles[0].GetProperty("id").GetGuid();
    using var upload=new MultipartFormDataContent();
    upload.Add(new StringContent(profileId.ToString()),"profileId");
    upload.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\\nSynthetic fixture\\n%%EOF")),"file","workflow.pdf");
    var uploadResponse=await http.PostAsync("/api/resumes",upload);uploadResponse.EnsureSuccessStatusCode();
    var version=await uploadResponse.Content.ReadFromJsonAsync<JsonElement>();
    var platformResponse=await http.PostAsJsonAsync("/api/platforms",new{name="Workflow fixture",url="https://example.invalid",adapter="test-form"});
    platformResponse.EnsureSuccessStatusCode();var platform=await platformResponse.Content.ReadFromJsonAsync<JsonElement>();
    var importedResponse=await http.PostAsJsonAsync("/api/platforms",new{name="Imported board",url="https://example.invalid",adapter="search-only"});
    importedResponse.EnsureSuccessStatusCode();
    var imported=await importedResponse.Content.ReadFromJsonAsync<JsonElement>();
    (await http.PostAsJsonAsync("/api/vacancies",new{jobPlatformId=imported.GetProperty("id").GetGuid(),externalId="imported",title="Imported synthetic vacancy"})).EnsureSuccessStatusCode();
    await page.ReloadAsync();
    await page.GetByLabel("Рабочий профиль").SelectOptionAsync(profileId.ToString());
    await NavigateAsync("Workflow");
    await page.GetByRole(AriaRole.Button,new(){Name="Создать",Exact=true}).ClickAsync();
    await page.GetByLabel("Общий браузерный профиль с вашими авторизациями").WaitForAsync();
    await page.ScreenshotAsync(new(){Path=Path.Combine(review,"browser-workflow-desktop.png"),FullPage=true});
    await page.SetViewportSizeAsync(390,844);
    await page.ScreenshotAsync(new(){Path=Path.Combine(review,"browser-workflow-mobile.png"),FullPage=true});
    if(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth"))throw new InvalidOperationException("Browser workflow mobile overflow.");
    await page.SetViewportSizeAsync(1440,1080);
    await page.GetByLabel("Режим: browser — агент в браузере; prepare — подготовка через API/импорт").SelectOptionAsync("prepare");
    await page.GetByLabel("Название").FillAsync("Full preparation");
    await page.GetByLabel("Версия резюме").SelectOptionAsync(version.GetProperty("id").GetString()!);
    await page.GetByLabel("Разрешить выполнение workflow").CheckAsync();
    await page.GetByRole(AriaRole.Button,new(){Name="Выбрать все площадки",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Button,new(){Name="Сохранить",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Cell,new(){Name="Workflow fixture, Imported board",Exact=true}).WaitForAsync();
    await page.GetByRole(AriaRole.Button,new(){Name="Запустить workflow",Exact=true}).ClickAsync();
    var prepared=false;
    for(var i=0;i<60;i++){
        var applications=await http.GetFromJsonAsync<JsonElement[]>("/api/applications");
        if(applications!.Count(x=>x.GetProperty("state").GetString()=="ReadyForReview")==2){prepared=true;break;}
        await Task.Delay(500);
    }
    if(!prepared)throw new InvalidOperationException("Workflow did not prepare application.");
    await page.ReloadAsync();
    await page.GetByLabel("Рабочий профиль").SelectOptionAsync(profileId.ToString());
    await NavigateAsync("Отклики");
    var testRow=page.GetByRole(AriaRole.Row).Filter(new(){HasText="TEST — Software engineer"});
    await testRow.GetByRole(AriaRole.Button,new(){Name="Письмо",Exact=true}).ClickAsync();
    await page.GetByLabel("Текст письма").FillAsync("Reviewed synthetic C# experience.");
    await page.GetByRole(AriaRole.Button,new(){Name="Сохранить письмо",Exact=true}).ClickAsync();
    await testRow.GetByRole(AriaRole.Button,new(){Name="ReadyToSubmit",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Button,new(){Name="Проверить тестовую отправку",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Cell,new(){Name="Submitted",Exact=true}).WaitForAsync();
    await NavigateAsync("Попытки отправки");
    await page.GetByRole(AriaRole.Cell,new(){Name="TestConfirmed",Exact=true}).WaitForAsync();
    await page.ScreenshotAsync(new(){Path=Path.Combine(review,"workflow-submitted.png"),FullPage=true});
    await page.GetByLabel("Рабочий профиль").SelectOptionAsync(other.GetProperty("id").GetString()!);
    await page.GetByRole(AriaRole.Heading,new(){Name="Здесь пока нет записей",Exact=true}).WaitForAsync();
    if(await page.GetByRole(AriaRole.Cell,new(){Name="TestConfirmed",Exact=true}).CountAsync()!=0)throw new InvalidOperationException("Attempt owner filter failed.");
    var browserResponse=await http.PostAsJsonAsync("/api/browser-profiles",new{name="Smoke saved browser"});
    browserResponse.EnsureSuccessStatusCode();
    await page.ReloadAsync();
    await NavigateAsync("Браузерные профили");
    await page.GetByRole(AriaRole.Button,new(){Name="Управлять браузером",Exact=true}).ClickAsync();
    await page.GetByLabel("Адрес сайта",new(){Exact=true}).FillAsync("http://127.0.0.1:5271/api/status");
    await page.GetByRole(AriaRole.Button,new(){Name="Открыть",Exact=true}).ClickAsync();
    await page.WaitForFunctionAsync("() => document.querySelector('.remote-browser select')?.selectedOptions[0]?.textContent.includes('/api/status')");
    await page.GetByAltText("Текущая вкладка сохранённого браузерного профиля").WaitForAsync();
    await page.WaitForFunctionAsync("() => { const img=document.querySelector('.remote-screen img'); return img?.complete && img.naturalWidth > 0; }");
    await page.ScreenshotAsync(new(){Path=Path.Combine(review,"remote-browser-desktop.png"),FullPage=true});
    await page.SetViewportSizeAsync(390,844);
    await page.ScreenshotAsync(new(){Path=Path.Combine(review,"remote-browser-mobile.png"),FullPage=true});
    if(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth"))throw new InvalidOperationException("Remote browser mobile overflow.");
    await page.GetByRole(AriaRole.Button,new(){Name="Завершить просмотр",Exact=true}).ClickAsync();
    foreach(var section in new[]{("Службы","services"),("Уведомления","notifications")})
    {
        if(section.Item2=="notifications")await page.GetByLabel("Рабочий профиль").SelectOptionAsync(profileId.ToString());
        await NavigateAsync(section.Item1);
        await page.SetViewportSizeAsync(1440,1080);
        await page.ScreenshotAsync(new(){Path=Path.Combine(review,section.Item2+"-desktop.png"),FullPage=true});
        await page.SetViewportSizeAsync(390,844);
        await page.ScreenshotAsync(new(){Path=Path.Combine(review,section.Item2+"-mobile.png"),FullPage=true});
        if(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth > innerWidth"))throw new InvalidOperationException(section.Item2+" mobile overflow.");
    }
    await page.GetByRole(AriaRole.Button,new(){Name="Прочитано",Exact=true}).First.ClickAsync();
    (await http.PostAsJsonAsync("/api/services/monitor/pause",new{})).EnsureSuccessStatusCode();
    await Task.Delay(3500);
    var paused=await http.GetFromJsonAsync<JsonElement[]>("/api/services");
    if(paused!.Single(x=>x.GetProperty("role").GetString()=="monitor").GetProperty("status").GetString()!="Paused")throw new InvalidOperationException("Monitor did not pause independently.");
    (await hostClient.GetAsync("http://127.0.0.1:5272/health")).EnsureSuccessStatusCode();
    (await http.PostAsJsonAsync("/api/services/monitor/start",new{})).EnsureSuccessStatusCode();
    (await http.PostAsJsonAsync("/api/services/monitor/stop",new{})).EnsureSuccessStatusCode();
    await monitor.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
    (await hostClient.GetAsync("http://127.0.0.1:5272/health")).EnsureSuccessStatusCode();
    (await http.PostAsJsonAsync("/api/services/monitor/start",new{})).EnsureSuccessStatusCode();
    var restarted=false;
    for(var i=0;i<40;i++)
    {
        try { using var health=await hostClient.GetAsync("http://127.0.0.1:5273/health"); if(health.IsSuccessStatusCode){restarted=true;break;} }catch(HttpRequestException){}
        await Task.Delay(500);
    }
    if(!restarted)throw new InvalidOperationException("API did not restart the independent monitor process.");
    if(errors.Count>0)throw new InvalidOperationException(string.Join("\\n",errors));
    Console.WriteLine("PASS: UI profile, workflow, review, isolated submission, browser view hosted by Search, independent Search/Monitor processes and lifecycle, notifications, desktop/mobile, no JS errors.");
    Console.WriteLine("Screenshots: " + review);
}
finally
{
    using var cleanup = new HttpClient(new HttpClientHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(5)};
    cleanup.DefaultRequestHeaders.Add("X-JobFlow-Client","local-ui");
    foreach(var port in new[]{5272,5273})try{using var stopped=await cleanup.PostAsync("http://127.0.0.1:"+port+"/shutdown",null);}catch(HttpRequestException){}catch(TaskCanceledException){}
    if (!worker.HasExited) worker.Kill(true);
    if (!monitor.HasExited) monitor.Kill(true);
    if (!api.HasExited) api.Kill(true);
    await worker.WaitForExitAsync(); await monitor.WaitForExitAsync(); await api.WaitForExitAsync();
}
Process Start(string name, string? role = null)
{
    var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(Path.Combine(root, "src", name, "bin", "Debug", "net10.0", name + ".dll"));
    info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(Path.Combine(root, "src", name));
    if(role!=null){info.ArgumentList.Add("--ServiceRole");info.ArgumentList.Add(role);}
    info.Environment["JOBFLOW_DATA_DIR"] = data;
    info.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:5271";
    info.Environment["Browser__Headless"] = "true";
    info.Environment["Services__UseWindowsServices"] = "false";
    return Process.Start(info) ?? throw new InvalidOperationException("Cannot start " + name);
}
static AriaRole HeadingRole() => AriaRole.Heading;

