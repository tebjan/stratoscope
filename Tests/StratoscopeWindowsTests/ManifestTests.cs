namespace Stratoscope.Tests;

public class ManifestTests
{
    /// <summary>
    /// The embedded Win32 manifest is text inside the exe. Without supportedOS (Windows 10) Windows runs the process in Windows 7
    /// compatibility mode and CreateWindowEx refuses a layered child window: the readout silently never appears. This broke once.
    /// </summary>
    [Fact]
    public void TheProgramCarriesTheManifestThatLetsItCreateALayeredChildOfTheTaskbar()
    {
        var text = System.Text.Encoding.ASCII.GetString(System.IO.File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Stratoscope.exe")));
        Assert.Contains("{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", text);
        Assert.Contains("PerMonitorV2", text);
    }
}
