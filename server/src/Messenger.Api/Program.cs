using Messenger.Api;
using Messenger.Infrastructure.Persistence;
using Serilog;

Log.Logger = LoggingSetup.CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args.Where(a => !Cli.IsCommand(a)).ToArray());
    builder.AddMessengerServices();
    var app = builder.Build();

    if (Cli.TryGetCommand(args, out var command))
    {
        return await Cli.RunAsync(app, command, args);
    }

    if (app.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
    }

    app.UseMessengerPipeline();
    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
