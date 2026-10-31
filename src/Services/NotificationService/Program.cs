using Amazon;
using Amazon.SimpleEmailV2;
using SmartFactory.BuildingBlocks.DashboardAccess;
using SmartFactory.Services.NotificationService;
var builder = WebApplication.CreateBuilder(args);
builder.AddDashboardAccess();
builder.Services.AddHttpClient("resend", client => {
    client.BaseAddress = new Uri("https://api.resend.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
var emailProvider = (builder.Configuration["EMAIL_PROVIDER"] ?? "resend").Trim().ToLowerInvariant();
if (emailProvider == "ses") {
    var region = builder.Configuration["SES_REGION"] ?? "us-east-1";
    builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ => new AmazonSimpleEmailServiceV2Client(RegionEndpoint.GetBySystemName(region)));
    builder.Services.AddSingleton<IEmailSender, SesMailSender>();
} else if (emailProvider == "resend") {
    builder.Services.AddSingleton<IEmailSender>(services => new ResendMailSender(services.GetRequiredService<IHttpClientFactory>().CreateClient("resend"), builder.Configuration));
} else if (emailProvider == "smtp") {
    builder.Services.AddSingleton<IEmailSender>(new SmtpMailSender(builder.Configuration));
} else throw new InvalidOperationException("EMAIL_PROVIDER must be 'smtp', 'ses' or 'resend'.");
builder.Services.AddHostedService<NotificationDeliveryWorker>();
builder.Services.AddSingleton(PasswordResetDeliveryOptions.FromConfiguration(builder.Configuration, builder.Environment.IsDevelopment()));
builder.Services.AddHostedService<PasswordResetDeliveryWorker>();
var app = builder.Build();
app.UseDashboardAccess("SELECT id FROM notification_inbox LIMIT 1");
app.MapGet("/api/notifications/status", (IEmailSender sender) => Results.Ok(new { emailConfigured = sender.Configured, provider = sender.Provider, delivery = "at-least-once" }));
app.Run();
