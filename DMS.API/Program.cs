using DMS.API;
using DMS.API.Controllers;
using DMS.API.Extension;
using DMS.Application.Interface;
using DMS.Data.EF.Features;
using DMS.Data.EF.Mapping;
using Microsoft.AspNetCore.ResponseCompression;
using System.IO.Compression;

var builder = WebApplication.CreateBuilder(args);

// Load appsettings*.json, user-secrets (Development) and environment variables before anything reads them.
builder.AddAssociatedConfiguration();

var services = builder.Services;
var configuration = builder.Configuration;

// Non-nullable string properties in request DTOs are not treated as required (an empty or missing
// reference, description or file name made Edit Circular fail with "One or more validation errors occurred").
services.AddControllers(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);
services.AddProblemDetails();

services.AddServices(configuration);      // Swagger, DbContexts, JWT auth, logging, CORS
services.AddDependency(configuration);    // queries + services

services.AddAutoMapper(cfg => cfg.AddMaps(typeof(CircularsMappingProfile).Assembly));

// JSON answers compressed (brotli or gzip, whichever the caller accepts). Above all for the vessels: a batch of
// changes (manual text included) is mostly text and shrinks several times, which matters on a satellite link.
// Files (PDF, Office, images) are not in the list: they are compressed already.
services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(AppDomain.CurrentDomain.GetAssemblies()));

var app = builder.Build();

// One-off admin command to set an HQ user's password (stores a hash, never the password):
//   dotnet run --project DMS.API -- set-password <userId> <password>
if (args.Length == 3 && args[0] == "set-password")
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<IHQUserService>();
    var ok = await users.SetPasswordHashAsync(args[1], LoginController.HashPassword(args[2]));
    Console.WriteLine(ok ? $"Password set for {args[1]}." : $"No HQ user with UserID '{args[1]}'.");
    return;
}

// Errors first, so every later failure is turned into a clean JSON response.
app.UseMiddleware<ExceptionHandlingMiddleware>();

// Browsers must take a file for what its Content-Type says it is, never guess (a "PDF" that is really HTML).
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await next();
});
app.UseResponseCompression();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseHttpLogging();
app.UseHttpsRedirection();

app.UseCors(AddServiceExtension.CorsPolicy);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
