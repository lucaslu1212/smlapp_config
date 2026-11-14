using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using SmlServer.Models;
using SmlServer.Services;

namespace SmlServer.Endpoints;

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public static class AuthEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context, DataStore store, AuditService audit) =>
        {
            var username = (request.Username ?? string.Empty).Trim();
            var password = request.Password ?? string.Empty;
            if (username.Length == 0 || password.Length == 0)
                return Results.BadRequest(new { error = "请输入用户名和密码" });

            var user = store.Read(s => s.Users.FirstOrDefault(u =>
                string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));

            if (user is null || !user.Enabled || !Security.VerifyPassword(password, user.PasswordHash, user.PasswordSalt))
            {
                audit.Write(username, "-", "auth.login.failed", username, "用户名或密码错误", context.Connection.RemoteIpAddress?.ToString() ?? "");
                return Results.Json(new { error = "用户名或密码不正确" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.Id),
                new(ClaimTypes.Name, user.Username),
                new(ClaimTypes.Role, user.Role),
                new("displayName", string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName),
            };

            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7),
                });

            store.Mutate(s =>
            {
                var target = s.Users.FirstOrDefault(u => u.Id == user.Id);
                if (target is not null) target.LastLoginAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            });

            audit.Write(user.Username, user.Role, "auth.login", user.Username, null,
                context.Connection.RemoteIpAddress?.ToString() ?? "");

            return Results.Ok(ToDto(user));
        });

        app.MapPost("/api/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization();

        app.MapGet("/api/auth/me", (HttpContext context, DataStore store) =>
        {
            var user = CurrentUser(context, store);
            if (user is null) return Results.Json(new { error = "未登录" }, statusCode: 401);
            return Results.Ok(ToDto(user));
        }).RequireAuthorization();
    }

    public static UserRecord? CurrentUser(HttpContext context, DataStore store)
    {
        var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(id)) return null;
        return store.Read(s => s.Users.FirstOrDefault(u => u.Id == id));
    }

    private static object ToDto(UserRecord user) => new
    {
        id = user.Id,
        username = user.Username,
        displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName,
        role = user.Role,
        classroomIds = user.ClassroomIds,
        lastLoginAt = user.LastLoginAt,
    };
}
