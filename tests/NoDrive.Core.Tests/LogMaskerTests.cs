using NoDrive.Core.Support;
using Xunit;

namespace NoDrive.Core.Tests;

/// <summary>Real log lines (names changed) that leaked through the old masking.</summary>
public class LogMaskerTests
{
    private const string Root = @"C:\Users\someone\NoDrive";

    private static string Mask(string line) => LogMasker.Mask(line, Root);

    [Theory]
    [InlineData("2026-11-15 21:19:36.922 INFO  free up space: WindowsUpdate5.log done in 3 ms", "WindowsUpdate5")]
    [InlineData("INFO  keep on device: DSC04652.JPG done in 3429 ms", "DSC04652")]
    [InlineData("WARN  keep on device: DSC01982.JPG -> 0x80070184; retry 1/5 in 5 s", "DSC01982")]
    [InlineData(@"WARN  CfCreatePlaceholders(C:\Users\someone\NoDrive\Libros\Capacitacion Microsoft) -> 0x800700B7, processed 18/18", "Capacitacion")]
    [InlineData(@"WARN  refresh of C:\Users\someone\NoDrive\_pruebas\c.txt -> 0x80070006", "_pruebas")]
    [InlineData(@"WARN  mark in sync: open C:\Users\someone\NoDrive\Docs\Assessment (1 semana)\~WRL0001.tmp -> 0x80070002", "Assessment")]
    [InlineData("INFO  uploaded file 'Secret plan.docx' in 3 ms", "Secret")]
    [InlineData("INFO  renamed/moved 'Old name.docx' to 'New name.docx' in the cloud", "Old name")]
    [InlineData(@"INFO  applied to C:\Users\someone\NoDrive\Photos 2015\IMG_0001.JPG", "IMG_0001")]
    public void No_name_survives(string line, string name)
    {
        string masked = Mask(line);
        Assert.DoesNotContain(name, masked, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_useful_parts_of_a_line_are_kept()
    {
        string masked = Mask(@"WARN  refresh of C:\Users\someone\NoDrive\_pruebas\c.txt -> 0x80070006");
        Assert.Contains("refresh of", masked);
        Assert.Contains("0x80070006", masked);
        Assert.Contains("<sync folder>", masked);
        Assert.Contains("done in 3 ms", Mask("INFO  free up space: a.log done in 3 ms"));
    }

    [Fact]
    public void Lines_without_names_are_untouched()
    {
        const string line = "INFO  reconcile: 5275 folders checked, 6 edited, 6 plain, 0 moved, 0 missing, 114377 ms";
        Assert.Equal(line, Mask(line));
    }
}
