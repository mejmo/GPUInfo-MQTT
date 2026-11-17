using GpuInfoMqtt;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddWindowsService(o => o.ServiceName = "GpuInfoMqtt");
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddSingleton<StatusTracker>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<GpuReader>();
builder.Services.AddHostedService<HistorySampler>();
builder.Services.AddHostedService<MqttWorker>();
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
