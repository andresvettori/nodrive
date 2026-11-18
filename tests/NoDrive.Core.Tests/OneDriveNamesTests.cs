using NoDrive.Core.Names;
using Xunit;

namespace NoDrive.Core.Tests;

public class OneDriveNamesTests
{
    [Theory]
    [InlineData("~$report.docx")]
    [InlineData("a.TMP")]
    [InlineData("movie.crdownload")]
    [InlineData("x.part")]
    [InlineData("Desktop.ini")]
    [InlineData("thumbs.db")]
    [InlineData(".lock")]
    [InlineData("site_vti_cnf")]
    [InlineData(" leading.txt")]
    public void Names_OneDrive_rejects_or_that_are_temporary_are_never_uploaded(string name) => Assert.True(OneDriveNames.IsNeverUploaded(name, atRoot: false));

    [Theory]
    [InlineData("report.docx")]
    [InlineData("forms.pdf")]
    [InlineData("my.lock.txt")]
    [InlineData("Photos")]
    public void Ordinary_names_are_uploaded(string name) => Assert.False(OneDriveNames.IsNeverUploaded(name, atRoot: true));

    [Fact]
    public void A_forms_folder_is_only_reserved_in_the_root()
    {
        Assert.True(OneDriveNames.IsNeverUploaded("Forms", atRoot: true));
        Assert.False(OneDriveNames.IsNeverUploaded("Forms", atRoot: false));
    }
}
