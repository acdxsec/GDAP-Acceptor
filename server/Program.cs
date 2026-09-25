using System.Threading.RateLimiting;
using System.Text.Json;
using Gdap.Server;
using Gdap.Status;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

try
{
    var app = CentralHost.Build(WebApplication.CreateBuilder(args), ServiceSettings.Load());
    await app.RunAsync();
    return 0;
}
catch
{
    Console.Error.WriteLine("Central status server could not start. Check non-secret settings, secret-file access and listener configuration; no configuration values are printed.");
    return 1;
}

namespace Gdap.Server
{
    internal static class CentralHost
    {
        internal static WebApplication Build(WebApplicationBuilder builder, ServiceSettings settings, Action<IServiceCollection>? testAdapters = null)
        {
            settings.Validate();
            // Avoid framework request/exception logs that might contain URLs,
            // claims or upstream response data. Only fixed-field audit events.
            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole();
            builder.Logging.AddFilter((_, category, level) => category == "GdapAudit" && level >= LogLevel.Information);
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.AddServerHeader = false;
                options.Limits.MaxRequestBodySize = 4096;
                options.Limits.MaxRequestLineSize = 2048;
                options.Limits.MaxRequestHeadersTotalSize = 32768;
                options.Limits.MaxConcurrentConnections = 128;
            });
            builder.Services.AddSingleton(settings);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<CippReader>();
            builder.Services.AddSingleton<InvitationJournal>();
            builder.Services.AddSingleton<CippInvitations>();
            builder.Services.AddHttpClient("cipp", client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
            }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false });
            builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                options.Authority = settings.Authority;
                options.Audience = settings.Audience;
                options.MapInboundClaims = false;
                options.IncludeErrorDetails = false;
                options.SaveToken = false;
                options.RequireHttpsMetadata = true;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = settings.Authority,
                    ValidateAudience = true, ValidAudience = settings.Audience,
                    ValidateLifetime = true, RequireExpirationTime = true,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30), RoleClaimType = "roles", NameClaimType = "oid"
                };
            });
            var staff = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
                .RequireClaim("tid", settings.IdentityTenantId).RequireClaim("ver", "2.0")
                .RequireClaim("azp", settings.DesktopClientId).RequireRole(StatusProtocol.Role)
                .RequireAssertion(context => context.User.FindAll("scp").Any(c => c.Value.Split(' ').Contains(StatusProtocol.Scope)) &&
                    Guid.TryParseExact(context.User.FindFirst("oid")?.Value, "D", out var oid) && oid != Guid.Empty)
                .Build();
            var creator = new AuthorizationPolicyBuilder().RequireAuthenticatedUser()
                .RequireClaim("tid", settings.IdentityTenantId).RequireClaim("ver", "2.0")
                .RequireClaim("azp", settings.DesktopClientId).RequireRole(InvitationProtocol.Role)
                .RequireAssertion(context => context.User.FindAll("scp").Any(c => c.Value.Split(' ').Contains(InvitationProtocol.Scope)) &&
                    Guid.TryParseExact(context.User.FindFirst("oid")?.Value, "D", out var oid) && oid != Guid.Empty).Build();
            builder.Services.AddAuthorization(options =>
            {
                options.DefaultPolicy = staff; options.FallbackPolicy = staff;
                options.AddPolicy("CreateInvitations", creator);
            });
            builder.Services.AddRateLimiter(options =>
            {
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetTokenBucketLimiter("all", _ => new()
                { TokenLimit = 120, TokensPerPeriod = 120, ReplenishmentPeriod = TimeSpan.FromMinutes(1), AutoReplenishment = true, QueueLimit = 0 }));
                options.RejectionStatusCode = 429;
                options.OnRejected = (context, _) => { context.HttpContext.Response.Headers.RetryAfter = "60"; return ValueTask.CompletedTask; };
            });
            testAdapters?.Invoke(builder.Services);
            var app = builder.Build();
            var audit = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("GdapAudit");
            app.Use(async (context, next) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                try { await next(); }
                catch { if (!context.Response.HasStarted) { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { error = "status_unavailable" }); } }
                if (context.Request.Path != "/healthz")
                    audit.LogInformation("Request completed HTTP {Status}; technician {Technician}", context.Response.StatusCode,
                        context.User.Identity?.IsAuthenticated == true && Guid.TryParse(context.User.FindFirst("oid")?.Value, out var oid) ? oid.ToString() : "unauthenticated");
            });
            app.UseRouting();
            app.UseRateLimiter();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/healthz", () => Results.Ok(new { status = "alive" })).AllowAnonymous();
            app.MapGet("/v1/connection", () => new Gdap.Status.ConnectionInfo(1, settings.CippOrigin, settings.PartnerTenantId)).RequireAuthorization();
            app.MapGet("/v1/invitations/connection", () => new Gdap.Status.ConnectionInfo(1, settings.CippOrigin, settings.PartnerTenantId)).RequireAuthorization("CreateInvitations");
            app.MapGet("/v1/invitations/templates", async (CippInvitations invitations, HttpContext context) =>
            {
                if (context.Request.QueryString.HasValue) return Results.BadRequest();
                // Stop before the desktop reserves an operation when deployment
                // is known to be incomplete. POST still checks independently.
                if (settings.InvitationJournalDirectory is null || !Directory.Exists(settings.InvitationJournalDirectory))
                    return Results.Json(new { error = "persistent_journal_required" }, statusCode: 503);
                return Results.Ok(await invitations.Templates(context.RequestAborted));
            }).RequireAuthorization("CreateInvitations");
            app.MapPost("/v1/invitations", async (CippInvitations invitations, HttpContext context) =>
            {
                if (settings.InvitationJournalDirectory is null) return Results.Json(new { error = "persistent_journal_required" }, statusCode: 503);
                if (context.Request.QueryString.HasValue || !context.Request.HasJsonContentType()) return Results.BadRequest();
                using var buffer = new MemoryStream();
                var chunk = new byte[1024];
                int read;
                while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                { if (buffer.Length + read > 4096) return Results.StatusCode(413); buffer.Write(chunk, 0, read); }
                CreateInvitation request;
                try
                {
                    using var json = System.Text.Json.JsonDocument.Parse(buffer.ToArray());
                    InvitationProtocol.UniqueTree(json.RootElement);
                    if (json.RootElement.EnumerateObject().Any(p => p.Name is not ("operationId" or "templateId" or "fingerprint" or "reference"))) return Results.BadRequest();
                    request = json.RootElement.Deserialize<CreateInvitation>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
                    InvitationProtocol.Validate(request);
                }
                catch { return Results.BadRequest(); }
                var result = await invitations.Create(request, context.User.FindFirst("oid")!.Value, context.RequestAborted);
                return result is null ? Results.Json(new { error = "creation_uncertain_use_recovery" }, statusCode: 409) : Results.Ok(result);
            }).RequireAuthorization("CreateInvitations");
            app.MapGet("/v1/invitations/operations/{operationId}", async (string operationId, CippInvitations invitations, HttpContext context) =>
            {
                if (context.Request.QueryString.HasValue || !Guid.TryParseExact(operationId, "D", out _)) return Results.BadRequest();
                var result = await invitations.Recover(operationId, context.User.FindFirst("oid")!.Value, context.RequestAborted);
                return result is null ? Results.Json(new { error = "creation_uncertain_no_retry" }, statusCode: 409) : Results.Ok(result);
            }).RequireAuthorization("CreateInvitations");
            app.MapGet("/v1/onboarding/{relationshipId}", async (string relationshipId, CippReader reader, HttpContext context) =>
            {
                if (!StatusProtocol.IsRelationship(relationshipId) || context.Request.QueryString.HasValue) return Results.BadRequest(new { error = "invalid_request" });
                try { return Results.Ok(await reader.Read(relationshipId, context.RequestAborted)); }
                catch (UpstreamUnavailable problem)
                {
                    context.Response.Headers.RetryAfter = problem.RetrySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return Results.Json(new { error = "upstream_unavailable" }, statusCode: 503);
                }
            }).RequireAuthorization();
            return app;
        }
    }
}
