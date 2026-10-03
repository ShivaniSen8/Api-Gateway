using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Diagnostics;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("storefront", policy =>
    {
        policy.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var jwtKey = builder.Configuration["Jwt:Key"];

if (string.IsNullOrWhiteSpace(jwtKey))
{
    throw new InvalidOperationException(
        "JWT Key is not configured.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,

            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],

            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("authenticated", policy =>
    {
        policy.RequireAuthenticatedUser();
    });
});
var app = builder.Build();

app.Use(async (context, next) =>
{
    const string correlationHeader = "X-Correlation-ID";
    var correlationId = context.Request.Headers[correlationHeader].FirstOrDefault();

    if (string.IsNullOrWhiteSpace(correlationId))
    {
        correlationId = Guid.NewGuid().ToString("N");
        context.Request.Headers[correlationHeader] = correlationId;
    }

    context.Response.Headers[correlationHeader] = correlationId;
    var activity = Activity.Current;
    var traceId = activity?.TraceId.ToString() ?? context.TraceIdentifier;
    var spanId = activity?.SpanId.ToString() ?? context.TraceIdentifier;
    context.Response.Headers["X-Trace-ID"] = traceId;
    context.Response.Headers["X-Span-ID"] = spanId;

    using (app.Logger.BeginScope(new Dictionary<string, object>
    {
        [correlationHeader] = correlationId,
        ["TraceId"] = traceId,
        ["SpanId"] = spanId
    }))
    {
        app.Logger.LogInformation(
            "Request started: {Method} {Path} TraceId={TraceId} SpanId={SpanId}",
            context.Request.Method,
            context.Request.Path,
            traceId,
            spanId);

        try
        {
            await next();
        }
        finally
        {
            app.Logger.LogInformation(
                "Request finished: {StatusCode} {Method} {Path} TraceId={TraceId} SpanId={SpanId}",
                context.Response.StatusCode,
                context.Request.Method,
                context.Request.Path,
                traceId,
                spanId);
        }
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";
        context.Response.Headers["X-Correlation-ID"] =
            context.Request.Headers["X-Correlation-ID"].FirstOrDefault() ?? string.Empty;
        var activity = Activity.Current;
        var traceId = activity?.TraceId.ToString() ?? context.TraceIdentifier;
        var spanId = activity?.SpanId.ToString() ?? context.TraceIdentifier;
        context.Response.Headers["X-Trace-ID"] = traceId;
        context.Response.Headers["X-Span-ID"] = spanId;

        await context.Response.WriteAsJsonAsync(new
        {
            success = false,
            message = app.Environment.IsDevelopment()
                ? exception?.Message ?? "An unexpected error occurred."
                : "An unexpected error occurred.",
            path = context.Request.Path.Value,
            correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault(),
            traceId,
            spanId
        });
    });
});

app.UseHttpsRedirection();
app.UseCors("storefront");
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapGet("/sadmin", () =>
    {
        try{
        var numerator = 1;
        var denominator = 0;

        return numerator / denominator;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("An error occurred in the /sadmin endpoint.", ex);
        }
    });
}

app.MapControllers();

app.MapReverseProxy();

app.Run();