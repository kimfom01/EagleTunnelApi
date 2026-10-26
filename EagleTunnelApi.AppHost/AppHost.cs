using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume();

var database = postgres.AddDatabase("appdb", "eagletunnelapi");

builder.AddProject<EagleTunnelApi>("eagletunnelapi")
    .WithReference(database, connectionName: "Postgres")
    .WaitFor(database);

builder.Build().Run();
