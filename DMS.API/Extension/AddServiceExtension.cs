using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi.Models;
using System.Net.Mime;
using System.Reflection;
using System.Text;
using DMS.API.Auth;
using DMS.Data.EF;
using DMS.Data.EF.Context;

namespace DMS.API.Extension;

public static class AddServiceExtension
{
    public static WebApplicationBuilder AddAssociatedConfiguration(this WebApplicationBuilder builder)
    {
        // Set the base path for configuration files
        builder.Configuration.SetBasePath(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Assembly location is null."));
        // Load configuration files
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
        builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true);
        if (builder.Environment.IsDevelopment())
            builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
        builder.Configuration.AddEnvironmentVariables();

        // Configure logging provider
        builder.Logging.ClearProviders(); // Optional: Clears default loggingproviders
        builder.Logging.AddConsole(); // Adds console logging provider
        builder.Logging.AddDebug(); // Adds Debug logging provider Set specific log levels for different librarues
        builder.Logging.SetMinimumLevel(LogLevel.Information); // Default minimum level
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning); // For Microsoft.* namespaces
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning); // For ASP.NET Core
        builder.Logging.AddFilter("System", LogLevel.Warning); // For System.* namespaces

        return builder;
    }

    public static IServiceCollection AddServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions();
        services.AddHttpClient();

        AddSwagger(services);
        AddDBContext(services, configuration);
        AddJWTToken(services, configuration);
        AddLogging(services);
        AddCORS(services, configuration);

        // The logged-in user for services that record who did something (logs, approvals).
        services.AddHttpContextAccessor();
        services.AddScoped<DMS.Application.Interface.ICurrentUser, HttpCurrentUser>();

        return services;
    }

    public static void AddSwagger(IServiceCollection services)
    {
        // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
        services.AddEndpointsApiExplorer();
        //services.AddSwaggerGen();

        //Add authentication in Swagger
        services.AddSwaggerGen(opt => {
            opt.SwaggerDoc("v1", new OpenApiInfo { Title = "DMS", Version = "v1" });
            opt.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { In = ParameterLocation.Header, Description = "Please enter token", Name = "Authorization", Type = SecuritySchemeType.Http, BearerFormat = "JWT", Scheme = "bearer" });
            opt.AddSecurityRequirement(new OpenApiSecurityRequirement { { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() } });
        });

    }

    public static void AddDBContext(IServiceCollection services, IConfiguration configuration)
    {
        var configValue = configuration.GetValue<string>("ConnectionStrings:LocalDBConnection");

        if (string.IsNullOrEmpty(configValue))
        {
            throw new InvalidOperationException("The connection string 'ConnectionStrings:LocalDBConnection' is not configured or is null/empty.");
        }

        services.AddDbContext<DmsContext>(options =>
            options.UseSqlServer(configValue));

        services.AddDbContext<CircularsContext>(options =>
            options.UseSqlServer(configValue));

        services.AddDbContext<GeneralContext>(options =>
            options.UseSqlServer(configValue));

        services.AddDbContext<OtherDocumentsContext>(options =>
            options.UseSqlServer(configValue));

        DBConnectionString dBConnectionStringOption = new(configValue);
        dBConnectionStringOption.DefaultConnection = configValue;
        services.AddSingleton(dBConnectionStringOption);
    }

    public static void AddJWTToken(IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        if (string.IsNullOrWhiteSpace(jwt.Key) || jwt.Key.Length < 32)
            throw new InvalidOperationException("Jwt:Key must be at least 32 random characters. Set it with user-secrets or the Jwt__Key environment variable.");

        // One scheme: bearer tokens issued by LoginController. (The previous setup registered Azure AD as a
        // cookie-based web app, which a React SPA calling the API cannot use.)
        // Set every default explicitly, so [Authorize] always knows to challenge with JWT bearer
        // ("No authenticationScheme was specified, and there was no DefaultChallengeScheme found").
		//  services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultForbidScheme = JwtBearerDefaults.AuthenticationScheme;
        })
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                    NameClaimType = DmsClaims.UserId,
                    RoleClaimType = DmsClaims.Role,
                    ClockSkew = TimeSpan.FromMinutes(2)
                };
            });
        services.AddAuthorization();
    }

    public static void AddLogging(IServiceCollection services)
    {
        services.AddLogging(configure => {
            configure.AddConsole();       // Log to the console
            configure.SetMinimumLevel(LogLevel.Information); // Minimum level
        });

        services.AddHttpLogging(configure =>
        {
            configure.LoggingFields = Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestMethod
                                      | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestPath
                                      | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.RequestQuery
                                      | Microsoft.AspNetCore.HttpLogging.HttpLoggingFields.ResponseStatusCode;
            configure.RequestHeaders.Add(HeaderNames.Referer);
            configure.MediaTypeOptions.AddText(MediaTypeNames.Application.Json);
            configure.RequestBodyLogLimit = 4096; // Limit request body size to 4KB
            configure.ResponseBodyLogLimit = 4096; // Limit response body size to 4KB   
            configure.ResponseHeaders.Add("X-Response-Time"); // Log custom response header
        });
    }

    public const string CorsPolicy = "DmsFrontend";

    public static void AddCORS(this IServiceCollection services, IConfiguration configuration)
    {
        // Only the frontend origins listed in AllowedCorsOrigin may call the API from a browser.
        var allowedCorsOrigin = configuration
            .GetSection("AllowedCorsOrigin")
            .Get<string[]>() ?? Array.Empty<string>();

        services.AddCors(options =>
        {
            options.AddPolicy(CorsPolicy, policy =>
            {
                policy.WithOrigins(allowedCorsOrigin)
                      .AllowAnyMethod()
                      .AllowAnyHeader()
                      .WithExposedHeaders("Content-Disposition"); // so the browser can read a download's file name
            });
        });
    }

    public static bool IsDebugInfoAllowed(this IWebHostEnvironment env)
    {
        string[] debugEnvSuffix = { "DV", "DEV", "QA", "Local" };
        return env.IsDevelopment() || debugEnvSuffix.Any(suffix => env.EnvironmentName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
