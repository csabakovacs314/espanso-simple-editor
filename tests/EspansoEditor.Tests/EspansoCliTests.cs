using EspansoEditor.Core.Espanso;
using Xunit;

namespace EspansoEditor.Tests;

/// <summary>0.1.1: status and folder detection must not depend on espanso's printed text alone.</summary>
public sealed class EspansoCliTests
{
    [Theory]
    [InlineData(true, 0, "", EspansoState.Running)]                      // Windows: text lost, exit code says running
    [InlineData(true, 0, "espanso is running", EspansoState.Running)]
    [InlineData(true, 4, "", EspansoState.NotRunning)]                   // SERVICE_NOT_RUNNING, text lost
    [InlineData(true, 4, "espanso is not running", EspansoState.NotRunning)]
    [InlineData(true, 9009, "'espanso' nem ismerhető fel", EspansoState.NotInstalled)] // cmd.exe, any language
    [InlineData(false, -1, "The system cannot find the file specified.", EspansoState.NotInstalled)]
    [InlineData(true, -1, "timed out", EspansoState.Unknown)]
    [InlineData(true, 1, "", EspansoState.Unknown)]
    public void Status_is_read_from_exit_codes(bool started, int exit, string output, EspansoState expected)
    {
        Assert.Equal(expected, EspansoCli.Interpret(started, exit, output).State);
    }

    [Fact]
    public void Config_folder_is_found_in_either_path_output()
    {
        bool Exists(string d) => d == @"C:\Users\x\AppData\Roaming\espanso";
        Assert.Equal(@"C:\Users\x\AppData\Roaming\espanso",
            EspansoCli.ParseConfigDir("Config: C:\\Users\\x\\AppData\\Roaming\\espanso\r\nPackages: C:\\p\r\nRuntime: C:\\r\r\n", Exists));
        Assert.Equal(@"C:\Users\x\AppData\Roaming\espanso", EspansoCli.ParseConfigDir("C:\\Users\\x\\AppData\\Roaming\\espanso\r\n", Exists));
        Assert.Null(EspansoCli.ParseConfigDir("", Exists));
        Assert.Null(EspansoCli.ParseConfigDir("Config: C:\\does\\not\\exist", Exists));
    }
}
