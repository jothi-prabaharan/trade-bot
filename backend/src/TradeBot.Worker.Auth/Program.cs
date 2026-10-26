using TradeBot.Application;
using TradeBot.Infrastructure;
using TradeBot.Worker.Auth;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSystemd();
builder.Services.AddTradeBotInfrastructure(builder.Configuration);
builder.Services.AddTradeBotBrokerAuth();
builder.Services.AddHostedService<AuthWorker>();
builder.Build().Run();
