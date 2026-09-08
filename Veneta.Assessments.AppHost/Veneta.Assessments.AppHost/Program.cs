var builder = DistributedApplication.CreateBuilder(args);
var httpPort = GetPort("CONSUMER_API_HTTP_PORT", 59142);
var httpsPort = GetPort("CONSUMER_API_HTTPS_PORT", 59141);
var repositoryRoot = FindRepositoryRoot();

var api = builder.AddProject<Projects.Veneta_Assessments_Backend_ConsumerService>("consumer-api").WithHttpEndpoint(port: httpPort, name: "http").WithHttpsEndpoint(port: httpsPort, name: "https").WithHttpHealthCheck("/ready");

builder.AddProject<Projects.Veneta_Assessments_Crawler>("consumer-crawler").WithEnvironment("Crawler__ApiBaseUrl", api.GetEndpoint("http")).WithEnvironment("Crawler__PageSize", "100").WithExplicitStart();

builder
    .AddExecutable("api-scenario", "dotnet", repositoryRoot, "run", "--project", "Veneta.Assessments.AppHost/ApiScenario/Veneta.Assessments.ApiScenario.csproj", "--no-build")
    .WithEnvironment("API_BASE_URL", api.GetEndpoint("http"))
    .WithExplicitStart();

// Exposes one-shot test commands as explicit dashboard actions.
builder
    .AddExecutable("backend-tests", "dotnet", repositoryRoot, "test", "Veneta.Assessments.Backend/Veneta.Assessments.Backend.ConsumerService.Tests/Veneta.Assessments.Backend.ConsumerService.Tests.csproj", "--no-restore")
    .WithExplicitStart();

builder.AddExecutable("crawler-tests", "dotnet", repositoryRoot, "test", "Veneta.Assessments.Crawler/Veneta.Assessments.Crawler.Tests/Veneta.Assessments.Crawler.Tests.csproj", "--no-restore").WithExplicitStart();

builder.Build().Run();

// Reads an optional fixed local port while retaining the documented defaults.
static int GetPort(string variableName, int defaultPort) => int.TryParse(Environment.GetEnvironmentVariable(variableName), out var configuredPort) && configuredPort is > 0 and <= 65535 ? configuredPort : defaultPort;

// Resolves the repository root from the compiled AppHost output directory.
static string FindRepositoryRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
