using HyperKeyLiberator;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "HyperKeyLiberator");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();