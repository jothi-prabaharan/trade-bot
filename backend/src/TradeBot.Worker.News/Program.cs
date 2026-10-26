using TradeBot.Application;
using TradeBot.Infrastructure;
using TradeBot.Worker.News;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSystemd();
builder.Services.AddTradeBotInfrastructure(builder.Configuration);
builder.Services.AddTradeBotNews();
builder.Services.AddHostedService<NewsWorker>();
builder.Build().Run();
