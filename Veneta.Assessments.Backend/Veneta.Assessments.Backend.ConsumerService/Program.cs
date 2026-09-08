using Veneta.Assessments.Backend.ConsumerService.Endpoints;
using Veneta.Assessments.Backend.ConsumerService.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddConsumerService();

var app = builder.Build();
var readiness = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
await app.InitializeConsumerServiceAsync(readiness);
app.MapConsumerServiceEndpoints(readiness);

app.Run();

public partial class Program { }
