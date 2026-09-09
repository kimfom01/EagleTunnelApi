using Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<EagleTunnelApi>("eagletunnelapi");

builder.Build().Run();