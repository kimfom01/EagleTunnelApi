var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.EagleTunnelApi>("eagletunnelapi");

builder.Build().Run();
