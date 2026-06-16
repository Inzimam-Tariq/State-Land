using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using StateLand.Data;
using StateLand.Hubs;
using StateLand.Models.OTP;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 🔹 Controllers
builder.Services.AddControllers();

// 🔹 DbContext (FIX THIS!)
// 🔹 DbContext (FIX THIS!)
builder.Services.AddDbContext<PulsegisContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions =>
        {
            sqlOptions.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(30),
                errorNumbersToAdd: null);
        })
);

// 🔹 JWT Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = "DynamicScheme";
    options.DefaultChallengeScheme = "DynamicScheme";
})
.AddPolicyScheme("DynamicScheme", "Dynamic JWT Scheme", options =>
{
    options.ForwardDefaultSelector = context =>
    {
        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();

        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
            return "SystemScheme";

        var token = authHeader.Substring("Bearer ".Length);

        var jwtHandler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var jwtToken = jwtHandler.ReadJwtToken(token);

        var issuer = jwtToken.Issuer;

        if (issuer == builder.Configuration["JwtForPublic:Issuer"])
            return "PublicScheme";

        return "SystemScheme";
    };
})

.AddJwtBearer("SystemScheme", options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,

        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],

        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]))
    };

    // 🔥 SignalR JWT Fix
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];

            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) &&
                path.StartsWithSegments("/api/balloting/ballotingHub"))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
})

.AddJwtBearer("PublicScheme", options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,

        ValidIssuer = builder.Configuration["JwtForPublic:Issuer"],
        ValidAudience = builder.Configuration["JwtForPublic:Audience"],

        IssuerSigningKey = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(builder.Configuration["JwtForPublic:Key"]))
    };

    // 🔥 SignalR JWT Fix
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];

            var path = context.HttpContext.Request.Path;

            if (!string.IsNullOrEmpty(accessToken) &&
                path.StartsWithSegments("/api/balloting/ballotingHub"))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
});

builder.Services.AddSignalR(options =>
{
    options.ClientTimeoutInterval = TimeSpan.FromMinutes(40);
    options.KeepAliveInterval = TimeSpan.FromSeconds(30);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.MaximumReceiveMessageSize = 1024 * 1024 * 100; // 100MB
});

//END Balloting Task
// 🔹 Authorization
builder.Services.AddAuthorization();

// 🔹 SMS Services
builder.Services.Configure<SmsApiSettings>(
    builder.Configuration.GetSection("SmsApi"));

builder.Services.AddHttpClient<SmsAuthService>();

// 🔹 Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 🔹 CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins(
                "http://localhost:4200",
                "https://akar.pulse.gop.pk",
                "https://akardev.pulse.gop.pk"
            )
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// 🔹 Swagger Middleware
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 🔹 Middleware Order (IMPORTANT)

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseRouting();

// 🔥 CORS MUST COME HERE
app.UseCors();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto
});

app.UseAuthentication();

app.UseAuthorization();

// 🔹 Controllers
app.MapControllers();

// 🔹 Root Redirect
app.MapGet("/", async context =>
{
    context.Response.Redirect("/login.html");
});

// 🔹 SignalR Hub
app.MapHub<BallotingHub>("/api/balloting/ballotingHub");

app.Run();