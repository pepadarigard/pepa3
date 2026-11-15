using Pepa.Host.Startup;

namespace Pepa.Host.Tests;

/// <summary>Запись автозапуска (D-086): командная строка должна разбираться обратно в ту же папку данных.</summary>
public sealed class AutostartEntryTests
{
    private static string Dir(params string[] parts) => Path.Combine([Path.GetTempPath(), .. parts]);

    [Theory]
    [InlineData(@"C:\Program Files\Pepa\Pepa.exe")]
    [InlineData(@"D:\Pepa\data\")]
    [InlineData(@"D:\")]
    [InlineData(@"C:\a b\c\\")]
    [InlineData("с \"кавычкой\" внутри")]
    [InlineData(@"\\server\share\Pepa data\")]
    public void Quoted_argument_reads_back_unchanged(string argument)
    {
        var parsed = AutostartEntry.Split($"\"C:\\Pepa\\Pepa.exe\" {AutostartEntry.Quote(argument)} --minimized");
        Assert.Equal(["C:\\Pepa\\Pepa.exe", argument, "--minimized"], parsed);
    }

    [Fact]
    public void Trailing_backslash_does_not_swallow_the_closing_quote()
    {
        // Раньше «--data-dir "D:\Pepa\data\"» читалось как папка «D:\Pepa\data"».
        Assert.Equal("\"D:\\Pepa\\data\\\\\"", AutostartEntry.Quote(@"D:\Pepa\data\"));
        Assert.Equal("\"D:\\\\\"", AutostartEntry.Quote(@"D:\"));
    }

    [Fact]
    public void Command_starts_its_own_data_folder_only()
    {
        var data = Dir("Pepa", "data");
        var command = AutostartEntry.Command(@"C:\Pepa\Pepa.exe", data + Path.DirectorySeparatorChar);
        Assert.True(AutostartEntry.Starts(command, data));
        Assert.True(AutostartEntry.Starts(command, data.ToUpperInvariant()));
        Assert.False(AutostartEntry.Starts(command, Dir("PepaTest", "data")));
        Assert.False(AutostartEntry.Starts(command, null));

        var byDefault = AutostartEntry.Command(@"C:\Pepa\Pepa.exe", null);
        Assert.Contains(AutostartEntry.MinimizedArgument, byDefault);
        Assert.True(AutostartEntry.Starts(byDefault, null));
        Assert.False(AutostartEntry.Starts(byDefault, data));
    }

    [Fact]
    public void Folders_with_the_same_name_get_different_entries()
    {
        var work = Dir("Pepa", "data");
        var test = Dir("PepaTest", "data");
        Assert.NotEqual(AutostartEntry.ValueName(work), AutostartEntry.ValueName(test));
        // Одна и та же папка — одно имя, с чертой в конце или без.
        Assert.Equal(AutostartEntry.ValueName(work), AutostartEntry.ValueName(work + Path.DirectorySeparatorChar));
        Assert.StartsWith("Pepa (data, ", AutostartEntry.ValueName(work));
        Assert.Equal("Pepa", AutostartEntry.ValueName(null));
        Assert.Equal("Pepa (data)", AutostartEntry.LegacyValueName(work));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(new byte[0], true)]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0 }, false)]
    [InlineData(new byte[] { 0x07, 1, 2, 3, 4, 5, 6, 7, 8, 0, 0, 0 }, false)]
    public void Startup_apps_switch_of_Windows_is_respected(byte[]? approval, bool approved) =>
        Assert.Equal(approved, AutostartEntry.IsApproved(approval));
}
