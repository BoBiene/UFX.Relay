var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Hello from downstream on-prem app");
app.MapGet("/status", () => Results.Ok(new { name = "downstream-onprem", status = "ok" }));

await app.RunAsync();
