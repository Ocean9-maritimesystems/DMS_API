using DMS.API;
using DMS.API.Controllers;
using DMS.API.Extension;
using DMS.Application.Interface;
using DMS.Data.EF.Mapping;

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

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
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
