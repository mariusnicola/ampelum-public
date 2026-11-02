using System.Security.Cryptography;

namespace SignalGrid.Ampelum.Agent.Tests;

internal static class TestSigningKey
{
    public static byte[] Create() => RandomNumberGenerator.GetBytes(32);
}
