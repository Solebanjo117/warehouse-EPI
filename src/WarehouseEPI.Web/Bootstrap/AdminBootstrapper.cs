using Microsoft.EntityFrameworkCore;
using WarehouseEPI.Core.Entities;
using WarehouseEPI.Infrastructure.Persistence;
using WarehouseEPI.Infrastructure.Security;

namespace WarehouseEPI.Web.Bootstrap;

public static class AdminBootstrapper
{
    public static async Task RunAsync(IServiceProvider services)
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(
                "La creación del administrador requiere una terminal interactiva.");
        }

        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        var userPinService = scope.ServiceProvider.GetRequiredService<UserPinService>();

        if (await dbContext.Users.AnyAsync())
        {
            throw new InvalidOperationException(
                "Ya existen usuarios. Use la administración web para crear otros usuarios.");
        }

        var adminRole = await dbContext.Roles
            .SingleAsync(role => role.Code == "ADMIN");

        Console.Write("Nombre completo del administrador: ");
        var fullName = Console.ReadLine()?.Trim();
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Length > 160)
        {
            throw new InvalidOperationException(
                "El nombre es obligatorio y no puede superar 160 caracteres.");
        }

        Console.Write("NIP (4 a 8 dígitos): ");
        var pin = ReadSecret();
        Console.Write("Confirmar NIP: ");
        var confirmation = ReadSecret();

        if (!string.Equals(pin, confirmation, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Los NIP no coinciden.");
        }

        var user = new User
        {
            FullName = fullName,
            RoleId = adminRole.Id,
            PinLookup = string.Empty,
            PinHash = string.Empty
        };

        var assignment = await userPinService.AssignAsync(user, pin);
        if (assignment == PinAssignmentResult.InvalidFormat)
        {
            throw new PinFormatException();
        }

        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        Console.WriteLine("Administrador creado correctamente.");
    }

    private static string ReadSecret()
    {
        var characters = new List<char>();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string([.. characters]);
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (characters.Count > 0)
                {
                    characters.RemoveAt(characters.Count - 1);
                }

                continue;
            }

            if (key.KeyChar is >= '0' and <= '9' && characters.Count < PinProtector.MaximumLength)
            {
                characters.Add(key.KeyChar);
            }
        }
    }
}
