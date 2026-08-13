namespace WarehouseEPI.Infrastructure.Security;

public sealed class PinFormatException()
    : ArgumentException("El NIP debe contener entre 4 y 8 dígitos.");
