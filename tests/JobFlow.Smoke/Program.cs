using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using JobFlow.Infrastructure;
using Microsoft.Playwright;

var root = Directory.GetCurrentDirectory();
if (!File.Exists(Path.Combine(root, "JobFlow.slnx"))) throw new InvalidOperationException("Run from the repository root.");
var data = Path.Combine(root, "data", "smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(data);
Environment.SetEnvironmentVariable("JOBFLOW_DATA_DIR", data);
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
using var worker = Start("JobFlow.Worker");
var apiOutput = api.StandardOutput.ReadToEndAsync(); var apiError = api.StandardError.ReadToEndAsync();
var workerOutput = worker.StandardOutput.ReadToEndAsync(); var workerError = worker.StandardError.ReadToEndAsync();
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
    using var playwright = await Playwright.CreateAsync();
    await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
    var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1440, Height = 1080 } });
    var errors = new List<string>(); page.PageError += (_, e) => errors.Add(e);
    await page.GotoAsync("http://127.0.0.1:5271");
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
    await page.GetByRole(AriaRole.Button, new() { Name = "Резюме", Exact = true }).ClickAsync();
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
    await page.GetByRole(AriaRole.Button, new() { Name = "Планировщик", Exact = true }).ClickAsync();
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
    await page.GetByRole(AriaRole.Button,new(){Name="Workflow",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Button,new(){Name="Создать",Exact=true}).ClickAsync();
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
    await page.GetByRole(AriaRole.Button,new(){Name="Отклики",Exact=true}).ClickAsync();
    var testRow=page.GetByRole(AriaRole.Row).Filter(new(){HasText="TEST — Software engineer"});
    await testRow.GetByRole(AriaRole.Button,new(){Name="Письмо",Exact=true}).ClickAsync();
    await page.GetByLabel("Текст письма").FillAsync("Reviewed synthetic C# experience.");
    await page.GetByRole(AriaRole.Button,new(){Name="Сохранить письмо",Exact=true}).ClickAsync();
    await testRow.GetByRole(AriaRole.Button,new(){Name="ReadyToSubmit",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Button,new(){Name="Проверить тестовую отправку",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Cell,new(){Name="Submitted",Exact=true}).WaitForAsync();
    await page.GetByRole(AriaRole.Button,new(){Name="Попытки отправки",Exact=true}).ClickAsync();
    await page.GetByRole(AriaRole.Cell,new(){Name="TestConfirmed",Exact=true}).WaitForAsync();
    await page.ScreenshotAsync(new(){Path=Path.Combine(review,"workflow-submitted.png"),FullPage=true});
    await page.GetByLabel("Рабочий профиль").SelectOptionAsync(other.GetProperty("id").GetString()!);
    await page.GetByRole(AriaRole.Heading,new(){Name="Здесь пока нет записей",Exact=true}).WaitForAsync();
    if(await page.GetByRole(AriaRole.Cell,new(){Name="TestConfirmed",Exact=true}).CountAsync()!=0)throw new InvalidOperationException("Attempt owner filter failed.");
    if(errors.Count>0)throw new InvalidOperationException(string.Join("\\n",errors));
    Console.WriteLine("PASS: UI profile, durable workflow, review editor, approval, isolated test submission, independent Worker, no JS errors.");
    Console.WriteLine("Screenshots: " + review);
}
finally
{
    if (!worker.HasExited) worker.Kill(true);
    if (!api.HasExited) api.Kill(true);
    await worker.WaitForExitAsync(); await api.WaitForExitAsync();
}
Process Start(string name)
{
    var info = new ProcessStartInfo("dotnet") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    info.ArgumentList.Add(Path.Combine(root, "src", name, "bin", "Debug", "net10.0", name + ".dll"));
    info.ArgumentList.Add("--contentRoot"); info.ArgumentList.Add(Path.Combine(root, "src", name));
    info.Environment["JOBFLOW_DATA_DIR"] = data;
    return Process.Start(info) ?? throw new InvalidOperationException("Cannot start " + name);
}
static AriaRole HeadingRole() => AriaRole.Heading;

