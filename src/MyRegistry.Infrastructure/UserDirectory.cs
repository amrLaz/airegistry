using Microsoft.EntityFrameworkCore;
using MyRegistry.Application;
using MyRegistry.Domain;

namespace MyRegistry.Infrastructure;

public class UserDirectory(AppDbContext db) : IUserDirectory
{
    public (Role Role, bool Active, string DisplayName) Resolve(string email, Role defaultRole)
    {
        var key = email.Trim().ToLowerInvariant();
        var u = db.Users.FirstOrDefault(x => x.Email == key);
        if (u is null)
        {
            u = new AppUser { Email = key, DisplayName = key.Split('@')[0], Role = defaultRole };
            db.Users.Add(u);
        }
        if (u.LastLogin is null || DateTime.UtcNow - u.LastLogin > TimeSpan.FromMinutes(5)) u.LastLogin = DateTime.UtcNow;
        db.SaveChanges();
        return (u.Role, u.Active, u.DisplayName);
    }
}
