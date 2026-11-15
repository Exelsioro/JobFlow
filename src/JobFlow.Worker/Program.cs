using JobFlow.Infrastructure;
using Microsoft.EntityFrameworkCore;
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole();
builder.Services.AddDbContext<JobFlowDb>(x => x.UseSqlite(Storage.ConnectionString));
builder.Services.AddScoped<TaskProcessor>();
builder.Services.AddHostedService<JobFlow.Worker.Worker>();
var host = builder.Build();
using (var scope = host.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<JobFlowDb>().Database.MigrateAsync();
await host.RunAsync();
