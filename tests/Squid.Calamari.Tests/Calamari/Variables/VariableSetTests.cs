using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Variables;

public class VariableSetTests
{
    [Fact]
    public void Set_And_Get_AreCaseInsensitive()
    {
        var set = new VariableSet();

        set.Set("My.Var", "123");

        set.Get("my.var").ShouldBe("123");
        set.Contains("MY.VAR").ShouldBeTrue();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void GetFlag_ParsesCommonValues(string value, bool expected)
    {
        var set = new VariableSet();
        set.Set("Flag", value);

        set.GetFlag("Flag").ShouldBe(expected);
    }

    [Fact]
    public void GetInt32_ParsesValue_OrReturnsNull()
    {
        var set = new VariableSet();
        set.Set("Good", "42");
        set.Set("Bad", "abc");

        set.GetInt32("Good").ShouldBe(42);
        set.GetInt32("Bad").ShouldBeNull();
        set.GetInt32("Missing").ShouldBeNull();
    }

    [Fact]
    public void Merge_LaterEntriesOverrideEarlier()
    {
        var set = new VariableSet();
        set.Set("Name", "first", isSensitive: false, source: "a");

        set.Merge(
        [
            new VariableEntry("Name", "second", IsSensitive: true, Source: "b")
        ]);

        set.Get("Name").ShouldBe("second");
        set.Entries.ShouldContain(e => e.Name == "Name" && e.IsSensitive && e.Source == "b");
    }
}
